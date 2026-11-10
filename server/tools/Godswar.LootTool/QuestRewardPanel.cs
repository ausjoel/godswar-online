using System.Diagnostics;
using System.Globalization;

namespace Godswar.LootTool;

/// <summary>
/// 「任务奖励」页签：直接改数据库拥有的 <c>quest_reward_slots</c>（奖励物品，8 个槽位）
/// 与 <c>quest_reward_values</c>（经验/TP/银币/金币）。左边是「哪些任务被覆盖过」的
/// 并集列表，右边是选中任务的奖励编辑区。
/// </summary>
/// <remarks>
/// 两张表都是**覆盖**语义：<c>quest_reward_slots</c> 里只要该任务有一行，奖励菜单就
/// 完全由这些行决定（没写的槽位就是空的），所以保存必须整份替换而不是逐槽补写；
/// 没有该任务的行 = 沿用服务端内置的抓包数据。数值同理，取消「覆盖数值」= 删掉那行。
/// <para>
/// 写库本身不影响正在跑的服务器：服务端只在启动时读一次，所以每次保存后界面都会
/// 用橙色字提醒必须重启。工具不碰 Docker，重启由操作者自己做。
/// </para>
/// </remarks>
internal sealed class QuestRewardPanel : UserControl, IAsyncDisposable
{
    private const int SlotColumn = 0;
    private const int ItemIdColumn = 1;
    private const int ItemNameColumn = 2;
    private const int EnabledColumn = 3;
    private const int AttributeColumn = 4;
    private const int AttributeButtonColumn = 5;

    private const string NotConnectedMessage = "请先在顶部连接数据库。";

    private readonly QuestRewardStore _store = new();
    private readonly TextBox _questIdBox = new();
    private readonly TextBox _filterBox = new();
    private readonly ComboBox _factionFilter = new();
    private readonly DataGridView _questGrid = new();
    private readonly DataGridView _slotGrid = new();
    private readonly NumericUpDown _experience = new();
    private readonly NumericUpDown _talentPoints = new();
    private readonly NumericUpDown _silver = new();
    private readonly NumericUpDown _gold = new();
    private readonly CheckBox _overrideValues = new();
    private readonly Label _questSummary = new();
    private readonly Label _currentQuestLabel = new();
    private readonly Label _dirtyLabel = new();
    private readonly Label _restartLabel = new();
    private readonly StatusLabel _status = new();

    /// <summary>手填过、库里还没有行的任务 ID：列表上要留着，否则载入完就找不回来了。</summary>
    private readonly HashSet<int> _manualQuestIds = [];

    /// <summary>8 个槽位各自的物品属性（行是固定的 0-7，所以按下标存最省事）。</summary>
    private readonly ItemAttributeValues[] _slotAttributes =
        Enumerable.Range(0, QuestRewardStore.MaximumSlots)
            .Select(static _ => new ItemAttributeValues())
            .ToArray();

    private ItemAttributeCatalog _attributeCatalog = ItemAttributeCatalog.Empty;
    private bool _attributeColumnsAvailable = true;

    private List<QuestRewardRow> _rows = [];
    private IReadOnlyList<ItemRow> _items = [];
    private Dictionary<int, ItemRow> _itemsById = new();
    private IReadOnlyList<QuestInfo> _questCatalogue = [];
    private QuestCatalog? _catalog;
    private string _clientRoot = string.Empty;
    private int _currentQuestId;
    private bool _connected;
    private bool _busy;
    private bool _dirty;
    private bool _pendingRestart;
    private bool _suppressEvents;
    private string _notReadyMessage = NotConnectedMessage;

    public QuestRewardPanel()
    {
        Dock = DockStyle.Fill;
        Controls.Add(BuildUi());
        UpdateIndicators();
    }

    /// <summary>有未保存的改动，关窗时要问一句。</summary>
    public bool IsDirty => _dirty;

    public void SetStatus(string text) => SetStatus(text, error: false);

