namespace WorkHub;

/// <summary>
/// Flexible runs and re-transcription: pick recordings (with the transcripts they already
/// have), the mode, which device × model combinations take part, languages, channel and
/// whether to overwrite; watch every device live. Choices here apply to this run only —
/// "Сделать настройками по умолчанию" copies them to the tray defaults.
/// </summary>
public sealed class TranscriptionCenterForm : Form
{
    private readonly TranscriptionController _ctl;
    private readonly ListView _list;
    private readonly RadioButton _distribute;
    private readonly RadioButton _compare;
    private readonly TableLayoutPanel _grid;
    private readonly CheckBox _ru;
    private readonly CheckBox _en;
    private readonly ComboBox _channel;
    private readonly CheckBox _overwrite;
    private readonly Button _startBtn;
    private readonly Button _stopBtn;
    private readonly ProgressBar _progress;
    private readonly TextBox _log;
    private readonly FlowLayoutPanel _devicePanel;
    private readonly Dictionary<string, Label> _deviceLabels = new();
    private readonly Dictionary<string, (string Job, DateTime Since)> _deviceJobs = new();
    private readonly List<(string Device, ModelOption Option, CheckBox Box)> _cells = new();
    private readonly ToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private TranscriptionRunner? _runner;
    private int _total, _finished;

    private const int RightPanelWidth = 720;

    private static readonly (string Key, string Label)[] Channels =
    {
        ("both", "Мою речь и собеседников"),
        ("others", "Только собеседников"),
        ("me", "Только меня"),
    };

