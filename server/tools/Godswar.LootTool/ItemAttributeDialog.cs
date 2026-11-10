using System.Globalization;

namespace Godswar.LootTool;

/// <summary>
/// 「属性…」弹窗：编辑**一件物品**的品质 / 等级 / 最多 5 条附加属性。
/// 怪物掉落规则和任务奖励槽位共用这一个弹窗。
/// </summary>
/// <remarks>
/// 界面上 <c>0</c> 一律表示「不配置」，写库时落成 <c>NULL</c>（服务端按默认处理）；
/// 属性下拉的第一项就是「（不配置）」，选了它这一行的 id 与等级都写 NULL。
/// 属性 id 的取值与中文名来自 <see cref="ItemAttributeCatalog"/>（客户端 + 服务端属性表），
/// 名表整个找不到时退化成纯数字输入框，不挡着 GM 用。
/// </remarks>
internal sealed class ItemAttributeDialog : Form
{
    private const int AttributeRows = 5;

    private readonly ItemAttributeCatalog _catalog;
    private readonly NumericUpDown _quality = new();
    private readonly NumericUpDown _grade = new();
    private readonly ComboBox[] _attributeBoxes = new ComboBox[AttributeRows];
    private readonly NumericUpDown[] _attributeIdInputs = new NumericUpDown[AttributeRows];

    /// <summary>5 个等级输入框：先建好实例，属性下拉构造时就要用它们设上限。</summary>
    private readonly NumericUpDown[] _attributeLevels =
        Enumerable.Range(0, AttributeRows).Select(static _ => new NumericUpDown()).ToArray();

    private readonly Label[] _attributeHints = new Label[AttributeRows];
    private readonly Label _problem = new();

    /// <summary>确定时算出来的 5 条属性（只有真正配了的）。</summary>
    private List<(int Id, short Level)> _entered = [];

    public ItemAttributeDialog(
        ItemAttributeCatalog catalog,
        string itemDescription,
        ItemAttributeValues initial)
    {
        _catalog = catalog;
        Values = initial;

        Text = $"物品属性 — {itemDescription}";
        Width = 720;
        Height = 420;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        BuildUi(initial);
    }

    /// <summary>确定后的结果（取消时保持原值，调用方看 DialogResult）。</summary>
    public ItemAttributeValues Values { get; private set; }

    private void BuildUi(ItemAttributeValues initial)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        root.Controls.Add(BuildTopRow(initial), 0, 0);
        root.Controls.Add(BuildAttributeGroup(initial), 0, 1);

