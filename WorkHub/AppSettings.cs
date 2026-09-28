using System.Text.Json;

namespace WorkHub;

/// <summary>Small persisted settings (Whisper model / language / executable).</summary>
public sealed class AppSettings
{
    public string WhisperExe { get; set; } = "whisper";
    public string WhisperModel { get; set; } = "small";   // base | small | medium | large-v3

    // Languages to transcribe. Whisper runs once per enabled language, producing a
    // separate *_<lang>.txt per file. Both on by default; either can be disabled.
    public bool TranscribeRussian { get; set; } = true;
    public bool TranscribeEnglish { get; set; } = true;

    /// <summary>Enabled language codes, in run order.</summary>
    public IReadOnlyList<string> EnabledLanguages()
    {
        var list = new List<string>();
        if (TranscribeRussian) list.Add("ru");
        if (TranscribeEnglish) list.Add("en");
        return list;
    }

    // Portable whisper.cpp engine (no Python / no ffmpeg). Populated by the built-in setup.
    public string WhisperCppExe { get; set; } = "";
    public string WhisperCppModel { get; set; } = "";
    public string WhisperCppRepo { get; set; } = "ggerganov/whisper.cpp";

    /// <summary>Launch the configured programs when the hub starts.</summary>
    public bool AutoLaunchOnStartup { get; set; } = true;

    /// <summary>Programs/shortcuts to launch (see <see cref="LaunchItem"/>).</summary>
    public List<LaunchItem> AutoLaunch { get; set; } = new();

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WorkHub", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { /* ignore persistence failures */ }
    }
}
