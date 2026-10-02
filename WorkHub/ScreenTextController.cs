using System.Diagnostics;

namespace WorkHub;

/// <summary>
/// Everything screen-text for the tray: the "Текст с экрана" menu (mode, OCR languages,
/// the optional local VLM model), running <see cref="ScreenTextExtractor"/> over one or many
/// call videos, progress in the tray tooltip, STOP.
/// </summary>
public sealed class ScreenTextController
{
    private readonly AppSettings _settings;
    private readonly Func<string> _outputFolder;
    private readonly Action<int, string, ToolTipIcon> _balloon;
    private readonly Action<string?> _status;   // tray tooltip; null = back to idle
    private readonly Action<Action> _runOnUi;

    private CancellationTokenSource? _cts;

    private ToolStripMenuItem? _latestItem, _newItem, _pickItem, _stopItem;
    private readonly Dictionary<string, ToolStripMenuItem> _modeItems = new();

    public ScreenTextController(AppSettings settings, Func<string> outputFolder,
        Action<int, string, ToolTipIcon> balloon, Action<string?> status, Action<Action> runOnUi)
    {
        _settings = settings;
        _outputFolder = outputFolder;
        _balloon = balloon;
        _status = status;
        _runOnUi = runOnUi;
    }

    public bool IsBusy => _cts != null;

    // ------------------------------------------------------------------ menu

