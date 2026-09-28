using System.Diagnostics;
using System.Text.Json;

namespace WorkHub;

/// <summary>
/// Everything transcription for the tray: the "Расшифровка" menu (quick actions, mode,
/// per-device executor switches and models), building a <see cref="TranscriptionPlan"/>,
/// running it with <see cref="TranscriptionRunner"/>, progress in the tray, STOP. The
/// transcription center window drives runs through the same controller.
/// </summary>
public sealed class TranscriptionController
{
    private readonly AppSettings _settings;
    private readonly Func<string> _outputFolder;
    private readonly Func<string?> _activeRecording;
    private readonly Action<int, string, ToolTipIcon> _balloon;
    private readonly Action<string?> _status; // tray tooltip; null = back to idle
    private readonly Action<Action> _runOnUi;

    private CancellationTokenSource? _cts;
    private TranscriptionCenterForm? _center;

    private ToolStripMenuItem? _lastItem, _newItem, _stopItem; // null until BuildMenu()
    private readonly Dictionary<string, ToolStripMenuItem> _deviceMenus = new();
    private readonly Dictionary<string, ToolStripMenuItem> _modeItems = new();
    private readonly Dictionary<string, ToolStripMenuItem> _channelItems = new();

    public TranscriptionController(AppSettings settings, Func<string> outputFolder, Func<string?> activeRecording,
        Action<int, string, ToolTipIcon> balloon, Action<string?> status, Action<Action> runOnUi)
    {
        _settings = settings;
        _outputFolder = outputFolder;
        _activeRecording = activeRecording;
        _balloon = balloon;
        _status = status;
        _runOnUi = runOnUi;
        _settings.EnsureSlots();
    }

    public AppSettings Settings => _settings;
    public bool IsBusy => _cts != null;

    /// <summary>Raised on the UI thread when a run starts (the center window attaches to it).</summary>
    public event Action<TranscriptionRunner, TranscriptionPlan>? RunStarted;
    public event Action<TranscriptionSummary?>? RunFinished;

    // ------------------------------------------------------------------ menu

