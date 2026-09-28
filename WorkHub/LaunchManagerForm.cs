using System.ComponentModel;

namespace WorkHub;

/// <summary>Editor for the hub's auto-launch list (path/shortcut + run-as-admin + enabled).</summary>
public sealed class LaunchManagerForm : Form
{
    private readonly AppSettings _settings;
    private readonly BindingList<LaunchItem> _items;
    private readonly DataGridView _grid;
    private readonly CheckBox _autoOnStartup;

    public LaunchManagerForm(AppSettings settings)
    {
        _settings = settings;
        _items = new BindingList<LaunchItem>(settings.AutoLaunch.Select(i => i.Clone()).ToList());

        Text = "Work Hub — автозапуск программ";
        Width = 820;
        Height = 460;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 320);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            DataSource = _items,
        };
        _grid.Columns.AddRange(
            new DataGridViewCheckBoxColumn
            {
                HeaderText = "Вкл", DataPropertyName = nameof(LaunchItem.Enabled), Width = 45,
            },
            new DataGridViewTextBoxColumn
            {
                HeaderText = "Имя", DataPropertyName = nameof(LaunchItem.Name), Width = 160,
            },
            new DataGridViewTextBoxColumn
            {
                HeaderText = "Путь к файлу/ярлыку", DataPropertyName = nameof(LaunchItem.Path),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true,
            },
            new DataGridViewTextBoxColumn
            {
                HeaderText = "Аргументы", DataPropertyName = nameof(LaunchItem.Arguments), Width = 120,
            },
            new DataGridViewCheckBoxColumn
            {
                HeaderText = "От админа", DataPropertyName = nameof(LaunchItem.RunAsAdmin), Width = 80,
            });

        // Commit checkbox edits immediately (otherwise they apply only on row change).
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        var addBtn = new Button { Text = "Добавить…", AutoSize = true };
        addBtn.Click += (_, _) => AddItem();
        var removeBtn = new Button { Text = "Удалить", AutoSize = true };
        removeBtn.Click += (_, _) => RemoveSelected();
        var runBtn = new Button { Text = "Запустить сейчас", AutoSize = true };
        runBtn.Click += (_, _) => RunNow();
        var saveBtn = new Button { Text = "Сохранить", AutoSize = true, DialogResult = DialogResult.OK };
        saveBtn.Click += (_, _) => Save();
        var cancelBtn = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };

        _autoOnStartup = new CheckBox
        {
            Text = "Запускать список при старте хаба",
            Checked = settings.AutoLaunchOnStartup,
            AutoSize = true,
            Padding = new Padding(8, 8, 0, 0),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Height = 46,
            Padding = new Padding(6),
            WrapContents = false,
        };
        buttons.Controls.AddRange(new Control[] { addBtn, removeBtn, runBtn, saveBtn, cancelBtn });

        var topBar = new Panel { Dock = DockStyle.Top, Height = 34 };
        topBar.Controls.Add(_autoOnStartup);

        Controls.Add(_grid);
        Controls.Add(buttons);
        Controls.Add(topBar);

        AcceptButton = saveBtn;
        CancelButton = cancelBtn;
    }

    private void AddItem()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Выберите программу или ярлык",
            Filter = "Программы и ярлыки|*.exe;*.lnk;*.bat;*.cmd|Все файлы|*.*",
            CheckFileExists = true,
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        _items.Add(new LaunchItem
        {
            Path = ofd.FileName,
            Name = Path.GetFileNameWithoutExtension(ofd.FileName),
            Enabled = true,
        });
    }

    private void RemoveSelected()
    {
        var toRemove = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Select(r => r.DataBoundItem as LaunchItem)
            .Where(i => i != null)
            .Cast<LaunchItem>()
            .ToList();
        foreach (var item in toRemove)
            _items.Remove(item);
    }

    private void RunNow()
    {
        var (launched, failed, errors) = Launcher.LaunchAll(_items);
        var msg = $"Запущено: {launched}, ошибок: {failed}";
        if (errors.Count > 0)
            msg += "\n\n" + string.Join("\n", errors);
        MessageBox.Show(this, msg, "Запуск программ",
            MessageBoxButtons.OK,
            failed > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    private void Save()
    {
        _settings.AutoLaunchOnStartup = _autoOnStartup.Checked;
        _settings.AutoLaunch = _items.ToList();
        _settings.Save();
    }
}