    /// <summary>
    /// 客户端根目录（任务目录的解析源）。换目录就把解析缓存作废 ——
    /// 否则 GM 换了客户端还在看上一个客户端的任务表。
    /// </summary>
    public void SetClientRoot(string clientRoot)
    {
        var root = clientRoot?.Trim() ?? string.Empty;
        if (string.Equals(_clientRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _clientRoot = root;
        _catalog = null;
        _questCatalogue = [];
    }

    /// <summary>物品目录（中文名已由 ClientTextCatalog 解析好），由主窗口读库后送进来。</summary>
    public void SetItems(IReadOnlyList<ItemRow> items)
    {
        _items = items;
        _itemsById = items.ToDictionary(static item => item.Id);
        RefreshItemNames();
    }

    /// <summary>附加属性表（id → 中文名/等级上限），由主窗口把客户端与库里的来源合好后送进来。</summary>
    public void SetAttributeCatalog(ItemAttributeCatalog catalog)
    {
        _attributeCatalog = catalog;
    }

    /// <summary>
    /// 旧库还没有那 12 列物品属性时，把「物品属性」与「属性…」两列一起藏起来：
    /// 留着只会让 GM 以为能配，保存时才报错。
    /// </summary>
    public void SetAttributeColumnsVisible(bool visible)
    {
        _attributeColumnsAvailable = visible;
        _slotGrid.Columns[AttributeColumn].Visible = visible;
        _slotGrid.Columns[AttributeButtonColumn].Visible = visible;
    }

    public async Task ConnectAndReadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        _store.Connect(connectionString);
        if (!await _store.HasQuestRewardSchemaAsync(cancellationToken))
        {
            // 旧库还没跑迁移：页签只给提示，不写也不读，免得把「表不存在」当成空数据。
            _connected = false;
            _notReadyMessage = QuestRewardStore.MissingSchemaMessage(_store.DatabaseName);
            SetStatus(_notReadyMessage, error: true);
            return;
        }

        _connected = true;
        _notReadyMessage = NotConnectedMessage;
        await ReadAsync(cancellationToken);
    }

    public async Task ReadAsync(CancellationToken cancellationToken = default)
    {
        if (_busy)
        {
            return;
        }

        await RunAsync(async () =>
        {
            await RefreshListAsync(_currentQuestId > 0 ? _currentQuestId : null);
            SetStatus(
                $"已刷新：{_rows.Count} 个任务有奖励覆盖" +
                "（quest_reward_slots ∪ quest_reward_values 里出现过的 quest_id）。");
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    private Control BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        root.Controls.Add(BuildToolbar(), 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };
        // SplitContainer 刚 new 出来时宽度只有默认的 150，这时设 SplitterDistance 会被夹到
        // 121（列表只剩一条缝），所以等它第一次被布局出真实宽度后再设，只设一次，
        // 之后 GM 自己拖过的位置不会被抢回去。
        var placed = false;
        split.SizeChanged += (_, _) =>
        {
            if (placed || split.Width < 800)
            {
                return;
            }

            placed = true;
            split.Panel1MinSize = 280;
            split.Panel2MinSize = 460;
            split.SplitterDistance = 440;
        };
        split.Panel1.Controls.Add(BuildQuestList());
        split.Panel2.Controls.Add(BuildEditor());
        root.Controls.Add(split, 0, 1);

        _restartLabel.Dock = DockStyle.Fill;
        _restartLabel.TextAlign = ContentAlignment.MiddleLeft;
        _restartLabel.ForeColor = Color.DarkOrange;
        root.Controls.Add(_restartLabel, 0, 2);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = NotConnectedMessage;
        root.Controls.Add(_status, 0, 3);
        return root;
    }

    private Control BuildToolbar()
    {
        var rows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        actions.Controls.Add(Label("任务 ID"));
        _questIdBox.Width = 90;
        _questIdBox.Margin = new Padding(0, 4, 0, 0);
        _questIdBox.KeyDown += async (_, e) =>
        {
            // 回车 = 点「载入」，手填 ID 是这个页签最常用的入口
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            e.SuppressKeyPress = true;
            await LoadTypedQuestAsync();
        };
        actions.Controls.Add(_questIdBox);
        actions.Controls.Add(MakeButton("载入", LoadTypedQuestAsync, 66));
        actions.Controls.Add(MakeButton("搜索任务", SearchQuestsAsync, 84));
        actions.Controls.Add(Label("按名字/ID 过滤"));
        _filterBox.Width = 120;
        _filterBox.Margin = new Padding(0, 4, 0, 0);
        _filterBox.TextChanged += (_, _) =>
            RefreshQuestGrid(_currentQuestId > 0 ? _currentQuestId : null);
        actions.Controls.Add(_filterBox);
        actions.Controls.Add(Label("阵营"));
        _factionFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _factionFilter.Width = 84;
        _factionFilter.Margin = new Padding(0, 4, 0, 0);
        _factionFilter.Items.AddRange(["全部", "斯巴达", "雅典"]);
        _factionFilter.SelectedIndex = 0;
        _factionFilter.SelectedIndexChanged += (_, _) =>
            RefreshQuestGrid(_currentQuestId > 0 ? _currentQuestId : null);
        actions.Controls.Add(_factionFilter);
        actions.Controls.Add(MakeButton("刷新", () => ReadAsync(), 66));
        _dirtyLabel.AutoSize = true;
        _dirtyLabel.Padding = new Padding(16, 8, 0, 0);
        actions.Controls.Add(_dirtyLabel);
        rows.Controls.Add(actions, 0, 0);

        _questSummary.AutoSize = true;
        _questSummary.Padding = new Padding(0, 6, 0, 0);
        _questSummary.ForeColor = Color.DimGray;
        rows.Controls.Add(_questSummary, 0, 1);
        return rows;
    }

    private Control BuildQuestList()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        ConfigureGrid(_questGrid, readOnly: true);
        _questGrid.Columns.Add(Column("任务ID", 58));
        _questGrid.Columns.Add(Column("等级+名字", 250));
        _questGrid.Columns.Add(Column("阵营", 58));
        _questGrid.Columns.Add(Column("覆盖状态", 175));
        _questGrid.SelectionChanged += async (_, _) => await SelectQuestFromGridAsync();
        panel.Controls.Add(_questGrid, 0, 0);

        _currentQuestLabel.Dock = DockStyle.Fill;
        _currentQuestLabel.TextAlign = ContentAlignment.MiddleLeft;
        _currentQuestLabel.ForeColor = Color.DimGray;
        _currentQuestLabel.Text = "当前任务：未选择";
        panel.Controls.Add(_currentQuestLabel, 0, 1);
        return panel;
    }

    private Control BuildEditor()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));

