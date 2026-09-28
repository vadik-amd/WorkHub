using System.Diagnostics;

namespace WorkHub;

/// <summary>
/// Shown when transcription is requested but no Whisper engine is available. Offers a
/// one-click portable whisper.cpp download (no install / Python / ffmpeg), plus fallbacks.
/// Returns DialogResult.OK once an engine is set up.
/// </summary>
public sealed class DependencySetupForm : Form
{
    private static readonly (string Model, string Label)[] ModelChoices =
    {
        ("tiny", "tiny — самая быстрая, грубая"),
        ("base", "base — быстрая"),
        ("small", "small — рекомендуется"),
        ("medium", "medium — точнее, медленнее"),
        ("large-v3", "large-v3 — максимум качества"),
    };

    private readonly AppSettings _settings;
    private readonly ComboBox _modelCombo;
    private readonly TextBox _log;
    private readonly ProgressBar _progress;
    private readonly Button _installBtn;
    private readonly Button _manualModelBtn;
    private readonly Button _modelsPageBtn;
    private readonly Button _pageBtn;
    private readonly Button _openAiBtn;
    private readonly Button _closeBtn;
    private CancellationTokenSource? _cts;

    public DependencySetupForm(AppSettings settings)
    {
        _settings = settings;

        Text = "Work Hub — установка Whisper";
        Width = 720;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 480);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 116,
            Padding = new Padding(12, 12, 12, 4),
            Text =
                "Whisper не найден. Для расшифровки нужен движок.\r\n\r\n" +
                "Рекомендуется портативный whisper.cpp: один exe, без установки, без Python " +
                "и без ffmpeg, без прав администратора. Хаб скачает его и выбранную модель в " +
                "папку профиля и настроит сам.\r\n\r\n" +
                "Выберите модель и нажмите «Скачать и настроить».",
        };

        var modelLabel = new Label { Text = "Модель:", AutoSize = true, Padding = new Padding(12, 10, 6, 0) };
        _modelCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 320,
            Left = 80,
            Top = 8,
        };
        foreach (var (model, label) in ModelChoices)
        {
            string hint = WhisperInstaller.ModelSizeHint(model);
            _modelCombo.Items.Add(new ModelItem(model, $"{label}  {hint}"));
        }
        SelectModel(settings.WhisperModel);

        var modelPanel = new Panel { Dock = DockStyle.Top, Height = 40 };
        modelPanel.Controls.Add(_modelCombo);
        modelPanel.Controls.Add(modelLabel);

        _progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 8,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 0, // static until busy
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

        _installBtn = new Button { Text = "Скачать и настроить", AutoSize = true };
        _installBtn.Click += async (_, _) => await InstallAsync();
        _manualModelBtn = new Button { Text = "Указать модель (.bin)…", AutoSize = true };
        _manualModelBtn.Click += async (_, _) => await SetModelManuallyAsync();
        _modelsPageBtn = new Button { Text = "Скачать модель (браузер)", AutoSize = true };
        _modelsPageBtn.Click += (_, _) => OpenUrl(WhisperInstaller.ModelsPageUrl);
        _pageBtn = new Button { Text = "Страница whisper.cpp", AutoSize = true };
        _pageBtn.Click += (_, _) => OpenUrl(WhisperInstaller.ReleasesPageUrl(_settings));
        _openAiBtn = new Button { Text = "openai-whisper", AutoSize = true };
        _openAiBtn.Click += (_, _) => ShowOpenAiInstructions();
        _closeBtn = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 84,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
        };
        buttons.Controls.AddRange(new Control[]
        {
            _installBtn, _manualModelBtn, _modelsPageBtn, _pageBtn, _openAiBtn, _closeBtn,
        });

        Controls.Add(_log);
        Controls.Add(_progress);
        Controls.Add(modelPanel);
        Controls.Add(info);
        Controls.Add(buttons);

        CancelButton = _closeBtn;
        FormClosing += (_, e) =>
        {
            if (_cts is { IsCancellationRequested: false } && IsBusy)
                _cts.Cancel();
        };
    }

    private bool IsBusy => _installBtn is { Enabled: false };

    private async Task InstallAsync()
    {
        var model = (_modelCombo.SelectedItem as ModelItem)?.Model ?? "small";
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            await WhisperInstaller.InstallAsync(_settings, model, AppendLog, _cts.Token);
            // remember chosen model for openai path consistency too
            _settings.WhisperModel = model;
            _settings.Save();

            if (Transcriber.ResolveEngine(_settings) == WhisperEngine.WhisperCpp)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                AppendLog("Не удалось подтвердить установку. Проверьте лог выше.");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Отменено пользователем.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка: " + ex.Message);
            MessageBox.Show(this,
                "Не удалось скачать автоматически:\n" + ex.Message +
                "\n\nМожно скачать вручную кнопкой «Открыть страницу whisper.cpp».",
                "Whisper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SetModelManuallyAsync()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Выберите файл модели ggml (.bin)",
            Filter = "Модель ggml|*.bin|Все файлы|*.*",
            CheckFileExists = true,
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            AppendLog("Проверка/загрузка бинаря whisper.cpp (GitHub)…");
            await WhisperInstaller.EnsureBinaryAsync(_settings, AppendLog, _cts.Token);
            _settings.WhisperCppModel = ofd.FileName;
            _settings.Save();

            if (Transcriber.ResolveEngine(_settings) == WhisperEngine.WhisperCpp)
            {
                AppendLog("Готово. Модель подключена.");
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                AppendLog("Не удалось подтвердить установку — проверьте файл модели.");
            }
        }
        catch (OperationCanceledException) { AppendLog("Отменено пользователем."); }
        catch (Exception ex) { AppendLog("Ошибка: " + ex.Message); }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _installBtn.Enabled = !busy;
        _manualModelBtn.Enabled = !busy;
        _modelCombo.Enabled = !busy;
        _progress.Visible = busy;
        _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
        UseWaitCursor = busy;
    }

    private void AppendLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
        _log.AppendText(line + Environment.NewLine);
    }

    private void SelectModel(string model)
    {
        for (int i = 0; i < _modelCombo.Items.Count; i++)
        {
            if (_modelCombo.Items[i] is ModelItem mi &&
                string.Equals(mi.Model, model, StringComparison.OrdinalIgnoreCase))
            {
                _modelCombo.SelectedIndex = i;
                return;
            }
        }
        _modelCombo.SelectedIndex = Math.Min(2, _modelCombo.Items.Count - 1); // default -> small
    }

    private void ShowOpenAiInstructions()
    {
        MessageBox.Show(this,
            "Альтернатива — классический openai-whisper (нужен Python + ffmpeg):\r\n\r\n" +
            "  winget install Gyan.FFmpeg\r\n" +
            "  pip install -U openai-whisper\r\n\r\n" +
            "Если под ваш Python нет PyTorch:\r\n" +
            "  pip install -U whisper-ctranslate2\r\n" +
            "и укажите в %APPDATA%\\WorkHub\\settings.json:\r\n" +
            "  \"WhisperExe\": \"whisper-ctranslate2\"\r\n\r\n" +
            "После этого «Проверить установку Whisper» в меню трея должно найти движок.",
            "openai-whisper", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private sealed record ModelItem(string Model, string Display)
    {
        public override string ToString() => Display;
    }
}
