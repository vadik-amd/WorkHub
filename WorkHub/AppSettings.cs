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

    // Which side of the stereo recording to transcribe. Recordings are mic → LEFT,
    // system/others → RIGHT. "both" = mix to mono (default); "others" = right channel
    // only (ignore my mic); "me" = left channel only.
    public string TranscribeChannel { get; set; } = "both";   // "both" | "others" | "me"

    public WhisperChannel TranscribeChannelParsed() => TranscribeChannel?.ToLowerInvariant() switch
    {
        "others" => WhisperChannel.Others,
        "me" => WhisperChannel.Me,
        _ => WhisperChannel.Both,
    };

    // --- Optional screen recording (starts together with call recording) ---
    // Off by default; opt in from the tray. The screen is captured to MP4 (video only,
    // via ffmpeg gdigrab + a hardware H.264 encoder), then the call's stereo WAV is muxed
    // in as the audio track — so no driver-level or loopback-audio hacks are needed.
    public bool RecordScreen { get; set; } = false;
    public string FfmpegExe { get; set; } = "";        // set by the built-in ffmpeg setup
    public bool ScreenAllMonitors { get; set; } = false; // false = primary monitor only
    public bool ScreenIncludeAudio { get; set; } = true; // mux the call WAV into the video
    public int ScreenFps { get; set; } = 15;             // meetings don't need more; keeps size down
    public string ScreenVideoBitrate { get; set; } = "4M";
    public string ScreenEncoder { get; set; } = "h264_mf"; // hardware (Media Foundation); CPU-light

    // Portable whisper.cpp engine (no Python / no ffmpeg). Populated by the built-in setup.
    public string WhisperCppExe { get; set; } = "";
    public string WhisperCppModel { get; set; } = "";
    public string WhisperCppRepo { get; set; } = "ggerganov/whisper.cpp";

    // Voice Activity Detection: skip non-speech regions so Whisper can't hallucinate on
    // silence (the "Субтитры создавал…", "Thank you." loops, "*Music*" tags all come from
    // silent stretches). Uses a small silero model; enabled once that model is present.
    public bool WhisperUseVad { get; set; } = true;
    public string WhisperVadModel { get; set; } = "";     // ggml-silero-*.bin; set by setup
    public double WhisperVadThreshold { get; set; } = 0.6; // higher = stricter (less noise, may clip very quiet speech)

    // CPU threads for whisper.cpp. whisper's own default is 4 — far too few on a many-core
    // chip (4/16 ≈ 25% load, ~2× slower). 0 = auto: use ALL logical cores, which on this
    // hardware measured fastest (16 threads ≈ 1.9× faster than 4). Set an explicit number
    // to cap it if you want to keep cores free for other work during transcription.
    public int WhisperCppThreads { get; set; } = 0;

    /// <summary>Resolved thread count: explicit value, or auto = all logical cores (min 1).</summary>
    public int ResolveWhisperThreads() =>
        WhisperCppThreads > 0 ? WhisperCppThreads : Math.Max(1, Environment.ProcessorCount);

    // Threads for whisper.cpp while the GPU / NPU transcribe at the same time: their sidecars
    // need CPU too (and share the chip's power budget), so leave some cores free.
    // 0 = auto: all logical cores minus 4 (min 2).
    public int WhisperCppThreadsParallel { get; set; } = 0;

    public int ResolveWhisperThreadsParallel() =>
        WhisperCppThreadsParallel > 0 ? WhisperCppThreadsParallel : Math.Max(2, Environment.ProcessorCount - 4);

    // --- Transcription devices ---
    // Each device (CPU / GPU / NPU) is an "executor" with its own engine + model and an
    // on/off switch. Default: only the CPU with whisper.cpp — i.e. the original behaviour.
    public sealed class DeviceSlot
    {
        public string Device { get; set; } = "CPU";          // "CPU" | "GPU" | "NPU"
        public bool Enabled { get; set; }
        public string Engine { get; set; } = Executor.WhisperCpp; // "whispercpp" (CPU only) | "openvino"
        public string Model { get; set; } = "";              // ggml .bin path or OpenVINO IR folder
    }

    public List<DeviceSlot>? TranscribeSlots { get; set; }

    // "distribute" = each file×language once, spread across the enabled devices (fastest);
    // "compare"    = every file on every enabled device, one transcript per device/model.
    public string TranscribeMode { get; set; } = "distribute";

    public bool CompareMode() => string.Equals(TranscribeMode, "compare", StringComparison.OrdinalIgnoreCase);

    public static readonly string[] Devices = { "CPU", "GPU", "NPU" };

    public DeviceSlot Slot(string device)
    {
        EnsureSlots();
        return TranscribeSlots!.First(s => s.Device == device);
    }

    /// <summary>Enabled devices as executors, in CPU/GPU/NPU order.</summary>
    public List<Executor> EnabledExecutors()
    {
        EnsureSlots();
        return TranscribeSlots!.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Model))
            .Select(s => new Executor(s.Device, s.Engine, s.Model)).ToList();
    }

    /// <summary>Creates the per-device slots, carrying over the single-engine settings.</summary>
    public void EnsureSlots()
    {
        TranscribeSlots ??= new List<DeviceSlot>();
        bool fresh = TranscribeSlots.Count == 0;
        bool legacyOv = string.Equals(TranscriptionEngine, "openvino", StringComparison.OrdinalIgnoreCase);
        string legacyDevice = (OpenVinoDevice ?? "AUTO").ToUpperInvariant();

        string ovModel = !string.IsNullOrWhiteSpace(OpenVinoModelDir)
            ? OpenVinoModelDir
            : OpenVinoInstaller.ModelDirFor(OpenVinoModelRepo);

        foreach (var dev in Devices)
        {
            if (TranscribeSlots.Any(s => s.Device == dev)) continue;
            var slot = new DeviceSlot { Device = dev };
            if (dev == "CPU")
            {
                bool ovOnCpu = legacyOv && legacyDevice == "CPU";
                slot.Engine = ovOnCpu ? Executor.OpenVino : Executor.WhisperCpp;
                slot.Model = ovOnCpu ? ovModel : WhisperCppModel;
                slot.Enabled = !legacyOv || ovOnCpu;
            }
            else
            {
                slot.Engine = Executor.OpenVino;
                // large-v3 doesn't run on this NPU; give the NPU turbo instead.
                slot.Model = dev == "NPU" && ModelCatalog.Incompatibility("NPU", Executor.OpenVino, ovModel) != null
                    ? OpenVinoInstaller.ModelDirFor(OpenVinoInstaller.KnownModels[0].Repo)
                    : ovModel;
                slot.Enabled = fresh && legacyOv && (legacyDevice == "AUTO" || legacyDevice == dev);
            }
            TranscribeSlots.Add(slot);
        }
        TranscribeSlots.Sort((a, b) => Array.IndexOf(Devices, a.Device).CompareTo(Array.IndexOf(Devices, b.Device)));
    }

    // Legacy single-engine switch (before per-device slots); only read to migrate.
    public string TranscriptionEngine { get; set; } = "whispercpp";
    public string OpenVinoDevice { get; set; } = "AUTO";

    public string OpenVinoPython { get; set; } = "";     // venv python.exe; set by the built-in setup
    public string OpenVinoModelDir { get; set; } = "";   // last installed / chosen OpenVINO IR model folder
    // large-v3-turbo: closest to large-v3 among the models that run on this NPU, and the
    // fastest on the GPU. (large-v3 itself fails on the NPU; medium invents phrases.)
    public string OpenVinoModelRepo { get; set; } = "OpenVINO/whisper-large-v3-turbo-int8-ov";

    // --- GlobalProtect auto-reconnect ---
    // Off by default: it drives the VPN client UI, which on a managed machine you may
    // want to opt into consciously. Toggle it from the tray.
    public bool GpAutoReconnectEnabled { get; set; } = false;
    public int GpProbeIntervalSeconds { get; set; } = 15;
    public int GpFailuresBeforeDown { get; set; } = 3;      // debounce before declaring "down"
    public int GpReconnectCooldownSeconds { get; set; } = 90; // min gap between reconnect attempts
    public int GpMaxAttemptsBeforeBackoff { get; set; } = 5;  // then widen the cooldown
    public string GpPanGpaPath { get; set; } =
        @"C:\Program Files\Palo Alto Networks\GlobalProtect\PanGPA.exe";
    // Hamburger position as a fraction of the GP window rect (DPI/size independent),
    // and the top menu item as a fraction of the popup rect height.
    public double GpHamburgerFracX { get; set; } = 0.902;
    public double GpHamburgerFracY { get; set; } = 0.073;
    public double GpMenuTopItemFracY { get; set; } = 0.15;
    public string[] GpProbeUrls { get; set; } =
    {
        "http://www.msftconnecttest.com/connecttest.txt",
        "http://www.gstatic.com/generate_204",
    };

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
