namespace Godswar.LootTool;

/// <summary>
/// Pet tab: reads the whole pet configuration out of the connected database and
/// writes the two things an operator asked to control - each species' hatch
/// aptitude bounds, and an owned pet's aptitude (with the talent mask the
/// database requires to travel with it).
/// </summary>
internal sealed class PetPanel : UserControl, IAsyncDisposable
{
    private readonly PetStore _store = new();
    private readonly TabControl _tabs = new();
    private readonly DataGridView _speciesGrid = new();
    private readonly DataGridView _petGrid = new();
    private readonly DataGridView _statGrid = new();
    private readonly DataGridView _aptitudeGrid = new();
    private readonly TextBox _ownerBox = new();
    private readonly ComboBox _targetAptitude = new();
    private readonly Label _petHint = new();
    private readonly Label _speciesHint = new();
    private readonly Label _revisionHint = new();
    private readonly StatusLabel _status = new();

    private PetTextCatalog _texts = PetTextCatalog.Load(null);
    private List<PetSpeciesRow> _species = [];
    private List<OwnedPetRow> _pets = [];
    private List<PetAptitudeRow> _aptitudes = [];
    private string _publishedRevision = string.Empty;
    private bool _connected;

    public string ConnectedDatabase => _store.DatabaseName;

    public PetPanel()
    {
        Dock = DockStyle.Fill;

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(BuildBoundsTab());
        _tabs.TabPages.Add(BuildOwnedPetTab());
        _tabs.TabPages.Add(BuildConfigurationTab());
        Controls.Add(_tabs);
    }

    public void SetStatus(string text) => _status.Text = text;

    public async Task ConnectAndReadAsync(
        string connectionString,
        string clientRoot,
        CancellationToken cancellationToken = default)
    {
        _store.Connect(connectionString);
        _texts = PetTextCatalog.Load(clientRoot);
        await _store.EnsureBoundsTableAsync(cancellationToken);
        _connected = true;
        await ReadAsync(cancellationToken);
    }

