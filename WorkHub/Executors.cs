using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkHub;

/// <summary>
/// "Who transcribes": a device (CPU / GPU / NPU) + engine (whisper.cpp / OpenVINO) + model
/// (a ggml .bin or an OpenVINO IR folder). Its <see cref="Tag"/> goes into transcript names:
/// call_…_ru_GPU-ov-turbo.txt.
/// </summary>
public sealed record Executor(string Device, string Engine, string Model)
{
    public const string WhisperCpp = "whispercpp";
    public const string OpenVino = "openvino";

    public bool IsOpenVino => Engine == OpenVino;

    /// <summary>Short model name: ggml-large-v3.bin → large-v3, whisper-large-v3-turbo-int8-ov → turbo.</summary>
    public string ModelShort => ShortName(Engine, Model);

    public string Tag => $"{Device}-{(IsOpenVino ? "ov" : "wcpp")}-{ModelShort}";

    public string Label => $"{Device}: {(IsOpenVino ? "OpenVINO" : "whisper.cpp")} {ModelShort}";

    public static string ShortName(string engine, string model)
    {
        string name = engine == OpenVino
            ? Path.GetFileName(model.TrimEnd('\\', '/'))
            : Path.GetFileNameWithoutExtension(model);
        name = name.ToLowerInvariant();
        if (engine == OpenVino)
        {
            if (name.StartsWith("whisper-")) name = name[8..];
            if (name.EndsWith("-ov")) name = name[..^3];
            name = name.Replace("-int8", "").Replace("large-v3-turbo", "turbo");
        }
        else if (name.StartsWith("ggml-"))
        {
            name = name[5..];
        }
        name = Regex.Replace(name, "[^a-z0-9.-]+", "-").Trim('-');
        return name.Length > 0 ? name : "model";
    }
}

/// <summary>A model offered for a device in the tray / transcription center.</summary>
public sealed record ModelOption(
    string Engine, string Model, string Label,
    bool Downloaded,     // files are on disk
    string? Repo,        // OpenVINO repo to download it from (known models)
    string? Blocked);    // why it can't be used on this device (e.g. "не работает на NPU")

/// <summary>Which models exist and which of them run on which device.</summary>
public static class ModelCatalog
{
    public static List<ModelOption> For(string device, AppSettings s)
    {
        var list = new List<ModelOption>();

        // whisper.cpp runs on the CPU only — everything that worked before.
        if (device == "CPU")
        {
            bool haveExe = !string.IsNullOrWhiteSpace(s.WhisperCppExe) && File.Exists(s.WhisperCppExe);
            foreach (var bin in WhisperCppModels(s))
                list.Add(new ModelOption(Executor.WhisperCpp, bin,
                    "whisper.cpp " + Executor.ShortName(Executor.WhisperCpp, bin), true, null,
                    haveExe ? null : "whisper.cpp не установлен"));
        }

        bool runtime = !string.IsNullOrWhiteSpace(s.OpenVinoPython) && File.Exists(s.OpenVinoPython);
        foreach (var dir in OpenVinoModels(s, out var repos))
        {
            bool downloaded = OpenVinoInstaller.MissingModelFiles(dir).Count == 0;
            string? blocked = !runtime ? "OpenVINO не установлен" : Incompatibility(device, Executor.OpenVino, dir);
            string label = "OpenVINO " + Executor.ShortName(Executor.OpenVino, dir);
            list.Add(new ModelOption(Executor.OpenVino, dir, label, downloaded, repos.GetValueOrDefault(dir), blocked));
        }
        return list;
    }