        _problem.Dock = DockStyle.Fill;
        _problem.ForeColor = Color.Firebrick;
        root.Controls.Add(_problem, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancel = new Button { Text = "取消", Width = 90, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "确定", Width = 90 };
        ok.Click += (_, _) => Confirm();
        var clear = new Button { Text = "全部清空（不配置）", Width = 160 };
        clear.Click += (_, _) => ResetAll();
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(clear);
        root.Controls.Add(buttons, 0, 3);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private Control BuildTopRow(ItemAttributeValues initial)
    {
        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        row.Controls.Add(Label("品质"));
        row.Controls.Add(MakeNumber(_quality, 90, initial.Quality));
        row.Controls.Add(Label("等级"));
        row.Controls.Add(MakeNumber(_grade, 90, initial.Grade));
        var hint = Label("（0 = 不配置，服务端按默认品质 1 / 等级 1 发）");
        hint.ForeColor = Color.DimGray;
        row.Controls.Add(hint);
        return row;
    }

    private Control BuildAttributeGroup(ItemAttributeValues initial)
    {
        var group = new GroupBox
        {
            Text = "附加属性（最多 5 条；属性等级 0 = 只配 id 不配等级）",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = AttributeRows + 1
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var pairs = initial.Pairs.ToList();
        for (var index = 0; index < AttributeRows; index++)
        {
            var (id, level) = pairs[index];
            // 先建提示与等级框：属性下拉在构造时就会去更新它们（那边要用到等级上限）
            _attributeHints[index] = Label(string.Empty);
            _attributeHints[index].ForeColor = Color.DimGray;
            var levelBox = MakeNumber(_attributeLevels[index], 84, level);
            var input = BuildAttributeInput(index, id);
            layout.Controls.Add(Label($"{index + 1}."), 0, index);
            layout.Controls.Add(input, 1, index);
            layout.Controls.Add(levelBox, 2, index);
            layout.Controls.Add(_attributeHints[index], 3, index);
        }

        var catalogHint = Label(_catalog.Attributes.Count == 0
            ? $"属性名表未找到，先填 id：{_catalog.Error}"
            : $"属性表：{_catalog.Attributes.Count} 条，其中 {_catalog.ChineseNameCount} 条有中文名" +
              $"（{(_catalog.UsedServerTable ? "等级上限取服务端 item_attribute_templates" : "等级上限取客户端 ItemAppendAttribute.xml")}）");
        catalogHint.ForeColor = _catalog.Attributes.Count == 0 ? Color.Firebrick : Color.DimGray;
        layout.Controls.Add(catalogHint, 1, AttributeRows);
        layout.SetColumnSpan(catalogHint, 3);

        group.Controls.Add(layout);
        return group;
    }

    /// <summary>有名表就用下拉，没有就用数字输入框（功能不因此卡住）。</summary>
    private Control BuildAttributeInput(int index, short? initial)
    {
        if (_catalog.Attributes.Count == 0)
        {
            // 名表找不到时纯数字填 id；这里 -1 才表示「不配置」，
            // 因为属性 id 从 0 开始（0 = AttackA）是合法属性
            var input = new NumericUpDown
            {
                Minimum = -1,
                Maximum = short.MaxValue,
                Width = 120,
                Margin = new Padding(0, 4, 0, 0)
            };
            input.Value = initial ?? -1;
            input.ValueChanged += (_, _) =>
            {
                UpdateAttributeHint(index);
                UpdateLevelLimit(index);
            };
            _attributeIdInputs[index] = input;
            return input;
        }

        var box = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 360,
            Margin = new Padding(0, 4, 0, 0)
        };
        box.Items.Add(NoAttributeLabel);
        foreach (var attribute in _catalog.Attributes)
        {
            box.Items.Add(attribute.LongName);
        }

        // 库里存了一个名表里没有的 id（别的客户端/新属性）时，也要能显示出来
        if (initial is { } id && _catalog.Find(id) is null)
        {
            box.Items.Add(UnknownLabel(id));
        }

        box.SelectedIndex = initial is { } value
            ? FindOrAddIndex(box, value)
            : 0;
        box.SelectedIndexChanged += (_, _) =>
        {
            UpdateAttributeHint(index);
            UpdateLevelLimit(index);
        };
        _attributeBoxes[index] = box;
        UpdateAttributeHint(index);
        UpdateLevelLimit(index);
        return box;
    }

    private const string NoAttributeLabel = "（不配置）";

    private static string UnknownLabel(int id) => $"{id}（名表里没有这个属性）";

    private int FindOrAddIndex(ComboBox box, int id) =>
        _catalog.Find(id) is { } attribute
            ? box.Items.IndexOf(attribute.LongName)
            : box.Items.IndexOf(UnknownLabel(id));

    private void UpdateAttributeHint(int index)
    {
        var attribute = SelectedAttribute(index);
        _attributeHints[index].Text = attribute is null
            ? string.Empty
            : $"最高 {attribute.MaxLevel} 级" +
              (attribute.Percent ? "（百分比）" : string.Empty) +
              $"　{attribute.NameKey}";
    }

    /// <summary>等级上限跟着属性走，从源头挡住越界值。</summary>
    private void UpdateLevelLimit(int index)
    {
        var level = _attributeLevels[index];
        var attribute = SelectedAttribute(index);
        var maximum = attribute is { MaxLevel: > 0 } ? attribute.MaxLevel : short.MaxValue;
        level.Maximum = maximum;
        if (level.Value > maximum)
        {
            level.Value = maximum;
        }

        if (SelectedAttributeId(index) is null)
        {
            level.Value = 0;
        }
    }

    private ItemAttributeInfo? SelectedAttribute(int index) =>
        SelectedAttributeId(index) is { } id ? _catalog.Find(id) : null;

    /// <summary>
    /// 下拉/输入框里选的属性 id；<c>null</c> = 不配置。
    /// 注意属性 id 0（AttackA）是**合法属性**，所以这里不能用 0 当哨兵值。
    /// </summary>
    private int? SelectedAttributeId(int index)
    {
        if (_catalog.Attributes.Count == 0)
        {
            var typed = (int)_attributeIdInputs[index].Value;
            return typed < 0 ? null : typed;
        }

        var box = _attributeBoxes[index];
        if (box.SelectedIndex <= 0)
        {
            return null;
        }

        // 已知属性按标题开头的数字取；未知属性的那一项也长这样
        var text = Convert.ToString(box.SelectedItem, CultureInfo.InvariantCulture) ?? string.Empty;
        var separator = text.IndexOf(' ');
        var head = separator > 0 ? text[..separator] : text;
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    private void ResetAll()
    {
        _quality.Value = 0;
        _grade.Value = 0;
        for (var index = 0; index < AttributeRows; index++)
        {
            _attributeLevels[index].Value = 0;
            if (_catalog.Attributes.Count == 0)
            {
                _attributeIdInputs[index].Value = -1;
            }
            else
            {
                _attributeBoxes[index].SelectedIndex = 0;
            }
        }

        _problem.Text = string.Empty;
    }

    private void Confirm()
    {
        // 属性等级 0 = 只配 id 不配等级 → 落库是 NULL（摘要里显示成 id/-）
        short? AttributeIdAt(int index) =>
            index < _entered.Count ? checked((short)_entered[index].Id) : null;

        short? AttributeLevelAt(int index) =>
            index < _entered.Count && _entered[index].Level > 0
                ? _entered[index].Level
                : null;

        var attributes = new List<(int Id, short Level)>();
        for (var index = 0; index < AttributeRows; index++)
        {
            var id = SelectedAttributeId(index);
            var level = (short)_attributeLevels[index].Value;
            if (id is null)
            {
                if (level != 0)
                {
                    Fail($"第 {index + 1} 行填了属性等级但没选属性：请选一个属性，或把等级改回 0。");
                    return;
                }

                continue;
            }

            if (level > 0)
            {
                var attribute = _catalog.Find(id.Value);
                if (attribute is { MaxLevel: > 0 } && level > attribute.MaxLevel)
                {
                    Fail($"第 {index + 1} 行：{attribute.DisplayName} 最高 {attribute.MaxLevel} 级。");
                    return;
                }
            }

            if (attributes.Any(existing => existing.Id == id.Value))
            {
                Fail($"第 {index + 1} 行：属性 {id.Value} 重复了，同一件物品上同一个属性只配一次。");
                return;
            }

            attributes.Add((id.Value, level));
        }

        _entered = attributes;
        Values = new ItemAttributeValues(
            ToNullable(_quality),
            ToNullable(_grade),
            AttributeIdAt(0),
            AttributeLevelAt(0),
            AttributeIdAt(1),
            AttributeLevelAt(1),
            AttributeIdAt(2),
            AttributeLevelAt(2),
            AttributeIdAt(3),
            AttributeLevelAt(3),
            AttributeIdAt(4),
            AttributeLevelAt(4));
        DialogResult = DialogResult.OK;
        Close();
    }

    private static short? ToNullable(NumericUpDown box) =>
        box.Value <= 0 ? null : (short)box.Value;

    private void Fail(string message)
    {
        _problem.Text = message;
        // 弹窗里的错误就地显示，不额外弹 MessageBox，免得连着弹两层
    }

    private static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(6, 8, 2, 0)
    };

    private static NumericUpDown MakeNumber(NumericUpDown box, int width, short? initial)
    {
        box.Minimum = 0;
        box.Maximum = short.MaxValue;
        box.Width = width;
        box.Margin = new Padding(0, 4, 8, 0);
        box.Value = initial ?? 0;
        return box;
    }
}
