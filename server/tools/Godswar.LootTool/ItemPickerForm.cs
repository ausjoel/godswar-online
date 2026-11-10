using System.Globalization;

namespace Godswar.LootTool;

/// <summary>
/// Item search dialog over <c>item_templates</c>, with the client's Chinese name
/// shown next to the server's English one. Shared by the loot and quest reward
/// tabs, so the caption is the caller's.
/// </summary>
internal sealed class ItemPickerForm : Form
{
    private const int MaximumRows = 500;

    private readonly IReadOnlyList<ItemRow> _items;
    private readonly TextBox _search = new();
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();

    public ItemPickerForm(
        IReadOnlyList<ItemRow> items,
        int initialItemId,
        string title = "选择掉落物品")
    {
        _items = items;
        Text = title;
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;

        var top = new Panel { Dock = DockStyle.Top, Height = 42 };
        var label = new Label
        {
            Text = "搜索（ID / 中文名 / 英文名 / name_key）：",
            AutoSize = true,
            Left = 12,
            Top = 13
        };
        _search.Left = 280;
        _search.Top = 9;
        _search.Width = 380;
        _search.TextChanged += (_, _) => Refresh_();
        top.Controls.Add(label);
        top.Controls.Add(_search);

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
        var ok = new Button { Text = "使用该物品", Width = 110, DialogResult = DialogResult.OK };
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
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "ID", Width = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "中文名", Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "英文名", Width = 260 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "name_key", Width = 150 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类型", Width = 120 });
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

        if (initialItemId > 0)
        {
            _search.Text = initialItemId.ToString(CultureInfo.InvariantCulture);
        }

        Refresh_();
    }

    public int SelectedItemId { get; private set; }

    private void Confirm_()
    {
        if (_grid.CurrentRow?.Tag is ItemRow item)
        {
            SelectedItemId = item.Id;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void Refresh_()
    {
        var query = _search.Text.Trim();
        IEnumerable<ItemRow> matches = _items;

        if (query.Length > 0)
        {
            if (int.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                matches = _items.Where(item =>
                    item.Id == id ||
                    item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.ChineseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.NameKey.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                matches = _items.Where(item =>
                    item.ChineseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    item.NameKey.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
        }

        var list = matches.ToList();
        _grid.Rows.Clear();
        foreach (var item in list.Take(MaximumRows))
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];
            row.Tag = item;
            row.Cells[0].Value = item.Id;
            row.Cells[1].Value = item.ChineseName;
            row.Cells[2].Value = item.DisplayName;
            row.Cells[3].Value = item.NameKey;
            row.Cells[4].Value = item.Kind;
        }

        _summary.Text = list.Count > MaximumRows
            ? $"匹配 {list.Count} 条，仅显示前 {MaximumRows} 条，请继续缩小范围。"
            : $"匹配 {list.Count} 条。";
    }
}