    public ToolStripMenuItem BuildMenu()
    {
        _latestItem = new ToolStripMenuItem("Распознать последнюю запись экрана", null,
            (_, _) => _ = RunLatestAsync());
        _newItem = new ToolStripMenuItem("Распознать все новые записи экрана", null,
            (_, _) => _ = RunNewAsync());
        _pickItem = new ToolStripMenuItem("Выбрать MP4…", null, (_, _) => _ = RunPickedAsync());
        _stopItem = new ToolStripMenuItem("⏹ Остановить распознавание", null, (_, _) => Stop()) { Enabled = false };

        var modeMenu = new ToolStripMenuItem("Что делать с записью экрана");
        foreach (var (key, label) in new[]
                 {
                     ("off", "Ничего (выключено)"),
                     ("ocr", "OCR: текстовый timeline"),
                     ("ocr+keyframes", "OCR + сохранять ключевые кадры (для Claude) — рекомендуется"),
                     ("vlm", "OCR + локальная VLM-модель (нужна модель на диске)"),
                 })
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => SetMode(key));
            _modeItems[key] = item;
            modeMenu.DropDownItems.Add(item);
        }
        UpdateModeChecks();

        var afterCall = new ToolStripMenuItem("Распознавать сразу после созвона", null, (_, _) => ToggleAfterCall())
        {
            Checked = _settings.ScreenTextAfterCall,
        };

        var langMenu = new ToolStripMenuItem("Языки OCR");
        langMenu.DropDownItems.Add(new ToolStripMenuItem("…"));
        langMenu.DropDownOpening += (_, _) => FillLanguageMenu(langMenu);

        var vlmMenu = new ToolStripMenuItem("Локальная VLM-модель");
        vlmMenu.DropDownItems.Add(new ToolStripMenuItem("…"));
        vlmMenu.DropDownOpening += (_, _) => FillVlmMenu(vlmMenu);

        var root = new ToolStripMenuItem("Текст с экрана записи");
        root.DropDownOpening += (_, _) => UpdateRootLabel(root);
        root.DropDownItems.AddRange(new ToolStripItem[]
        {
            _latestItem, _newItem, _pickItem, _stopItem,
            new ToolStripSeparator(),
            modeMenu, afterCall, langMenu, vlmMenu,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Открыть папку записей", null, (_, _) => OpenFolder()),
            new ToolStripMenuItem("Открыть лог", null, (_, _) => OpenLog()),
        });
        UpdateRootLabel(root);
        return root;
    }

    private void UpdateRootLabel(ToolStripMenuItem root)
    {
        string mode = _settings.ScreenModeParsed() switch
        {
            ScreenMode.Ocr => "OCR",
            ScreenMode.OcrKeyFrames => "OCR + кадры",
            ScreenMode.Vlm => "OCR + VLM",
            _ => "выкл.",
        };
        root.Text = "Текст с экрана записи — " + mode;
    }

    private void FillLanguageMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var available = WindowsOcrEngine.AvailableLanguageNames();
        if (available.Count == 0)
        {
            menu.DropDownItems.Add(new ToolStripMenuItem(
                "В Windows нет языков распознавания текста") { Enabled = false });
        }
        foreach (var (tag, name) in available)
        {
            bool on = _settings.ScreenOcrLanguages.Count == 0 ||
                      _settings.ScreenOcrLanguages.Any(t => Matches(t, tag));
            var item = new ToolStripMenuItem($"{name} ({tag})", null, (_, _) => ToggleLanguage(tag)) { Checked = on };
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem(
            "Языки OCR ставятся в Параметрах Windows: Время и язык → Язык и регион →\n" +
            "нужный язык → Параметры языка → Оптическое распознавание текста.") { Enabled = false });
    }

    /// <summary>"ru" and "ru-RU" mean the same recognizer.</summary>
    private static bool Matches(string wanted, string tag) =>
        tag.StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ||
        wanted.StartsWith(tag.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    private void ToggleLanguage(string tag)
    {
        var list = _settings.ScreenOcrLanguages;
        if (list.Count == 0) list.AddRange(WindowsOcrEngine.AvailableLanguages());

        var existing = list.Where(t => Matches(t, tag)).ToList();
        if (existing.Count > 0)
        {
            if (list.Count - existing.Count == 0)
            {
                _balloon(3000, "Хотя бы один язык OCR должен остаться включённым.", ToolTipIcon.Info);
                return;
            }
            foreach (var t in existing) list.Remove(t);
        }
        else list.Add(tag);

        _settings.Save();
        _balloon(2500, "Языки OCR: " + string.Join(", ", list), ToolTipIcon.Info);
    }

    private void FillVlmMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        string? why = ScreenVlmEngine.Unavailable(_settings);
        menu.DropDownItems.Add(new ToolStripMenuItem(
            why == null
                ? "Готова: " + Path.GetFileName(_settings.ScreenVlmModelDir)
                : "Не готова — " + why) { Enabled = false });
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Скачать модель…", null, (_, _) => OpenVlmSetup()));

        // The models themselves, so a downloaded one can be switched to without the window.
        foreach (var (repo, label) in OpenVinoInstaller.KnownVlmModels)
        {
            string dir = OpenVinoInstaller.VlmModelDirFor(repo);
            bool downloaded = OpenVinoInstaller.IsVlmModelDownloaded(repo);
            var item = new ToolStripMenuItem(label + (downloaded ? "" : "  — скачать"), null,
                (_, _) => SelectVlmModel(repo))
            {
                Checked = ModelCatalog.SamePath(_settings.ScreenVlmModelDir, dir),
            };
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Указать свою папку модели (OpenVINO IR)…", null,
            (_, _) => PickVlmModel()));

        var deviceMenu = new ToolStripMenuItem("Устройство");
        foreach (var dev in new[] { "AUTO", "NPU", "GPU", "CPU" })
        {
            var item = new ToolStripMenuItem(dev == "AUTO" ? "AUTO (NPU → GPU → CPU)" : dev, null,
                (_, _) => { _settings.ScreenVlmDevice = dev; _settings.Save(); })
            {
                Checked = string.Equals(dev, _settings.ScreenVlmDevice, StringComparison.OrdinalIgnoreCase),
            };
            deviceMenu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(deviceMenu);
    }

    /// <summary>The model window: pick one from the list and download it, no Python needed.</summary>
    private void OpenVlmSetup()
    {
        using var form = new ScreenVlmSetupForm(_settings) { TopMost = true };
        form.ShowDialog();
    }

    /// <summary>Switches to one of the ready-made models, downloading it first if needed.</summary>
    private void SelectVlmModel(string repo)
    {
        if (!OpenVinoInstaller.IsVlmModelDownloaded(repo))
        {
            OpenVlmSetup();
            return;
        }
        _settings.ScreenVlmModelDir = OpenVinoInstaller.VlmModelDirFor(repo);
        _settings.Save();
        _balloon(3000, "Модель выбрана: " + Path.GetFileName(_settings.ScreenVlmModelDir), ToolTipIcon.Info);
    }

    private void PickVlmModel()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Папка с OpenVINO IR моделью (файлы *.xml / *.bin)",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_settings.ScreenVlmModelDir)
                ? _settings.ScreenVlmModelDir
                : OpenVinoInstaller.ModelsDir,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;

        _settings.ScreenVlmModelDir = dialog.SelectedPath;
        _settings.Save();
        string? why = ScreenVlmEngine.Unavailable(_settings);
        _balloon(5000, why == null
            ? "Модель принята: " + Path.GetFileName(dialog.SelectedPath)
            : "Модель не годится — " + why, why == null ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    private void SetMode(string mode)
    {
        _settings.ScreenUnderstanding = mode;
        _settings.Save();
        UpdateModeChecks();

        // Choosing the VLM mode without a model is pointless — offer to fetch one right away.
        if (mode == "vlm" && ScreenVlmEngine.Unavailable(_settings) != null)
        {
            var answer = MessageBox.Show(
                "Для этого режима нужна локальная VLM-модель (готовая, ~0.8–3 ГБ).\n\n" +
                "Скачать её сейчас? Без модели будет работать только OCR.",
                "Текст с экрана", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer == DialogResult.Yes) OpenVlmSetup();
        }
        else if (mode != "off")
            _balloon(3000, "Режим: " + mode + ". Запускается из этого же подменю или сразу после созвона.",
                ToolTipIcon.Info);
    }

    private void UpdateModeChecks()
    {
        string current = _settings.ScreenUnderstanding?.ToLowerInvariant() ?? "off";
        foreach (var (key, item) in _modeItems) item.Checked = key == current;
    }

    private void ToggleAfterCall()
    {
        _settings.ScreenTextAfterCall = !_settings.ScreenTextAfterCall;
        // Switching it on with the feature off would do nothing — pick the useful default.
        if (_settings.ScreenTextAfterCall && _settings.ScreenModeParsed() == ScreenMode.Off)
        {
            _settings.ScreenUnderstanding = "ocr+keyframes";
            UpdateModeChecks();
        }
        _settings.Save();
        _balloon(3000, _settings.ScreenTextAfterCall
            ? "Текст с экрана будет распознаваться сразу после созвона."
            : "Автоматическое распознавание после созвона выключено.", ToolTipIcon.Info);
    }

    // ------------------------------------------------------------------ running

    /// <summary>Screen videos of calls, newest first.</summary>
    public List<string> Videos()
    {
        string folder = _outputFolder();
        if (!Directory.Exists(folder)) return new List<string>();
        return Directory.EnumerateFiles(folder, "call_*.mp4")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    private async Task RunLatestAsync()
    {
        var mp4 = Videos().FirstOrDefault();
        if (mp4 == null)
        {
            _balloon(5000, "Нет ни одной записи экрана (call_*.mp4). Включите «Записывать экран вместе с созвоном».",
                ToolTipIcon.Info);
            return;
        }
        await RunAsync(new[] { mp4 });
    }

    private async Task RunNewAsync()
    {
        var todo = Videos().Where(v => !ScreenTextPaths.Exists(v)).ToList();
        if (todo.Count == 0)
        {
            _balloon(3000, "Все записи экрана уже распознаны.", ToolTipIcon.Info);
            return;
        }
        await RunAsync(todo);
    }

    private async Task RunPickedAsync()
    {
        string folder = _outputFolder();
        string? picked = null;
        using (var dialog = new OpenFileDialog
               {
                   Title = "Запись экрана созвона",
                   Filter = "Видео (*.mp4;*.mkv)|*.mp4;*.mkv|Все файлы|*.*",
                   InitialDirectory = Directory.Exists(folder) ? folder : null,
               })
        {
            if (dialog.ShowDialog() == DialogResult.OK) picked = dialog.FileName;
        }
        if (picked != null) await RunAsync(new[] { picked });
    }

    /// <summary>Runs the extraction for the given videos, one after another.</summary>
    public async Task RunAsync(IReadOnlyList<string> videos)
    {
        if (IsBusy)
        {
            _balloon(3000, "Распознавание экрана уже идёт — дождитесь окончания или остановите его.",
                ToolTipIcon.Info);
            return;
        }
        if (videos.Count == 0) return;

        if (string.IsNullOrWhiteSpace(_settings.FfmpegExe) || !File.Exists(_settings.FfmpegExe))
        {
            _balloon(6000, "ffmpeg не установлен — включите «Записывать экран вместе с созвоном», " +
                           "он его скачает один раз.", ToolTipIcon.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetBusyUi(true);

        int done = 0, failed = 0, frames = 0, blocks = 0;
        var started = DateTime.Now;
        TranscriptionLog.Write($"=== Текст с экрана: {videos.Count} видео, режим {_settings.ScreenUnderstanding}");
        _balloon(4000, $"Распознаю текст с экрана: {videos.Count} видео.", ToolTipIcon.Info);

        try
        {
            for (int i = 0; i < videos.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string mp4 = videos[i];
                string name = Path.GetFileName(mp4);
                int index = i + 1;

                var extractor = new ScreenTextExtractor();
                extractor.Log += TranscriptionLog.Write;
                extractor.Progress += (d, total) => _runOnUi(() =>
                    _status(Truncate($"Work Hub — экран {index}/{videos.Count}: кадр {d}/{total}", 63)));
                _runOnUi(() => _status(Truncate($"Work Hub — экран {index}/{videos.Count}: {name}", 63)));

                try
                {
                    var result = await Task.Run(() => extractor.RunAsync(mp4, _settings, ct), ct);
                    if (result == null)
                    {
                        failed++;
                        continue;
                    }
                    done++;
                    frames += result.KeyFrames;
                    blocks += result.Blocks;
                    if (videos.Count <= 3)
                        _runOnUi(() => _balloon(5000,
                            $"{Path.GetFileName(result.TxtPath)}: кадров {result.KeyFrames}, " +
                            $"блоков {result.Blocks}, символов {result.Chars}.", ToolTipIcon.Info));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    TranscriptionLog.Write($"Экран: {name} — ошибка: {ex.Message}");
                    _runOnUi(() => _balloon(6000, $"{name}: {ex.Message}", ToolTipIcon.Warning));
                }
            }

            var elapsed = DateTime.Now - started;
            string msg = $"Текст с экрана готов за {elapsed.TotalSeconds:0} с: видео {done}" +
                         (failed > 0 ? $", без результата {failed}" : "") +
                         $", ключевых кадров {frames}, блоков {blocks}.";
            TranscriptionLog.Write("=== " + msg);
            _runOnUi(() => _balloon(6000, msg, failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info));
        }
        catch (OperationCanceledException)
        {
            TranscriptionLog.Write("=== Текст с экрана: остановлено пользователем");
            _runOnUi(() => _balloon(4000, "Распознавание экрана остановлено.", ToolTipIcon.Info));
        }
        catch (Exception ex)
        {
            TranscriptionLog.Write("Текст с экрана — ошибка: " + ex);
            _runOnUi(() => _balloon(6000, "Ошибка распознавания экрана: " + ex.Message, ToolTipIcon.Warning));
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            SetBusyUi(false);
        }
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null || cts.IsCancellationRequested) return;
        _runOnUi(() =>
        {
            if (_stopItem != null) _stopItem.Enabled = false;
            _status("Work Hub — останавливаю распознавание экрана…");
        });
        try { cts.Cancel(); } catch { }
    }

    private void SetBusyUi(bool on) => _runOnUi(() =>
    {
        if (_latestItem != null) _latestItem.Enabled = !on;
        if (_newItem != null) _newItem.Enabled = !on;
        if (_pickItem != null) _pickItem.Enabled = !on;
        if (_stopItem != null) _stopItem.Enabled = on;
        if (!on) _status(null);
    });

    private void OpenFolder()
    {
        try
        {
            string folder = _outputFolder();
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch { /* ignore */ }
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
