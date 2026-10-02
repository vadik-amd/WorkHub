using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
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
/// <para>
/// The system side is a loopback of EVERY active output device, mixed together. Call
/// apps don't necessarily play to the default device: Slack / Zoom / Teams often use the
/// "default communications" device (e.g. a Bluetooth headset) while the default device
/// is the laptop speakers — a loopback of the default device alone then records silence.
/// Devices that show up mid-call (headset connected) are picked up by a periodic re-scan.
/// </para>
/// </summary>
public sealed class CallRecorder : IDisposable
{
    // Whisper works internally at 16 kHz; matching it gives the smallest file with no
    // accuracy loss and no extra resample step in the transcriber.
    private const int TargetSampleRate = 16000;
    private static readonly TimeSpan DeviceRescan = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();

    // Loopback per output device, keyed by endpoint id; all of them feed _systemMix.
    private readonly Dictionary<string, Loopback> _loopbacks = new();
    private LiveMixer? _systemMix;
    private System.Threading.Timer? _rescanTimer;

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
                CurrentFilePath = Path.Combine(
                    OutputFolder, $"call_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.wav");
                RecorderLog.Write($"=== Запись {Path.GetFileName(CurrentFilePath)}");

                // --- System output: loopback of every active render device -> mono 16 kHz ---
                _systemMix = new LiveMixer(TargetSampleRate);
                SyncLoopbacks(starting: true);
                if (_loopbacks.Count == 0)
                    Error?.Invoke(this, "Не найдено ни одного устройства вывода — голос собеседников не запишется.");