    /// <summary>ggml models next to whisper.cpp, plus the configured one if it lives elsewhere.</summary>
    public static List<string> WhisperCppModels(AppSettings s)
    {
        var list = new List<string>();
        if (Directory.Exists(WhisperInstaller.ToolsDir))
            list.AddRange(Directory.EnumerateFiles(WhisperInstaller.ToolsDir, "ggml-*.bin")
                .Where(f => !Path.GetFileName(f).StartsWith("ggml-silero", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f));
        if (!string.IsNullOrWhiteSpace(s.WhisperCppModel) && File.Exists(s.WhisperCppModel) &&
            !list.Any(f => SamePath(f, s.WhisperCppModel)))
            list.Add(s.WhisperCppModel);
        return list;
    }

    /// <summary>Known repos (downloaded or not), other IR folders in the models dir, the configured one.</summary>
    private static List<string> OpenVinoModels(AppSettings s, out Dictionary<string, string> repos)
    {
        repos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var (repo, _) in OpenVinoInstaller.KnownModels)
        {
            string dir = OpenVinoInstaller.ModelDirFor(repo);
            list.Add(dir);
            repos[dir] = repo;
        }
        if (Directory.Exists(OpenVinoInstaller.ModelsDir))
            foreach (var dir in Directory.EnumerateDirectories(OpenVinoInstaller.ModelsDir))
                if (!list.Any(d => SamePath(d, dir)) && OpenVinoInstaller.MissingModelFiles(dir).Count == 0)
                    list.Add(dir);
        if (OpenVinoInstaller.MissingModelFiles(s.OpenVinoModelDir).Count == 0 &&
            !list.Any(d => SamePath(d, s.OpenVinoModelDir)))
            list.Add(s.OpenVinoModelDir);
        return list;
    }

    /// <summary>
    /// Why <paramref name="model"/> can't run on <paramref name="device"/>, from the sidecar's
    /// persistent marks (cache\&lt;model&gt;\device_health.json), or null if it can / wasn't tried.
    /// </summary>
    public static string? Incompatibility(string device, string engine, string model)
    {
        if (engine != Executor.OpenVino || device == "CPU") return null;
        var mark = HealthMarks(model).FirstOrDefault(m => m.Device == device);
        return mark.Device == null ? null : $"не работает на {device}";
    }

    /// <summary>Forgets the "doesn't work on this device" marks, so the device is tried again.</summary>
    public static void ClearIncompatibility(string device)
    {
        if (!Directory.Exists(OpenVinoInstaller.CacheDir)) return;
        foreach (var file in Directory.EnumerateFiles(OpenVinoInstaller.CacheDir, "device_health.json", SearchOption.AllDirectories))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file)) ?? new();
                int before = dict.Count;
                foreach (var key in dict.Keys.Where(k => k.Contains($"|{device}|")).ToList()) dict.Remove(key);
                if (dict.Count != before)
                    File.WriteAllText(file, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* unreadable mark file — leave it */ }
        }
    }

    private static List<(string? Device, string Error)> HealthMarks(string model)
    {
        var result = new List<(string?, string)>();
        string file = Path.Combine(OpenVinoInstaller.CacheDir, Path.GetFileName(model.TrimEnd('\\', '/')), "device_health.json");
        if (!File.Exists(file)) return result;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                // key: <model>|<openvino version>|<device>|<npu driver>
                var parts = p.Name.Split('|');
                if (parts.Length >= 3)
                    result.Add((parts[2], p.Value.TryGetProperty("error", out var e) ? e.GetString() ?? "" : ""));
            }
        }
        catch { /* ignore */ }
        return result;
    }

    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

/// <summary>Transcript file names: call_…_ru_GPU-ov-turbo.txt (older ones: call_…_ru.txt).</summary>
public static class TranscriptPaths
{
    public static string For(string wav, string lang, Executor e) =>
        Path.Combine(Path.GetDirectoryName(wav)!, Path.GetFileNameWithoutExtension(wav) + "_" + lang + "_" + e.Tag + ".txt");

    /// <summary>Pre-executor name (before device/model tags); still counts as "transcribed".</summary>
    public static string Legacy(string wav, string lang) =>
        Path.Combine(Path.GetDirectoryName(wav)!, Path.GetFileNameWithoutExtension(wav) + "_" + lang + ".txt");

    /// <summary>Is there any transcript of this recording in this language (any device/model)?</summary>
    public static bool HasAny(string wav, string lang) => File.Exists(Legacy(wav, lang)) || ExistingTags(wav, lang).Any();

    /// <summary>Tags of the tagged transcripts that exist, e.g. "GPU-ov-turbo".</summary>
    public static IEnumerable<string> ExistingTags(string wav, string lang)
    {
        string dir = Path.GetDirectoryName(wav)!;
        string prefix = Path.GetFileNameWithoutExtension(wav) + "_" + lang + "_";
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, prefix + "*.txt"))
            yield return Path.GetFileNameWithoutExtension(f)[prefix.Length..];
    }
}
