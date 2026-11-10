using System.Globalization;

namespace Godswar.LootTool;

/// <summary>
/// The Lelantine Farm tab: the activity's score board and both of its ledgers,
/// the eggs and tuck nets a character carries, and read-only views of the rules
/// the server keeps in code and of the roster the current revision publishes.
/// </summary>
/// <remarks>
/// The faction total is derived from the ledgers, so this panel never edits a
/// faction total: it edits the rows the total is the sum of. Every write is a
/// transaction, refreshes the view afterwards, and states its consequence in the
/// confirmation prompt.
/// </remarks>
internal sealed class FarmPanel : UserControl, IAsyncDisposable
{
    private readonly FarmStore _store = new();
    private readonly TextBox _query = new();
    private readonly DataGridView _characterGrid = new();
    private readonly DataGridView _donationGrid = new();
    private readonly DataGridView _killGrid = new();
    private readonly DataGridView _bagGrid = new();
    private readonly DataGridView _ruleGrid = new();
    private readonly DataGridView _npcGrid = new();
    private readonly ComboBox _grantItem = new();
    private readonly ComboBox _grantQuality = new();
    private readonly NumericUpDown _grantQuantity = new();
    private readonly TextBox _grantNote = new();
    private readonly CheckBox _dryRun = new();
    private readonly NumericUpDown _adjustPoints = new();
    private readonly ComboBox _adjustFaction = new();
    private readonly ComboBox _resetFaction = new();
    private readonly Label _totals = new();
    private readonly Label _characterHint = new();
    private readonly StatusLabel _status = new();

    private IReadOnlyList<FarmCharacterRow> _characters = [];
    private List<FarmDonationRow> _donations = [];
    private List<FarmKillRow> _kills = [];
    private List<FarmBagRow> _bag = [];
    private bool _connected;
    private bool _busy;

    public FarmPanel()
    {
        Dock = DockStyle.Fill;
        Controls.Add(BuildUi());
    }

    public void SetStatus(string text) => SetStatus(text, error: false);

    public async Task ConnectAndReadAsync(
        string connectionString,
        string clientRoot,
        CancellationToken cancellationToken = default)
    {
        _store.Connect(connectionString);
        if (!await _store.HasFarmSchemaAsync(cancellationToken))
        {
            _connected = false;
            SetStatus(
                $"数据库 {_store.DatabaseName} 没有农场积分表/视图（服务端迁移 " +
                "20260926_210 未应用），农场页只读部分仍可查看。",
                error: true);
            return;
        }

        _connected = true;
        await ReadAsync(cancellationToken);
    }

