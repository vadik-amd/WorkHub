namespace WorkHub;

/// <summary>
/// Append-only log file under %APPDATA%\WorkHub\logs. Rolls over to .old at ~5 MB.
/// Logging must never break the caller, so every failure is swallowed.
/// </summary>
public sealed class FileLog(string fileName)
{
    private const long MaxBytes = 5_000_000;
    private readonly object _gate = new();

    public string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkHub", "logs", fileName);

    public void Write(string line)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* ignore */ }
    }
}

/// <summary>
/// Log of transcription runs (engine output, which OpenVINO device was used, timings,
/// errors) at %APPDATA%\WorkHub\logs\transcription.log.
/// </summary>
public static class TranscriptionLog
{
    private static readonly FileLog Log = new("transcription.log");
    public static string FilePath => Log.FilePath;
    public static void Write(string line) => Log.Write(line);
}

/// <summary>
/// Log of call recordings (which output devices were captured, which microphone was
/// picked, devices appearing / disappearing mid-call) at %APPDATA%\WorkHub\logs\recorder.log.
/// </summary>
public static class RecorderLog
{
    private static readonly FileLog Log = new("recorder.log");
    public static string FilePath => Log.FilePath;
    public static void Write(string line) => Log.Write(line);
}
