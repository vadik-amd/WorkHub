using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace WorkHub;

/// <summary>
/// Optional second layer: a local vision-language model describing each key frame ("which app
/// is shown, what is happening"), on top of — never instead of — the OCR timeline.
///
/// It runs in a Python sidecar (ov_screen_vlm.py) on OpenVINO GenAI's VLMPipeline, the same
/// shape as the Whisper sidecar, with the device chain NPU → GPU → CPU. The model is loaded
/// once and then fed frame after frame over stdin/stdout, because loading it costs far more
/// than describing a frame.
///
/// No weights are ever downloaded: huggingface.co is blocked on this machine, so an IR model
/// has to be exported elsewhere and dropped into a folder by hand (see README). Nothing in
/// place → <see cref="TryCreateAsync"/> returns null, the log says why, and the pipeline
/// carries on as plain OCR.
/// </summary>
public sealed class ScreenVlmEngine : IScreenTextEngine, IAsyncDisposable
{
    private const string ScriptName = "ov_screen_vlm.py";
    private const string ReadyPrefix = "WORKHUB_READY ";
    private const string FramePrefix = "WORKHUB_FRAME ";

    /// <summary>Compiling a VLM for the NPU/GPU the first time really can take minutes.</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Frames are scaled down to this width before the model sees them.</summary>
    private const int MaxFrameWidth = 1344;

    private readonly Process _proc;
    private readonly Action<string> _log;
    private readonly string _prompt;
    private readonly string _tempDir;
    private readonly string _device;

    public string Name => $"OpenVINO VLM {Path.GetFileName(_modelDir)} ({_device})";
    private readonly string _modelDir;

    private ScreenVlmEngine(Process proc, string device, string modelDir, string prompt,
        string tempDir, Action<string> log)
    {
        _proc = proc;
        _device = device;
        _modelDir = modelDir;
        _prompt = prompt;
        _tempDir = tempDir;
        _log = log;
    }

