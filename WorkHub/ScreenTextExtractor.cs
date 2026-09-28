using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkHub;

/// <summary>Where the screen-text artefacts of a recording live.</summary>
public static class ScreenTextPaths
{
    private static string Base(string mp4) =>
        Path.Combine(Path.GetDirectoryName(mp4)!, Path.GetFileNameWithoutExtension(mp4));

    public static string Txt(string mp4) => Base(mp4) + ".screen.txt";
    public static string Json(string mp4) => Base(mp4) + ".screen.json";
    public static string FramesDir(string mp4) => Base(mp4) + ".frames";

    /// <summary>Has this video already been processed?</summary>
    public static bool Exists(string mp4) => File.Exists(Txt(mp4));
}

/// <summary>One entry of the timeline: a stretch of the call that showed the same thing.</summary>
public sealed record ScreenTextBlock(
    double Seconds,
    double EndSeconds,
    string Text,
    string? Language,
    string? FrameFile,
    string? Description);

public sealed record ScreenTextResult(
    string Mp4, string TxtPath, string JsonPath, string? FramesDir,
    int KeyFrames, int Blocks, int Chars, TimeSpan Elapsed);

/// <summary>
/// Extracts "what was on screen" from a recorded call video as a text timeline.
///
/// The shared screen belongs to somebody else's machine: it reaches us only as video pixels
/// inside the meeting window, so there is no accessibility tree to read, no process name that
/// means anything (it would say "Zoom"), and the pointer in the picture is the presenter's,
/// not ours. Everything here is therefore vision-only — ffmpeg pulls key frames out of the
/// MP4 that <see cref="ScreenRecorder"/> already wrote, an <see cref="IScreenTextEngine"/>
/// reads them, near-duplicates are folded together, and the result is saved next to the video
/// as .screen.txt (for a human) and .screen.json (to hand to a model).
///
/// Frames are chosen by change, not by clock: a shared screen is nearly static, so the video
/// is first sampled down to ~1 fps and a frame is kept only when it differs enough from the
/// previous one. A 40-minute call typically yields tens of frames, not tens of thousands.
/// </summary>
public sealed class ScreenTextExtractor
{
    public event Action<string>? Log;
    public event Action<int, int>? Progress;   // done, total

