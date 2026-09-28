using System.Diagnostics;

namespace WorkHub;

/// <summary>
/// The always-on tray application: an icon in the notification area with a menu to
/// start/stop recording manually, toggle mic auto-detection, toggle autostart, and exit.
/// </summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly Control _sync = new();
    private readonly NotifyIcon _tray;
    private readonly CallRecorder _recorder;
    private readonly MicMonitor _monitor;
    private readonly Icon _idleIcon;
    private readonly Icon _recIcon;

    private readonly AppSettings _settings;
    private readonly Transcriber _transcriber = new();

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _startupItem;

    private readonly ToolStripMenuItem _transcribeLastItem;
    private readonly ToolStripMenuItem _transcribeNewItem;
    private readonly ToolStripMenuItem _modelMenu;
    private readonly ToolStripMenuItem _langMenu;
    private readonly ToolStripMenuItem _langRuItem;
    private readonly ToolStripMenuItem _langEnItem;
    private volatile bool _transcribing;

    private static readonly string[] Models = { "base", "small", "medium", "large-v3" };

    public TrayAppContext()
    {
        _ = _sync.Handle; // force handle creation on the UI thread for marshalling

        _settings = AppSettings.Load();
        _recorder = new CallRecorder();
        _monitor = new MicMonitor();

        _idleIcon = IconFactory.Create(Color.FromArgb(130, 130, 130));
        _recIcon = IconFactory.Create(Color.FromArgb(220, 40, 40));

        _statusItem = new ToolStripMenuItem("Статус: ожидание") { Enabled = false };
        _startItem = new ToolStripMenuItem("▶  Начать запись", null, (_, _) => StartManual());
        _stopItem = new ToolStripMenuItem("⏹  Остановить запись", null, (_, _) => StopManual()) { Enabled = false };
        _autoItem = new ToolStripMenuItem("Авто-запись по микрофону", null, (_, _) => ToggleAutoDetect())
        {
            CheckOnClick = false,
        };
        _startupItem = new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) => ToggleStartup())
        {
            CheckOnClick = false,
        };
        var openFolderItem = new ToolStripMenuItem("Открыть папку записей", null, (_, _) => OpenFolder());
        var exitItem = new ToolStripMenuItem("Выход", null, (_, _) => ExitApp());

        // --- Whisper transcription submenu ---
        _transcribeLastItem = new ToolStripMenuItem("Расшифровать последнюю запись", null, (_, _) => TranscribeLatest());
        _transcribeNewItem = new ToolStripMenuItem("Расшифровать все новые", null, (_, _) => TranscribeNew());

        _modelMenu = new ToolStripMenuItem("Модель");
        foreach (var model in Models)
        {
            var captured = model;
            var mi = new ToolStripMenuItem(model, null, (_, _) => SetModel(captured))
            {
                Checked = string.Equals(model, _settings.WhisperModel, StringComparison.OrdinalIgnoreCase),
            };
            _modelMenu.DropDownItems.Add(mi);
        }

        _langMenu = new ToolStripMenuItem("Языки транскрипции");
        _langRuItem = new ToolStripMenuItem("Русский") { CheckOnClick = true, Checked = _settings.TranscribeRussian };
        _langRuItem.CheckedChanged += (_, _) => { _settings.TranscribeRussian = _langRuItem.Checked; _settings.Save(); };
        _langEnItem = new ToolStripMenuItem("English") { CheckOnClick = true, Checked = _settings.TranscribeEnglish };
        _langEnItem.CheckedChanged += (_, _) => { _settings.TranscribeEnglish = _langEnItem.Checked; _settings.Save(); };
        _langMenu.DropDownItems.AddRange(new ToolStripItem[] { _langRuItem, _langEnItem });

        var checkWhisperItem = new ToolStripMenuItem("Проверить установку Whisper", null, (_, _) => CheckWhisper());
        var setupWhisperItem = new ToolStripMenuItem("Установить / настроить Whisper…", null, (_, _) => OpenDependencySetup());

        // --- Program auto-launch submenu ---
        var launchConfigItem = new ToolStripMenuItem("Настроить список…", null, (_, _) => OpenLaunchManager());
        var launchNowItem = new ToolStripMenuItem("Запустить программы сейчас", null, (_, _) => LaunchNow());
        var launchMenu = new ToolStripMenuItem("Автозапуск программ");
        launchMenu.DropDownItems.AddRange(new ToolStripItem[] { launchConfigItem, launchNowItem });

        var transcribeMenu = new ToolStripMenuItem("Расшифровка (Whisper)");
        transcribeMenu.DropDownItems.AddRange(new ToolStripItem[]
        {
            _transcribeLastItem,
            _transcribeNewItem,
            new ToolStripSeparator(),
            _modelMenu,
            _langMenu,
            checkWhisperItem,
            setupWhisperItem,
        });

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _startItem,
            _stopItem,
            new ToolStripSeparator(),
            launchMenu,
            transcribeMenu,
            openFolderItem,
            new ToolStripSeparator(),
            _autoItem,
            _startupItem,
            new ToolStripSeparator(),
            exitItem,
        });

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Visible = true,
            Text = "Work Hub — ожидание",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ToggleRecording();

        _recorder.RecordingStarted += (_, file) => RunOnUi(() => OnRecordingState(true, file));
        _recorder.RecordingStopped += (_, file) => RunOnUi(() => OnRecordingState(false, file));
        _recorder.Error += (_, msg) => RunOnUi(() =>
            _tray.ShowBalloonTip(4000, "Work Hub", msg, ToolTipIcon.Warning));

        _monitor.CallStarted += (_, _) => RunOnUi(() =>
        {
            if (!_recorder.IsRecording) _recorder.Start();
        });
        _monitor.CallStopped += (_, _) => RunOnUi(() =>
        {
            if (_recorder.IsRecording) _recorder.Stop();
        });

        _startupItem.Checked = StartupManager.IsEnabled();

        // Auto-detect enabled by default so calls are captured hands-free.
        SetAutoDetect(true);

        // On hub startup, launch the configured programs.
        if (_settings.AutoLaunchOnStartup && _settings.AutoLaunch.Count > 0)
            ScheduleStartupLaunch();
    }

    private void OpenLaunchManager()
    {
        using var form = new LaunchManagerForm(_settings) { TopMost = true };
        form.ShowDialog();
    }

    private void LaunchNow()
    {
        var (launched, failed, errors) = Launcher.LaunchAll(_settings.AutoLaunch);
        ShowLaunchResult("Запуск программ", launched, failed, errors);
    }

    private void ScheduleStartupLaunch()
    {
        Task.Run(async () =>
        {
            // Small delay so the shell/tray settles before we spawn UAC prompts.
            await Task.Delay(1500);
            var (launched, failed, errors) = Launcher.LaunchAll(_settings.AutoLaunch);
            RunOnUi(() => ShowLaunchResult("Автозапуск", launched, failed, errors));
        });
    }

    private void ShowLaunchResult(string title, int launched, int failed, List<string> errors)
    {
        if (launched == 0 && failed == 0) return;
        var msg = $"Запущено: {launched}, ошибок: {failed}";
        if (errors.Count > 0)
            msg += "\n" + string.Join("\n", errors);
        _tray.ShowBalloonTip(5000, "Work Hub — " + title, msg,
            failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void RunOnUi(Action action)
    {
        if (_sync.IsDisposed) return;
        if (_sync.InvokeRequired) _sync.BeginInvoke(action);
        else action();
    }

    private void StartManual()
    {
        if (_recorder.IsRecording) return;
        _recorder.Start();
    }

    private void StopManual()
    {
        if (!_recorder.IsRecording) return;
        _recorder.Stop();
    }

    private void ToggleRecording()
    {
        if (_recorder.IsRecording) StopManual();
        else StartManual();
    }

    private void ToggleAutoDetect() => SetAutoDetect(!_autoItem.Checked);

    private void SetAutoDetect(bool on)
    {
        _autoItem.Checked = on;
        if (on) _monitor.Enable();
        else _monitor.Disable();
    }

    private void ToggleStartup()
    {
        bool newState = !_startupItem.Checked;
        try
        {
            StartupManager.Set(newState);
            _startupItem.Checked = StartupManager.IsEnabled();
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(4000, "Work Hub",
                "Не удалось изменить автозапуск: " + ex.Message, ToolTipIcon.Warning);
        }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_recorder.OutputFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = _recorder.OutputFolder,
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }

    private void SetModel(string model)
    {
        _settings.WhisperModel = model;
        _settings.Save();
        foreach (ToolStripMenuItem mi in _modelMenu.DropDownItems)
            mi.Checked = string.Equals(mi.Text, model, StringComparison.OrdinalIgnoreCase);
    }

    private void CheckWhisper()
    {
        Task.Run(() =>
        {
            var engine = Transcriber.ResolveEngine(_settings);
            bool ffmpeg = engine == WhisperEngine.OpenAi && Transcriber.IsFfmpegAvailable();
            RunOnUi(() =>
            {
                switch (engine)
                {
                    case WhisperEngine.WhisperCpp:
                        _tray.ShowBalloonTip(5000, "Whisper",
                            "Портативный whisper.cpp найден — всё готово к расшифровке.", ToolTipIcon.Info);
                        break;
                    case WhisperEngine.OpenAi:
                        _tray.ShowBalloonTip(6000, "Whisper",
                            ffmpeg
                                ? "openai-whisper найден, ffmpeg на месте — всё готово."
                                : "openai-whisper найден, но НЕ найден ffmpeg (winget install Gyan.FFmpeg).",
                            ffmpeg ? ToolTipIcon.Info : ToolTipIcon.Warning);
                        break;
                    default:
                        OpenDependencySetup();
                        break;
                }
            });
        });
    }

    private bool OpenDependencySetup()
    {
        using var form = new DependencySetupForm(_settings) { TopMost = true };
        return form.ShowDialog() == DialogResult.OK;
    }

    private async Task<bool> EnsureEngineReadyAsync()
    {
        var engine = await Task.Run(() => Transcriber.ResolveEngine(_settings));
        if (engine != WhisperEngine.None) return true;
        // No engine: offer the built-in setup, then re-check.
        return OpenDependencySetup() && Transcriber.ResolveEngine(_settings) != WhisperEngine.None;
    }

    private async void TranscribeLatest()
    {
        if (!EnsureNotBusy() || !EnsureLanguagesSelected()) return;
        var wav = GetWavCandidates()
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (wav == null)
        {
            _tray.ShowBalloonTip(3000, "Whisper", "Нет записей для расшифровки.", ToolTipIcon.Info);
            return;
        }
        if (!await EnsureEngineReadyAsync()) return;
        await RunTranscriptionAsync(new[] { wav }, overwrite: true);
    }

    private async void TranscribeNew()
    {
        if (!EnsureNotBusy() || !EnsureLanguagesSelected()) return;
        var langs = _settings.EnabledLanguages();
        var wavs = GetWavCandidates()
            .Where(w => langs.Any(l => !File.Exists(TxtPathFor(w, l))))
            .OrderBy(File.GetLastWriteTimeUtc)
            .ToArray();
        if (wavs.Length == 0)
        {
            _tray.ShowBalloonTip(3000, "Whisper", "Новых записей без расшифровки нет.", ToolTipIcon.Info);
            return;
        }
        if (!await EnsureEngineReadyAsync()) return;
        await RunTranscriptionAsync(wavs, overwrite: false);
    }

    private bool EnsureLanguagesSelected()
    {
        if (_settings.EnabledLanguages().Count > 0) return true;
        _tray.ShowBalloonTip(4000, "Whisper",
            "Включите хотя бы один язык в «Расшифровка → Языки транскрипции».", ToolTipIcon.Warning);
        return false;
    }

    private static string TxtPathFor(string wav, string lang) =>
        Path.Combine(Path.GetDirectoryName(wav)!, Path.GetFileNameWithoutExtension(wav) + "_" + lang + ".txt");

    /// <summary>All call WAVs, excluding the file currently being recorded.</summary>
    private IEnumerable<string> GetWavCandidates()
    {
        if (!Directory.Exists(_recorder.OutputFolder))
            return Enumerable.Empty<string>();

        string? active = _recorder.IsRecording ? _recorder.CurrentFilePath : null;
        return Directory.EnumerateFiles(_recorder.OutputFolder, "call_*.wav")
            .Where(w => active == null ||
                        !string.Equals(Path.GetFullPath(w), Path.GetFullPath(active),
                            StringComparison.OrdinalIgnoreCase));
    }

    private bool EnsureNotBusy()
    {
        if (_transcribing)
        {
            _tray.ShowBalloonTip(3000, "Whisper", "Расшифровка уже идёт, дождитесь окончания.", ToolTipIcon.Info);
            return false;
        }
        return true;
    }

    private async Task RunTranscriptionAsync(IReadOnlyList<string> wavs, bool overwrite)
    {
        var langs = _settings.EnabledLanguages();
        var jobs = new List<(string wav, string lang)>();
        foreach (var wav in wavs)
            foreach (var lang in langs)
                if (overwrite || !File.Exists(TxtPathFor(wav, lang)))
                    jobs.Add((wav, lang));

        if (jobs.Count == 0)
        {
            _tray.ShowBalloonTip(3000, "Whisper",
                "Всё уже расшифровано для выбранных языков.", ToolTipIcon.Info);
            return;
        }

        SetTranscribing(true);
        _tray.ShowBalloonTip(4000, "Whisper",
            $"Расшифровка началась: {jobs.Count} задан. (языки: {string.Join(", ", langs)}, модель {_settings.WhisperModel}). Это может занять время…",
            ToolTipIcon.Info);

        try
        {
            int done = 0;
            foreach (var (wav, lang) in jobs)
            {
                int idx = done + 1;
                RunOnUi(() => _tray.Text = $"Work Hub — расшифровка {idx}/{jobs.Count} ({lang})…");
                string outTxt = TxtPathFor(wav, lang);
                await Task.Run(() =>
                    _transcriber.TranscribeAsync(wav, lang, outTxt, _settings, null, CancellationToken.None));
                done++;
                RunOnUi(() => _tray.ShowBalloonTip(2500, "Whisper",
                    "Готово: " + Path.GetFileName(outTxt), ToolTipIcon.Info));
            }

            RunOnUi(() => _tray.ShowBalloonTip(4000, "Whisper",
                $"Расшифровка завершена ({done}/{jobs.Count}).", ToolTipIcon.Info));
        }
        catch (Transcriber.WhisperNotFoundException ex)
        {
            RunOnUi(() => _tray.ShowBalloonTip(8000, "Whisper",
                ex.Message + " Откройте «Установить / настроить Whisper…».", ToolTipIcon.Warning));
        }
        catch (Exception ex)
        {
            RunOnUi(() => _tray.ShowBalloonTip(6000, "Whisper",
                "Ошибка расшифровки: " + ex.Message, ToolTipIcon.Warning));
        }
        finally
        {
            SetTranscribing(false);
        }
    }

    private void SetTranscribing(bool on)
    {
        _transcribing = on;
        RunOnUi(() =>
        {
            _transcribeLastItem.Enabled = !on;
            _transcribeNewItem.Enabled = !on;
            if (!on && !_recorder.IsRecording)
                _tray.Text = "Work Hub — ожидание";
        });
    }

    private void OnRecordingState(bool recording, string file)
    {
        _tray.Icon = recording ? _recIcon : _idleIcon;
        _statusItem.Text = recording ? "Статус: идёт запись…" : "Статус: ожидание";
        _tray.Text = recording ? "Work Hub — запись…" : "Work Hub — ожидание";
        _startItem.Enabled = !recording;
        _stopItem.Enabled = recording;

        if (recording)
            _tray.ShowBalloonTip(2500, "Work Hub",
                "Запись началась:\n" + Path.GetFileName(file), ToolTipIcon.Info);
        else
            _tray.ShowBalloonTip(2500, "Work Hub",
                "Запись сохранена:\n" + Path.GetFileName(file), ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        try { _monitor.Disable(); } catch { }
        try { _monitor.Dispose(); } catch { }
        try { _recorder.Dispose(); } catch { }

        _tray.Visible = false;
        _tray.Dispose();

        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sync.Dispose();
        }
        base.Dispose(disposing);
    }
}
