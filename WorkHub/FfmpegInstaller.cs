using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace WorkHub;

/// <summary>
/// Downloads a portable ffmpeg.exe (used for screen recording) into
/// %APPDATA%\WorkHub\tools\ffmpeg. No install, no admin. We take a static LGPL build
/// from BtbN's GitHub releases and keep just ffmpeg.exe.
/// </summary>
public static class FfmpegInstaller
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WorkHub", "1.0"));
        return c;
    }

    public static string ToolsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkHub", "tools", "ffmpeg");

    public static string ExePath => Path.Combine(ToolsDir, "ffmpeg.exe");

    private const string ZipUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

    /// <summary>Returns a usable ffmpeg.exe path, downloading it once if needed.</summary>
    public static async Task<string> EnsureAsync(AppSettings settings, Action<string> log, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.FfmpegExe) && File.Exists(settings.FfmpegExe))
            return settings.FfmpegExe;

        if (File.Exists(ExePath))
        {
            settings.FfmpegExe = ExePath;
            settings.Save();
            return ExePath;
        }

        Directory.CreateDirectory(ToolsDir);
        string zip = Path.Combine(ToolsDir, "ffmpeg.zip");
        log("Загрузка ffmpeg (~170 МБ, один раз)…");
        await DownloadAsync(ZipUrl, zip, log, ct);

        log("Распаковка ffmpeg…");
        ExtractFfmpegExe(zip, ExePath);
        try { File.Delete(zip); } catch { }

        if (!File.Exists(ExePath))
            throw new Exception("В архиве ffmpeg не найден ffmpeg.exe.");

        settings.FfmpegExe = ExePath;
        settings.Save();
        log("ffmpeg готов.");
        return ExePath;
    }

    private static void ExtractFfmpegExe(string zipPath, string destExe)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
        if (entry == null) throw new Exception("ffmpeg.exe отсутствует в архиве.");
        entry.ExtractToFile(destExe, overwrite: true);
    }

    private static async Task DownloadAsync(string url, string destPath, Action<string> log, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        string tmp = destPath + ".part";

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[1 << 16];
            long read = 0, lastReported = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                read += n;
                if (read - lastReported >= 10_000_000)
                {
                    lastReported = read;
                    log(total.HasValue
                        ? $"  {read / 1_048_576.0:0} / {total.Value / 1_048_576.0:0} МБ"
                        : $"  {read / 1_048_576.0:0} МБ");
                }
            }
        }

        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmp, destPath);
    }
}
