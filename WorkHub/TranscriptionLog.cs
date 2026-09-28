namespace WorkHub;

/// <summary>
/// Append-only log of transcription runs (engine output, which OpenVINO device was used,
/// timings, errors) at %APPDATA%\WorkHub\logs\transcription.log. Rolls over to .old at ~5 MB.
/// </summary>
public static class TranscriptionLog
{
    private const long MaxBytes = 5_000_000;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkHub", "logs", "transcription.log");

    public static void Write(string line)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break transcription */ }
    }
}
