using System.ComponentModel;
using System.Diagnostics;

namespace WorkHub;

public enum WhisperEngine { None, WhisperCpp, OpenAi }

/// <summary>
/// Transcribes a WAV to a .txt next to it using whichever Whisper engine is available:
///   • whisper.cpp – a portable standalone exe (no Python / no ffmpeg), our preferred engine;
///   • openai-whisper – the classic CLI on PATH (needs Python + ffmpeg).
/// Whisper reads the 16 kHz WAV directly and averages our L=mic / R=system channels to
/// mono, so both sides are transcribed.
/// </summary>
public sealed class Transcriber
{
    public sealed class WhisperNotFoundException(string exe)
        : Exception($"Whisper не найден ('{exe}').");

    /// <summary>Which engine is currently usable, given the settings.</summary>
    public static WhisperEngine ResolveEngine(AppSettings s)
    {
        if (!string.IsNullOrWhiteSpace(s.WhisperCppExe) && File.Exists(s.WhisperCppExe) &&
            !string.IsNullOrWhiteSpace(s.WhisperCppModel) && File.Exists(s.WhisperCppModel))
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
    /// Transcribes one WAV in a single language, writing exactly <paramref name="outputTxtPath"/>.
    /// </summary>
    public async Task<string> TranscribeAsync(
        string wavPath, string language, string outputTxtPath,
        AppSettings settings, Action<string>? log, CancellationToken ct)
    {
        var engine = ResolveEngine(settings);
        if (engine == WhisperEngine.None)
            throw new WhisperNotFoundException(settings.WhisperExe);

        string outDir = Path.GetDirectoryName(outputTxtPath)!;

        var psi = engine == WhisperEngine.WhisperCpp
            ? BuildWhisperCppStartInfo(wavPath, StripTxt(outputTxtPath), language, settings)
            : BuildOpenAiStartInfo(wavPath, outDir, language, settings);

        Process p;
        try
        {
            p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Invoke(e.Data); };
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
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0)
                throw new Exception($"Whisper завершился с кодом {p.ExitCode}.");
        }

        // openai-whisper names output after the input file; move it to the per-language path.
        if (engine == WhisperEngine.OpenAi)
        {
            string produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(wavPath) + ".txt");
            if (!string.Equals(produced, outputTxtPath, StringComparison.OrdinalIgnoreCase) && File.Exists(produced))
            {
                if (File.Exists(outputTxtPath)) File.Delete(outputTxtPath);
                File.Move(produced, outputTxtPath);
            }
        }

        return outputTxtPath;
    }

    private static string StripTxt(string path) =>
        path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;

    private static ProcessStartInfo BuildWhisperCppStartInfo(string wav, string outBase, string lang, AppSettings s)
    {
        var psi = NewHidden(s.WhisperCppExe);
        psi.WorkingDirectory = Path.GetDirectoryName(s.WhisperCppExe) ?? "";
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(s.WhisperCppModel);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(wav);
        psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(lang);
        psi.ArgumentList.Add("-otxt");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add(outBase);
        return psi;
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

    private static ProcessStartInfo NewHidden(string exe) => new()
    {
        FileName = exe,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
    };
}