    public async Task ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected || _busy)
        {
            return;
        }

        await RunAsync(
            async () =>
            {
                var totals = await _store.LoadTotalsAsync(cancellationToken);
                _totals.Text =
                    $"阵营总分（= 同阵营个人积分之和）：斯巴达 {totals.SpartaPoints}｜" +
                    $"雅典 {totals.AthensPoints}｜有分角色 {totals.Members} 个｜" +
                    $"捐卵流水 {totals.DonationRows} 行 / 击杀行 {totals.KillRows} 行";
                _characters = await _store.LoadCharactersAsync(
                    _query.Text,
                    limit: 500,
                    cancellationToken);
                FillCharacterGrid();
                await ReloadSelectedAsync(cancellationToken);
                FillRuleGrid();
                await FillNpcGridAsync(cancellationToken);
                _characterHint.Text =
                    $"角色 {_characters.Count} 个（按分数排名）｜" +
                    "分数由两张流水表派生，改流水即改阵营分";
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    private async Task RunAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            SetStatus("请先在顶部连接数据库（数据库里必须有农场积分表/视图）。", error: true);
            return;
        }

        try
        {
            _busy = true;
            UseWaitCursor = true;
            await action();
        }
        catch (Exception ex) when (
            ex is FarmToolException or Npgsql.PostgresException or
                Npgsql.NpgsqlException or InvalidOperationException)
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

    private FarmCharacterRow? Selected =>
        _characterGrid.CurrentRow?.Index is { } index &&
        index >= 0 && index < _characters.Count
            ? _characters[index]
            : null;

    private Control BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        root.Controls.Add(BuildToolbar(), 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 520,
            FixedPanel = FixedPanel.Panel1
        };
        split.Panel1.Controls.Add(BuildCharacterGrid());
        split.Panel2.Controls.Add(BuildDetailTabs());
        root.Controls.Add(split, 0, 1);

        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = "请先在顶部连接数据库。";
        root.Controls.Add(_status, 0, 2);
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
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

        var search = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        search.Controls.Add(new Label
        {
            Text = "角色名 / ID",
            AutoSize = true,
            Padding = new Padding(0, 8, 2, 0)
        });
        _query.Width = 200;
        _query.Margin = new Padding(0, 4, 0, 0);
        search.Controls.Add(_query);
        search.Controls.Add(Button("查询", async () => await ReadAsync()));
        search.Controls.Add(Button("显示全部", async () =>
        {
            _query.Text = string.Empty;
            await ReadAsync();
        }));
        search.Controls.Add(Button("刷新积分总览", async () => await ReadAsync()));
        _characterHint.AutoSize = true;
        _characterHint.Padding = new Padding(12, 8, 0, 0);
        search.Controls.Add(_characterHint);
        rows.Controls.Add(search, 0, 0);

        _totals.AutoSize = true;
        _totals.Padding = new Padding(0, 6, 0, 0);
        rows.Controls.Add(_totals, 0, 1);
        return rows;
    }

    private Control BuildCharacterGrid()
    {
        ConfigureGrid(_characterGrid, readOnly: true);
        _characterGrid.Columns.Add(Column("ID", 60));
        _characterGrid.Columns.Add(Column("角色名", 150));
        _characterGrid.Columns.Add(Column("阵营", 70));
        _characterGrid.Columns.Add(Column("个人积分", 90));
        _characterGrid.Columns.Add(Column("捐卵分", 90));
        _characterGrid.Columns.Add(Column("击杀分", 90));
        _characterGrid.Columns.Add(Column("排名", 70));
        _characterGrid.SelectionChanged += async (_, _) =>
        {
            if (_busy)
            {
                return;
            }

            await RunAsync(async () => await ReloadSelectedAsync(CancellationToken.None));
        };
        return _characterGrid;
    }

    private Control BuildDetailTabs()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildDonationTab());
        tabs.TabPages.Add(BuildKillTab());
        tabs.TabPages.Add(BuildBagTab());
        tabs.TabPages.Add(BuildResetTab());
        tabs.TabPages.Add(BuildRuleTab());
        tabs.TabPages.Add(BuildNpcTab());
        return tabs;
    }

    private TabPage BuildDonationTab()
    {
        var page = new TabPage("捐卵流水");
        var root = DockGrid(page);
        ConfigureGrid(_donationGrid, readOnly: true);
        _donationGrid.Columns.Add(Column("行 ID", 70));
        _donationGrid.Columns.Add(Column("阵营", 70));
        _donationGrid.Columns.Add(Column("物品", 70));
        _donationGrid.Columns.Add(Column("卵数", 60));
        _donationGrid.Columns.Add(Column("分值", 80));
        _donationGrid.Columns.Add(Column("时间", 150));
        _donationGrid.Columns.Add(Column("说明", 220));
        root.Controls.Add(_donationGrid, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        actions.Controls.Add(Button("改选中行分值", async () =>
        {
            if (Selected is not { } character ||
                _donationGrid.CurrentRow?.Index is not { } index ||
                index < 0 || index >= _donations.Count)
            {
                SetStatus("请先选中一行捐卵流水。", error: true);
                return;
            }

            var row = _donations[index];
            var text = Prompt.Show(
                $"把流水 {row.Id} 的分值改成（当前 {row.Points}）：",
                "改分值",
                row.Points.ToString(CultureInfo.InvariantCulture));
            if (text is null)
            {
                return;
            }

            if (!LelantineFarmRules.TryParsePoints(text, out var points) || points < 1)
            {
                SetStatus("分值必须是大于 0 的整数；要清零请删除该行。", error: true);
                return;
            }

            await RunAsync(async () =>
            {
                await _store.SetDonationPointsAsync(row.Id, points);
                await RefreshAfterWriteAsync(
                    $"流水 {row.Id} 分值改为 {points}（阵营总分随之变化）");
            });
        }));
        actions.Controls.Add(Button("删除选中行", async () =>
        {
            if (Selected is not { } character ||
                _donationGrid.CurrentRow?.Index is not { } index ||
                index < 0 || index >= _donations.Count)
            {
                SetStatus("请先选中一行捐卵流水。", error: true);
                return;
            }

            var row = _donations[index];
            if (!Confirm(
                    $"删除流水 {row.Id}（{row.Points} 分）？\n" +
                    $"角色 {character.Name} 与" +
                    $"{(row.Faction == LelantineFarmRules.SpartaCamp ? "斯巴达" : "雅典")}" +
                    "阵营的总分会同时下降，且不可撤销。"))
            {
                return;
            }

            await RunAsync(async () =>
            {
                await _store.DeleteDonationAsync(row.Id);
                await RefreshAfterWriteAsync($"已删除流水 {row.Id}");
            });
        }));
        actions.Controls.Add(new Label
        {
            Text = "  手动加一笔调整（item_id=0 的调整行）：",
            AutoSize = true,
            Padding = new Padding(12, 8, 0, 0)
        });
        _adjustFaction.DropDownStyle = ComboBoxStyle.DropDownList;
        _adjustFaction.Items.AddRange(["斯巴达", "雅典"]);
        _adjustFaction.SelectedIndex = 0;
        _adjustFaction.Width = 90;
        actions.Controls.Add(_adjustFaction);
        _adjustPoints.Minimum = 1;
        _adjustPoints.Maximum = 999999999;
        _adjustPoints.Value = 100;
        _adjustPoints.Width = 110;
        actions.Controls.Add(_adjustPoints);
        actions.Controls.Add(Button("加到该角色", async () =>
        {
            if (Selected is not { } character)
            {
                SetStatus("请先在左侧选中一个角色。", error: true);
                return;
            }

            var faction = (short)(_adjustFaction.SelectedIndex == 1
                ? LelantineFarmRules.AthensCamp
                : LelantineFarmRules.SpartaCamp);
            var points = (int)_adjustPoints.Value;
            await RunAsync(async () =>
            {
                await _store.AddAdjustmentAsync(
                    character.Id,
                    faction,
                    points,
                    _dryRun.Checked);
                await RefreshAfterWriteAsync(
                    _dryRun.Checked
                        ? $"[演练] 给 {character.Name} 加 {points} 分（未写库）"
                        : $"给 {character.Name} 加了 {points} 分调整行");
            });
        }));
        root.Controls.Add(actions, 0, 1);
        return page;
    }

    private TabPage BuildKillTab()
    {
        var page = new TabPage("击杀积分");
        var root = DockGrid(page);
        ConfigureGrid(_killGrid, readOnly: true);
        _killGrid.Columns.Add(Column("阵营", 80));
        _killGrid.Columns.Add(Column("击杀积分", 110));
        _killGrid.Columns.Add(Column("计入击杀数", 110));
        _killGrid.Columns.Add(Column("更新时间", 170));
        root.Controls.Add(_killGrid, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        actions.Controls.Add(Button("设选中行为…", async () =>
        {
            if (Selected is not { } character ||
                _killGrid.CurrentRow?.Index is not { } index ||
                index < 0 || index >= _kills.Count)
            {
                SetStatus("请先选中一行击杀积分。", error: true);
                return;
            }

            var row = _kills[index];
            var text = Prompt.Show(
                $"阵营 {(row.Faction == LelantineFarmRules.SpartaCamp ? "斯巴达" : "雅典")}" +
                $" 击杀积分改成（当前 {row.KillPoints}）：",
                "设击杀积分",
                row.KillPoints.ToString(CultureInfo.InvariantCulture));
            if (text is null)
            {
                return;
            }

            if (!long.TryParse(text.Trim(), out var points) || points < 0)
            {
                SetStatus("击杀积分必须是不小于 0 的整数。", error: true);
                return;
            }

            await RunAsync(async () =>
            {
                await _store.SetKillPointsAsync(
                    character.Id,
                    row.Faction,
                    points,
                    row.CreditedKills);
                await RefreshAfterWriteAsync(
                    $"{character.Name} 的击杀积分设为 {points}");
            });
        }));
        actions.Controls.Add(Button("清零选中行", async () =>
        {
            if (Selected is not { } character ||
                _killGrid.CurrentRow?.Index is not { } index ||
                index < 0 || index >= _kills.Count)
            {
                SetStatus("请先选中一行击杀积分。", error: true);
                return;
            }

            var row = _kills[index];
            await RunAsync(async () =>
            {
                await _store.SetKillPointsAsync(character.Id, row.Faction, 0, 0);
                await RefreshAfterWriteAsync(
                    $"{character.Name} 的击杀积分已清零（行保留）");
            });
        }));
        actions.Controls.Add(Button("删除选中行", async () =>
        {
            if (Selected is not { } character ||
                _killGrid.CurrentRow?.Index is not { } index ||
                index < 0 || index >= _kills.Count)
            {
                SetStatus("请先选中一行击杀积分。", error: true);
                return;
            }

            var row = _kills[index];
            if (!Confirm(
                    $"删除 {character.Name} 在该阵营的击杀积分行" +
                    $"（{row.KillPoints} 分）？阵营总分会同时下降。"))
            {
                return;
            }

            await RunAsync(async () =>
            {
                await _store.DeleteKillRowAsync(character.Id, row.Faction);
                await RefreshAfterWriteAsync("击杀积分行已删除");
            });
        }));
        root.Controls.Add(actions, 0, 1);
        return page;
    }

    private TabPage BuildBagTab()
    {
        var page = new TabPage("背包农场物品");
        var root = DockGrid(page);
        ConfigureGrid(_bagGrid, readOnly: true);
        _bagGrid.Columns.Add(Column("槽位", 60));
        _bagGrid.Columns.Add(Column("物品", 70));
        _bagGrid.Columns.Add(Column("名称", 150));
        _bagGrid.Columns.Add(Column("资质/品质", 110));
        _bagGrid.Columns.Add(Column("数量", 70));
        root.Controls.Add(_bagGrid, 0, 0);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var grant = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        grant.Controls.Add(new Label
        {
            Text = "发放：",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        });
        _grantItem.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var (id, name) in FarmItems)
        {
            _grantItem.Items.Add($"{id} {name}");
        }

        _grantItem.SelectedIndex = 0;
        _grantItem.Width = 190;
        grant.Controls.Add(_grantItem);
        _grantQuality.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (var (aptitude, name, points) in LelantineFarmRules.EggRungs)
        {
            _grantQuality.Items.Add($"资质 {aptitude} {name} = {points} 分/个");
        }

        _grantQuality.Items.Add("资质 1 的其他物品（网兜等，品质 1）");
        _grantQuality.SelectedIndex = 0;
        _grantQuality.Width = 250;
        grant.Controls.Add(_grantQuality);
        _grantQuantity.Minimum = 1;
        _grantQuantity.Maximum = 99;
        _grantQuantity.Value = 99;
        _grantQuantity.Width = 70;
        grant.Controls.Add(_grantQuantity);
        grant.Controls.Add(new Label
        {
            Text = "备注",
            AutoSize = true,
            Padding = new Padding(8, 8, 0, 0)
        });
        _grantNote.Width = 170;
        _grantNote.Margin = new Padding(0, 4, 0, 0);
        grant.Controls.Add(_grantNote);
        grant.Controls.Add(Button("发放", async () => await GrantAsync()));
        bottom.Controls.Add(grant, 0, 0);

        var remove = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        remove.Controls.Add(Button("删除选中堆", async () =>
        {
            if (Selected is not { } character ||
                _bagGrid.CurrentRow?.Index is not { } index ||
                index < 0 || index >= _bag.Count)
            {
                SetStatus("请先选中一行背包物品。", error: true);
                return;
            }

            var row = _bag[index];
            if (!Confirm(
                    $"删除 {character.Name} 槽位 {row.Slot} 的 {row.DisplayName}" +
                    $"（品质 {row.Quality}，{row.Stack} 个）？不可撤销。"))
            {
                return;
            }

            await RunAsync(async () =>
            {
                var deleted = await _store.DeleteBagStacksAsync(
                    character.Id,
                    row.ItemId,
                    row.Quality,
                    _grantNote.Text,
                    dryRun: false);
                await RefreshAfterWriteAsync($"已删除该品质的 {deleted} 堆");
            });
        }));
        remove.Controls.Add(Button("删除该物品全部堆", async () =>
        {
            if (Selected is not { } character ||
                _bagGrid.CurrentRow?.Index is not { } index ||
                index < 0 || index >= _bag.Count)
            {
                SetStatus("请先选中一行背包物品。", error: true);
                return;
            }

            var row = _bag[index];
            if (!Confirm(
                    $"删除 {character.Name} 背包里全部 {row.DisplayName}" +
                    $"（物品 {row.ItemId}，所有资质/品质的所有堆）？不可撤销。"))
            {
                return;
            }

            await RunAsync(async () =>
            {
                var deleted = await _store.DeleteBagStacksAsync(
                    character.Id,
                    row.ItemId,
                    quality: null,
                    _grantNote.Text,
                    dryRun: false);
                await RefreshAfterWriteAsync($"已删除 {deleted} 堆");
            });
        }));
        remove.Controls.Add(_dryRun);
        remove.Controls.Add(new Label
        {
            Text = "（勾选后发放/加分为演练，不写库）",
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        });
        bottom.Controls.Add(remove, 0, 1);
        root.Controls.Add(bottom, 0, 2);
        return page;
    }

    private TabPage BuildResetTab()
    {
        var page = new TabPage("清空/重置");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(Button("清空选中角色的全部农场积分", async () =>
        {
            if (Selected is not { } character)
            {
                SetStatus("请先在左侧选中一个角色。", error: true);
                return;
            }

            if (!Confirm(
                    $"清空 {character.Name}（id={character.Id}）的全部农场积分？\n" +
                    $"会删除该角色 {_donations.Count} 行捐卵流水与 {_kills.Count} 行击杀积分，" +
                    "其个人分与所属阵营总分同时下降。不可撤销。"))
            {
                return;
            }

            await RunAsync(async () =>
            {
                var report = await _store.ResetCharacterAsync(character.Id);
                await RefreshAfterWriteAsync(
                    $"已清空 {character.Name}：捐卵流水 {report.DonationRows} 行、" +
                    $"击杀行 {report.KillRows} 行");
            });
        }), 0, 0);

        var factionRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        _resetFaction.DropDownStyle = ComboBoxStyle.DropDownList;
        _resetFaction.Items.AddRange(["斯巴达", "雅典"]);
        _resetFaction.SelectedIndex = 0;
        _resetFaction.Width = 90;
        factionRow.Controls.Add(_resetFaction);
        factionRow.Controls.Add(Button("清空该阵营全部农场积分", async () =>
        {
            var faction = (short)(_resetFaction.SelectedIndex == 1
                ? LelantineFarmRules.AthensCamp
                : LelantineFarmRules.SpartaCamp);
            var name = faction == LelantineFarmRules.SpartaCamp ? "斯巴达" : "雅典";
            if (!Confirm(
                    $"清空 {name} 阵营的全部农场积分？\n" +
                    "该阵营所有角色的捐卵流水与击杀积分都会被删除，" +
                    "阵营总分与这些角色的个人分一起归零。不可撤销。"))
            {
                return;
            }

            await RunAsync(async () =>
            {
                var report = await _store.ResetFactionAsync(faction);
                await RefreshAfterWriteAsync(
                    $"已清空{name}：捐卵流水 {report.DonationRows} 行、" +
                    $"击杀行 {report.KillRows} 行，原总分 {report.Points}");
            });
        }));
        root.Controls.Add(factionRow, 0, 1);

        root.Controls.Add(new Label
        {
            Text = "说明：阵营总分由流水派生（= 同阵营个人积分之和），所以本工具没有" +
                   "「直接改阵营分」的操作——改流水就是改阵营分，也不会有第二份会漂移的数字。",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        }, 0, 2);
        return page;
    }

    private TabPage BuildRuleTab()
    {
        var page = new TabPage("只读：规则常量");
        var root = DockGrid(page);
        ConfigureGrid(_ruleGrid, readOnly: true);
        _ruleGrid.Columns.Add(Column("常量", 260));
        _ruleGrid.Columns.Add(Column("值（读自服务端源码）", 300));
        _ruleGrid.Columns.Add(Column("含义", 380));
        root.Controls.Add(_ruleGrid, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        actions.Controls.Add(Button("重新读取服务端源码", () =>
        {
            FillRuleGrid();
            SetStatus("已按服务端源码重新读取规则常量。");
            return Task.CompletedTask;
        }));
        actions.Controls.Add(new Label
        {
            Text = "  这些值在服务端是代码常量，工具只能显示、不能修改；" +
                   "要改必须改服务端代码并重新部署。",
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0)
        });
        root.Controls.Add(actions, 0, 1);
        return page;
    }

    private TabPage BuildNpcTab()
    {
        var page = new TabPage("只读：已发布 NPC");
        var root = DockGrid(page);
        ConfigureGrid(_npcGrid, readOnly: true);
        _npcGrid.Columns.Add(Column("地图", 60));
        _npcGrid.Columns.Add(Column("NPC 键", 190));
        _npcGrid.Columns.Add(Column("模板键", 240));
        _npcGrid.Columns.Add(Column("对象 ID", 90));
        _npcGrid.Columns.Add(Column("X", 80));
        _npcGrid.Columns.Add(Column("Z", 80));
        _npcGrid.Columns.Add(Column("朝向", 70));
        _npcGrid.Columns.Add(Column("外观字", 90));
        root.Controls.Add(_npcGrid, 0, 0);
        root.Controls.Add(new Label
        {
            Text = "名册与坐标来自当前发布版本（npc_content_publication 的头版本）；" +
                   "要改放置需要改服务端的内容基线并重新发布，工具不直接改这些行。",
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        }, 0, 1);
        return page;
    }

    private async Task GrantAsync()
    {
        if (Selected is not { } character)
        {
            SetStatus("请先在左侧选中一个角色。", error: true);
            return;
        }

        var (itemId, itemName) = FarmItems[_grantItem.SelectedIndex];
        var isEgg = itemId == LelantineFarmRules.HoundEggItemId;
        var quality = isEgg
            ? LelantineFarmRules.EggRungs[
                Math.Clamp(_grantQuality.SelectedIndex, 0, 2)].Aptitude
            : (short)1;
        var quantity = (int)_grantQuantity.Value;
        await RunAsync(async () =>
        {
            var plan = await _store.GrantItemAsync(
                character.Id,
                itemId,
                quantity,
                quality,
                _grantNote.Text,
                _dryRun.Checked);
            var slots = string.Join(
                "、",
                plan.Placements.Select(placement =>
                    placement.NewRow
                        ? $"槽{placement.Slot}(新 {placement.After})"
                        : $"槽{placement.Slot}({placement.Before}→{placement.After})"));
            var message =
                $"{(plan.DryRun ? "[演练] " : string.Empty)}" +
                $"{plan.CharacterName} 发放 {plan.Quantity} 个 {plan.DisplayName}" +
                $"（物品 {plan.ItemId}，品质/资质 {plan.Quality}）：{slots}";
            if (plan.SlotsBeyondUnlockedPages > 0)
            {
                message += $"；注意 {plan.SlotsBeyondUnlockedPages} 个堆超出已解锁背包页，客户端可能看不到";
            }

            await RefreshAfterWriteAsync(message);
        });
    }

    private async Task RefreshAfterWriteAsync(string message)
    {
        var totals = await _store.LoadTotalsAsync();
        _totals.Text =
            $"阵营总分（= 同阵营个人积分之和）：斯巴达 {totals.SpartaPoints}｜" +
            $"雅典 {totals.AthensPoints}｜有分角色 {totals.Members} 个｜" +
            $"捐卵流水 {totals.DonationRows} 行 / 击杀行 {totals.KillRows} 行";
        var selectedId = Selected?.Id;
        _characters = await _store.LoadCharactersAsync(_query.Text, limit: 500);
        FillCharacterGrid(selectedId);
        await ReloadSelectedAsync();
        SetStatus(message);
    }

    private void FillCharacterGrid(int? keepSelectedId = null)
    {
        _characterGrid.Rows.Clear();
        foreach (var row in _characters)
        {
            _characterGrid.Rows.Add(
                row.Id,
                row.Name,
                row.CampName,
                row.Personal,
                row.Donated,
                row.Kills,
                row.Rank == 0 ? "-" : row.Rank);
        }

        if (keepSelectedId is { } id)
        {
            for (var index = 0; index < _characters.Count; index++)
            {
                if (_characters[index].Id == id)
                {
                    _characterGrid.CurrentCell = _characterGrid.Rows[index].Cells[0];
                    break;
                }
            }
        }
    }

    private async Task ReloadSelectedAsync(
        CancellationToken cancellationToken = default)
    {
        if (Selected is not { } character)
        {
            _donations = [];
            _kills = [];
            _bag = [];
        }
        else
        {
            _donations = [.. await _store.LoadDonationsAsync(character.Id, cancellationToken)];
            _kills = [.. await _store.LoadKillsAsync(character.Id, cancellationToken)];
            _bag = [.. await _store.LoadFarmBagAsync(character.Id, cancellationToken)];
        }

        FillDetailGrids();
    }

    private void FillDetailGrids()
    {
        _donationGrid.Rows.Clear();
        foreach (var row in _donations)
        {
            _donationGrid.Rows.Add(
                row.Id,
                row.Faction == LelantineFarmRules.SpartaCamp ? "斯巴达" : "雅典",
                row.ItemId,
                row.EggCount,
                row.Points,
                row.DonatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                row.ItemId == 0
                    ? "GM 手动调整行"
                    : row.ItemId == LelantineFarmRules.HoundEggItemId
                        ? "忠犬卵捐赠"
                        : "其他");
        }

        _killGrid.Rows.Clear();
        foreach (var row in _kills)
        {
            _killGrid.Rows.Add(
                row.Faction == LelantineFarmRules.SpartaCamp ? "斯巴达" : "雅典",
                row.KillPoints,
                row.CreditedKills,
                row.UpdatedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        _bagGrid.Rows.Clear();
        foreach (var row in _bag)
        {
            _bagGrid.Rows.Add(
                row.Slot,
                row.ItemId,
                row.DisplayName,
                row.ItemId == LelantineFarmRules.HoundEggItemId
                    ? LelantineFarmRules.AptitudeName(row.Quality)
                    : row.Quality.ToString(CultureInfo.InvariantCulture),
                row.Stack);
        }
    }

    private void FillRuleGrid()
    {
        _ruleGrid.Rows.Clear();
        foreach (var constant in FarmStore.LoadRuleConstants())
        {
            _ruleGrid.Rows.Add(constant.Name, constant.Value, constant.Meaning);
        }
    }

    private async Task FillNpcGridAsync(CancellationToken cancellationToken)
    {
        var rows = await _store.LoadPublishedNpcsAsync(cancellationToken);
        _npcGrid.Rows.Clear();
        foreach (var row in rows)
        {
            _npcGrid.Rows.Add(
                row.MapId,
                row.NpcKey,
                row.TemplateKey,
                row.ObjectId,
                row.X.ToString("0.##", CultureInfo.InvariantCulture),
                row.Z.ToString("0.##", CultureInfo.InvariantCulture),
                row.Facing.ToString("0.##", CultureInfo.InvariantCulture),
                row.AppearanceType);
        }
    }

    private static readonly (int Id, string Name)[] FarmItems =
    [
        (LelantineFarmRules.HoundEggItemId, "忠犬卵"),
        (10080, "木质网兜"),
        (10081, "网兜"),
        (10082, "网兜"),
        (10083, "网兜"),
        (10084, "神秘网兜")
    ];

    private bool Confirm(string message) =>
        MessageBox.Show(
            this,
            message,
            "确认",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) == DialogResult.Yes;

    private static TableLayoutPanel DockGrid(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        page.Controls.Add(root);
        return root;
    }

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

    private static Button Button(string text, Func<Task> action)
    {
        var button = new Button { Text = text, Width = 150, Height = 28 };
        button.Click += async (_, _) => await action();
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

    /// <summary>A one-line text prompt, because WinForms ships no input box.</summary>
    private static class Prompt
    {
        public static string? Show(string label, string title, string initial)
        {
            using var form = new Form
            {
                Text = title,
                Width = 460,
                Height = 170,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false
            };
            var caption = new Label
            {
                Text = label,
                Left = 12,
                Top = 14,
                Width = 420,
                AutoSize = false,
                Height = 34
            };
            var input = new TextBox
            {
                Text = initial,
                Left = 12,
                Top = 52,
                Width = 420
            };
            var ok = new Button
            {
                Text = "确定",
                Left = 268,
                Top = 88,
                Width = 78,
                DialogResult = DialogResult.OK
            };
            var cancel = new Button
            {
                Text = "取消",
                Left = 354,
                Top = 88,
                Width = 78,
                DialogResult = DialogResult.Cancel
            };
            form.Controls.Add(caption);
            form.Controls.Add(input);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            return form.ShowDialog() == DialogResult.OK ? input.Text : null;
        }
    }
}
