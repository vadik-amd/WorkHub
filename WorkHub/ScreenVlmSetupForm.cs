using System.Diagnostics;

namespace WorkHub;

/// <summary>
/// Downloads a ready-made vision-language IR model for the screen-text VLM layer, the same way
/// the Whisper models are downloaded: pick one from the list, press the button, done — no
/// optimum-cli, no conversion, no Python commands. A model folder the user already has can be
/// pointed at instead. Returns DialogResult.OK once a usable model is set.
/// </summary>
public sealed class ScreenVlmSetupForm : Form
{
    private readonly AppSettings _settings;
    private readonly ListBox _models;
    private readonly TextBox _log;
    private readonly ProgressBar _progress;
    private readonly Button _downloadBtn;
    private readonly Button _manualBtn;
    private readonly Button _pageBtn;
    private readonly Button _closeBtn;
    private CancellationTokenSource? _cts;

    public ScreenVlmSetupForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Work Hub — модель для описания экрана (VLM)";
        Width = 760;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(680, 520);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 112,
            Padding = new Padding(12, 12, 12, 4),
            Text =
                "Локальная vision-language модель добавляет к OCR описание каждого ключевого " +
                "кадра: какое приложение показано и что на нём происходит. Работает оффлайн на " +
                "NPU / Arc / CPU (порядок NPU → GPU → CPU выбирается автоматически).\r\n\r\n" +
                "Модели готовые, в формате OpenVINO IR — конвертировать ничего не нужно. " +
                "Скачиваются в папку профиля, без прав администратора.",
        };

        _models = new ListBox { Dock = DockStyle.Top, Height = 116, IntegralHeight = false };
        RefreshModelList();

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 8,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0,
            Visible = false,
        };

        _log = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window,
        };

        _downloadBtn = new Button { Text = "Скачать и подключить", AutoSize = true };
        _downloadBtn.Click += async (_, _) => await DownloadAsync();
        _manualBtn = new Button { Text = "Указать папку модели…", AutoSize = true };
        _manualBtn.Click += (_, _) => SetManually();
        _pageBtn = new Button { Text = "Страница модели (браузер)", AutoSize = true };
        _pageBtn.Click += (_, _) => OpenUrl("https://huggingface.co/" + SelectedRepo());
        _closeBtn = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
        };
        buttons.Controls.AddRange(new Control[] { _downloadBtn, _manualBtn, _pageBtn, _closeBtn });

        Controls.Add(_log);
        Controls.Add(_progress);
        Controls.Add(_models);
        Controls.Add(info);
        Controls.Add(buttons);

        CancelButton = _closeBtn;
        FormClosing += (_, _) =>
        {
            if (_cts is { IsCancellationRequested: false } && IsBusy) _cts.Cancel();
        };

        if (ScreenVlmEngine.Unavailable(_settings) is { } why)
            AppendLog("Сейчас VLM-слой не готов: " + why);
        else
            AppendLog("Текущая модель: " + _settings.ScreenVlmModelDir);
    }

    private bool IsBusy => _downloadBtn is { Enabled: false };

    private void RefreshModelList()
    {
        int selected = Math.Max(0, _models.SelectedIndex);
        _models.Items.Clear();
        foreach (var (repo, label) in OpenVinoInstaller.KnownVlmModels)
        {
            bool downloaded = OpenVinoInstaller.IsVlmModelDownloaded(repo);
            bool active = ModelCatalog.SamePath(_settings.ScreenVlmModelDir, OpenVinoInstaller.VlmModelDirFor(repo));
            _models.Items.Add(label + (active ? "  — выбрана" : downloaded ? "  — скачана" : ""));
        }
        if (_models.Items.Count > 0) _models.SelectedIndex = Math.Min(selected, _models.Items.Count - 1);
    }

    private string SelectedRepo() =>
        OpenVinoInstaller.KnownVlmModels[Math.Max(0, _models.SelectedIndex)].Repo;

    private async Task DownloadAsync()
    {
        string repo = SelectedRepo();
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(async () =>
            {
                // The sidecar needs the portable Python + openvino-genai; install it if the
                // user hasn't been through the transcription setup yet.
                await OpenVinoInstaller.EnsureRuntimeAsync(_settings, AppendLog, _cts.Token);
                string dir = await OpenVinoInstaller.EnsureVlmModelAsync(repo, AppendLog, _cts.Token);
                _settings.ScreenVlmModelDir = dir;
                _settings.Save();
            });

            if (ScreenVlmEngine.Unavailable(_settings) is { } why)
            {
                AppendLog("Модель скачана, но слой не готов: " + why);
                RefreshModelList();
                return;
            }

            AppendLog("Готово. Модель подключена: " + _settings.ScreenVlmModelDir);
            RefreshModelList();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            AppendLog("Отменено пользователем. Скачанные файлы сохранены — загрузка продолжится с них.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Модель VLM", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetManually()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Папка с OpenVINO IR моделью VLM (openvino_language_model.xml, …)",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_settings.ScreenVlmModelDir)
                ? _settings.ScreenVlmModelDir
                : OpenVinoInstaller.ModelsDir,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var missing = OpenVinoInstaller.MissingVlmFiles(dialog.SelectedPath);
        if (missing.Count > 0)
        {
            AppendLog("В папке не хватает файлов модели: " + string.Join(", ", missing));
            return;
        }

        _settings.ScreenVlmModelDir = dialog.SelectedPath;
        _settings.Save();
        AppendLog("Модель подключена: " + dialog.SelectedPath);
        RefreshModelList();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SetBusy(bool busy)
    {
        _downloadBtn.Enabled = !busy;
        _manualBtn.Enabled = !busy;
        _models.Enabled = !busy;
        _progress.Visible = busy;
        _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
        UseWaitCursor = busy;
    }

    private void AppendLog(string line)
    {
        TranscriptionLog.Write("[vlm-setup] " + line.Trim());
        AppendToBox(line);
    }

    private void AppendToBox(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => AppendToBox(line)); return; }
        _log.AppendText(line + Environment.NewLine);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }
}
