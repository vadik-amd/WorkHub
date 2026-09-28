using System.Diagnostics;

namespace WorkHub;

/// <summary>
/// One-time setup of the OpenVINO engine: portable Python + openvino-genai + an IR Whisper
/// model, or a model folder the user already has. Returns DialogResult.OK once ready.
/// </summary>
public sealed class OpenVinoSetupForm : Form
{
    private readonly AppSettings _settings;
    private readonly TextBox _log;
    private readonly ProgressBar _progress;
    private readonly Button _installBtn;
    private readonly Button _manualModelBtn;
    private readonly Button _modelPageBtn;
    private readonly Button _closeBtn;
    private CancellationTokenSource? _cts;

    /// <param name="switchToRepo">Tray model choice: download/activate settings.OpenVinoModelRepo
    /// even if another model is currently set up.</param>
    public OpenVinoSetupForm(AppSettings settings, bool switchToRepo = false)
    {
        _settings = settings;

        Text = "Work Hub — установка OpenVINO (NPU / GPU)";
        Width = 720;
        Height = 540;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 480);

        var info = new Label
        {
            Dock = DockStyle.Top,
            Height = 128,
            Padding = new Padding(12, 12, 12, 4),
            Text =
                "Движок OpenVINO GenAI распознаёт речь на NPU (Intel AI Boost) или встроенной " +
                "видеокарте Arc, с автоматическим переходом NPU → GPU → CPU.\r\n\r\n" +
                "Хаб скачает в папку профиля (без прав администратора и без установки Python в " +
                "систему): uv и портативный Python (GitHub, ~40 МБ), openvino-genai (PyPI, ~100 МБ) " +
                $"и готовую модель {settings.OpenVinoModelRepo} (~0.8–1.5 ГБ).\r\n\r\n" +
                "Если источники модели недоступны — скачайте папку модели сами и укажите её.",
        };

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

        _installBtn = new Button { Text = "Скачать и настроить", AutoSize = true };
        _installBtn.Click += async (_, _) =>
            await RunAsync(ct => OpenVinoInstaller.InstallAsync(_settings, AppendLog, ct, switchToRepo));
        _manualModelBtn = new Button { Text = "Указать папку модели…", AutoSize = true };
        _manualModelBtn.Click += async (_, _) => await SetModelManuallyAsync();
        _modelPageBtn = new Button { Text = "Страница модели (браузер)", AutoSize = true };
        _modelPageBtn.Click += (_, _) => OpenUrl(OpenVinoInstaller.ModelPageUrl(_settings));
        _closeBtn = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 50,
            Padding = new Padding(8),
            FlowDirection = FlowDirection.LeftToRight,
        };
        buttons.Controls.AddRange(new Control[] { _installBtn, _manualModelBtn, _modelPageBtn, _closeBtn });

        Controls.Add(_log);
        Controls.Add(_progress);
        Controls.Add(info);
        Controls.Add(buttons);

        CancelButton = _closeBtn;
        FormClosing += (_, _) =>
        {
            if (_cts is { IsCancellationRequested: false } && IsBusy)
                _cts.Cancel();
        };

        if (switchToRepo)
            AppendLog($"Модель {settings.OpenVinoModelRepo} ещё не скачана — нажмите «Скачать и настроить».");
        else if (OpenVinoInstaller.IsReady(settings))
            AppendLog("OpenVINO уже настроен. Модель: " + settings.OpenVinoModelDir);
    }

    private bool IsBusy => _installBtn is { Enabled: false };

    private async Task SetModelManuallyAsync()
    {
        using var fbd = new FolderBrowserDialog
        {
            Description = "Папка с OpenVINO IR-моделью Whisper (openvino_encoder_model.xml, …)",
            UseDescriptionForTitle = true,
        };
        if (fbd.ShowDialog(this) != DialogResult.OK) return;

        var missing = OpenVinoInstaller.MissingModelFiles(fbd.SelectedPath);
        if (missing.Count > 0)
        {
            AppendLog("В папке не хватает файлов модели: " + string.Join(", ", missing));
            return;
        }
        string dir = fbd.SelectedPath;
        await RunAsync(async ct =>
        {
            OpenVinoInstaller.SetModelDir(_settings, dir);
            AppendLog("Модель подключена: " + dir);
            await OpenVinoInstaller.EnsureRuntimeAsync(_settings, AppendLog, ct);
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => work(_cts.Token));
            if (OpenVinoInstaller.IsReady(_settings))
            {
                AppendLog("Готово.");
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                AppendLog("Установка не завершена — проверьте лог выше.");
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("Отменено пользователем.");
        }
        catch (Exception ex)
        {
            AppendLog("Ошибка: " + ex.Message);
            MessageBox.Show(this, ex.Message, "OpenVINO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (!IsDisposed) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _installBtn.Enabled = !busy;
        _manualModelBtn.Enabled = !busy;
        _progress.Visible = busy;
        _progress.MarqueeAnimationSpeed = busy ? 30 : 0;
        UseWaitCursor = busy;
    }

    private void AppendLog(string line)
    {
        TranscriptionLog.Write("[setup] " + line.Trim());
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
