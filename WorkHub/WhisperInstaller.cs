using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WorkHub;

/// <summary>
/// Downloads a portable whisper.cpp build + a ggml model into %APPDATA%\WorkHub\tools and
/// records their paths in settings. No installation, no admin, no Python, no ffmpeg.
/// </summary>
public static class WhisperInstaller
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WorkHub", "1.0"));
        return c;
    }

    public static string ToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkHub", "tools", "whispercpp");

    public static string ReleasesPageUrl(AppSettings s) => $"https://github.com/{s.WhisperCppRepo}/releases";

    /// <summary>Approximate download size of a ggml model, for the UI.</summary>
    public static string ModelSizeHint(string model) => model switch
    {
        "tiny" => "~75 МБ",
        "base" => "~148 МБ",
        "small" => "~488 МБ",
        "medium" => "~1.5 ГБ",
        "large-v3" => "~3.1 ГБ",
        _ => "",
    };

    /// <summary>Full setup: portable binary (GitHub) + ggml model + VAD model (Hugging Face).</summary>
    public static async Task InstallAsync(
        AppSettings settings, string modelName, Action<string> log, CancellationToken ct)
    {
        await EnsureBinaryAsync(settings, log, ct);
        await EnsureModelAsync(settings, modelName, log, ct);
        await EnsureVadModelAsync(settings, log, ct);
        log("Готово. Портативный Whisper настроен.");
    }

    // Silero VAD model used to skip non-speech regions (kills silence hallucinations).
    private const string VadModelFileName = "ggml-silero-v5.1.2.bin";
    private const string VadModelUrl =
        "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin?download=true";

    /// <summary>Downloads the ~0.9 MB silero VAD model if missing; never fatal to setup.</summary>
    public static async Task EnsureVadModelAsync(AppSettings settings, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        string vadFile = Path.Combine(ToolsDir, VadModelFileName);
        if (!File.Exists(vadFile))
        {
            try
            {
                await DownloadFileAsync(VadModelUrl, vadFile, "VAD-модель (~0.9 МБ)", log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // VAD is an enhancement, not a requirement — transcription still works without it.
                log($"VAD-модель скачать не удалось ({ex.Message}). Расшифровка будет работать и без неё.");
                return;
            }
        }
        else
        {
            log("VAD-модель уже загружена.");
        }

        settings.WhisperVadModel = vadFile;
        settings.Save();
    }

    /// <summary>Downloads/extracts the whisper.cpp x64 binary (from GitHub) if not present.</summary>
    public static async Task<string> EnsureBinaryAsync(AppSettings settings, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        string binDir = Path.Combine(ToolsDir, "bin");
        string? exe = FindExe(binDir);
        if (exe == null)
        {
            log("Поиск сборки whisper.cpp на GitHub…");
            var (assetUrl, assetName) = await FindBinaryAssetAsync(settings.WhisperCppRepo, ct);
            log($"Найдено: {assetName}");

            string zipPath = Path.Combine(ToolsDir, assetName);
            await DownloadFileAsync(assetUrl, zipPath, "бинарь whisper.cpp", log, ct);

            log("Распаковка…");
            if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true);
            ZipFile.ExtractToDirectory(zipPath, binDir, overwriteFiles: true);
            try { File.Delete(zipPath); } catch { }

            exe = FindExe(binDir)
                  ?? throw new Exception("В архиве не найден whisper-cli.exe / main.exe.");
        }
        else
        {
            log("Бинарь whisper.cpp уже загружен.");
        }

        settings.WhisperCppExe = exe;
        settings.Save();
        return exe;
    }

    /// <summary>Downloads the ggml model (from Hugging Face) if not present.</summary>
    public static async Task EnsureModelAsync(
        AppSettings settings, string modelName, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        string modelFile = Path.Combine(ToolsDir, $"ggml-{modelName}.bin");
        if (!File.Exists(modelFile))
        {
            string modelUrl =
                $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-{modelName}.bin?download=true";
            try
            {
                await DownloadFileAsync(modelUrl, modelFile,
                    $"модель {modelName} ({ModelSizeHint(modelName)})", log, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new Exception(
                    $"Не удалось скачать модель с huggingface.co ({ex.Message}). " +
                    "Возможно, сайт заблокирован в вашей сети. Скачайте файл " +
                    $"ggml-{modelName}.bin вручную и укажите его кнопкой «Указать модель (.bin)…». " +
                    $"Папка хаба: {ToolsDir}");
            }
        }
        else
        {
            log($"Модель {modelName} уже загружена.");
        }

        settings.WhisperCppModel = modelFile;
        settings.Save();
    }

    public static string ModelsPageUrl => "https://huggingface.co/ggerganov/whisper.cpp/tree/main";

    private static string? FindExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        foreach (var name in new[] { "whisper-cli.exe", "main.exe" })
        {
            var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    private static async Task<(string url, string name)> FindBinaryAssetAsync(string repo, CancellationToken ct)
    {
        // Prebuilt Windows binaries live in the nightly ("bNNNN") releases, not the
        // versioned ones — so scan the releases list (newest first) and take the first
        // that carries a matching x64 CPU zip.
        var apiUrl = $"https://api.github.com/repos/{repo}/releases?per_page=40";
        using var resp = await Http.GetAsync(apiUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;
            var best = PickBestAsset(assets);
            if (best != null) return best.Value;
        }

        throw new Exception("Не найден Windows x64 архив whisper.cpp в последних релизах.");
    }

    private static (string url, string name)? PickBestAsset(JsonElement assets)
    {
        string? bestUrl = null, bestName = null;
        int bestScore = int.MinValue;

        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            var lower = name.ToLowerInvariant();

            if (!lower.EndsWith(".zip") || !lower.Contains("x64")) continue;
            // Skip GPU/other builds that need extra runtimes or a different CPU arch.
            if (lower.Contains("cublas") || lower.Contains("cuda") || lower.Contains("hip") ||
                lower.Contains("vulkan") || lower.Contains("arm64") || lower.Contains("win32"))
                continue;

            int score = 0;
            if (lower.Contains("bin-x64")) score += 10;
            if (!lower.Contains("blas")) score += 2; // prefer plain CPU build (fewer deps)
            if (score > bestScore) { bestScore = score; bestUrl = url; bestName = name; }
        }

        return bestUrl != null && bestName != null ? (bestUrl, bestName) : null;
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, string label, Action<string> log, CancellationToken ct)
    {
        log($"Загрузка: {label}…");
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        string tmp = destPath + ".part";

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1 << 16];
            long read = 0;
            long lastReported = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (read - lastReported >= 5_000_000) // report every ~5 MB
                {
                    lastReported = read;
                    log(total.HasValue
                        ? $"  {Mb(read)} / {Mb(total.Value)} ({read * 100 / total.Value}%)"
                        : $"  {Mb(read)}");
                }
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);
        log($"  загружено: {label}");
    }

    private static string Mb(long bytes) => $"{bytes / 1_048_576.0:0.0} МБ";
}
