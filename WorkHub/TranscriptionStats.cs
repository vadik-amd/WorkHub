using System.Text.Json;

namespace WorkHub;

/// <summary>
/// Measured speed of each executor (device + engine + model), used by the scheduler to
/// decide which device should take which recording. Starts from benchmarks on this
/// machine and learns from every finished job (moving average), persisted in
/// %APPDATA%\WorkHub\transcription_stats.json.
/// </summary>
public static class TranscriptionStats
{
    public sealed class Entry
    {
        public double Rtf { get; set; }     // processing seconds per second of audio
        public double LoadS { get; set; }   // model load from the compiled cache
        public int Runs { get; set; }
    }

    private const double Alpha = 0.4; // weight of the newest measurement
    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _data;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkHub", "transcription_stats.json");

    // Core Ultra 7 255H / Arc 140T / NPU3720, 355 s Russian call (see README).
    private static readonly Dictionary<string, (double Rtf, double LoadS)> Defaults = new()
    {
        ["CPU-wcpp-large-v3"] = (0.68, 2),
        ["CPU-wcpp-medium"] = (0.35, 1),
        ["CPU-wcpp-small"] = (0.12, 1),
        ["CPU-wcpp-base"] = (0.05, 1),
        ["CPU-ov-large-v3"] = (0.27, 4),
        ["CPU-ov-turbo"] = (0.10, 3),
        ["CPU-ov-medium"] = (0.15, 3),
        ["GPU-ov-large-v3"] = (0.11, 5),
        ["GPU-ov-turbo"] = (0.035, 1),
        ["GPU-ov-medium"] = (0.065, 1.5),
        ["NPU-ov-turbo"] = (0.085, 5),
        ["NPU-ov-medium"] = (0.13, 5),
    };

    // A first run on a device compiles the model; roughly how long that takes.
    private static double CompileSeconds(string device) => device switch
    {
        "NPU" => 240,
        "GPU" => 15,
        _ => 6,
    };

    /// <summary>Estimated wall time for <paramref name="e"/> to transcribe <paramref name="audioS"/> seconds.</summary>
    public static double Estimate(Executor e, double audioS)
    {
        var (rtf, load) = Get(e);
        if (e.IsOpenVino && !HasCompiledCache(e)) load = CompileSeconds(e.Device);
        return load + rtf * audioS;
    }

    public static (double Rtf, double LoadS) Get(Executor e)
    {
        lock (Gate)
        {
            Load();
            if (_data!.TryGetValue(e.Tag, out var x) && x.Runs > 0) return (x.Rtf, x.LoadS);
        }
        if (Defaults.TryGetValue(e.Tag, out var d)) return d;
        return e.Device switch { "GPU" => (0.1, 5), "NPU" => (0.15, 8), _ => (e.IsOpenVino ? 0.3 : 0.7, 3) };
    }

    /// <summary>Folds one finished job into the averages.</summary>
    public static void Record(Executor e, double audioS, double processS, double loadS)
    {
        if (audioS < 5 || processS <= 0) return; // too short to say anything
        lock (Gate)
        {
            Load();
            double rtf = processS / audioS;
            if (!_data!.TryGetValue(e.Tag, out var x) || x.Runs == 0)
                _data[e.Tag] = x = new Entry { Rtf = rtf, LoadS = loadS };
            else
            {
                x.Rtf = Alpha * rtf + (1 - Alpha) * x.Rtf;
                // A compile-length load is a one-off, not the steady state.
                if (loadS < CompileSeconds(e.Device) / 2) x.LoadS = Alpha * loadS + (1 - Alpha) * x.LoadS;
            }
            x.Runs++;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* stats are best-effort */ }
        }
    }

    private static bool HasCompiledCache(Executor e)
    {
        string dir = Path.Combine(OpenVinoInstaller.CacheDir, Path.GetFileName(e.Model.TrimEnd('\\', '/')), e.Device.ToLowerInvariant());
        return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.blob").Any();
    }

    private static void Load()
    {
        if (_data != null) return;
        try
        {
            if (File.Exists(FilePath))
                _data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath));
        }
        catch { /* start fresh */ }
        _data ??= new Dictionary<string, Entry>();
    }
}
