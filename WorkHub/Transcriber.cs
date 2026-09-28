using System.ComponentModel;
using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WorkHub;

public enum WhisperEngine { None, WhisperCpp, OpenAi, OpenVino }

/// <summary>Which side of the stereo recording (mic=Left, others=Right) to transcribe.</summary>
public enum WhisperChannel { Both, Others, Me }

/// <summary>What happened on one finished transcription.</summary>
public sealed record TranscriptionResult(
    string OutputPath, string? DeviceUsed, double AudioSeconds, double WallSeconds,
    double? LoadSeconds, double? InferSeconds);

/// <summary>
/// Transcribes a WAV to a .txt with a given <see cref="Executor"/> (device + engine + model):
///   • whisper.cpp – a portable standalone exe on the CPU (no Python / no ffmpeg); falls
///     back to openai-whisper on PATH when whisper.cpp isn't installed, as before;
///   • OpenVINO GenAI – a portable-Python sidecar (ov_whisper.py) running WhisperPipeline
///     on the executor's device (CPU / GPU / NPU).
/// Stateless, so several devices can use one instance at the same time. Whisper reads the
/// 16 kHz WAV directly and averages our L=mic / R=system channels to mono.
/// </summary>
public sealed class Transcriber
{
    public sealed class WhisperNotFoundException(string exe)
        : Exception($"Whisper не найден ('{exe}').");

    /// <summary>
    /// A failed run. <see cref="Incompatible"/>: this model can't run on this device (retrying
    /// is pointless); <see cref="Transient"/>: the device glitched (hung / lost), worth a retry.
    /// </summary>
    public sealed class TranscriptionFailedException(string message, bool incompatible, bool transient)
        : Exception(message)
    {
        public bool Incompatible { get; } = incompatible;
        public bool Transient { get; } = transient;
    }

    private const string OvResultPrefix = "WORKHUB_RESULT ";

    // ov_whisper.py exit codes.
    private const int OvExitIncompatible = 3;
    private const int OvExitTransient = 4;