    private static readonly Regex MetaFrame = new(
        @"^frame:(\d+)\s+pts:\S+\s+pts_time:(-?[\d.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Runs the whole pipeline for one video. Returns null when there was nothing to save.
    /// Temporary frames are always removed, cancellation included.
    /// </summary>
    public async Task<ScreenTextResult?> RunAsync(string mp4, AppSettings settings, CancellationToken ct)
    {
        if (!File.Exists(mp4)) throw new FileNotFoundException("Видео не найдено: " + mp4, mp4);
        if (string.IsNullOrWhiteSpace(settings.FfmpegExe) || !File.Exists(settings.FfmpegExe))
            throw new Exception("ffmpeg не установлен — распознавание экрана невозможно.");

        var mode = settings.ScreenModeParsed();
        if (mode == ScreenMode.Off) mode = ScreenMode.Ocr;   // asked for explicitly from the tray

        var sw = Stopwatch.StartNew();
        string tempDir = Path.Combine(Path.GetTempPath(), "WorkHub",
            "screen_" + Path.GetFileNameWithoutExtension(mp4) + "_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            Log?.Invoke($"Экран: {Path.GetFileName(mp4)} — ищу ключевые кадры " +
                        $"(порог смены {settings.ScreenSceneThreshold:0.###}, выборка {settings.ScreenSampleFps:0.##} к/с)…");

            var frames = await ExtractKeyFramesAsync(mp4, tempDir, settings, ct);
            if (frames.Count == 0)
            {
                Log?.Invoke("Экран: ключевых кадров не найдено (видео пустое или порог слишком высокий).");
                return null;
            }
            Log?.Invoke($"Экран: ключевых кадров {frames.Count} — распознаю…");

            using var ocr = new WindowsOcrEngine(settings.ScreenOcrLanguages, m => Log?.Invoke("Экран: " + m));
            Log?.Invoke("Экран: движок — " + ocr.Name);

            // Optional second layer; silently absent when no model is in place.
            await using var vlm = mode == ScreenMode.Vlm
                ? await ScreenVlmEngine.TryCreateAsync(settings, m => Log?.Invoke("Экран/VLM: " + m), ct)
                : null;
            if (mode == ScreenMode.Vlm && vlm == null)
                Log?.Invoke("Экран: VLM-слой не активен — работаю только по OCR.");

            string? framesDir = null;
            if (settings.ScreenKeepKeyFrames())
            {
                framesDir = ScreenTextPaths.FramesDir(mp4);
                Directory.CreateDirectory(framesDir);
                foreach (var stale in Directory.EnumerateFiles(framesDir, "*.jpg")) TryDelete(stale);
            }

            var blocks = new List<ScreenTextBlock>();
            int recognized = 0, empty = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (seconds, path) = frames[i];
                double endSeconds = i + 1 < frames.Count ? frames[i + 1].Seconds : seconds;

                FrameText text;
                try
                {
                    text = await ocr.RecognizeAsync(path, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log?.Invoke($"Экран: кадр {Stamp(seconds)} не распознан: {ex.Message}");
                    text = FrameText.Empty;
                }

                if (text.Lines == 0) empty++;
                else recognized++;

                // Fold a frame into the previous block when it says the same thing: a shared
                // screen sits still, and a mouse move alone must not produce a new entry.
                var previous = blocks.Count > 0 ? blocks[^1] : null;
                bool duplicate = previous != null && text.Lines > 0 &&
                                 Similarity(previous.Text, text.Text) >= 0.90;
                if (duplicate)
                {
                    blocks[^1] = previous! with { EndSeconds = endSeconds };
                    Progress?.Invoke(i + 1, frames.Count);
                    continue;
                }
                if (text.Lines == 0 && previous != null)
                {
                    // Nothing readable (a camera view, a dark screen): extend, don't add noise.
                    blocks[^1] = previous with { EndSeconds = endSeconds };
                    Progress?.Invoke(i + 1, frames.Count);
                    continue;
                }
                if (text.Lines == 0)
                {
                    Progress?.Invoke(i + 1, frames.Count);
                    continue;
                }

                string? frameFile = null;
                if (framesDir != null)
                {
                    frameFile = $"{blocks.Count + 1:0000}_{Stamp(seconds).Replace(':', '-')}.jpg";
                    await SaveKeyFrameAsync(settings, path, Path.Combine(framesDir, frameFile), ct);
                }

                string? description = null;
                if (vlm != null)
                {
                    try { description = (await vlm.RecognizeAsync(path, ct)).Text; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Log?.Invoke("Экран/VLM: кадр пропущен: " + ex.Message); }
                    if (string.IsNullOrWhiteSpace(description)) description = null;
                }

                blocks.Add(new ScreenTextBlock(seconds, endSeconds, text.Text, text.Detail, frameFile, description));
                Progress?.Invoke(i + 1, frames.Count);
            }

            if (blocks.Count == 0)
            {
                Log?.Invoke($"Экран: текст не распознан ни на одном из {frames.Count} кадров.");
                return null;
            }

            string engineName = ocr.Name + (vlm != null ? " + " + vlm.Name : "");
            string txt = ScreenTextPaths.Txt(mp4), json = ScreenTextPaths.Json(mp4);
            await WriteTimelineAsync(txt, mp4, engineName, mode, frames.Count, blocks);
            await WriteJsonAsync(json, mp4, engineName, mode, frames.Count, blocks, framesDir);

            int chars = blocks.Sum(b => b.Text.Length);
            Log?.Invoke($"Экран: готово за {sw.Elapsed.TotalSeconds:0} с — кадров {frames.Count} " +
                        $"(с текстом {recognized}, пустых {empty}), блоков {blocks.Count}, символов {chars}. " +
                        $"Файл: {Path.GetFileName(txt)}");
            return new ScreenTextResult(mp4, txt, json, framesDir, frames.Count, blocks.Count, chars, sw.Elapsed);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    // ------------------------------------------------------------------ key frames

    /// <summary>
    /// Pulls the key frames into <paramref name="tempDir"/>, returning (seconds, path) pairs.
    ///
    /// The filter chain samples to ScreenSampleFps first (cheap, and it makes the scene score
    /// mean "changed within a second" rather than "changed within a frame"), then keeps the
    /// first frame plus every frame whose difference from the previous one exceeds the
    /// threshold. metadata=print puts "frame:N … pts_time:X" on stdout, where N is the index
    /// of the *selected* frame — so it lines up with the f_%05d files exactly.
    /// </summary>
    private async Task<List<(double Seconds, string Path)>> ExtractKeyFramesAsync(
        string mp4, string tempDir, AppSettings settings, CancellationToken ct)
    {
        double fps = Math.Clamp(settings.ScreenSampleFps, 0.05, 5.0);
        double threshold = Math.Clamp(settings.ScreenSceneThreshold, 0.001, 1.0);
        double upscale = Math.Clamp(settings.ScreenOcrUpscale, 1.0, 4.0);

        string chain = $"fps={fps.ToString("0.###", CultureInfo.InvariantCulture)}," +
                       $"select='eq(n\\,0)+gt(scene\\,{threshold.ToString("0.####", CultureInfo.InvariantCulture)})'," +
                       "metadata=print:file=-";
        if (upscale > 1.0)
            chain += $",scale=iw*{upscale.ToString("0.##", CultureInfo.InvariantCulture)}:-1:flags=lanczos";

        var psi = new ProcessStartInfo
        {
            FileName = settings.FfmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", mp4,
            "-an",
            "-vf", chain,
            "-fps_mode", "vfr",           // -vsync is gone from current ffmpeg builds
            "-q:v", "2",                  // near-lossless JPEG: plenty for OCR, small on disk
            Path.Combine(tempDir, "f_%05d.jpg"),
        }) psi.ArgumentList.Add(a);

        var times = new List<double>();
        var errors = new StringBuilder();

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            var m = MetaFrame.Match(e.Data);
            if (m.Success &&
                double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double t))
                lock (times) times.Add(Math.Max(0, t));
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null && errors.Length < 2000) errors.AppendLine(e.Data);
        };

        if (!p.Start()) throw new Exception("ffmpeg не запустился.");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            try { p.WaitForExit(3000); } catch { }
            throw;
        }
        if (p.ExitCode != 0)
            throw new Exception($"ffmpeg завершился с кодом {p.ExitCode}: {errors.ToString().Trim()}");

        // Pair each timestamp with its frame file (1-based, in the order they were selected).
        var all = new List<(double Seconds, string Path)>();
        lock (times)
        {
            for (int i = 0; i < times.Count; i++)
            {
                string path = Path.Combine(tempDir, $"f_{i + 1:00000}.jpg");
                if (File.Exists(path)) all.Add((times[i], path));
            }
        }
        int written = Directory.EnumerateFiles(tempDir, "f_*.jpg").Count();
        if (written != times.Count)
            Log?.Invoke($"Экран: ffmpeg записал {written} кадров, а метки времени пришли для {times.Count} — " +
                        "беру только совпавшие.");

        return Thin(all, settings);
    }

    /// <summary>Applies the minimum gap between frames and the hard cap on their number.</summary>
    private List<(double Seconds, string Path)> Thin(
        List<(double Seconds, string Path)> frames, AppSettings settings)
    {
        double minGap = Math.Max(0, settings.ScreenMinSecondsBetweenFrames);
        var kept = new List<(double Seconds, string Path)>();
        double last = double.NegativeInfinity;
        foreach (var f in frames)
        {
            if (kept.Count > 0 && f.Seconds - last < minGap) continue;
            kept.Add(f);
            last = f.Seconds;
        }
        if (minGap > 0 && kept.Count < frames.Count)
            Log?.Invoke($"Экран: {frames.Count - kept.Count} кадров отброшено — ближе {minGap:0} с к предыдущему.");

        int cap = Math.Max(1, settings.ScreenMaxKeyFrames);
        if (kept.Count > cap)
        {
            // Keep an even spread over the call rather than just its beginning.
            double step = (double)kept.Count / cap;
            var capped = new List<(double Seconds, string Path)>(cap);
            for (int i = 0; i < cap; i++) capped.Add(kept[(int)(i * step)]);
            Log?.Invoke($"Экран: кадров было {kept.Count}, оставляю {cap} (предел ScreenMaxKeyFrames).");
            kept = capped;
        }
        return kept;
    }

    /// <summary>Copies a key frame next to the video, downscaled, for a multimodal model to read.</summary>
    private async Task SaveKeyFrameAsync(AppSettings settings, string source, string dest, CancellationToken ct)
    {
        int maxWidth = Math.Clamp(settings.ScreenKeyFrameMaxWidth, 640, 4096);
        var psi = new ProcessStartInfo
        {
            FileName = settings.FfmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-i", source,
            "-vf", $"scale='min({maxWidth},iw)':-2:flags=lanczos",
            "-q:v", "4", dest,
        }) psi.ArgumentList.Add(a);

        Process? p = null;
        try
        {
            p = Process.Start(psi)!;
            _ = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 && !File.Exists(dest)) File.Copy(source, dest, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation has to take the process with it, not leave it running to the end.
            if (p != null) { try { p.Kill(entireProcessTree: true); } catch { } }
            throw;
        }
        catch
        {
            // ffmpeg unavailable for this one frame — the untouched frame is still useful.
            try { File.Copy(source, dest, overwrite: true); } catch { }
        }
        finally { p?.Dispose(); }
    }

    // ------------------------------------------------------------------ output

    private static async Task WriteTimelineAsync(string path, string mp4, string engine, ScreenMode mode,
        int keyFrames, List<ScreenTextBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Текст с экрана записи: {Path.GetFileName(mp4)}");
        sb.AppendLine($"# Движок: {engine}; режим: {ModeName(mode)}");
        sb.AppendLine($"# Ключевых кадров: {keyFrames}; блоков: {blocks.Count}");
        sb.AppendLine("# Это распознавание пикселей записанного видео (OCR), а не доступ к приложениям");
        sb.AppendLine("# участников: чужой шаринг приходит на эту машину только как картинка.");
        sb.AppendLine();

        foreach (var b in blocks)
        {
            // "## " prefix so a recognized line that itself starts with "[" can't be mistaken
            // for a heading — real screens are full of "[2] build.ps1" style text.
            sb.AppendLine("## " + Range(b) + (b.Language != null ? $"  ({b.Language})" : ""));
            if (b.Description != null) sb.AppendLine("экран: " + b.Description);
            sb.AppendLine(b.Text);
            if (b.FrameFile != null) sb.AppendLine("кадр: " + b.FrameFile);
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static async Task WriteJsonAsync(string path, string mp4, string engine, ScreenMode mode,
        int keyFrames, List<ScreenTextBlock> blocks, string? framesDir)
    {
        var payload = new
        {
            version = 1,
            source = Path.GetFileName(mp4),
            generated = DateTimeOffset.Now.ToString("o"),
            engine,
            mode = ModeName(mode),
            note = "OCR распознанных пикселей записи экрана; чужой шаринг доступен только как видео.",
            keyFrames,
            framesDir = framesDir != null ? Path.GetFileName(framesDir) : null,
            blocks = blocks.Select(b => new
            {
                t = Stamp(b.Seconds),
                tEnd = b.EndSeconds > b.Seconds ? Stamp(b.EndSeconds) : null,
                seconds = Math.Round(b.Seconds, 2),
                endSeconds = Math.Round(b.EndSeconds, 2),
                lang = b.Language,
                text = b.Text,
                description = b.Description,
                frame = b.FrameFile,
            }),
        };
        await File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }),
            new UTF8Encoding(false));
    }

    private static string ModeName(ScreenMode mode) => mode switch
    {
        ScreenMode.OcrKeyFrames => "ocr+keyframes",
        ScreenMode.Vlm => "vlm",
        _ => "ocr",
    };

    private static string Range(ScreenTextBlock b) =>
        b.EndSeconds - b.Seconds >= 1
            ? $"[{Stamp(b.Seconds)} – {Stamp(b.EndSeconds)}]"
            : $"[{Stamp(b.Seconds)}]";

    private static string Stamp(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");

    // ------------------------------------------------------------------ dedup

    /// <summary>
    /// Dice coefficient over character trigrams of the normalized text: 1.0 = the same screen,
    /// and it stays high when OCR wobbles by a letter or a timestamp in the corner ticks over.
    /// </summary>
    internal static double Similarity(string a, string b)
    {
        var x = Trigrams(Normalize(a));
        var y = Trigrams(Normalize(b));
        if (x.Count == 0 || y.Count == 0) return x.Count == y.Count ? 1 : 0;
        int shared = x.Count <= y.Count ? x.Count(y.Contains) : y.Count(x.Contains);
        return 2.0 * shared / (x.Count + y.Count);
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (char c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); space = false; }
            else if (!space) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    private static HashSet<string> Trigrams(string s)
    {
        var set = new HashSet<string>();
        for (int i = 0; i + 3 <= s.Length; i++) set.Add(s.Substring(i, 3));
        if (set.Count == 0 && s.Length > 0) set.Add(s);
        return set;
    }

    // ------------------------------------------------------------------ cleanup

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