    /// <summary>Why the VLM layer can't run, or null when everything is in place.</summary>
    public static string? Unavailable(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.ScreenVlmModelDir))
            return "модель не задана (Текст с экрана → Локальная VLM-модель…)";
        if (!Directory.Exists(s.ScreenVlmModelDir))
            return "папки модели нет: " + s.ScreenVlmModelDir;
        if (!Directory.EnumerateFiles(s.ScreenVlmModelDir, "*.xml").Any())
            return "в папке модели нет файлов OpenVINO IR (*.xml): " + s.ScreenVlmModelDir;
        if (string.IsNullOrWhiteSpace(s.OpenVinoPython) || !File.Exists(s.OpenVinoPython))
            return "не установлен портативный Python OpenVINO (Расшифровка → Установить / настроить OpenVINO…)";
        return null;
    }

    /// <summary>
    /// Starts the sidecar and waits until the model is loaded. Returns null — with the reason
    /// in the log — whenever the layer can't run; the caller then stays on OCR only.
    /// </summary>
    public static async Task<ScreenVlmEngine?> TryCreateAsync(AppSettings settings, Action<string> log,
        CancellationToken ct)
    {
        string? why = Unavailable(settings);
        if (why != null) { log(why); return null; }

        string script;
        try { script = OpenVinoInstaller.EnsureScript(ScriptName); }
        catch (Exception ex) { log("не удалось подготовить сайдкар: " + ex.Message); return null; }

        string tempDir = Path.Combine(Path.GetTempPath(), "WorkHub", "vlm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        var psi = new ProcessStartInfo
        {
            FileName = settings.OpenVinoPython,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            WorkingDirectory = OpenVinoInstaller.ToolsDir,
        };
        // The sidecar prints Russian text; don't let Python fall back to the ANSI codepage.
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONNOUSERSITE"] = "1";
        foreach (var a in new[]
        {
            script,
            "--model", settings.ScreenVlmModelDir,
            "--device", string.IsNullOrWhiteSpace(settings.ScreenVlmDevice) ? "AUTO" : settings.ScreenVlmDevice,
            "--max-tokens", Math.Clamp(settings.ScreenVlmMaxTokens, 16, 1024).ToString(),
            "--cache-dir", OpenVinoInstaller.CacheDir,
        }) psi.ArgumentList.Add(a);

        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new Exception("python не запустился");
            // stderr is drained in the background so a noisy traceback can't block the pipe.
            _ = Task.Run(async () =>
            {
                try
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync()) != null)
                        if (line.Trim().Length > 0) log("stderr: " + line.Trim());
                }
                catch { /* the process is gone */ }
            }, CancellationToken.None);

            string? device = null;
            using (var load = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                load.CancelAfter(LoadTimeout);
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(load.Token)) != null)
                {
                    if (line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
                    {
                        device = ReadString(line[ReadyPrefix.Length..], "device") ?? "?";
                        break;
                    }
                    if (line.StartsWith("[vlm] ", StringComparison.Ordinal)) log(line[6..]);
                }
            }

            if (device == null)
            {
                log("сайдкар не смог загрузить модель — остаюсь на OCR.");
                KillQuietly(proc);
                TryDeleteDir(tempDir);
                return null;
            }

            log($"модель загружена, устройство: {device}");
            return new ScreenVlmEngine(proc, device, settings.ScreenVlmModelDir,
                settings.ScreenVlmPrompt, tempDir, log);
        }
        catch (Exception ex)
        {
            log((ex is OperationCanceledException ? "загрузка модели прервана" : "ошибка запуска: " + ex.Message));
            if (proc != null) KillQuietly(proc);
            TryDeleteDir(tempDir);
            return null;
        }
    }

    public async Task<FrameText> RecognizeAsync(string framePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_proc.HasExited) return FrameText.Empty;

        string raw = Path.Combine(_tempDir, "frame.rgb");
        var (width, height) = WriteRawRgb(framePath, raw);

        string request = JsonSerializer.Serialize(new
        {
            raw, width, height, prompt = _prompt,
        });
        await _proc.StandardInput.WriteLineAsync(request.AsMemory(), ct);
        await _proc.StandardInput.FlushAsync(ct);

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(FrameTimeout);
        string? line;
        while ((line = await _proc.StandardOutput.ReadLineAsync(wait.Token)) != null)
        {
            if (line.StartsWith("[vlm] ", StringComparison.Ordinal)) { _log(line[6..]); continue; }
            if (!line.StartsWith(FramePrefix, StringComparison.Ordinal)) continue;

            string payload = line[FramePrefix.Length..];
            string? error = ReadString(payload, "error");
            if (error != null) { _log("кадр не описан: " + error); return FrameText.Empty; }
            string text = ReadString(payload, "text") ?? "";
            return text.Trim().Length == 0 ? FrameText.Empty : new FrameText(text.Trim(), 1, _device);
        }
        return FrameText.Empty;
    }

    /// <summary>
    /// JPEG → raw RGB24, downscaled. The sidecar's venv has numpy and openvino but no image
    /// library, and raw pixels need neither side to agree on a codec.
    /// </summary>
    private static (int Width, int Height) WriteRawRgb(string framePath, string rawPath)
    {
        using var source = new Bitmap(framePath);
        int width = source.Width, height = source.Height;
        if (width > MaxFrameWidth)
        {
            height = Math.Max(1, (int)Math.Round(height * (double)MaxFrameWidth / width));
            width = MaxFrameWidth;
        }

        using var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(source, 0, 0, width, height);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly,
            PixelFormat.Format24bppRgb);
        try
        {
            using var file = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var row = new byte[data.Stride];
            var rgb = new byte[width * 3];
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (int x = 0; x < width; x++)
                {
                    rgb[x * 3] = row[x * 3 + 2];      // GDI+ stores BGR
                    rgb[x * 3 + 1] = row[x * 3 + 1];
                    rgb[x * 3 + 2] = row[x * 3];
                }
                file.Write(rgb, 0, rgb.Length);
            }
        }
        finally { bmp.UnlockBits(data); }

        return (width, height);
    }

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_proc.HasExited)
            {
                await _proc.StandardInput.WriteLineAsync("{\"cmd\":\"quit\"}");
                await _proc.StandardInput.FlushAsync();
                if (!_proc.WaitForExit(5000)) KillQuietly(_proc);
            }
        }
        catch { KillQuietly(_proc); }
        finally
        {
            _proc.Dispose();
            TryDeleteDir(_tempDir);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(3000); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
