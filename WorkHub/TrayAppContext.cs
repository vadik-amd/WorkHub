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
    private readonly ScreenRecorder _screen = new();
    private readonly Icon _idleIcon;
    private readonly Icon _recIcon;

    private readonly AppSettings _settings;
    private readonly TranscriptionController _transcription;
    private readonly ScreenTextController _screenText;
    private readonly GpAutoReconnect _gp;

    private ToolStripMenuItem _gpEnableItem = null!;
    private ToolStripMenuItem _gpStatusItem = null!;

    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _screenItem;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _startupItem;

    public TrayAppContext()
    {
        _ = _sync.Handle; // force handle creation on the UI thread for marshalling

        _settings = AppSettings.Load();
        _recorder = new CallRecorder();
        _monitor = new MicMonitor();
        _gp = new GpAutoReconnect(_settings);

        _idleIcon = IconFactory.Create(Color.FromArgb(130, 130, 130));
        _recIcon = IconFactory.Create(Color.FromArgb(220, 40, 40));

        _statusItem = new ToolStripMenuItem("Статус: ожидание") { Enabled = false };
        _startItem = new ToolStripMenuItem("▶  Начать запись", null, (_, _) => StartManual());
        _stopItem = new ToolStripMenuItem("⏹  Остановить запись", null, (_, _) => StopManual()) { Enabled = false };
        _screenItem = new ToolStripMenuItem("Записывать экран вместе с созвоном", null, (_, _) => ToggleRecordScreen())
        {
            CheckOnClick = false,
            Checked = _settings.RecordScreen,
        };
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

        var menu = new ContextMenuStrip();
        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Visible = true,
            Text = "Work Hub — ожидание",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ToggleRecording();
        // --- Transcription submenu: devices (CPU / GPU / NPU), models, modes, transcription center ---
        _transcription = new TranscriptionController(_settings,
            () => _recorder.OutputFolder,
            () => _recorder.IsRecording ? _recorder.CurrentFilePath : null,
            (ms, text, icon) => _tray.ShowBalloonTip(ms, "Whisper", text, icon),
            text => _tray.Text = text ?? (_recorder.IsRecording ? "Work Hub — запись…" : "Work Hub — ожидание"),
            RunOnUi);
        var transcribeMenu = _transcription.BuildMenu();

        // --- Screen text: OCR over the recorded screen video (the shared screen is pixels only) ---
        _screenText = new ScreenTextController(_settings,
            () => _recorder.OutputFolder,
            (ms, text, icon) => _tray.ShowBalloonTip(ms, "Текст с экрана", text, icon),
            text => _tray.Text = text ?? (_recorder.IsRecording ? "Work Hub — запись…" : "Work Hub — ожидание"),
            RunOnUi);
        var screenTextMenu = _screenText.BuildMenu();

        // --- Program auto-launch submenu ---
        var launchConfigItem = new ToolStripMenuItem("Настроить список…", null, (_, _) => OpenLaunchManager());
        var launchNowItem = new ToolStripMenuItem("Запустить программы сейчас", null, (_, _) => LaunchNow());
        var launchMenu = new ToolStripMenuItem("Автозапуск программ");
        launchMenu.DropDownItems.AddRange(new ToolStripItem[] { launchConfigItem, launchNowItem });

        // --- GlobalProtect auto-reconnect submenu ---
        _gpStatusItem = new ToolStripMenuItem("Статус: выключено") { Enabled = false };
        _gpEnableItem = new ToolStripMenuItem("Авто-реконнект при потере интернета", null, (_, _) => ToggleGpAutoReconnect())
        {
            CheckOnClick = false,
            Checked = _settings.GpAutoReconnectEnabled,
        };
        var gpNowItem = new ToolStripMenuItem("Обновить подключение сейчас", null, (_, _) => _ = _gp.ReconnectNowAsync());
        var gpMenu = new ToolStripMenuItem("GlobalProtect");
        gpMenu.DropDownItems.AddRange(new ToolStripItem[] { _gpStatusItem, _gpEnableItem, gpNowItem });

        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _startItem,
            _stopItem,
            _screenItem,
            new ToolStripSeparator(),
            launchMenu,
            gpMenu,
            transcribeMenu,
            screenTextMenu,
            openFolderItem,
            new ToolStripSeparator(),
            _autoItem,
            _startupItem,
            new ToolStripSeparator(),
            exitItem,
        });


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

        _gp.Status += msg => RunOnUi(() => _gpStatusItem.Text = "Статус: " + msg);
        _gp.Notify += (title, msg, warn) => RunOnUi(() =>
            _tray.ShowBalloonTip(5000, title, msg, warn ? ToolTipIcon.Warning : ToolTipIcon.Info));

        // Keep the autostart entry pointing at this exe (the publish folder moves when the
        // target framework changes), then reflect its state in the menu.
        StartupManager.RefreshPathIfEnabled();
        _startupItem.Checked = StartupManager.IsEnabled();

        // Auto-detect enabled by default so calls are captured hands-free.
        SetAutoDetect(true);

        // GlobalProtect auto-reconnect (opt-in).
        if (_settings.GpAutoReconnectEnabled)
            SetGpAutoReconnect(true);

        // On hub startup, launch the configured programs.
        if (_settings.AutoLaunchOnStartup && _settings.AutoLaunch.Count > 0)
            ScheduleStartupLaunch();
    }

    private void ToggleGpAutoReconnect() => SetGpAutoReconnect(!_gpEnableItem.Checked);

    private void SetGpAutoReconnect(bool on)
    {
        _gpEnableItem.Checked = on;
        _settings.GpAutoReconnectEnabled = on;
        _settings.Save();
        if (on) _gp.Enable(_settings);
        else _gp.Disable();
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

    private async void ToggleRecordScreen()
    {
        if (_settings.RecordScreen)
        {
            // Turning it off.
            _settings.RecordScreen = false;
            _settings.Save();
            _screenItem.Checked = false;
            return;
        }

        // Turning it on — make sure ffmpeg is available first (download once if needed).
        if (string.IsNullOrWhiteSpace(_settings.FfmpegExe) || !File.Exists(_settings.FfmpegExe))
        {
            if (!File.Exists(FfmpegInstaller.ExePath))
                _tray.ShowBalloonTip(4000, "Запись экрана",
                    "Скачиваю ffmpeg для записи экрана (~170 МБ, один раз)…", ToolTipIcon.Info);
            _screenItem.Enabled = false;
            try
            {
                await Task.Run(() => FfmpegInstaller.EnsureAsync(
                    _settings, m => RunOnUi(() => _tray.Text = "Work Hub — " + m), CancellationToken.None));
            }
            catch (Exception ex)
            {
                RunOnUi(() =>
                {
                    _tray.ShowBalloonTip(8000, "Запись экрана",
                        "Не удалось получить ffmpeg: " + ex.Message, ToolTipIcon.Warning);
                    _screenItem.Enabled = true;
                });
                return;
            }
            _screenItem.Enabled = true;
        }

        _settings.RecordScreen = true;
        _settings.Save();
        _screenItem.Checked = true;

        // If a call is already being recorded, start the screen capture right now instead
        // of waiting for the next recording to begin.
        if (_recorder.IsRecording && _recorder.CurrentFilePath != null)
        {
            StartScreenIfEnabled(_recorder.CurrentFilePath);
            _tray.ShowBalloonTip(3000, "Запись экрана",
                "Включено. Захват экрана начат для текущего созвона (основной монитор).", ToolTipIcon.Info);
        }
        else
        {
            _tray.ShowBalloonTip(3000, "Запись экрана",
                "Включено. Экран будет писаться вместе с созвоном (основной монитор).", ToolTipIcon.Info);
        }
    }

    private void OnRecordingState(bool recording, string file)
    {
        _tray.Icon = recording ? _recIcon : _idleIcon;
        _statusItem.Text = recording ? "Статус: идёт запись…" : "Статус: ожидание";
        _tray.Text = recording ? "Work Hub — запись…" : "Work Hub — ожидание";
        _startItem.Enabled = !recording;
        _stopItem.Enabled = recording;

        if (recording)
        {
            _tray.ShowBalloonTip(2500, "Work Hub",
                "Запись началась:\n" + Path.GetFileName(file), ToolTipIcon.Info);
            StartScreenIfEnabled(file);
        }
        else
        {
            _tray.ShowBalloonTip(2500, "Work Hub",
                "Запись сохранена:\n" + Path.GetFileName(file), ToolTipIcon.Info);
            StopScreenIfRunning();
        }
    }

    private void StartScreenIfEnabled(string wavPath)
    {
        if (!_settings.RecordScreen || _screen.IsRecording) return;
        if (string.IsNullOrWhiteSpace(_settings.FfmpegExe) || !File.Exists(_settings.FfmpegExe))
        {
            _tray.ShowBalloonTip(4000, "Запись экрана",
                "ffmpeg не установлен — экран не пишется. Переключите пункт «Записывать экран…».",
                ToolTipIcon.Warning);
            return;
        }

        string mp4 = Path.ChangeExtension(wavPath, ".mp4");
        Task.Run(() =>
        {
            bool ok = _screen.Start(mp4, wavPath, _settings);
            if (!ok)
                RunOnUi(() => _tray.ShowBalloonTip(4000, "Запись экрана",
                    "Не удалось запустить запись экрана.", ToolTipIcon.Warning));
        });
    }

    private void StopScreenIfRunning()
    {
        if (!_screen.IsRecording) return;
        Task.Run(async () =>
        {
            string? mp4 = await _screen.StopAsync();
            RunOnUi(() =>
            {
                if (mp4 != null)
                    _tray.ShowBalloonTip(3000, "Запись экрана",
                        "Видео сохранено:\n" + Path.GetFileName(mp4), ToolTipIcon.Info);
            });

            // Optionally read the screen right away, while nothing else is running.
            if (mp4 != null && _settings.ScreenTextAfterCall &&
                _settings.ScreenModeParsed() != ScreenMode.Off)
                await _screenText.RunAsync(new[] { mp4 });
        });
    }

    private void ExitApp()
    {
        try { _monitor.Disable(); } catch { }
        try { _monitor.Dispose(); } catch { }
        try { _screenText.Stop(); } catch { }
        try { _gp.Dispose(); } catch { }
        try { _recorder.Dispose(); } catch { }
        try { if (_screen.IsRecording) _screen.StopAsync().GetAwaiter().GetResult(); } catch { }

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
