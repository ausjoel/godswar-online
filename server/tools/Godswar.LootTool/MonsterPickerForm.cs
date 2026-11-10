namespace Godswar.LootTool;

/// <summary>
/// Monster search dialog: type a Chinese or English name (or the template key)
/// and pick the monster directly, instead of scrolling the full list. Spawnable
/// monsters are listed first, because a template that never spawns can never
/// drop anything.
/// </summary>
internal sealed class MonsterPickerForm : Form
{
    private const int MaximumRows = 500;

    private readonly IReadOnlyList<MonsterRow> _monsters;
    private readonly TextBox _search = new();
    private readonly CheckBox _onlySpawned = new();
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();

    public MonsterPickerForm(IReadOnlyList<MonsterRow> monsters, string? initialQuery)
    {
        _monsters = monsters;
        Text = "搜索怪物";
        Width = 1000;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        top.Controls.Add(new Label
        {
            Text = "搜索（中文名 / 英文名 / template_key）：",
            AutoSize = true,
            Padding = new Padding(12, 13, 4, 0)
        });
        _search.Width = 300;
        _search.Margin = new Padding(0, 9, 8, 0);
        _search.TextChanged += (_, _) => Refresh_();
        top.Controls.Add(_search);
        _onlySpawned.Text = "只看会刷出来的怪";
        _onlySpawned.AutoSize = true;
        _onlySpawned.Padding = new Padding(8, 12, 0, 0);
        _onlySpawned.CheckedChanged += (_, _) => Refresh_();
        top.Controls.Add(_onlySpawned);

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = 26;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Padding = new Padding(12, 0, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 6, 12, 0)
        };
        var cancel = new Button { Text = "取消", Width = 90, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "选择该怪物", Width = 110, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => Confirm_();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoGenerateColumns = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.RowHeadersVisible = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "中文名", Width = 130 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "英文名", Width = 190 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "template_key", Width = 230 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "区域", Width = 170 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地图", Width = 80 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "刷怪点", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "掉落", Width = 130 });
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                Confirm_();
            }
        };

        Controls.Add(_grid);
        Controls.Add(_summary);
        Controls.Add(buttons);
        Controls.Add(top);

        AcceptButton = ok;
        CancelButton = cancel;

        if (!string.IsNullOrWhiteSpace(initialQuery))
        {
            _search.Text = initialQuery;
        }

        Refresh_();
    }

    public string SelectedTemplateKey { get; private set; } = string.Empty;

    private void Confirm_()
    {
        if (_grid.CurrentRow?.Tag is MonsterRow monster)
        {
            SelectedTemplateKey = monster.TemplateKey;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void Refresh_()
    {
        var query = _search.Text.Trim();
        IEnumerable<MonsterRow> matches = _monsters;

        if (_onlySpawned.Checked)
        {
            matches = matches.Where(static monster => monster.IsSpawned);
        }

        if (query.Length > 0)
        {
            matches = matches.Where(monster =>
                monster.TemplateKey.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                monster.ChineseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                monster.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        // Spawnable monsters first: those are the ones whose loot can trigger.
        var list = matches
            .OrderByDescending(static monster => monster.SpawnCount)
            .ThenBy(static monster => monster.TemplateKey, StringComparer.Ordinal)
            .ToList();

        _grid.Rows.Clear();
        foreach (var monster in list.Take(MaximumRows))
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];
            row.Tag = monster;
            row.Cells[0].Value = monster.ChineseName;
            row.Cells[1].Value = monster.EnglishName;
            row.Cells[2].Value = monster.TemplateKey;
            row.Cells[3].Value = monster.Scenes;
            row.Cells[4].Value = monster.Maps;
            row.Cells[5].Value = monster.SpawnSummary;
            row.Cells[6].Value = monster.LootSummary;
            if (!monster.IsSpawned)
            {
                row.Cells[5].Style.ForeColor = Color.Firebrick;
            }
        }

        var spawned = list.Count(static monster => monster.IsSpawned);
        var shown = Math.Min(list.Count, MaximumRows);
        _summary.Text = list.Count > MaximumRows
            ? $"匹配 {list.Count} 条（能刷出来 {spawned} 条），仅显示前 {shown} 条，请继续缩小范围。"
            : $"匹配 {list.Count} 条，其中能刷出来 {spawned} 条。刷怪点为红色 = 该怪未在世界内容中，掉落不会生效。";
    }
}