    /// <summary>Is the whisper.cpp engine (or its openai-whisper fallback) usable?</summary>
    public static WhisperEngine ResolveEngine(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.WhisperCppExe) && File.Exists(s.WhisperCppExe) &&
            !string.IsNullOrWhiteSpace(s.WhisperCppModel) && File.Exists(s.WhisperCppModel))
            return WhisperEngine.WhisperCpp;

        if (IsCommandAvailable(s.WhisperExe, "--help", 15000))
            return WhisperEngine.OpenAi;

        return WhisperEngine.None;
    }

    /// <summary>Which engine will actually run <paramref name="e"/>.</summary>
    public static WhisperEngine ResolveEngine(AppSettings s, Executor e)
    {
        if (e.IsOpenVino)
            return !string.IsNullOrWhiteSpace(s.OpenVinoPython) && File.Exists(s.OpenVinoPython) &&
                   OpenVinoInstaller.MissingModelFiles(e.Model).Count == 0
                ? WhisperEngine.OpenVino
                : WhisperEngine.None;

        if (!string.IsNullOrWhiteSpace(s.WhisperCppExe) && File.Exists(s.WhisperCppExe) && File.Exists(e.Model))
            return WhisperEngine.WhisperCpp;

        if (IsCommandAvailable(s.WhisperExe, "--help", 15000))
            return WhisperEngine.OpenAi;

        return WhisperEngine.None;
    }

    /// <summary>ffmpeg is required only by the openai-whisper engine.</summary>
    public static bool IsFfmpegAvailable() => IsCommandAvailable("ffmpeg", "-version", 8000);

    private static bool IsCommandAvailable(string exe, string arg, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(arg);

            using var p = Process.Start(psi);
            if (p == null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return true;
        }
        catch (Win32Exception) { return false; } // not found on PATH
        catch { return false; }
    }

    /// <summary>
    /// Transcribes one WAV in a single language with <paramref name="exec"/>, writing exactly
    /// <paramref name="outputTxtPath"/>. <paramref name="cpuThreads"/> overrides whisper.cpp's
    /// thread count (used when other devices run at the same time).
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAsync(
        string wavPath, string language, string outputTxtPath, AppSettings settings, Executor exec,
        int? cpuThreads, Action<string>? log, CancellationToken ct)
    {
        var engine = ResolveEngine(settings, exec);
        if (engine == WhisperEngine.None)
            throw new WhisperNotFoundException(exec.IsOpenVino ? "OpenVINO / " + exec.ModelShort : settings.WhisperExe);

        string outDir = Path.GetDirectoryName(outputTxtPath)!;
        var sw = Stopwatch.StartNew();
        double audioS = AudioSeconds(wavPath);

        // Optionally transcribe only one side of the stereo recording (mic=Left, others=
        // Right): extract that channel to a temp mono WAV and feed that to Whisper.
        string inputWav = wavPath;
        bool tempInput = false;
        var channel = settings.TranscribeChannelParsed();
        if (channel != WhisperChannel.Both)
        {
            string? extracted = TryExtractChannel(wavPath, channel == WhisperChannel.Others ? 1 : 0);
            if (extracted != null) { inputWav = extracted; tempInput = true; }
        }

        try
        {
            var psi = engine switch
            {
                WhisperEngine.WhisperCpp => BuildWhisperCppStartInfo(inputWav, StripTxt(outputTxtPath), language,
                    exec.Model, cpuThreads ?? settings.ResolveWhisperThreads(), settings),
                WhisperEngine.OpenVino => BuildOpenVinoStartInfo(inputWav, outputTxtPath, language, exec, settings),
                _ => BuildOpenAiStartInfo(inputWav, outDir, language, settings),
            };

            // The OpenVINO sidecar reports the device and timings in a final machine-readable
            // line; keep its last "[ov]" line too, as the reason if it fails.
            System.Text.Json.JsonElement? ovResult = null;
            string? ovLastLine = null;
            void OnLine(string? line)
            {
                if (line == null) return;
                if (engine == WhisperEngine.OpenVino)
                {
                    if (line.StartsWith(OvResultPrefix, StringComparison.Ordinal))
                    {
                        ovResult = ParseJson(line[OvResultPrefix.Length..]);
                        return;
                    }
                    if (line.StartsWith("[ov] ", StringComparison.Ordinal)) ovLastLine = line[5..];
                }
                log?.Invoke(line);
            }

            Process p;
            try
            {
                p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (_, e) => OnLine(e.Data);
                p.ErrorDataReceived += (_, e) => OnLine(e.Data);
                if (!p.Start())
                    throw new WhisperNotFoundException(psi.FileName);
            }
            catch (Win32Exception)
            {
                throw new WhisperNotFoundException(psi.FileName);
            }

            using (p)
            {
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                try
                {
                    await p.WaitForExitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation must actually stop whisper — WaitForExitAsync alone doesn't.
                    try { p.Kill(entireProcessTree: true); } catch { }
                    try { p.WaitForExit(3000); } catch { }
                    // Drop the half-written .txt so it isn't mistaken for a finished transcript.
                    try { if (File.Exists(outputTxtPath)) File.Delete(outputTxtPath); } catch { }
                    try { if (File.Exists(outputTxtPath + ".part")) File.Delete(outputTxtPath + ".part"); } catch { }
                    throw;
                }
                if (p.ExitCode != 0)
                {
                    if (engine != WhisperEngine.OpenVino)
                        throw new Exception($"Whisper завершился с кодом {p.ExitCode}.");
                    throw new TranscriptionFailedException(
                        $"OpenVINO ({exec.Device}) завершился с кодом {p.ExitCode}: {ovLastLine ?? "см. лог"}",
                        incompatible: p.ExitCode == OvExitIncompatible,
                        transient: p.ExitCode == OvExitTransient);
                }
            }

            // openai-whisper names output after the input file; move it to the per-language path.
            if (engine == WhisperEngine.OpenAi)
            {
                string produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(inputWav) + ".txt");
                if (!string.Equals(produced, outputTxtPath, StringComparison.OrdinalIgnoreCase) && File.Exists(produced))
                {
                    if (File.Exists(outputTxtPath)) File.Delete(outputTxtPath);
                    File.Move(produced, outputTxtPath);
                }
            }

            double? Num(string name) =>
                ovResult is { } r && r.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : null;
            string? device = ovResult is { } res && res.TryGetProperty("device", out var dv) ? dv.GetString() : exec.Device;
            return new TranscriptionResult(outputTxtPath, device, audioS, sw.Elapsed.TotalSeconds,
                Num("load_s"), Num("infer_s"));
        }
        finally
        {
            if (tempInput) { try { File.Delete(inputWav); } catch { } }
        }
    }

    /// <summary>Duration of a WAV in seconds (0 if unreadable).</summary>
    public static double AudioSeconds(string wav)
    {
        try
        {
            using var r = new WaveFileReader(wav);
            return r.TotalTime.TotalSeconds;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Extracts one channel (0=Left/mic, 1=Right/others) of a stereo recording into a temp
    /// mono 16-bit WAV and returns its path; returns null (use the original) if the file is
    /// already mono or extraction fails.
    /// </summary>
    private static string? TryExtractChannel(string stereoWav, int channelIndex)
    {
        try
        {
            using var reader = new AudioFileReader(stereoWav);
            if (reader.WaveFormat.Channels < 2) return null; // mono — nothing to split

            var mux = new MultiplexingSampleProvider(new[] { (ISampleProvider)reader }, 1);
            mux.ConnectInputToOutput(channelIndex, 0);

            string tmp = Path.Combine(Path.GetTempPath(),
                "workhub_" + Guid.NewGuid().ToString("N") + ".wav");
            WaveFileWriter.CreateWaveFile16(tmp, mux);
            return tmp;
        }
        catch
        {
            return null; // fall back to transcribing the whole file
        }
    }

    private static string StripTxt(string path) =>
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;

    private static ProcessStartInfo BuildWhisperCppStartInfo(
        string wav, string outBase, string lang, string model, int threads, AppSettings s)
    {
        var psi = NewHidden(s.WhisperCppExe);
        psi.WorkingDirectory = Path.GetDirectoryName(s.WhisperCppExe) ?? "";
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wav);
        psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(lang);

        // Use most of the CPU (whisper's default of 4 threads leaves a many-core chip idle).
        psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(threads.ToString());

        // Anti-repetition / anti-hallucination, always on:
        //   -mc 0  : don't feed prior text back as context → kills the "×200" decode loops
        //            ("Also yesterday Shlomi…" / "Hello. Hello." repeating forever).
        //   -sns   : suppress non-speech tokens → no "*Music*" / "*Dogs barking*" tags.
        psi.ArgumentList.Add("-mc"); psi.ArgumentList.Add("0");
        psi.ArgumentList.Add("-sns");

        // Voice Activity Detection: only feed Whisper the speech regions, so it can't
        // invent text on silence (the classic "Субтитры создавал…" / "Корректор…" loops).
        string? vadModel = ResolveVadModel(s, model);
        if (s.WhisperUseVad && vadModel != null)
        {
            psi.ArgumentList.Add("--vad");
            psi.ArgumentList.Add("--vad-model"); psi.ArgumentList.Add(vadModel);
            psi.ArgumentList.Add("-vt"); psi.ArgumentList.Add(
                s.WhisperVadThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        psi.ArgumentList.Add("-otxt");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outBase);
        return psi;
    }

    /// <summary>
    /// The VAD model path from settings, or — if that's blank/missing (e.g. settings got
    /// reset) — a ggml-silero*.bin sitting next to the whisper.cpp model or exe. Self-heals
    /// so VAD keeps working even when the stored path is lost.
    /// </summary>
    private static string? ResolveVadModel(AppSettings s, string model)
    {
        if (!string.IsNullOrWhiteSpace(s.WhisperVadModel) && File.Exists(s.WhisperVadModel))
            return s.WhisperVadModel;

        foreach (var baseFile in new[] { model, s.WhisperCppModel, s.WhisperCppExe })
        {
            var dir = Path.GetDirectoryName(baseFile);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir, "ggml-silero*.bin").FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    private static ProcessStartInfo BuildOpenAiStartInfo(string wav, string outDir, string lang, AppSettings s)
    {
        var psi = NewHidden(s.WhisperExe);
        psi.WorkingDirectory = outDir;
        psi.ArgumentList.Add(wav);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(s.WhisperModel);
        psi.ArgumentList.Add("--output_format"); psi.ArgumentList.Add("txt");
        psi.ArgumentList.Add("--output_dir"); psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("--task"); psi.ArgumentList.Add("transcribe");
        psi.ArgumentList.Add("--language"); psi.ArgumentList.Add(lang);
        return psi;
    }

    private static ProcessStartInfo BuildOpenVinoStartInfo(string wav, string outTxt, string lang, Executor e, AppSettings s)
    {
        var psi = NewHidden(s.OpenVinoPython);
        psi.WorkingDirectory = OpenVinoInstaller.ToolsDir;
        // The sidecar prints Russian text; don't let Python fall back to the ANSI codepage.
        psi.StandardOutputEncoding = System.Text.Encoding.UTF8;
        psi.StandardErrorEncoding = System.Text.Encoding.UTF8;
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONNOUSERSITE"] = "1";

        psi.ArgumentList.Add(OpenVinoInstaller.EnsureScript());
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(e.Model);
        psi.ArgumentList.Add("--wav"); psi.ArgumentList.Add(wav);
        psi.ArgumentList.Add("--lang"); psi.ArgumentList.Add(lang);
        psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(outTxt);
        psi.ArgumentList.Add("--device"); psi.ArgumentList.Add(e.Device);
        // Compiled-model cache (per model; the sidecar adds a per-device level): turns a
        // minutes-long NPU/GPU compile into seconds after the first run.
        psi.ArgumentList.Add("--cache-dir");
        psi.ArgumentList.Add(Path.Combine(OpenVinoInstaller.CacheDir, Path.GetFileName(e.Model.TrimEnd('\\', '/'))));
        return psi;
    }

    private static System.Text.Json.JsonElement? ParseJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    private static ProcessStartInfo NewHidden(string exe) => new()
    {
        FileName = exe,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
}
