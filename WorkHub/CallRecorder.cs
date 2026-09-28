using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WorkHub;

/// <summary>
/// Records a call into a single Whisper-optimized WAV file at 16 kHz 16-bit
/// (Whisper's native sample rate). The microphone (you) goes to the LEFT channel and
/// the system output / loopback (everyone else) to the RIGHT channel, so both voices
/// live in one file yet stay clip-free and separable. Whisper down-mixes to mono on
/// load, so transcription just works. If no mic is available, a mono file is written.
/// Writing is paced to the wall clock so the timeline stays real-time on long calls.
/// </summary>
public sealed class CallRecorder : IDisposable
{
    // Whisper works internally at 16 kHz; matching it gives the smallest file with no
    // accuracy loss and no extra resample step in the transcriber.
    private const int TargetSampleRate = 16000;

    private readonly object _gate = new();

    private WasapiLoopbackCapture? _loopback;
    private WasapiCapture? _mic;
    private WaveFileWriter? _writer;
    private Thread? _writerThread;
    private volatile bool _running;

    public bool IsRecording
    {
        get { lock (_gate) return _running; }
    }

    public string? CurrentFilePath { get; private set; }

    public string OutputFolder { get; }

    public event EventHandler<string>? RecordingStarted; // arg = file path
    public event EventHandler<string>? RecordingStopped;  // arg = file path
    public event EventHandler<string>? Error;             // arg = message

    public CallRecorder(string? outputFolder = null)
    {
        OutputFolder = outputFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "CallRecordings");
        Directory.CreateDirectory(OutputFolder);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;

            try
            {
                // --- System output (loopback) on the default render device -> mono 16 kHz ---
                _loopback = new WasapiLoopbackCapture();
                var loopbackBuffer = new BufferedWaveProvider(_loopback.WaveFormat)
                {
                    ReadFully = true,
                    DiscardOnBufferOverflow = true,
                    BufferDuration = TimeSpan.FromSeconds(20),
                };
                _loopback.DataAvailable += (_, e) =>
                    loopbackBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                ISampleProvider systemMono = ToMono16k(loopbackBuffer.ToSampleProvider());

                // --- Microphone (default communications capture device) -> mono 16 kHz, optional ---
                ISampleProvider? micMono = null;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var micDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    _mic = new WasapiCapture(micDevice);
                    var micBuffer = new BufferedWaveProvider(_mic.WaveFormat)
                    {
                        ReadFully = true,
                        DiscardOnBufferOverflow = true,
                        BufferDuration = TimeSpan.FromSeconds(20),
                    };
                    _mic.DataAvailable += (_, e) =>
                        micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    micMono = ToMono16k(micBuffer.ToSampleProvider());
                }
                catch (Exception ex)
                {
                    _mic = null;
                    Error?.Invoke(this, "Микрофон недоступен, пишу только системный звук: " + ex.Message);
                }

                // Single file. With a mic -> stereo (L = mic / you, R = system / others).
                // Without a mic -> plain mono of the system output.
                IWaveProvider pcm16;
                if (micMono != null)
                {
                    var stereo = new MultiplexingSampleProvider(new[] { micMono, systemMono }, 2);
                    pcm16 = new SampleToWaveProvider16(stereo);
                }
                else
                {
                    pcm16 = new SampleToWaveProvider16(systemMono);
                }

                CurrentFilePath = Path.Combine(
                    OutputFolder, $"call_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav");
                _writer = new WaveFileWriter(CurrentFilePath, pcm16.WaveFormat);

                _running = true;
                _loopback.StartRecording();
                _mic?.StartRecording();

                _writerThread = new Thread(() => WriterLoop(pcm16))
                {
                    IsBackground = true,
                    Name = "CallRecorder.Writer",
                };
                _writerThread.Start();

                RecordingStarted?.Invoke(this, CurrentFilePath);
            }
            catch (Exception ex)
            {
                _running = false;
                CleanupCaptures();
                CleanupWriter();
                Error?.Invoke(this, "Не удалось начать запись: " + ex.Message);
            }
        }
    }

    public void Stop()
    {
        Thread? writerThread;
        string? file;
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            writerThread = _writerThread;
            file = CurrentFilePath;

            try { _loopback?.StopRecording(); } catch { /* ignore */ }
            try { _mic?.StopRecording(); } catch { /* ignore */ }
        }

        writerThread?.Join(3000);

        lock (_gate)
        {
            CleanupCaptures();
            CleanupWriter();
        }

        if (file != null) RecordingStopped?.Invoke(this, file);
    }

    /// <summary>
    /// Reads the combined stream paced to the wall clock and writes it to disk.
    /// ReadFully-backed sources return silence for gaps, so the file stays aligned
    /// to real time over the whole call.
    /// </summary>
    private void WriterLoop(IWaveProvider source)
    {
        var format = source.WaveFormat;
        int avgBytesPerSecond = format.AverageBytesPerSecond;
        int blockAlign = format.BlockAlign;
        var buffer = new byte[8192 - 8192 % blockAlign];

        var sw = Stopwatch.StartNew();
        long bytesWritten = 0;

        while (true)
        {
            bool running;
            lock (_gate) running = _running;
            if (!running) break;

            long targetBytes = (long)(sw.Elapsed.TotalSeconds * avgBytesPerSecond);
            targetBytes -= targetBytes % blockAlign;
            long toRead = targetBytes - bytesWritten;

            while (toRead > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, toRead);
                int read = source.Read(buffer, 0, chunk);
                if (read <= 0) break;
                lock (_gate)
                {
                    if (_writer == null) break;
                    _writer.Write(buffer, 0, read);
                }
                bytesWritten += read;
                toRead -= read;
            }

            Thread.Sleep(20);
        }
    }

    /// <summary>Resample to 16 kHz and down-mix to a single mono channel.</summary>
    private static ISampleProvider ToMono16k(ISampleProvider source)
    {
        if (source.WaveFormat.SampleRate != TargetSampleRate)
            source = new WdlResamplingSampleProvider(source, TargetSampleRate);

        switch (source.WaveFormat.Channels)
        {
            case 1:
                return source;
            case 2:
                return new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
            default:
                var stereo = new MultiplexingSampleProvider(new[] { source }, 2);
                return new StereoToMonoSampleProvider(stereo) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
    }

    private void CleanupCaptures()
    {
        try { _loopback?.Dispose(); } catch { /* ignore */ }
        try { _mic?.Dispose(); } catch { /* ignore */ }
        _loopback = null;
        _mic = null;
    }

    private void CleanupWriter()
    {
        try { _writer?.Flush(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
    }

    public void Dispose() => Stop();
}