        root.Controls.Add(BuildSlotGroup(), 0, 0);
        root.Controls.Add(BuildValueGroup(), 0, 1);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Text =
                "语义：quest_reward_slots 里只要该任务有一行，奖励菜单就完全由这些行决定" +
                "（未勾选的槽位=空，不是「部分覆盖」）；没有行=沿用服务端内置的抓包数据（内置内容工具不显示）。" +
                "quest_reward_values 有行=覆盖交付时的经验/TP/银币/金币，取消勾选并保存=删掉该行、恢复内置。"
        };
        root.Controls.Add(hint, 0, 2);
        return root;
    }

    private Control BuildSlotGroup()
    {
        var group = new GroupBox
        {
            Text = "奖励物品（槽位 0-7，勾选「启用」= 该槽有奖励）",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        ConfigureSlotGrid();
        layout.Controls.Add(_slotGrid, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0)
        };
        actions.Controls.Add(MakeButton("浏览…", PickItem, 84));
        actions.Controls.Add(MakeButton("重新载入该任务", ReloadCurrentQuestAsync, 128));
        actions.Controls.Add(MakeButton("清空该任务覆盖", ClearQuestAsync, 128));
        actions.Controls.Add(MakeButton("保存", SaveAsync, 84));
        layout.Controls.Add(actions, 0, 1);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildValueGroup()
    {
        var group = new GroupBox
        {
            Text = "奖励数值",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0)
        };
        _overrideValues.Text = "覆盖数值";
        _overrideValues.AutoSize = true;
        _overrideValues.Padding = new Padding(0, 8, 10, 0);
        _overrideValues.CheckedChanged += (_, _) =>
        {
            UpdateValueInputsEnabled();
            MarkDirty();
        };
        flow.Controls.Add(_overrideValues);
        flow.Controls.Add(Label("经验"));
        flow.Controls.Add(MakeNumber(_experience, 110));
        flow.Controls.Add(Label("TP（天赋点）"));
        flow.Controls.Add(MakeNumber(_talentPoints, 80));
        flow.Controls.Add(Label("银币"));
        flow.Controls.Add(MakeNumber(_silver, 110));
        flow.Controls.Add(Label("金币"));
        flow.Controls.Add(MakeNumber(_gold, 90));
        flow.Controls.Add(MakeButton("保存", SaveAsync, 84));
        group.Controls.Add(flow);
        return group;
    }

    private void ConfigureSlotGrid()
    {
        // 固定 8 行：槽位数是客户端画出来的，界面不提供增删行，只提供勾选/取消
        _slotGrid.Dock = DockStyle.Fill;
        _slotGrid.AllowUserToAddRows = false;
        _slotGrid.AllowUserToDeleteRows = false;
        _slotGrid.AllowUserToResizeRows = false;
        _slotGrid.RowHeadersVisible = false;
        _slotGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _slotGrid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        _slotGrid.AutoGenerateColumns = false;
        _slotGrid.BackgroundColor = SystemColors.Window;
        _slotGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "槽位",
            Width = 60,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _slotGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "物品ID",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _slotGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "物品名",
            Width = 330,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _slotGrid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            HeaderText = "启用",
            Width = 60,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _slotGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "物品属性",
            Width = 250,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _slotGrid.Columns.Add(new DataGridViewButtonColumn
        {
            HeaderText = "属性",
            Width = 66,
            Text = "属性…",
            UseColumnTextForButtonValue = true,
            FlatStyle = FlatStyle.Flat,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _slotGrid.CellValueChanged += OnSlotCellValueChanged;
        _slotGrid.CellContentClick += OnSlotCellContentClick;
        _slotGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            // 勾选框改动要立刻提交，否则 CellValueChanged 不会触发
            if (_slotGrid.IsCurrentCellDirty)
            {
                _slotGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        // 建初始行时不能算成「改动」，否则页签一打开就显示有未保存内容
        _suppressEvents = true;
        try
        {
            for (var slot = 0; slot < QuestRewardStore.MaximumSlots; slot++)
            {
                var index = _slotGrid.Rows.Add();
                var row = _slotGrid.Rows[index];
                row.Cells[SlotColumn].Value = slot;
                row.Cells[ItemIdColumn].Value = 0;
                row.Cells[ItemNameColumn].Value = DescribeItem(0);
                row.Cells[EnabledColumn].Value = false;
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private async Task RunAsync(Func<Task> action, bool requireConnection = true)
    {
        if (requireConnection && !_connected)
        {
            SetStatus(_notReadyMessage, error: true);
            return;
        }

        try
        {
            _busy = true;
            UseWaitCursor = true;
            await action();
        }
        catch (Exception ex) when (
            ex is QuestRewardToolException or Npgsql.PostgresException or
                Npgsql.NpgsqlException or InvalidOperationException or IOException)
        {
            SetStatus($"操作失败：{ex.Message}", error: true);
        }
        finally
        {
            _busy = false;
            UseWaitCursor = false;
        }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.SeaGreen;
    }

    private int SelectedQuestId =>
        _questGrid.CurrentRow?.Tag is int questId ? questId : 0;

    private async Task RefreshListAsync(int? keepSelectedId)
    {
        _rows = await _store.LoadQuestRowsAsync();
        RefreshQuestGrid(keepSelectedId);
    }

    /// <summary>
    /// 「搜索任务」：把客户端任务表整份解析出来（解析一次就缓存住，几千条不必每次重来），
    /// 然后和库里的覆盖情况合起来填列表。解析失败时把期望路径原样报出来。
    /// </summary>
    private async Task SearchQuestsAsync()
    {
        if (_clientRoot.Length == 0 || !Directory.Exists(_clientRoot))
        {
            SetStatus(
                "还没有可用的客户端目录：请在顶部「客户端目录」里选到游戏客户端根目录" +
                "（要含 Localization 文件夹），再点「搜索任务」。",
                error: true);
            return;
        }

        await RunAsync(
            async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                var catalog = _catalog;
                if (catalog is null)
                {
                    // 读上千个 .dat 是纯文件 IO，扔到线程池别把界面顶住
                    catalog = await Task.Run(() => QuestCatalog.Load(_clientRoot));
                    _catalog = catalog;
                    _questCatalogue = catalog.Quests;
                }

                stopwatch.Stop();
                if (catalog.Error is { Length: > 0 })
                {
                    _questCatalogue = [];
                    RefreshQuestGrid(_currentQuestId > 0 ? _currentQuestId : null);
                    SetStatus(catalog.Error, error: true);
                    return;
                }

                var keep = _currentQuestId > 0 ? _currentQuestId : (int?)null;
                if (_connected)
                {
                    await RefreshListAsync(keep);
                }
                else
                {
                    RefreshQuestGrid(keep);
                }

                SetStatus(
                    $"已读取客户端任务表 {catalog.QuestXmlPath}：" +
                    $"{catalog.Quests.Count} 个任务" +
                    $"，其中 {catalog.TitleCount} 条的标题来自 " +
                    $"{catalog.QuestTextDirectory}\\*.dat" +
                    $"（语言目录 {catalog.Language}，耗时 {stopwatch.ElapsedMilliseconds} ms）。");
            },
            requireConnection: false);
    }

    /// <summary>列表的一行：任务本身的资料 + 库里有没有覆盖。</summary>
    private sealed record QuestListRow(
        int Id,
        string Title,
        string Faction,
        string Coverage,
        bool IsOverridden);

    private List<QuestListRow> BuildQuestRows()
    {
        var coverage = _rows.ToDictionary(static row => row.QuestId);
        var catalogue = _questCatalogue.ToDictionary(static quest => quest.Id);
        var ids = new SortedSet<int>();
        foreach (var quest in _questCatalogue)
        {
            ids.Add(quest.Id);
        }

        foreach (var row in _rows)
        {
            ids.Add(row.QuestId);
        }

        foreach (var id in _manualQuestIds)
        {
            ids.Add(id);
        }

        var rows = new List<QuestListRow>(ids.Count);
        foreach (var id in ids)
        {
            catalogue.TryGetValue(id, out var quest);
            coverage.TryGetValue(id, out var state);
            rows.Add(new QuestListRow(
                id,
                quest?.Title ?? "（客户端任务表里没有这个 ID）",
                quest?.FactionName ?? "—",
                state?.Status ?? "未覆盖（沿用内置）",
                state?.IsOverridden ?? false));
        }

        return rows;
    }

    private void RefreshQuestGrid(int? keepSelectedId)
    {
        var query = _filterBox.Text.Trim();
        var all = BuildQuestRows();
        IEnumerable<QuestListRow> matches = all;

        // 阵营筛选：0=全部，1=斯巴达，2=雅典（未标注的 -1 只在「全部」里出现）
        var factionIndex = _factionFilter.SelectedIndex;
        if (factionIndex is 1 or 2)
        {
            var wanted = factionIndex == 1 ? "斯巴达" : "雅典";
            matches = matches.Where(row => row.Faction == wanted);
        }

        if (query.Length > 0)
        {
            matches = matches.Where(row =>
                row.Id.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal) ||
                row.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var visible = matches.ToList();
        _suppressEvents = true;
        try
        {
            _questGrid.Rows.Clear();
            foreach (var row in visible)
            {
                var index = _questGrid.Rows.Add();
                var gridRow = _questGrid.Rows[index];
                gridRow.Tag = row.Id;
                gridRow.Cells[0].Value = row.Id;
                gridRow.Cells[1].Value = row.Title;
                gridRow.Cells[2].Value = row.Faction;
                gridRow.Cells[3].Value = row.Coverage;
                if (!row.IsOverridden)
                {
                    // 一眼看出这个任务现在还是内置奖励
                    gridRow.Cells[3].Style.ForeColor = Color.Firebrick;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }

        SelectQuestRow(keepSelectedId);
        var manualOnly = _manualQuestIds.Count(id => _rows.All(row => row.QuestId != id));
        var catalogueText = _questCatalogue.Count > 0
            ? $"客户端任务表 {_questCatalogue.Count} 条"
            : "还没搜索客户端任务表（点「搜索任务」列出全部任务）";
        _questSummary.Text =
            $"列表 {visible.Count} / {all.Count} 个任务｜{catalogueText}" +
            $"｜库里已覆盖 {_rows.Count} 个" +
            (manualOnly > 0 ? $"｜手填 {manualOnly} 个" : string.Empty);
    }

    private void SelectQuestRow(int? questId)
    {
        if (questId is not { } id)
        {
            return;
        }

        _suppressEvents = true;
        try
        {
            foreach (DataGridViewRow row in _questGrid.Rows)
            {
                if (row.Tag is int value && value == id)
                {
                    row.Selected = true;
                    _questGrid.CurrentCell = row.Cells[0];
                    return;
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private async Task SelectQuestFromGridAsync()
    {
        if (_suppressEvents || _busy)
        {
            return;
        }

        var questId = SelectedQuestId;
        if (questId <= 0 || questId == _currentQuestId)
        {
            return;
        }

        if (!ConfirmDiscardChanges())
        {
            SelectQuestRow(_currentQuestId);
            return;
        }

        await LoadQuestAsync(questId);
    }

    private async Task LoadTypedQuestAsync()
    {
        if (!TryParseQuestId(_questIdBox.Text, out var questId))
        {
            SetStatus("任务 ID 必须是大于 0 的整数。", error: true);
            return;
        }

        if (!ConfirmDiscardChanges())
        {
            return;
        }

        await LoadQuestAsync(questId);
    }

    private async Task ReloadCurrentQuestAsync()
    {
        if (_currentQuestId <= 0)
        {
            SetStatus("请先在左侧选中一个任务，或填任务 ID 后点「载入」。", error: true);
            return;
        }

        if (!ConfirmDiscardChanges())
        {
            return;
        }

        await LoadQuestAsync(_currentQuestId);
    }

    private async Task LoadQuestAsync(int questId)
    {
        if (questId <= 0)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var slots = await _store.LoadSlotsAsync(questId);
            var values = await _store.LoadValuesAsync(questId);
            _currentQuestId = questId;
            _manualQuestIds.Add(questId);
            _questIdBox.Text = questId.ToString(CultureInfo.InvariantCulture);
            BindSlots(slots);
            BindValues(values);
            await RefreshListAsync(questId);
            SetCurrentQuestHint(questId, slots.Count, values is not null);
            _dirty = false;
            UpdateIndicators();
            SetStatus(slots.Count == 0 && values is null
                ? $"任务 {questId} 未覆盖（沿用内置）：两张表里都没有它的行，槽位与数值都是空的。"
                : $"已载入任务 {questId} 现有的覆盖。改完点「保存」写库。");
        });
    }

    private async Task SaveAsync()
    {
        if (_currentQuestId <= 0)
        {
            SetStatus("请先在左侧选中一个任务，或填任务 ID 后点「载入」。", error: true);
            return;
        }

        // 输入框里换了 ID 却没点「载入」时，先问一句，别把改动写到上一个任务上
        if (TryParseQuestId(_questIdBox.Text, out var typedId) &&
            typedId != _currentQuestId &&
            !Confirm(
                $"输入框里是任务 {typedId}，但当前编辑的是任务 {_currentQuestId}。\n" +
                $"保存到任务 {_currentQuestId} 吗？（要改任务 {typedId} 请先点「载入」）"))
        {
            return;
        }

        List<QuestRewardSlotInput> slots;
        try
        {
            slots = ReadSlots();
        }
        catch (QuestRewardToolException ex)
        {
            SetStatus(ex.Message, error: true);
            return;
        }

        var questId = _currentQuestId;
        var overrideValues = _overrideValues.Checked;
        var values = new QuestRewardValues(
            (int)_experience.Value,
            (int)_talentPoints.Value,
            (int)_silver.Value,
            (int)_gold.Value);

        await RunAsync(async () =>
        {
            await _store.SaveAsync(questId, slots, overrideValues, values);

            // 保存后重新读回，界面上留下的就是库里真实落下的内容
            var savedSlots = await _store.LoadSlotsAsync(questId);
            var savedValues = await _store.LoadValuesAsync(questId);
            BindSlots(savedSlots);
            BindValues(savedValues);
            await RefreshListAsync(questId);
            SetCurrentQuestHint(questId, savedSlots.Count, savedValues is not null);
            _dirty = false;
            _pendingRestart = true;
            UpdateIndicators();

            var slotText = slots.Count == 0
                ? "奖励物品为空（该任务不再给物品）"
                : $"{slots.Count} 个奖励槽（" +
                  string.Join(
                      "、",
                      slots.Select(static slot => $"槽{slot.SlotIndex}=物品{slot.ItemId}")) +
                  "）";
            var valueText = overrideValues
                ? $"数值覆盖：经验 {values.Experience}／TP {values.TalentPoints}／" +
                  $"银币 {values.Silver}／金币 {values.Gold}"
                : "数值未覆盖（已删除 quest_reward_values 里该任务的行，恢复内置）";
            SetStatus($"已保存任务 {questId}：{slotText}；{valueText}。需要重启游戏服务器才生效。");
            MessageBox.Show(
                this,
                $"已保存任务 {questId} 的奖励。\n\n{slotText}\n{valueText}\n\n" +
                "注意：任务奖励只在游戏服务器启动时读取，需要重启服务器才会生效。",
                "保存成功",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        });
    }

    private async Task ClearQuestAsync()
    {
        if (_currentQuestId <= 0)
        {
            SetStatus("请先在左侧选中一个任务，或填任务 ID 后点「载入」。", error: true);
            return;
        }

        var questId = _currentQuestId;
        if (!Confirm(
                $"清空任务 {questId} 的全部奖励覆盖？\n" +
                "会删掉 quest_reward_slots 与 quest_reward_values 里该任务的所有行，" +
                "该任务回到服务端内置的抓包奖励（同样需要重启服务器才生效）。"))
        {
            return;
        }

        await RunAsync(async () =>
        {
            await _store.DeleteOverridesAsync(questId);
            BindSlots([]);
            BindValues(null);
            await RefreshListAsync(questId);
            SetCurrentQuestHint(questId, 0, false);
            _dirty = false;
            _pendingRestart = true;
            UpdateIndicators();
            SetStatus(
                $"已清空任务 {questId} 的覆盖：两张表里该任务的行都删掉了，回到内置奖励。" +
                "需要重启游戏服务器才生效。");
        });
    }

    private void PickItem()
    {
        if (_items.Count == 0)
        {
            SetStatus(
                "物品目录还是空的：请先在顶部连接数据库并点「一键读取数据」。",
                error: true);
            return;
        }

        var row = _slotGrid.CurrentCell?.OwningRow;
        if (row is null)
        {
            SetStatus("请先在奖励物品表里选中一个槽位。", error: true);
            return;
        }

        using var picker = new ItemPickerForm(
            _items,
            ReadItemId(row, SlotOf(row), required: false),
            "选择奖励物品");
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _suppressEvents = true;
        try
        {
            row.Cells[ItemIdColumn].Value = picker.SelectedItemId;
            row.Cells[ItemNameColumn].Value = DescribeItem(picker.SelectedItemId);
            // 刚选完物品就勾上启用：GM 打开物品表就是为了给这个槽加奖励
            row.Cells[EnabledColumn].Value = true;
        }
        finally
        {
            _suppressEvents = false;
        }

        _dirty = true;
        UpdateIndicators();
        SetStatus(
            $"槽位 {SlotOf(row)} 选了物品 {picker.SelectedItemId}（已自动勾选「启用」，" +
            "点「保存」才写库）。");
    }

    private void OnSlotCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0)
        {
            return;
        }

        var row = _slotGrid.Rows[e.RowIndex];
        if (e.ColumnIndex == ItemIdColumn)
        {
            _suppressEvents = true;
            try
            {
                row.Cells[ItemNameColumn].Value =
                    DescribeItem(ReadItemId(row, SlotOf(row), required: false));
            }
            finally
            {
                _suppressEvents = false;
            }
        }

        MarkDirty();
    }

    private void BindSlots(IReadOnlyList<QuestRewardSlot> slots)
    {
        var bySlot = slots.ToDictionary(static slot => (int)slot.SlotIndex);
        _suppressEvents = true;
        try
        {
            foreach (DataGridViewRow row in _slotGrid.Rows)
            {
                var slot = (int)Convert.ToInt32(
                    row.Cells[SlotColumn].Value,
                    CultureInfo.InvariantCulture);
                if (bySlot.TryGetValue(slot, out var entry) && entry is not null)
                {
                    row.Cells[ItemIdColumn].Value = entry.ItemId;
                    row.Cells[ItemNameColumn].Value = DescribeItem(entry.ItemId);
                    row.Cells[EnabledColumn].Value = true;
                    SetSlotAttributes(slot, entry.Attributes ?? new ItemAttributeValues());
                }
                else
                {
                    row.Cells[ItemIdColumn].Value = 0;
                    row.Cells[ItemNameColumn].Value = DescribeItem(0);
                    row.Cells[EnabledColumn].Value = false;
                    SetSlotAttributes(slot, new ItemAttributeValues());
                }
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    /// <summary>把某个槽位的属性写进数组并刷新摘要列。</summary>
    private void SetSlotAttributes(int slot, ItemAttributeValues attributes)
    {
        _slotAttributes[slot] = attributes;
        _slotGrid.Rows[slot].Cells[AttributeColumn].Value = attributes.Summary;
    }

    /// <summary>「属性…」按钮：弹窗改这一件奖励物品的品质/等级/附加属性。</summary>
    private void OnSlotCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != AttributeButtonColumn)
        {
            return;
        }

        var row = _slotGrid.Rows[e.RowIndex];
        var slot = (int)Convert.ToInt32(
            row.Cells[SlotColumn].Value,
            CultureInfo.InvariantCulture);
        var itemId = row.Cells[ItemIdColumn].Value is int value ? value : 0;
        using var dialog = new ItemAttributeDialog(
            _attributeCatalog,
            itemId > 0 ? DescribeItem(itemId) : $"槽位 {slot}（还没选物品）",
            _slotAttributes[slot]);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _suppressEvents = true;
        try
        {
            SetSlotAttributes(slot, dialog.Values);
        }
        finally
        {
            _suppressEvents = false;
        }

        _dirty = true;
        UpdateIndicators();
        SetStatus(
            $"槽位 {slot} 的物品属性已改为：{dialog.Values.Summary}（点「保存」才写库）。");
    }

    private void BindValues(QuestRewardValues? values)
    {
        _suppressEvents = true;
        try
        {
            _overrideValues.Checked = values is not null;
            _experience.Value = values?.Experience ?? 0;
            _talentPoints.Value = values?.TalentPoints ?? 0;
            _silver.Value = values?.Silver ?? 0;
            _gold.Value = values?.Gold ?? 0;
        }
        finally
        {
            _suppressEvents = false;
        }

        UpdateValueInputsEnabled();
    }

    private void RefreshItemNames()
    {
        _suppressEvents = true;
        try
        {
            foreach (DataGridViewRow row in _slotGrid.Rows)
            {
                var itemId = ReadItemId(row, SlotOf(row), required: false);
                row.Cells[ItemNameColumn].Value = DescribeItem(itemId);
            }
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void UpdateValueInputsEnabled()
    {
        // 没勾「覆盖数值」时输入框就是内置值的位置，禁用比留空更不容易看错
        var enabled = _overrideValues.Checked;
        _experience.Enabled = enabled;
        _talentPoints.Enabled = enabled;
        _silver.Enabled = enabled;
        _gold.Enabled = enabled;
    }

    private void SetCurrentQuestHint(int questId, int slotCount, bool hasValues)
    {
        // 搜过客户端任务表就把「阵营｜等级+名字」一起报出来，GM 不用回去翻 ID
        var quest = _questCatalogue.FirstOrDefault(candidate => candidate.Id == questId);
        var name = quest is null
            ? string.Empty
            : $"（{quest.FactionName}｜{quest.Title}）";
        _currentQuestLabel.Text = slotCount == 0 && !hasValues
            ? $"当前任务 {questId}{name}：未覆盖（沿用内置）——两张表里都没有它的行"
            : $"当前任务 {questId}{name}：" +
              (slotCount == 0 ? "物品未覆盖" : $"{slotCount} 个奖励槽") +
              "，" +
              (hasValues ? "数值已覆盖" : "数值未覆盖（沿用内置）");
    }

    private void MarkDirty()
    {
        if (_suppressEvents)
        {
            return;
        }

        _dirty = true;
        UpdateIndicators();
    }

    private void UpdateIndicators()
    {
        _dirtyLabel.Text = _dirty ? "● 有未保存的改动" : "○ 无未保存改动";
        _dirtyLabel.ForeColor = _dirty ? Color.Firebrick : Color.DimGray;
        _restartLabel.Text = _pendingRestart
            ? "⚠ 已写入数据库，但必须重启游戏服务器才会生效（任务奖励只在服务器启动时读取）"
            : string.Empty;
    }

    private List<QuestRewardSlotInput> ReadSlots()
    {
        var slots = new List<QuestRewardSlotInput>();
        foreach (DataGridViewRow row in _slotGrid.Rows)
        {
            var slot = (short)Convert.ToInt32(
                row.Cells[SlotColumn].Value,
                CultureInfo.InvariantCulture);
            if (row.Cells[EnabledColumn].Value is not true)
            {
                // 未勾选 = 该槽为空：界面上留着的物品 ID 不写库
                continue;
            }

            var itemId = ReadItemId(row, slot, required: true);
            if (itemId <= 0)
            {
                throw new QuestRewardToolException(
                    $"槽位 {slot} 勾了「启用」却没有物品：请用「浏览…」选一个，或取消勾选。");
            }

            slots.Add(new QuestRewardSlotInput(slot, itemId, _slotAttributes[slot]));
        }

        return slots;
    }

    private static int ReadItemId(DataGridViewRow row, int slot, bool required)
    {
        var text = Convert.ToString(row.Cells[ItemIdColumn].Value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        if (int.TryParse(
                text.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var itemId))
        {
            return itemId;
        }

        if (required)
        {
            throw new QuestRewardToolException($"槽位 {slot} 的物品 ID「{text}」不是整数。");
        }

        return 0;
    }

    private static int SlotOf(DataGridViewRow row) =>
        Convert.ToInt32(row.Cells[SlotColumn].Value, CultureInfo.InvariantCulture);

    private string DescribeItem(int itemId)
    {
        if (itemId <= 0)
        {
            return "（空槽）";
        }

        if (!_itemsById.TryGetValue(itemId, out var item))
        {
            return $"⚠ 未知物品 {itemId}（item_templates 中不存在，保存会被外键拒绝）";
        }

        var name = string.IsNullOrWhiteSpace(item.ChineseName)
            ? item.DisplayName
            : $"{item.ChineseName} / {item.DisplayName}";
        return $"{name}（{item.Kind}）";
    }

    private bool ConfirmDiscardChanges()
    {
        if (!_dirty)
        {
            return true;
        }

        return Confirm("当前任务有未保存的改动，切换后将丢失。继续吗？");
    }

    private static bool TryParseQuestId(string text, out int questId) =>
        int.TryParse(
            text.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out questId) && questId > 0;

    private bool Confirm(string message) =>
        MessageBox.Show(
            this,
            message,
            "确认",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;

    private static void ConfigureGrid(DataGridView grid, bool readOnly)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = readOnly;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.BackgroundColor = SystemColors.Window;
        grid.Font = new Font("Consolas", 9F);
    }

    private static DataGridViewTextBoxColumn Column(string header, int width) =>
        new() { HeaderText = header, Width = width };

    private static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(8, 8, 2, 0)
    };

    private NumericUpDown MakeNumber(NumericUpDown box, int width)
    {
        box.Minimum = 0;
        box.Maximum = int.MaxValue;
        box.ThousandsSeparator = true;
        box.Width = width;
        box.Margin = new Padding(0, 4, 8, 0);
        box.ValueChanged += (_, _) => MarkDirty();
        return box;
    }

    private Button MakeButton(string text, Func<Task> action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += async (_, _) => await action();
        return button;
    }

    private Button MakeButton(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += (_, _) => action();
        return button;
    }

    private sealed class StatusLabel : Label
    {
        public StatusLabel()
        {
            AutoSize = false;
            Height = 22;
        }
    }
}
