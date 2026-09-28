using System.Diagnostics;

namespace WorkHub;

/// <summary>
/// Records the screen to an MP4 alongside the call, using ffmpeg's gdigrab input and a
/// hardware H.264 encoder (video only). On stop, the call's stereo WAV is muxed in as the
/// audio track. Nothing here touches the meeting apps or bypasses capture protection —
/// it's a plain OS-level screen grab, which Zoom/Slack neither detect nor are notified of.
/// </summary>
public sealed class ScreenRecorder
{
    private Process? _proc;
    private string? _tempVideo;
    private string? _finalMp4;
    private string? _wavPath;
    private AppSettings _settings = new();

    public bool IsRecording => _proc is { HasExited: false };

    /// <summary>Starts capturing to a temp video file for the given final MP4 path.</summary>
    public bool Start(string finalMp4Path, string wavPath, AppSettings settings, Action<string>? log = null)
    {
        if (IsRecording) return false;

        _settings = settings;
        _finalMp4 = finalMp4Path;
        _wavPath = wavPath;
        _tempVideo = finalMp4Path + ".video.tmp.mp4";

        var (x, y, w, h) = CaptureRegion(settings);
        // gdigrab needs even dimensions for yuv420p.
        w -= w % 2; h -= h % 2;

        var psi = new ProcessStartInfo
        {
            FileName = settings.FfmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,   // send 'q' to stop cleanly
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "gdigrab",
            "-framerate", Math.Clamp(settings.ScreenFps, 5, 60).ToString(),
            "-offset_x", x.ToString(), "-offset_y", y.ToString(),
            "-video_size", $"{w}x{h}",
            "-i", "desktop",
            "-c:v", string.IsNullOrWhiteSpace(settings.ScreenEncoder) ? "h264_mf" : settings.ScreenEncoder,
            "-b:v", string.IsNullOrWhiteSpace(settings.ScreenVideoBitrate) ? "4M" : settings.ScreenVideoBitrate,
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            _tempVideo,
        }) psi.ArgumentList.Add(a);

        try
        {
            _proc = Process.Start(psi);
            if (_proc == null) return false;
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
            _proc.BeginErrorReadLine();
            _proc.BeginOutputReadLine();
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke("ffmpeg не запустился: " + ex.Message);
            _proc = null;
            return false;
        }
    }

    /// <summary>
    /// Stops capture (finalizing the video), then muxes in the call WAV. Returns the final
    /// MP4 path, or null if nothing usable was produced. Call after the WAV is closed.
    /// </summary>
    public async Task<string?> StopAsync(Action<string>? log = null)
    {
        var proc = _proc;
        _proc = null;
        if (proc == null) return null;

        try
        {
            if (!proc.HasExited)
            {
                try { await proc.StandardInput.WriteLineAsync("q"); await proc.StandardInput.FlushAsync(); } catch { }
                if (!proc.WaitForExit(10000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    proc.WaitForExit(3000);
                }
            }
        }
        catch { /* ignore */ }
        finally { proc.Dispose(); }

        string? tempVideo = _tempVideo, finalMp4 = _finalMp4, wav = _wavPath;
        if (tempVideo == null || finalMp4 == null || !File.Exists(tempVideo) || new FileInfo(tempVideo).Length == 0)
        {
            log?.Invoke("Видео экрана не записалось.");
            TryDelete(tempVideo);
            return null;
        }

        if (_settings.ScreenIncludeAudio && wav != null && File.Exists(wav))
        {
            bool muxed = await MuxAsync(_settings.FfmpegExe, tempVideo, wav, finalMp4, log);
            if (muxed) { TryDelete(tempVideo); return finalMp4; }
            // Mux failed — keep the silent video as the result instead of losing it.
        }

        try
        {
            if (File.Exists(finalMp4)) File.Delete(finalMp4);
            File.Move(tempVideo, finalMp4);
            return finalMp4;
        }
        catch (Exception ex)
        {
            log?.Invoke("Не удалось сохранить видео: " + ex.Message);
            return File.Exists(tempVideo) ? tempVideo : null;
        }
    }

    private static async Task<bool> MuxAsync(
        string ffmpeg, string video, string wav, string outMp4, Action<string>? log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", video, "-i", wav,
            "-c:v", "copy", "-c:a", "aac", "-b:a", "128k",
            "-shortest", outMp4,
        }) psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi)!;
            string err = await p.StandardError.ReadToEndAsync();
            _ = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0 && File.Exists(outMp4)) return true;
            log?.Invoke("Вклейка звука не удалась: " + err);
            return false;
        }
        catch (Exception ex)
        {
            log?.Invoke("Вклейка звука не удалась: " + ex.Message);
            return false;
        }
    }

    private static (int x, int y, int w, int h) CaptureRegion(AppSettings s)
    {
        if (s.ScreenAllMonitors)
        {
            var v = SystemInformation.VirtualScreen;
            return (v.X, v.Y, v.Width, v.Height);
        }
        var b = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        return (b.X, b.Y, b.Width, b.Height);
    }

    private static void TryDelete(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