    public TranscriptionCenterForm(TranscriptionController ctl)
    {
        _ctl = ctl;
        var s = ctl.Settings;

        Text = "Work Hub — центр расшифровки";
        Width = 1320;
        Height = 760;
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;

        // ---- left: recordings
        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HideSelection = false,
        };
        _list.Columns.Add("Запись", 190);
        _list.Columns.Add("Длит.", 60);
        _list.Columns.Add("Есть расшифровки", 330);

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(4) };
        listButtons.Controls.AddRange(new Control[]
        {
            MakeButton("Без расшифровки", (_, _) => CheckWhere(i => ((Rec)i.Tag!).Missing)),
            MakeButton("Все", (_, _) => CheckWhere(_ => true)),
            MakeButton("Снять", (_, _) => CheckWhere(_ => false)),
            MakeButton("Обновить", (_, _) => LoadRecordings()),
        });
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_list);
        left.Controls.Add(listButtons);
        left.Controls.Add(new Label { Text = "Записи (отметьте, что расшифровать):", Dock = DockStyle.Top, Height = 22 });

        // ---- right: options
        _distribute = new RadioButton { Text = "Распределять по устройствам — каждая запись один раз, быстрее всего", AutoSize = true, Checked = !s.CompareMode() };
        _compare = new RadioButton { Text = "Сравнение — каждую запись каждой отмеченной комбинацией устройство × модель", AutoSize = true, Checked = s.CompareMode() };
        _distribute.CheckedChanged += (_, _) => { if (_distribute.Checked) EnforceOnePerDevice(null); };
        var modeBox = new GroupBox { Text = "Режим", Dock = DockStyle.Top, Height = 84 };
        var modeFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        modeFlow.Controls.AddRange(new Control[] { _distribute, _compare });
        modeBox.Controls.Add(modeFlow);

        _grid = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
        var gridBox = new GroupBox { Text = "Устройства и модели", Dock = DockStyle.Top, Height = 150 };
        gridBox.Controls.Add(_grid);

        _ru = new CheckBox { Text = "Русский", AutoSize = true, Checked = s.TranscribeRussian };
        _en = new CheckBox { Text = "English", AutoSize = true, Checked = s.TranscribeEnglish };
        _channel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var (_, label) in Channels) _channel.Items.Add(label);
        _channel.SelectedIndex = Math.Max(0, Array.FindIndex(Channels, c => c.Key == s.TranscribeChannel));
        _overwrite = new CheckBox { Text = "Перезаписывать расшифровки с той же меткой устройства/модели", AutoSize = true };
        var optFlow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(4) };
        optFlow.Controls.AddRange(new Control[]
        {
            new Label { Text = "Языки:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _ru, _en,
            new Label { Text = "   Канал:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, _channel, _overwrite,
        });

        _startBtn = MakeButton("▶ Расшифровать отмеченные", async (_, _) => await StartAsync());
        _stopBtn = MakeButton("⏹ Стоп", (_, _) => _ctl.Stop());
        _stopBtn.Enabled = false;
        var saveBtn = MakeButton("Сделать настройками по умолчанию", (_, _) => SaveAsDefaults());
        var actFlow = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4) };
        actFlow.Controls.AddRange(new Control[] { _startBtn, _stopBtn, saveBtn });

        _devicePanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 84, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4) };
        foreach (var dev in AppSettings.Devices)
        {
            var l = new Label { AutoSize = true, Text = $"{dev}: —" };
            _deviceLabels[dev] = l;
            _devicePanel.Controls.Add(l);
        }
        _progress = new ProgressBar { Dock = DockStyle.Top, Height = 14 };
        _log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 0, 0) };
        right.Controls.Add(_log);
        right.Controls.Add(_progress);
        right.Controls.Add(_devicePanel);
        right.Controls.Add(actFlow);
        right.Controls.Add(optFlow);
        right.Controls.Add(gridBox);
        right.Controls.Add(modeBox);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        // Sizes only stick once the container has its real width.
        Load += (_, _) =>
        {
            split.SplitterDistance = Math.Max(320, split.Width - RightPanelWidth);
            split.Panel2MinSize = 600;
        };

        BuildGrid();
        LoadRecordings();

        _timer.Tick += (_, _) => RefreshDeviceLabels();
        _ctl.RunStarted += OnRunStarted;
        _ctl.RunFinished += OnRunFinished;
        FormClosed += (_, _) =>
        {
            _ctl.RunStarted -= OnRunStarted;
            _ctl.RunFinished -= OnRunFinished;
            DetachRunner();
            _timer.Dispose();
        };
        if (_ctl.IsBusy) SetRunning(true);
    }

    // ------------------------------------------------------------------ recordings

    private sealed record Rec(string Wav, bool Missing);

    private void LoadRecordings()
    {
        var langs = SelectedLangs();
        var previouslyChecked = _list.CheckedItems.Cast<ListViewItem>().Select(i => ((Rec)i.Tag!).Wav).ToHashSet();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var wav in _ctl.Recordings())
        {
            var parts = new List<string>();
            bool missing = false;
            foreach (var lang in new[] { "ru", "en" })
            {
                var tags = TranscriptPaths.ExistingTags(wav, lang).ToList();
                if (File.Exists(TranscriptPaths.Legacy(wav, lang))) tags.Insert(0, "старый");
                if (tags.Count > 0) parts.Add(lang + ": " + string.Join(", ", tags));
                if (langs.Contains(lang) && tags.Count == 0) missing = true;
            }
            var dur = TimeSpan.FromSeconds(Transcriber.AudioSeconds(wav));
            var item = new ListViewItem(new[]
            {
                Path.GetFileNameWithoutExtension(wav),
                dur.TotalHours >= 1 ? dur.ToString(@"h\:mm\:ss") : dur.ToString(@"m\:ss"),
                parts.Count > 0 ? string.Join("; ", parts) : "—",
            })
            {
                Tag = new Rec(wav, missing),
                Checked = previouslyChecked.Contains(wav),
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
    }

    private void CheckWhere(Func<ListViewItem, bool> pred)
    {
        foreach (ListViewItem i in _list.Items) i.Checked = pred(i);
    }

    // ------------------------------------------------------------------ device × model grid

    private void BuildGrid()
    {
        var s = _ctl.Settings;
        _grid.SuspendLayout();
        _grid.Controls.Clear();
        _cells.Clear();

        // Columns: every model any device offers (whisper.cpp ones only apply to the CPU).
        var perDevice = AppSettings.Devices.ToDictionary(d => d, d => ModelCatalog.For(d, s));
        var columns = perDevice.Values.SelectMany(v => v)
            .GroupBy(o => (o.Engine, Key: Path.GetFullPath(o.Model).TrimEnd('\\').ToLowerInvariant()))
            .Select(g => g.First()).ToList();

        _grid.ColumnCount = columns.Count + 1;
        _grid.RowCount = AppSettings.Devices.Length + 1;
        _grid.Controls.Add(new Label { Text = "", AutoSize = true }, 0, 0);
        for (int c = 0; c < columns.Count; c++)
            _grid.Controls.Add(new Label { Text = columns[c].Label, AutoSize = true, Padding = new Padding(3) }, c + 1, 0);

        for (int r = 0; r < AppSettings.Devices.Length; r++)
        {
            string dev = AppSettings.Devices[r];
            var slot = s.Slot(dev);
            _grid.Controls.Add(new Label { Text = dev, AutoSize = true, Padding = new Padding(3, 6, 3, 3), Font = new Font(Font, FontStyle.Bold) }, 0, r + 1);
            for (int c = 0; c < columns.Count; c++)
            {
                var opt = perDevice[dev].FirstOrDefault(o => o.Engine == columns[c].Engine && ModelCatalog.SamePath(o.Model, columns[c].Model));
                if (opt == null)
                {
                    _grid.Controls.Add(new Label { Text = "—", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(3, 6, 3, 3) }, c + 1, r + 1);
                    continue;
                }
                string? reason = opt.Blocked ?? (opt.Downloaded ? null : "не скачана (скачать — в меню трея у устройства)");
                var box = new CheckBox
                {
                    Text = reason == null ? "" : "✗",
                    AutoSize = true,
                    Enabled = reason == null,
                    Checked = reason == null && slot.Enabled && slot.Engine == opt.Engine && ModelCatalog.SamePath(slot.Model, opt.Model),
                };
                if (reason != null) _tips.SetToolTip(box, reason);
                var capturedDev = dev;
                box.CheckedChanged += (_, _) => { if (box.Checked && _distribute.Checked) EnforceOnePerDevice((capturedDev, box)); };
                _cells.Add((dev, opt, box));
                _grid.Controls.Add(box, c + 1, r + 1);
            }
        }
        _grid.ResumeLayout();
    }

    /// <summary>Distribute mode takes one model per device: keep <paramref name="keep"/>, clear the rest of its row.</summary>
    private void EnforceOnePerDevice((string Device, CheckBox Box)? keep)
    {
        foreach (var dev in AppSettings.Devices)
        {
            var row = _cells.Where(c => c.Device == dev && c.Box.Checked).ToList();
            var survivor = keep is { } k && k.Device == dev ? k.Box : row.FirstOrDefault().Box;
            foreach (var c in row)
                if (c.Box != survivor) c.Box.Checked = false;
        }
    }

    private List<Executor> SelectedExecutors() => _cells.Where(c => c.Box.Checked)
        .Select(c => new Executor(c.Device, c.Option.Engine, c.Option.Model)).ToList();

    private List<string> SelectedLangs()
    {
        var l = new List<string>();
        if (_ru is { Checked: true }) l.Add("ru");
        if (_en is { Checked: true }) l.Add("en");
        return l;
    }

    // ------------------------------------------------------------------ run

    private async Task StartAsync()
    {
        var wavs = _list.CheckedItems.Cast<ListViewItem>().Select(i => ((Rec)i.Tag!).Wav).ToList();
        var execs = SelectedExecutors();
        var langs = SelectedLangs();
        if (wavs.Count == 0 || execs.Count == 0 || langs.Count == 0)
        {
            MessageBox.Show(this, "Отметьте хотя бы одну запись, одну клетку «устройство × модель» и один язык.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // A per-run copy: the channel chosen here doesn't change the tray default.
        var runSettings = _ctl.CloneSettings();
        runSettings.TranscribeChannel = Channels[Math.Max(0, _channel.SelectedIndex)].Key;

        var jobs = TranscriptionController.BuildJobs(wavs, langs, onlyMissing: false);
        var plan = new TranscriptionPlan(jobs, execs, _compare.Checked, _overwrite.Checked);
        await _ctl.StartAsync(plan, runSettings);
    }

    private void OnRunStarted(TranscriptionRunner runner, TranscriptionPlan plan)
    {
        DetachRunner();
        _runner = runner;
        _total = plan.Compare ? plan.Jobs.Count * plan.Executors.Count : plan.Jobs.Count;
        _finished = 0;
        _deviceJobs.Clear();
        runner.Log += OnLog;
        runner.JobStarted += OnJobStarted;
        runner.JobDone += OnJobDone;
        runner.JobFailed += OnJobFailed;
        Ui(() =>
        {
            _log.Clear();
            _progress.Maximum = Math.Max(1, _total);
            _progress.Value = 0;
            SetRunning(true);
        });
    }

    private void OnRunFinished(TranscriptionSummary? summary)
    {
        DetachRunner();
        Ui(() =>
        {
            SetRunning(false);
            _deviceJobs.Clear();
            RefreshDeviceLabels();
            LoadRecordings();
        });
    }

    private void DetachRunner()
    {
        if (_runner == null) return;
        _runner.Log -= OnLog;
        _runner.JobStarted -= OnJobStarted;
        _runner.JobDone -= OnJobDone;
        _runner.JobFailed -= OnJobFailed;
        _runner = null;
    }

    private void OnLog(string line)
    {
        // Engine chatter (whisper.cpp timings etc.) stays in the log file; show the gist here.
        if (!(line.Contains("---") || line.Contains("Готово") || line.Contains("Ошибка") || line.Contains("[ov]") ||
              line.StartsWith("===") || line.Contains("передаю") || line.Contains("сбой")))
            return;
        Ui(() =>
        {
            _log.AppendText(line + Environment.NewLine);
        });
    }

    private void OnJobStarted(Executor e, TranscriptionJob j) =>
        Ui(() => { _deviceJobs[e.Device] = ($"{e.Tag}: {j.Name}", DateTime.Now); RefreshDeviceLabels(); });

    private void OnJobDone(Executor e, TranscriptionJob j, TranscriptionResult r) => Ui(() =>
    {
        _deviceJobs.Remove(e.Device);
        _finished++;
        _progress.Value = Math.Min(_progress.Maximum, _finished);
        RefreshDeviceLabels();
    });

    private void OnJobFailed(Executor e, TranscriptionJob j, string error) =>
        Ui(() => { _deviceJobs.Remove(e.Device); RefreshDeviceLabels(); });

    private void RefreshDeviceLabels()
    {
        foreach (var (dev, label) in _deviceLabels)
            label.Text = _deviceJobs.TryGetValue(dev, out var j)
                ? $"{dev}: {j.Job} — {(DateTime.Now - j.Since):mm\\:ss}"
                : $"{dev}: —";
        if (_runner != null) Text = $"Work Hub — центр расшифровки ({_finished}/{_total})";
        else Text = "Work Hub — центр расшифровки";
    }

    private void SetRunning(bool on)
    {
        _startBtn.Enabled = !on;
        _stopBtn.Enabled = on;
        if (on) _timer.Start(); else _timer.Stop();
    }

    private void SaveAsDefaults()
    {
        var s = _ctl.Settings;
        foreach (var dev in AppSettings.Devices)
        {
            var slot = s.Slot(dev);
            var pick = _cells.FirstOrDefault(c => c.Device == dev && c.Box.Checked);
            slot.Enabled = pick.Box != null;
            if (pick.Box != null) { slot.Engine = pick.Option.Engine; slot.Model = pick.Option.Model; }
        }
        s.TranscribeMode = _compare.Checked ? "compare" : "distribute";
        s.TranscribeRussian = _ru.Checked;
        s.TranscribeEnglish = _en.Checked;
        s.TranscribeChannel = Channels[Math.Max(0, _channel.SelectedIndex)].Key;
        s.Save();
        MessageBox.Show(this,
            "Сохранено. Пункты трея «Расшифровать последнюю / все новые» теперь используют эти устройства и модели" +
            (_compare.Checked ? "" : " (по одной модели на устройство)") + ".",
            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a);
        else a();
    }

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        return b;
    }
}