    public async Task ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!_connected)
        {
            return;
        }

        _species = await _store.LoadSpeciesAsync(cancellationToken);
        _aptitudes = await _store.LoadAptitudesAsync(cancellationToken);
        _publishedRevision = (await _store.LoadPublishedRevisionAsync()).Revision;
        var pets = await _store.LoadOwnedPetsAsync(_ownerBox.Text, cancellationToken);
        FillSpeciesGrid();
        FillAptitudeGrid();
        _pets = pets;
        FillPetGrid();
        _speciesHint.Text =
            $"种族 {_species.Count} 个（已保存上下限 {_species.Count(row => row.BoundsSaved)} 个）" +
            $"｜客户端中文名 {(_texts.TextCount == 0 ? "未启用（未找到 Message_Pet.dat）" : _texts.TextCount + " 条")}";
        _revisionHint.Text =
            $"当前发布版本 {_publishedRevision}｜6-10 档的服务端语义与客户端显示名是错开的" +
            "（项目有意重排，见 docs/pet-system-foundation.md）；改区间会发布成新版本，" +
            "服务端只在启动时读，改完要重启才生效。";
    }

    private TabPage BuildBoundsTab()
    {
        var page = new TabPage("种族档位上下限");
        var root = DockGrid(page);

        _speciesGrid.Dock = DockStyle.Fill;
        ConfigureGrid(_speciesGrid, readOnly: false);
        _speciesGrid.Columns.Add(Column("种族 ID", 70));
        _speciesGrid.Columns.Add(Column("宠物中文名", 140));
        _speciesGrid.Columns.Add(Column("服务器英文名", 160));
        _speciesGrid.Columns.Add(Column("食性", 60));
        _speciesGrid.Columns.Add(Column("初始技能（客户端名）", 200));
        _speciesGrid.Columns.Add(Column("寿命候选", 110));
        _speciesGrid.Columns.Add(Column("蛋道具", 70));
        _speciesGrid.Columns.Add(Column("魂玉道具", 80));
        _speciesGrid.Columns.Add(Column("最低档（可改）", 100));
        _speciesGrid.Columns.Add(Column("最高档（可改）", 100));
        _speciesGrid.Columns.Add(Column("已保存", 70));
        _speciesGrid.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex < 0 || (e.ColumnIndex != 8 && e.ColumnIndex != 9))
            {
                return;
            }

            var row = _speciesGrid.Rows[e.RowIndex];
            if (row.Cells[8].Value is not string minimumText ||
                row.Cells[9].Value is not string maximumText ||
                !short.TryParse(minimumText, out var minimum) ||
                !short.TryParse(maximumText, out var maximum) ||
                minimum < 1 || maximum > 16 || minimum > maximum)
            {
                _status.Text = "档位必须是 1-16 的整数，且最低档不高于最高档；本次输入不会保存。";
                row.Cells[8].Value = _species[e.RowIndex].MinimumAptitude.ToString();
                row.Cells[9].Value = _species[e.RowIndex].MaximumAptitude.ToString();
            }
        };

        var save = Button("保存全部修改", async () => await SaveBoundsAsync());
        var reset = Button("删除某种族的设置", async () => await DeleteBoundsAsync());
        var eggs = Button("把上下限随机写进背包里的蛋", async () => await RerollEggsAsync());
        var refresh = Button("重新读取", async () => await ReadAsync());

        root.Controls.Add(BuildToolbar(refresh, save, reset, eggs), 0, 0);
        root.Controls.Add(_speciesGrid, 0, 1);
        _speciesHint.Dock = DockStyle.Fill;
        root.Controls.Add(_speciesHint, 0, 2);
        return page;
    }

    private TabPage BuildOwnedPetTab()
    {
        var page = new TabPage("已有宠物（改档位）");
        var root = DockGrid(page);

        _petGrid.Dock = DockStyle.Fill;
        ConfigureGrid(_petGrid, readOnly: true);
        foreach (var header in new[]
                 {
                     "宠物 id", "角色", "宠物名", "种族", "等级", "当前档",
                     "当前天赋", "天赋掩码", "技能槽", "资质总和", "rank", "revision"
                 })
        {
            _petGrid.Columns.Add(Column(header, header.Length > 4 ? 130 : 80));
        }

        _petGrid.SelectionChanged += (_, _) => ShowPetStats();

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 320
        };
        split.Panel1.Controls.Add(_petGrid);
        split.Panel2.Controls.Add(BuildEditor());

        var ownerLabel = new Label
        {
            Text = "角色名筛选",
            AutoSize = true,
            Margin = new Padding(6, 8, 2, 3)
        };
        _ownerBox.Width = 160;
        _ownerBox.PlaceholderText = "留空 = 全部角色";
        var read = Button("读取宠物", async () => await ReadAsync());
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        toolbar.Controls.Add(ownerLabel);
        toolbar.Controls.Add(_ownerBox);
        toolbar.Controls.Add(read);
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(split, 0, 1);
        _status.Dock = DockStyle.Fill;
        root.Controls.Add(_status, 0, 2);
        return page;
    }

    private Control BuildEditor()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        panel.Controls.Add(new Label
        {
            Text = "目标档位",
            AutoSize = true,
            Margin = new Padding(6, 8, 2, 3)
        });
        _targetAptitude.DropDownStyle = ComboBoxStyle.DropDownList;
        _targetAptitude.Width = 380;
        foreach (var info in PetContent.Aptitudes)
        {
            _targetAptitude.Items.Add(
                $"{info.Value}｜项目 {info.ProjectChinese}｜客户端显示 {info.ClientChinese}" +
                $"｜天赋 {PetContent.DescribeTalentMask(PetContent.TalentMaskFor(info.Value))}");
        }

        _targetAptitude.SelectedIndex = 0;
        panel.Controls.Add(_targetAptitude);
        panel.Controls.Add(Button("应用到此宠物", async () => await ApplyAptitudeAsync()));
        panel.Controls.Add(new Label
        {
            Text = "说明：只改档位与数据库强制要求同步的天赋掩码/合体投影/孵化 rank，" +
                   "六维资质与成长率完全不动。游戏服务器只在启动时读配置，改完要重启服务器才生效。",
            AutoSize = false,
            Width = 900,
            Height = 34,
            Margin = new Padding(6, 4, 2, 2)
        });

        _statGrid.Dock = DockStyle.None;
        _statGrid.Location = new Point(8, 74);
        _statGrid.Width = 520;
        _statGrid.Height = 150;
        ConfigureGrid(_statGrid, readOnly: true);
        _statGrid.Columns.Add(Column("属性", 90));
        _statGrid.Columns.Add(Column("基础资质", 110));
        _statGrid.Columns.Add(Column("附加值", 110));
        _statGrid.Columns.Add(Column("基础成长率", 110));
        _petHint.Text = "未选中宠物";
        _petHint.AutoSize = false;
        _petHint.Width = 360;
        _petHint.Height = 150;
        _petHint.Location = new Point(540, 74);
        _petHint.Margin = new Padding(6, 4, 2, 2);

        var host = new Panel { Dock = DockStyle.Fill };
        host.Controls.Add(panel);
        host.Controls.Add(_statGrid);
        host.Controls.Add(_petHint);
        panel.Location = new Point(0, 0);
        panel.Height = 70;
        return host;
    }

    private TabPage BuildConfigurationTab()
    {
        var page = new TabPage("16 档区间（可发布新版本）");
        var root = DockGrid(page);

        _aptitudeGrid.Dock = DockStyle.Fill;
        ConfigureGrid(_aptitudeGrid, readOnly: false);
        foreach (var (header, width) in new (string, int)[]
                 {
                     ("档", 44), ("项目名", 110), ("项目中文", 110),
                     ("客户端显示名", 120),
                     ("成长率下限（可改）", 130), ("成长率上限（可改）", 130),
                     ("出生资质下限（可改）", 140), ("出生资质上限（可改）", 140),
                     ("附加值下限（可改）", 130), ("附加值上限（可改）", 130),
                     ("单维偏差上限", 100),
                     ("库内天赋掩码", 190), ("本工具算出的掩码", 130)
                 })
        {
            _aptitudeGrid.Columns.Add(Column(header, width));
        }

        // The talent mask is not editable: both pet_content_aptitude_definitions
        // and character_pets carry a CHECK that recomputes it from the tier, so a
        // different value cannot be published or even hatched.
        for (var index = 0; index < _aptitudeGrid.Columns.Count; index++)
        {
            _aptitudeGrid.Columns[index].ReadOnly = index is < 4 or > 9;
        }

        _aptitudeGrid.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex is < 4 or > 9)
            {
                return;
            }

            var text = _aptitudeGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
            var integerColumn = e.ColumnIndex >= 6;
            var ok = integerColumn
                ? int.TryParse(text, out var whole) && whole >= 0
                : decimal.TryParse(text, out var fraction) && fraction > 0m;
            if (!ok)
            {
                _status.Text =
                    "数值非法：成长率必须是正数，资质/附加值必须是非负整数。已还原该格。";
                FillAptitudeGrid();
            }
        };

        root.Controls.Add(BuildToolbar(
            Button("重新读取", async () => await ReadAsync()),
            Button("发布为新版本（改 16 档区间）", async () => await PublishAptitudesAsync())), 0, 0);
        root.Controls.Add(_aptitudeGrid, 0, 1);
        root.Controls.Add(_revisionHint, 0, 2);
        return page;
    }

    private void FillAptitudeGrid()
    {
        _aptitudeGrid.Rows.Clear();
        foreach (var row in _aptitudes)
        {
            var info = PetContent.Aptitudes.FirstOrDefault(
                candidate => candidate.Value == row.Aptitude);
            var expected = PetContent.TalentMaskFor(row.Aptitude);
            _aptitudeGrid.Rows.Add(
                row.Aptitude,
                row.DisplayName,
                info?.ProjectChinese ?? "?",
                info?.ClientChinese ?? "?",
                row.MinimumTotalGrowth.ToString("0.####"),
                row.MaximumTotalGrowth.ToString("0.####"),
                row.MinimumInitialSavvy,
                row.MaximumInitialSavvy,
                row.MinimumAddedSavvy,
                row.MaximumAddedSavvy,
                "0.12（固定）",
                $"{row.InnateTalentMask}（{PetContent.DescribeTalentMask(row.InnateTalentMask)}）",
                row.InnateTalentMask == expected
                    ? "一致"
                    : $"不一致！{expected}（{PetContent.DescribeTalentMask(expected)}）");
        }
    }

    private async Task PublishAptitudesAsync()
    {
        var edits = new List<PetAptitudeEdit>();
        for (var index = 0; index < _aptitudes.Count; index++)
        {
            var row = _aptitudes[index];
            var cells = _aptitudeGrid.Rows[index].Cells;
            edits.Add(new PetAptitudeEdit(
                row.Aptitude,
                ReadDecimal(cells[4].Value, row.MinimumTotalGrowth),
                ReadDecimal(cells[5].Value, row.MaximumTotalGrowth),
                ReadInt32(cells[6].Value, row.MinimumInitialSavvy),
                ReadInt32(cells[7].Value, row.MaximumInitialSavvy),
                ReadInt32(cells[8].Value, row.MinimumAddedSavvy),
                ReadInt32(cells[9].Value, row.MaximumAddedSavvy)));
        }

        var changed = edits
            .Zip(_aptitudes, (edit, row) => (edit, row))
            .Count(pair => pair.edit.MinimumTotalGrowth != pair.row.MinimumTotalGrowth ||
                pair.edit.MaximumTotalGrowth != pair.row.MaximumTotalGrowth ||
                pair.edit.MinimumInitialSavvy != pair.row.MinimumInitialSavvy ||
                pair.edit.MaximumInitialSavvy != pair.row.MaximumInitialSavvy ||
                pair.edit.MinimumAddedSavvy != pair.row.MinimumAddedSavvy ||
                pair.edit.MaximumAddedSavvy != pair.row.MaximumAddedSavvy);
        if (changed == 0)
        {
            _status.Text = "界面上的区间与库里完全一样，没有要发布的东西。";
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"这会复制整份宠物内容（{edits.Count} 档 + 其余 11 张内容表）成一个新的发布版本，" +
            $"然后把发布指针指到它。\n旧版本保留在库里，已有宠物的外键不会断。\n" +
            $"改动的档位数：{changed}\n\n服务端只在启动时读内容，发布后必须重启服务器才生效。" +
            "\n确定发布吗？",
            "发布新内容版本",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            _status.Text = "已取消，未发布。";
            return;
        }

        await GuardAsync(async () =>
        {
            var revision = await _store.PublishEditedAptitudesAsync(edits);
            await ReadAsync();
            _status.Text = $"已发布新版本 {revision}（重启游戏服务器后才生效）。";
        });
    }

    private static decimal ReadDecimal(object? value, decimal fallback) =>
        decimal.TryParse(value?.ToString(), out var parsed) ? parsed : fallback;

    private static int ReadInt32(object? value, int fallback) =>
        int.TryParse(value?.ToString(), out var parsed) ? parsed : fallback;

    private void FillSpeciesGrid()
    {
        _speciesGrid.Rows.Clear();
        foreach (var row in _species)
        {
            _speciesGrid.Rows.Add(
                row.SpeciesId,
                _texts.SpeciesName(row.SpeciesId, row.DisplayName),
                row.DisplayName,
                PetContent.FoodKind(row.FoodKind),
                $"{row.StarterSkillId} {_texts.SkillName(row.StarterSkillId, row.StarterSkillName)}",
                row.LifetimeValues,
                row.EggItemId?.ToString() ?? "无",
                row.MagicJadeItemId?.ToString() ?? "无",
                row.MinimumAptitude.ToString(),
                row.MaximumAptitude.ToString(),
                row.BoundsSaved ? "是" : "否（默认 1-16）");
        }
    }

    private void FillPetGrid()
    {
        _petGrid.Rows.Clear();
        foreach (var pet in _pets)
        {
            _petGrid.Rows.Add(
                pet.PetId,
                pet.OwnerName,
                pet.PetName,
                $"{pet.SpeciesId} {_texts.SpeciesName(pet.SpeciesId, string.Empty)}",
                pet.Level,
                PetContent.DescribeAptitude(pet.Aptitude),
                PetContent.DescribeTalentMask(pet.TalentMask),
                pet.TalentMask,
                pet.OpenedSkillSlots,
                pet.InitialSavvyBaseline,
                pet.Rank,
                pet.Revision);
        }

        _status.Text = $"宠物 {_pets.Count} 只（库：{_store.DatabaseName}）";
    }

    private async void ShowPetStats()
    {
        if (!_connected || _petGrid.CurrentRow is null)
        {
            return;
        }

        var index = _petGrid.CurrentRow.Index;
        if (index < 0 || index >= _pets.Count)
        {
            return;
        }

        var pet = _pets[index];
        _statGrid.Rows.Clear();
        foreach (var stat in await _store.LoadPetStatsAsync(pet.PetId))
        {
            _statGrid.Rows.Add(
                stat.Stat,
                stat.InitialSavvy.ToString("0.####"),
                stat.AddedSavvy.ToString("0.####"),
                stat.GrowthRate.ToString("0.######"));
        }

        _petHint.Text =
            $"宠物 {pet.PetId}（{pet.OwnerName}）{pet.PetName}\n" +
            $"当前档：{PetContent.DescribeAptitude(pet.Aptitude)}\n" +
            $"天赋：{PetContent.DescribeTalentMask(pet.TalentMask)}\n" +
            $"六维基础资质合计：{pet.InitialSavvyBaseline}\n" +
            $"寿命：{pet.RemainingLifetime}｜rank：{pet.Rank}";
    }

    private async Task SaveBoundsAsync()
    {
        var changes = new List<(short, short, short)>();
        for (var index = 0; index < _species.Count; index++)
        {
            var row = _species[index];
            var gridRow = _speciesGrid.Rows[index];
            var minimum = Parse(gridRow, 8, row.MinimumAptitude);
            var maximum = Parse(gridRow, 9, row.MaximumAptitude);
            if (minimum != row.MinimumAptitude ||
                maximum != row.MaximumAptitude ||
                !row.BoundsSaved)
            {
                changes.Add((row.SpeciesId, minimum, maximum));
            }
        }

        if (changes.Count == 0)
        {
            _status.Text = "没有需要保存的改动。";
            return;
        }

        await GuardAsync(async () =>
        {
            await _store.SaveSpeciesBoundsAsync(changes);
            await ReadAsync();
            _status.Text = $"已保存 {changes.Count} 个种族的档位上下限。";
        });
        return;

        static short Parse(DataGridViewRow row, int column, short fallback) =>
            short.TryParse(row.Cells[column].Value?.ToString(), out var value)
                ? value
                : fallback;
    }

    private async Task DeleteBoundsAsync()
    {
        if (_speciesGrid.CurrentRow is not { Index: >= 0 } gridRow ||
            gridRow.Index >= _species.Count)
        {
            _status.Text = "请先选中要清除设置的种族行。";
            return;
        }

        var species = _species[gridRow.Index];
        await GuardAsync(async () =>
        {
            await _store.DeleteSpeciesBoundsAsync(species.SpeciesId);
            await ReadAsync();
            _status.Text =
                $"种族 {species.SpeciesId} 已恢复默认 1-16（表里已删除该行的设置）。";
        });
    }

    private async Task RerollEggsAsync()
    {
        var answer = MessageBox.Show(
            this,
            "这会直接改写背包里宠物蛋实例的 item_quality（=孵化出来的档位），" +
            "按每个种族保存的上下限随机取值。\n角色名筛选留空 = 所有角色的全部蛋。" +
            "\n确定执行吗？",
            "写入游戏数据确认",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            _status.Text = "已取消，未改动任何蛋。";
            return;
        }

        await GuardAsync(async () =>
        {
            var count = await _store.RandomizeEggAptitudesAsync(_species, _ownerBox.Text);
            _status.Text = count == 0
                ? "没有找到符合筛选条件的宠物蛋，未改动任何数据。"
                : $"已按上下限随机重写 {count} 颗蛋的档位。";
        });
    }

    private async Task ApplyAptitudeAsync()
    {
        if (_petGrid.CurrentRow is not { Index: >= 0 } row || row.Index >= _pets.Count)
        {
            _status.Text = "请先在上面的表里选中一只宠物。";
            return;
        }

        var pet = _pets[row.Index];
        var target = (short)(_targetAptitude.SelectedIndex + 1);
        if (target == pet.Aptitude)
        {
            _status.Text = $"目标档位与当前档位相同（{target}），未做任何改动。";
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"宠物 {pet.PetId}「{pet.PetName}」（主人 {pet.OwnerName}）\n" +
            $"{PetContent.DescribeAptitude(pet.Aptitude)}\n        ↓\n" +
            $"{PetContent.DescribeAptitude(target)}\n\n" +
            $"天赋掩码会同步为 {PetContent.TalentMaskFor(target)}" +
            $"（{PetContent.DescribeTalentMask(PetContent.TalentMaskFor(target))}）。" +
            $"六维资质与成长率不变。\n确定写入数据库吗？",
            "改档位确认",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            _status.Text = "已取消，未改动。";
            return;
        }

        await GuardAsync(async () =>
        {
            var summary = await _store.UpdatePetAptitudeAsync(pet.PetId, target);
            await ReadAsync();
            _status.Text = summary;
        });
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            _status.Text = "执行中…";
            await action();
        }
        catch (Exception ex)
        {
            _status.Text = $"失败：{ex.Message}";
            MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static TableLayoutPanel DockGrid(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(6)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        page.Controls.Add(root);
        return root;
    }

    private static FlowLayoutPanel BuildToolbar(params Control[] controls)
    {
        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        foreach (var control in controls)
        {
            toolbar.Controls.Add(control);
        }

        return toolbar;
    }

    private Button Button(string text, Func<Task> onClick)
    {
        var button = new Button
        {
            Text = text,
            Height = 28,
            AutoSize = true,
            Margin = new Padding(4, 4, 0, 3)
        };
        button.Click += async (_, _) => await onClick();
        return button;
    }

    private static void ConfigureGrid(DataGridView grid, bool readOnly)
    {
        grid.ReadOnly = readOnly;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
    }

    private static DataGridViewTextBoxColumn Column(string header, int width) =>
        new() { HeaderText = header, Width = width };

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    private sealed class StatusLabel : Label
    {
        public StatusLabel()
        {
            Dock = DockStyle.Fill;
            TextAlign = ContentAlignment.MiddleLeft;
            AutoEllipsis = true;
        }
    }
}