    public ToolStripMenuItem BuildMenu()
    {
        _lastItem = new ToolStripMenuItem("Расшифровать последнюю запись", null, (_, _) => _ = TranscribeLatestAsync());
        _newItem = new ToolStripMenuItem("Расшифровать все новые", null, (_, _) => _ = TranscribeNewAsync());
        var centerItem = new ToolStripMenuItem("Центр расшифровки…", null, (_, _) => OpenCenter());
        _stopItem = new ToolStripMenuItem("⏹ Остановить расшифровку", null, (_, _) => Stop()) { Enabled = false };

        var modeMenu = new ToolStripMenuItem("Режим");
        foreach (var (key, label) in new[]
                 {
                     ("distribute", "Распределять задачи по устройствам (быстрее)"),
                     ("compare", "Сравнение: каждую запись на всех включённых устройствах"),
                 })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetMode(key));
            _modeItems[key] = item;
            modeMenu.DropDownItems.Add(item);
        }
        UpdateModeChecks();

        var deviceItems = new List<ToolStripItem>();
        foreach (var dev in AppSettings.Devices)
        {
            var menu = new ToolStripMenuItem(dev);
            menu.DropDownItems.Add(new ToolStripMenuItem("…")); // placeholder so the arrow shows
            menu.DropDownOpening += (_, _) => FillDeviceMenu(dev, menu);
            _deviceMenus[dev] = menu;
            deviceItems.Add(menu);
        }

        var langMenu = new ToolStripMenuItem("Языки транскрипции");
        var ru = new ToolStripMenuItem("Русский") { CheckOnClick = true, Checked = _settings.TranscribeRussian };
        ru.CheckedChanged += (_, _) => { _settings.TranscribeRussian = ru.Checked; _settings.Save(); };
        var en = new ToolStripMenuItem("English") { CheckOnClick = true, Checked = _settings.TranscribeEnglish };
        en.CheckedChanged += (_, _) => { _settings.TranscribeEnglish = en.Checked; _settings.Save(); };
        langMenu.DropDownItems.AddRange(new ToolStripItem[] { ru, en });

        // What to transcribe: both sides, or only one channel of the stereo recording
        // (mic = my voice on the left, others = system audio on the right).
        var channelMenu = new ToolStripMenuItem("Что расшифровывать");
        foreach (var (key, label) in new[]
                 {
                     ("both", "Мою речь и собеседников"),
                     ("others", "Только собеседников (без моего микрофона)"),
                     ("me", "Только меня"),
                 })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetChannel(key));
            _channelItems[key] = item;
            channelMenu.DropDownItems.Add(item);
        }
        UpdateChannelChecks();

        var root = new ToolStripMenuItem("Расшифровка (Whisper)");
        root.DropDownOpening += (_, _) => UpdateDeviceLabels();
        root.DropDownItems.AddRange(new ToolStripItem[] { _lastItem, _newItem, centerItem, _stopItem, new ToolStripSeparator(), modeMenu });
        root.DropDownItems.AddRange(deviceItems.ToArray());
        root.DropDownItems.AddRange(new ToolStripItem[]
        {
            langMenu,
            channelMenu,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Проверить установку Whisper", null, (_, _) => CheckInstall()),
            new ToolStripMenuItem("Установить / настроить whisper.cpp…", null, (_, _) => OpenWhisperCppSetup()),
            new ToolStripMenuItem("Установить / настроить OpenVINO…", null, (_, _) => OpenOpenVinoSetup()),
            new ToolStripMenuItem("Открыть лог расшифровки", null, (_, _) => OpenLog()),
        });
        UpdateDeviceLabels();
        return root;
    }

    private void UpdateDeviceLabels()
    {
        foreach (var (dev, menu) in _deviceMenus)
        {
            var slot = _settings.Slot(dev);
            string model = string.IsNullOrWhiteSpace(slot.Model)
                ? "модель не выбрана"
                : (slot.Engine == Executor.OpenVino ? "OpenVINO " : "whisper.cpp ") + Executor.ShortName(slot.Engine, slot.Model);
            menu.Text = $"{dev}: {model}" + (slot.Enabled ? "" : "  — выкл.");
            menu.Checked = slot.Enabled;
        }
    }

    /// <summary>Per-device submenu: on/off + the models that exist for it (unusable ones greyed with the reason).</summary>
    private void FillDeviceMenu(string dev, ToolStripMenuItem menu)
    {
        var slot = _settings.Slot(dev);
        menu.DropDownItems.Clear();

        var enable = new ToolStripMenuItem("Участвует в расшифровке", null, (_, _) => ToggleDevice(dev)) { Checked = slot.Enabled };
        menu.DropDownItems.Add(enable);
        menu.DropDownItems.Add(new ToolStripSeparator());

        var options = ModelCatalog.For(dev, _settings);
        foreach (var opt in options)
        {
            bool current = slot.Engine == opt.Engine && ModelCatalog.SamePath(slot.Model, opt.Model);
            string text = opt.Label;
            if (opt.Blocked != null) text += "  — " + opt.Blocked;
            else if (!opt.Downloaded) text += "  — скачать";
            var item = new ToolStripMenuItem(text, null, (_, _) => SelectModel(dev, opt))
            {
                Checked = current,
                // "не установлен" is fixable from here; "не работает на NPU" is not.
                Enabled = opt.Blocked == null || opt.Blocked.Contains("не установлен"),
            };
            menu.DropDownItems.Add(item);
        }

        bool anyBlocked = options.Any(o => o.Blocked?.StartsWith("не работает") == true);
        if (dev == "CPU" || anyBlocked) menu.DropDownItems.Add(new ToolStripSeparator());
        if (dev == "CPU")
            menu.DropDownItems.Add(new ToolStripMenuItem("Скачать другую модель whisper.cpp…", null, (_, _) => OpenWhisperCppSetup()));
        if (anyBlocked)
            menu.DropDownItems.Add(new ToolStripMenuItem($"Проверить заново модели «не работает на {dev}»", null, (_, _) =>
            {
                ModelCatalog.ClearIncompatibility(dev);
                _balloon(4000, $"Отметки сняты: при следующем запуске {dev} попробует эти модели снова " +
                               "(первый раз — компиляция, может занять минуты).", ToolTipIcon.Info);
            }));
    }

    private void ToggleDevice(string dev)
    {
        if (!EnsureNotBusy()) return;
        var slot = _settings.Slot(dev);
        if (!slot.Enabled)
        {
            // Make sure the device has something runnable before switching it on.
            if (string.IsNullOrWhiteSpace(slot.Model) || !IsUsable(new Executor(dev, slot.Engine, slot.Model)))
            {
                var opt = ModelCatalog.For(dev, _settings).FirstOrDefault(o => o.Blocked == null && o.Downloaded);
                if (opt != null) { slot.Engine = opt.Engine; slot.Model = opt.Model; }
                else if (!(dev == "CPU" ? OpenWhisperCppSetup() : OpenOpenVinoSetup())) return;
                else if (ModelCatalog.For(dev, _settings).FirstOrDefault(o => o.Blocked == null && o.Downloaded) is { } o2)
                { slot.Engine = o2.Engine; slot.Model = o2.Model; }
            }
        }
        slot.Enabled = !slot.Enabled;
        _settings.Save();
        UpdateDeviceLabels();
        var on = _settings.EnabledExecutors();
        _balloon(2500, on.Count == 0
            ? "Все устройства выключены — расшифровка не запустится."
            : "Расшифровывают: " + string.Join(", ", on.Select(e => e.Label)) + ".", ToolTipIcon.Info);
    }

    private void SelectModel(string dev, ModelOption opt)
    {
        if (!EnsureNotBusy()) return;
        if (opt.Blocked != null)
        {
            // whisper.cpp / OpenVINO not installed yet — offer the setup.
            if (!(opt.Engine == Executor.WhisperCpp ? OpenWhisperCppSetup() : OpenOpenVinoSetup())) return;
        }
        string model = opt.Model;
        if (!opt.Downloaded && opt.Repo != null && !DownloadOpenVinoModel(opt.Repo)) return;

        var slot = _settings.Slot(dev);
        slot.Engine = opt.Engine;
        slot.Model = model;
        _settings.Save();
        UpdateDeviceLabels();
        _balloon(2500, $"{dev}: {opt.Label}" + (slot.Enabled ? "." : " (устройство выключено — включите «Участвует»)."), ToolTipIcon.Info);
    }

    private bool DownloadOpenVinoModel(string repo)
    {
        string previous = _settings.OpenVinoModelRepo;
        _settings.OpenVinoModelRepo = repo;
        using var form = new OpenVinoSetupForm(_settings, switchToRepo: true) { TopMost = true };
        bool ok = form.ShowDialog() == DialogResult.OK && OpenVinoInstaller.IsModelDownloaded(repo);
        if (!ok) { _settings.OpenVinoModelRepo = previous; _settings.Save(); }
        return ok;
    }

    private void SetMode(string mode)
    {
        _settings.TranscribeMode = mode;
        _settings.Save();
        UpdateModeChecks();
    }

    private void UpdateModeChecks()
    {
        foreach (var (key, item) in _modeItems)
            item.Checked = key == (_settings.CompareMode() ? "compare" : "distribute");
    }

    private void SetChannel(string value)
    {
        _settings.TranscribeChannel = value;
        _settings.Save();
        UpdateChannelChecks();
    }

    private void UpdateChannelChecks()
    {
        foreach (var (key, item) in _channelItems)
            item.Checked = string.Equals(key, _settings.TranscribeChannel, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ setup dialogs

    public bool OpenWhisperCppSetup()
    {
        using var form = new DependencySetupForm(_settings) { TopMost = true };
        return form.ShowDialog() == DialogResult.OK;
    }

    public bool OpenOpenVinoSetup()
    {
        using var form = new OpenVinoSetupForm(_settings) { TopMost = true };
        return form.ShowDialog() == DialogResult.OK;
    }

    private void CheckInstall()
    {
        Task.Run(() =>
        {
            var lines = _settings.EnabledExecutors()
                .Select(e => $"{e.Label}: {(IsUsable(e) ? "готово" : "НЕ настроено")}").ToList();
            bool anyBroken = _settings.EnabledExecutors().Any(e => !IsUsable(e));
            if (lines.Count == 0) lines.Add("Все устройства выключены.");
            _runOnUi(() => _balloon(6000, string.Join("\n", lines), anyBroken ? ToolTipIcon.Warning : ToolTipIcon.Info));
        });
    }

    private void OpenLog()
    {
        try
        {
            if (!File.Exists(TranscriptionLog.FilePath)) TranscriptionLog.Write("(лог пуст)");
            Process.Start(new ProcessStartInfo { FileName = TranscriptionLog.FilePath, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    public void OpenCenter()
    {
        if (_center is { IsDisposed: false })
        {
            _center.Activate();
            return;
        }
        _center = new TranscriptionCenterForm(this);
        _center.Show();
    }

    // ------------------------------------------------------------------ running

    /// <summary>All call WAVs, newest first, excluding the one being recorded right now.</summary>
    public List<string> Recordings()
    {
        string folder = _outputFolder();
        if (!Directory.Exists(folder)) return new List<string>();
        string? active = _activeRecording();
        return Directory.EnumerateFiles(folder, "call_*.wav")
            .Where(w => active == null || !ModelCatalog.SamePath(w, active))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    /// <summary>Can <paramref name="e"/> run right now (engine installed, model on disk)?</summary>
    public bool IsUsable(Executor e)
    {
        if (e.IsOpenVino)
            return !string.IsNullOrWhiteSpace(_settings.OpenVinoPython) && File.Exists(_settings.OpenVinoPython) &&
                   OpenVinoInstaller.MissingModelFiles(e.Model).Count == 0;
        if (!string.IsNullOrWhiteSpace(_settings.WhisperCppExe) && File.Exists(_settings.WhisperCppExe) && File.Exists(e.Model))
            return true;
        // No whisper.cpp: the CPU still works through openai-whisper on PATH, as before.
        return Transcriber.ResolveEngine(_settings, e) == WhisperEngine.OpenAi;
    }

    private async Task TranscribeLatestAsync()
    {
        var wav = Recordings().FirstOrDefault();
        if (wav == null)
        {
            _balloon(3000, "Нет записей для расшифровки.", ToolTipIcon.Info);
            return;
        }
        await StartAsync(BuildJobs(new[] { wav }, _settings.EnabledLanguages(), onlyMissing: false), overwrite: true);
    }

    private async Task TranscribeNewAsync()
    {
        var langs = _settings.EnabledLanguages();
        var wavs = Recordings().Where(w => langs.Any(l => !TranscriptPaths.HasAny(w, l))).ToList();
        if (wavs.Count == 0)
        {
            _balloon(3000, "Новых записей без расшифровки нет.", ToolTipIcon.Info);
            return;
        }
        await StartAsync(BuildJobs(wavs, langs, onlyMissing: true), overwrite: false);
    }

    /// <summary>Jobs for the given recordings; <paramref name="onlyMissing"/> skips languages already transcribed.</summary>
    public static List<TranscriptionJob> BuildJobs(IEnumerable<string> wavs, IReadOnlyList<string> langs, bool onlyMissing)
    {
        var jobs = new List<TranscriptionJob>();
        foreach (var wav in wavs)
        {
            double dur = Transcriber.AudioSeconds(wav);
            foreach (var lang in langs)
                if (!onlyMissing || !TranscriptPaths.HasAny(wav, lang))
                    jobs.Add(new TranscriptionJob(wav, lang, dur));
        }
        return jobs;
    }

    private Task StartAsync(List<TranscriptionJob> jobs, bool overwrite) =>
        StartAsync(new TranscriptionPlan(jobs, _settings.EnabledExecutors(), _settings.CompareMode(), overwrite), _settings);

    /// <summary>
    /// Runs a plan (from the tray or the center window). <paramref name="settings"/> may be a
    /// per-run copy (e.g. a different channel chosen in the center window).
    /// </summary>
    public async Task StartAsync(TranscriptionPlan plan, AppSettings settings)
    {
        if (!EnsureNotBusy()) return;
        if (plan.Jobs.Count == 0)
        {
            _balloon(3000, "Нечего расшифровывать: включите хотя бы один язык или выберите записи.", ToolTipIcon.Info);
            return;
        }

        // Drop executors that can't run (offer the matching setup once).
        var execs = new List<Executor>();
        foreach (var e in plan.Executors)
        {
            if (!IsUsable(e))
            {
                if (e.IsOpenVino) OpenOpenVinoSetup();
                else OpenWhisperCppSetup();
            }
            if (!IsUsable(e))
            {
                _balloon(5000, $"{e.Label}: не настроено — устройство пропущено.", ToolTipIcon.Warning);
                continue;
            }
            execs.Add(e);
        }
        if (execs.Count == 0)
        {
            _balloon(5000, "Нет ни одного готового устройства: включите его в «Расшифровка → CPU / GPU / NPU».",
                ToolTipIcon.Warning);
            return;
        }
        plan = plan with { Executors = execs };

        _cts = new CancellationTokenSource();
        var runner = new TranscriptionRunner();
        int total = plan.Compare ? plan.Jobs.Count * execs.Count : plan.Jobs.Count;
        int finished = 0;
        var active = new Dictionary<string, string>(); // device -> job name
        void ShowProgress()
        {
            string devs;
            lock (active) devs = string.Join(" ", active.Keys);
            _runOnUi(() => _status(Truncate($"Work Hub — расшифровка {finished}/{total} {devs}", 63)));
        }

        runner.Log += TranscriptionLog.Write;
        runner.JobStarted += (e, j) => { lock (active) active[e.Device] = j.Name; ShowProgress(); };
        runner.JobDone += (e, j, r) =>
        {
            Interlocked.Increment(ref finished);
            lock (active) active.Remove(e.Device);
            ShowProgress();
            if (total <= 3)
                _runOnUi(() => _balloon(2500, $"Готово: {Path.GetFileName(r.OutputPath)} ({r.WallSeconds:0} с)", ToolTipIcon.Info));
        };
        runner.JobFailed += (e, _, _) => { lock (active) active.Remove(e.Device); ShowProgress(); };
        runner.ExecutorStopped += (e, reason) => _runOnUi(() => _balloon(6000,
            $"{e.Label} выбыл: {reason}" + (plan.Compare ? "" : " Его задачи доделают другие устройства."), ToolTipIcon.Warning));

        SetBusyUi(true);
        RunStarted?.Invoke(runner, plan);
        _balloon(4000, $"Расшифровка: {plan.Jobs.Count} задан. на {string.Join(", ", execs.Select(e => e.Tag))} " +
                       $"({(plan.Compare ? "сравнение" : "распределение")}).", ToolTipIcon.Info);
        ShowProgress();

        TranscriptionSummary? summary = null;
        try
        {
            summary = await Task.Run(() => runner.RunAsync(plan, settings, _cts.Token));
            var s = summary;
            string msg = $"Расшифровка завершена за {s.Elapsed.TotalSeconds:0} с: готово {s.Done}" +
                         (s.Failed > 0 ? $", ошибок {s.Failed}" : "") + (s.Skipped > 0 ? $", пропущено {s.Skipped}" : "") + ".";
            if (s.Errors.Count > 0) msg += "\n" + string.Join("\n", s.Errors.Take(3));
            _runOnUi(() => _balloon(6000, msg, s.Failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info));
        }
        catch (OperationCanceledException)
        {
            TranscriptionLog.Write("=== Остановлено пользователем");
            _runOnUi(() => _balloon(4000, "Расшифровка остановлена.", ToolTipIcon.Info));
        }
        catch (Exception ex)
        {
            TranscriptionLog.Write("Ошибка: " + ex);
            _runOnUi(() => _balloon(6000, "Ошибка расшифровки: " + ex.Message, ToolTipIcon.Warning));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusyUi(false);
            _runOnUi(() => RunFinished?.Invoke(summary));
        }
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null || cts.IsCancellationRequested) return;
        _runOnUi(() =>
        {
            if (_stopItem != null) _stopItem.Enabled = false;
            _status("Work Hub — останавливаю расшифровку…");
        });
        try { cts.Cancel(); } catch { }
    }

    private void SetBusyUi(bool on) => _runOnUi(() =>
    {
        if (_lastItem != null) _lastItem.Enabled = !on;
        if (_newItem != null) _newItem.Enabled = !on;
        if (_stopItem != null) _stopItem.Enabled = on;
        if (!on) _status(null);
    });

    private bool EnsureNotBusy()
    {
        if (!IsBusy) return true;
        _balloon(3000, "Расшифровка уже идёт, дождитесь окончания или остановите её.", ToolTipIcon.Info);
        return false;
    }

    /// <summary>Independent copy of the settings for one run (the center window's choices).</summary>
    public AppSettings CloneSettings() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings))!;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