                // --- Microphone (the one the call app has open) -> mono 16 kHz, optional ---
                ISampleProvider? micMono = null;
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    var micDevice = PickMicrophone(enumerator);
                    RecorderLog.Write($"Микрофон: {micDevice.FriendlyName} ({micDevice.AudioClient.MixFormat})");
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
                    RecorderLog.Write("Микрофон недоступен: " + ex.Message);
                    Error?.Invoke(this, "Микрофон недоступен, пишу только системный звук: " + ex.Message);
                }

                // Single file. With a mic -> stereo (L = mic / you, R = system / others).
                // Without a mic -> plain mono of the system output.
                IWaveProvider pcm16;
                if (micMono != null)
                {
                    var stereo = new MultiplexingSampleProvider(new[] { micMono, _systemMix }, 2);
                    pcm16 = new SampleToWaveProvider16(stereo);
                }
                else
                {
                    pcm16 = new SampleToWaveProvider16(_systemMix);
                }

                _writer = new WaveFileWriter(CurrentFilePath, pcm16.WaveFormat);

                _running = true;
                foreach (var lb in _loopbacks.Values) lb.Capture.StartRecording();
                _mic?.StartRecording();

                _writerThread = new Thread(() => WriterLoop(pcm16))
                {
                    IsBackground = true,
                    Name = "CallRecorder.Writer",
                };
                _writerThread.Start();
                _rescanTimer = new System.Threading.Timer(_ => Rescan(), null, DeviceRescan, DeviceRescan);

                RecordingStarted?.Invoke(this, CurrentFilePath);
            }
            catch (Exception ex)
            {
                _running = false;
                CleanupCaptures();
                CleanupWriter();
                RecorderLog.Write("Не удалось начать запись: " + ex);
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

            _rescanTimer?.Dispose();
            _rescanTimer = null;
            foreach (var lb in _loopbacks.Values)
                try { lb.Capture.StopRecording(); } catch { /* ignore */ }
            try { _mic?.StopRecording(); } catch { /* ignore */ }
        }

        writerThread?.Join(3000);

        lock (_gate)
        {
            CleanupCaptures();
            CleanupWriter();
        }

        if (file != null)
        {
            RecorderLog.Write($"=== Стоп {Path.GetFileName(file)}");
            RecordingStopped?.Invoke(this, file);
        }
    }

    private void Rescan()
    {
        lock (_gate)
        {
            if (!_running) return;
            try { SyncLoopbacks(starting: false); }
            catch (Exception ex) { RecorderLog.Write("Ошибка пересканирования устройств: " + ex.Message); }
        }
    }

    /// <summary>
    /// Brings the set of loopback captures in line with the active render devices: adds
    /// new ones (a headset connected mid-call), drops ones that vanished or whose capture
    /// died (device unplugged / invalidated — it is re-added if it is still active).
    /// Called under <see cref="_gate"/>.
    /// </summary>
    private void SyncLoopbacks(bool starting)
    {
        if (_systemMix == null) return;

        var active = new Dictionary<string, MMDevice>();
        using (var enumerator = new MMDeviceEnumerator())
            foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                active[dev.ID] = dev;

        foreach (var (id, lb) in _loopbacks.ToList())
        {
            if (active.ContainsKey(id) && !lb.Failed) continue;
            RecorderLog.Write($"Выход отключён: {lb.Name}" + (lb.Failed ? " (захват прервался)" : ""));
            _systemMix.Remove(lb.Mono);
            try { lb.Capture.Dispose(); } catch { /* ignore */ }
            _loopbacks.Remove(id);
        }

        foreach (var (id, dev) in active)
        {
            if (_loopbacks.ContainsKey(id)) { dev.Dispose(); continue; }
            try
            {
                var lb = new Loopback(dev);
                _loopbacks[id] = lb;
                _systemMix.Add(lb.Mono);
                if (!starting) lb.Capture.StartRecording();
                RecorderLog.Write($"Выход{(starting ? "" : " подключён")}: {lb.Name} ({lb.Capture.WaveFormat})");
            }
            catch (Exception ex)
            {
                RecorderLog.Write($"Не удалось захватить выход {dev.FriendlyName}: {ex.Message}");
                dev.Dispose();
            }
        }
    }

    /// <summary>
    /// The microphone the call app actually has open (an active capture session of another
    /// process), preferring the default communications device; falls back to that default
    /// when nobody holds a mic yet (manual start).
    /// </summary>
    private static MMDevice PickMicrophone(MMDeviceEnumerator enumerator)
    {
        MMDevice? comms = null;
        try { comms = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications); }
        catch { /* no default */ }

        uint ownPid = (uint)Environment.ProcessId;
        MMDevice? inUse = null;
        foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            if (!IsUsedByOthers(dev, ownPid)) continue;
            if (comms != null && dev.ID == comms.ID) return comms;
            inUse ??= dev;
        }
        return inUse ?? comms ?? throw new InvalidOperationException("нет устройства записи");
    }

    private static bool IsUsedByOthers(MMDevice device, uint ownPid)
    {
        try
        {
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                uint pid;
                try { pid = session.GetProcessID; }
                catch { continue; }
                if (pid != 0 && pid != ownPid) return true;
            }
        }
        catch { /* treat as unused */ }
        return false;
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
        _rescanTimer?.Dispose();
        _rescanTimer = null;
        foreach (var lb in _loopbacks.Values)
            try { lb.Capture.Dispose(); } catch { /* ignore */ }
        _loopbacks.Clear();
        _systemMix = null;
        try { _mic?.Dispose(); } catch { /* ignore */ }
        _mic = null;
    }

    private void CleanupWriter()
    {
        try { _writer?.Flush(); } catch { /* ignore */ }
        try { _writer?.Dispose(); } catch { /* ignore */ }
        _writer = null;
    }

    public void Dispose() => Stop();

    /// <summary>Loopback capture of one output device, buffered and converted to mono 16 kHz.</summary>
    private sealed class Loopback
    {
        public string Name { get; }
        public WasapiLoopbackCapture Capture { get; }
        public ISampleProvider Mono { get; }
        public volatile bool Failed;

        public Loopback(MMDevice device)
        {
            Name = device.FriendlyName;
            Capture = new WasapiLoopbackCapture(device);
            var buffer = new BufferedWaveProvider(Capture.WaveFormat)
            {
                ReadFully = true,
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(20),
            };
            Capture.DataAvailable += (_, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            // A capture that stops on its own (device unplugged / invalidated) is replaced
            // on the next re-scan.
            Capture.RecordingStopped += (_, e) => { if (e.Exception != null) Failed = true; };
            Mono = ToMono16k(buffer.ToSampleProvider());
        }
    }

    /// <summary>
    /// Sums any number of mono inputs; inputs can come and go while it is being read.
    /// Always returns the full count (silence when there are no inputs or an input comes
    /// up short), so the right channel never ends early and the timeline stays intact.
    /// Clipping is handled by the 16-bit converter downstream.
    /// </summary>
    private sealed class LiveMixer(int sampleRate) : ISampleProvider
    {
        private readonly List<ISampleProvider> _inputs = new();
        private float[] _scratch = Array.Empty<float>();

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public void Add(ISampleProvider input) { lock (_inputs) _inputs.Add(input); }
        public void Remove(ISampleProvider input) { lock (_inputs) _inputs.Remove(input); }

        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            lock (_inputs)
            {
                if (_scratch.Length < count) _scratch = new float[count];
                foreach (var input in _inputs)
                {
                    int read = input.Read(_scratch, 0, count);
                    for (int i = 0; i < read; i++) buffer[offset + i] += _scratch[i];
                }
            }
            return count;
        }
    }
}
