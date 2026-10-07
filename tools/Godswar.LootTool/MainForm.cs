using System.Globalization;

namespace Godswar.LootTool;

/// <summary>
/// Main window: connection bar on top (host/port/database/user/password, then
/// connect and one-click read), monster list on the left, the selected monster's
/// loot table plus bulk drop-rate tools on the right. Edits go straight to
/// PostgreSQL; the game server only loads loot at startup, so the window keeps
/// telling the operator that a restart is required (it never touches Docker).
/// </summary>
internal sealed class MainForm : Form
{
    private readonly LootToolSettings _settings = LootToolSettings.Load();
    private readonly LootStore _store = new();
    private readonly CharacterPanel _characterPanel = new();
    private readonly MonsterSpawnPanel _monsterPanel = new();
    private readonly ExportPanel _exportPanel = new();
    private readonly NpcDialoguePanel _npcPanel = new();
    private readonly PetPanel _petPanel = new();
    private readonly FarmPanel _farmPanel = new();
    private readonly QuestRewardPanel _questPanel = new();
    private readonly ConnectionPanel _connection = new();
    private readonly TextBox _filterBox = new();
    private readonly ComboBox _filterMode = new();
    private readonly DataGridView _monsterGrid = new();
    private readonly Label _monsterSummary = new();
    private readonly Label _detailTitle = new();
    private readonly Label _detailInfo = new();
    private readonly CheckBox _headerEnabled = new();
    private readonly NumericUpDown _maximumDrops = new();
    private readonly Label _ruleCountLabel = new();
    private readonly NumericUpDown _factor = new();
    private readonly NumericUpDown _uniformChance = new();
    private readonly DataGridView _ruleGridControl = new();
    private readonly Label _dirtyLabel = new();
    private readonly Label _restartLabel = new();
    private readonly Label _tableHint = new();
    private readonly Label _statusLabel = new();

    private LootRuleGrid? _ruleGrid;
    private ClientTextCatalog _catalog = ClientTextCatalog.Load(null);
    private Dictionary<int, ItemRow> _items = new();
    private List<MonsterRow> _monsters = [];
    private List<ItemAttributeTemplateRow> _itemAttributeTemplates = [];
    private ItemAttributeCatalog _attributeCatalog = ItemAttributeCatalog.Empty;
    private string? _currentKey;
    private bool _dirty;
    private bool _pendingRestart;
    private bool _boundColumnAvailable = true;
    private bool _attributeColumnsAvailable = true;
    private bool _suppressSelection;
    private bool _suppressHeaderEvents;

    public MainForm()
    {
        Text = "Godswar GM 工具（角色与标记点 + 利兰丁农场 + 掉落表 + 任务奖励 + 宠物档位）";
        Width = 1420;
        Height = 880;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1120, 660);

        BuildUi();
        _ruleGrid = new LootRuleGrid(_ruleGridControl);
        _ruleGrid.SetItemDescriber(DescribeItem);
        _ruleGrid.EditAttributes = EditItemAttributes;
        _ruleGrid.Changed += (_, _) => MarkDirty();

        _connection.ConnectRequested += async (_, _) => await ConnectAsync();
        _connection.ReadRequested += async (_, _) => await ReadAllAsync();
        _connection.ClientRootBrowsed += (_, _) => BrowseClientRoot();
        _connection.LoadFrom(_settings);
        _connection.SetStatus("未连接", healthy: false);
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_dirty || _questPanel.IsDirty)
        {
            var answer = MessageBox.Show(
                this,
                "还有未保存的改动，确定关闭吗？",
                "未保存的改动",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        await _store.DisposeAsync();
        await _characterPanel.DisposeAsync();
        await _monsterPanel.DisposeAsync();
        await _exportPanel.DisposeAsync();
        await _npcPanel.DisposeAsync();
        await _petPanel.DisposeAsync();
        await _farmPanel.DisposeAsync();
        await _questPanel.DisposeAsync();
        base.OnFormClosing(e);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        root.Controls.Add(_connection, 0, 0);
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6)
        };
        var lootPage = new TabPage("怪物掉落表");
        lootPage.Controls.Add(BuildBody());
        var questPage = new TabPage("任务奖励");
        questPage.Controls.Add(_questPanel);
        var petPage = new TabPage("宠物档位");
        petPage.Controls.Add(_petPanel);
        var farmPage = new TabPage("利兰丁农场");
        farmPage.Controls.Add(_farmPanel);
        var characterPage = new TabPage("角色与标记点");
        characterPage.Controls.Add(_characterPanel);
        var monsterPage = new TabPage("刷怪");
        monsterPage.Controls.Add(_monsterPanel);
        var exportPage = new TabPage("导出/导入");
        exportPage.Controls.Add(_exportPanel);
        var npcPage = new TabPage("NPC 对话");
        npcPage.Controls.Add(_npcPanel);
        tabs.TabPages.Add(characterPage);
        tabs.TabPages.Add(monsterPage);
        tabs.TabPages.Add(exportPage);
        tabs.TabPages.Add(npcPage);
        tabs.TabPages.Add(farmPage);
        tabs.TabPages.Add(lootPage);
        tabs.TabPages.Add(questPage);
        tabs.TabPages.Add(petPage);
        root.Controls.Add(tabs, 0, 1);
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Text = "请先填写连接信息，点「连接数据库」，再点「一键读取数据」。";
        root.Controls.Add(_statusLabel, 0, 2);
        Controls.Add(root);
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 620,
            FixedPanel = FixedPanel.Panel1
        };
        split.Panel1.Controls.Add(BuildMonsterPanel());
        split.Panel2.Controls.Add(BuildLootPanel());
        return split;
    }

    private Control BuildMonsterPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        var filter = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        var showAll = new Button { Text = "显示所有", Width = 80, Height = 26 };
        showAll.Click += (_, _) =>
        {
            _filterBox.Text = string.Empty;
            _filterMode.SelectedIndex = 0;
            RefreshMonsterGrid();
        };
        filter.Controls.Add(showAll);
        var search = new Button { Text = "搜索怪物…", Width = 96, Height = 26 };
        search.Click += async (_, _) => await SearchMonsterAsync();
        filter.Controls.Add(search);
        filter.Controls.Add(new Label
        {
            Text = "按怪物名查询",
            AutoSize = true,
            Padding = new Padding(10, 8, 4, 0)
        });
        _filterBox.Width = 160;
        _filterBox.Margin = new Padding(0, 4, 0, 0);
        _filterBox.TextChanged += (_, _) => RefreshMonsterGrid();
        filter.Controls.Add(_filterBox);
        var query = new Button { Text = "查询", Width = 62, Height = 26 };
        query.Click += (_, _) => RefreshMonsterGrid();
        filter.Controls.Add(query);
        _filterMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _filterMode.Width = 130;
        _filterMode.Margin = new Padding(8, 4, 0, 0);
        _filterMode.Items.AddRange(new object[]
        {
            "全部", "能刷出来的怪", "未配置掉落", "已配置掉落"
        });
        _filterMode.SelectedIndex = 0;
        _filterMode.SelectedIndexChanged += (_, _) => RefreshMonsterGrid();
        filter.Controls.Add(_filterMode);

        _monsterGrid.Dock = DockStyle.Fill;
        _monsterGrid.ReadOnly = true;
        _monsterGrid.AllowUserToAddRows = false;
        _monsterGrid.AllowUserToDeleteRows = false;
        _monsterGrid.AutoGenerateColumns = false;
        _monsterGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _monsterGrid.MultiSelect = false;
        _monsterGrid.RowHeadersVisible = false;
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "中文名", Width = 120 });
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "英文名", Width = 180 });
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "template_key", Width = 220 });
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "档位", Width = 56 });
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "区域", Width = 160 });
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "地图", Width = 70 });
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "刷怪点", Width = 100 });
        _monsterGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "掉落", Width = 110 });
        _monsterGrid.SelectionChanged += async (_, _) =>
        {
            if (!_suppressSelection)
            {
                await LoadSelectedLootAsync();
            }
        };

        _monsterSummary.Dock = DockStyle.Fill;
        _monsterSummary.TextAlign = ContentAlignment.MiddleLeft;

        panel.Controls.Add(filter, 0, 0);
        panel.Controls.Add(_monsterGrid, 0, 1);
        panel.Controls.Add(_monsterSummary, 0, 2);
        return panel;
    }

    private Control BuildLootPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        panel.Controls.Add(BuildDetailPanel(), 0, 0);
        panel.Controls.Add(BuildSettingsAndBulkPanel(), 0, 1);

        _tableHint.Dock = DockStyle.Fill;
        _tableHint.TextAlign = ContentAlignment.MiddleLeft;
        _tableHint.ForeColor = Color.DimGray;
        panel.Controls.Add(_tableHint, 0, 2);

        _ruleGridControl.Dock = DockStyle.Fill;
        panel.Controls.Add(_ruleGridControl, 0, 3);

        panel.Controls.Add(BuildRuleButtons(), 0, 4);
        panel.Controls.Add(BuildWarnings(), 0, 5);
        return panel;
    }

    private Control BuildDetailPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        _detailTitle.Dock = DockStyle.Top;
        _detailTitle.Height = 24;
        _detailTitle.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
        _detailTitle.TextAlign = ContentAlignment.MiddleLeft;
        _detailInfo.Dock = DockStyle.Fill;
        _detailInfo.TextAlign = ContentAlignment.TopLeft;
        panel.Controls.Add(_detailInfo);
        panel.Controls.Add(_detailTitle);
        return panel;
    }

    private Control BuildSettingsAndBulkPanel()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));

        var settings = new GroupBox
        {
            Text = "掉落设置",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var settingsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        _headerEnabled.Text = "启用该掉落表";
        _headerEnabled.AutoSize = true;
        _headerEnabled.Padding = new Padding(0, 6, 12, 0);
        _headerEnabled.CheckedChanged += (_, _) => MarkDirty();
        settingsFlow.Controls.Add(_headerEnabled);
        settingsFlow.Controls.Add(new Label
        {
            Text = "单次最多掉落件数",
            AutoSize = true,
            Padding = new Padding(0, 8, 4, 0)
        });
        _maximumDrops.Minimum = 1;
        _maximumDrops.Maximum = 32;
        _maximumDrops.Value = 1;
        _maximumDrops.Width = 56;
        _maximumDrops.Margin = new Padding(0, 5, 0, 0);
        _maximumDrops.ValueChanged += (_, _) => MarkDirty();
        settingsFlow.Controls.Add(_maximumDrops);
        settingsFlow.Controls.Add(MakeAsyncButton("确认修改", SaveAsync, 90));
        _ruleCountLabel.AutoSize = true;
        _ruleCountLabel.Padding = new Padding(0, 10, 0, 0);
        _ruleCountLabel.ForeColor = Color.DimGray;
        settingsFlow.Controls.Add(_ruleCountLabel);
        settings.Controls.Add(settingsFlow);

        var bulk = new GroupBox
        {
            Text = "批量调整（对已保存的数据）",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
        var bulkFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };
        bulkFlow.Controls.Add(new Label
        {
            Text = "整体调整",
            AutoSize = true,
            Padding = new Padding(0, 8, 4, 0)
        });
        _factor.DecimalPlaces = 2;
        _factor.Minimum = 0.01m;
        _factor.Maximum = 1000m;
        _factor.Value = 1m;
        _factor.Width = 62;
        _factor.Margin = new Padding(0, 5, 0, 0);
        bulkFlow.Controls.Add(_factor);
        bulkFlow.Controls.Add(new Label
        {
            Text = "倍",
            AutoSize = true,
            Padding = new Padding(4, 8, 6, 0)
        });
        bulkFlow.Controls.Add(MakeActionButton("应用当前怪", ApplyFactorToCurrent, 92));
        bulkFlow.Controls.Add(MakeAsyncButton("应用所有怪", () => ApplyFactorToAllAsync(), 92));

        bulkFlow.Controls.Add(new Label
        {
            Text = "统一概率",
            AutoSize = true,
            Padding = new Padding(0, 8, 4, 0)
        });
        _uniformChance.DecimalPlaces = 2;
        _uniformChance.Minimum = 0.01m;
        _uniformChance.Maximum = 100m;
        _uniformChance.Value = 25m;
        _uniformChance.Width = 62;
        _uniformChance.Margin = new Padding(0, 5, 0, 0);
        bulkFlow.Controls.Add(_uniformChance);
        bulkFlow.Controls.Add(new Label
        {
            Text = "%",
            AutoSize = true,
            Padding = new Padding(4, 8, 6, 0)
        });
        bulkFlow.Controls.Add(MakeActionButton("改当前怪", ApplyChanceToCurrent, 84));
        bulkFlow.Controls.Add(MakeAsyncButton("改所有怪", () => ApplyChanceToAllAsync(), 84));
        bulk.Controls.Add(bulkFlow);

        row.Controls.Add(settings, 0, 0);
        row.Controls.Add(bulk, 1, 0);
        return row;
    }

    private Control BuildRuleButtons()
    {
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0)
        };
        buttons.Controls.Add(MakeActionButton("新增规则", () =>
        {
            _ruleGrid?.AddRow(DefaultItemId(), 2500);
            UpdateIndicators();
        }, 90));
        buttons.Controls.Add(MakeActionButton("删除选中规则", () =>
        {
            _ruleGrid?.RemoveSelectedRows();
            UpdateIndicators();
        }, 110));
        buttons.Controls.Add(MakeActionButton("物品表…", PickItem, 84));
        buttons.Controls.Add(MakeAsyncButton("确认修改", SaveAsync, 90));
        buttons.Controls.Add(MakeAsyncButton("重新载入", LoadSelectedLootAsync, 90));
        buttons.Controls.Add(MakeAsyncButton("删除整张表", DeleteTableAsync, 100));
        buttons.Controls.Add(MakeAsyncButton("自检", RunSelfCheckAsync, 72));
        return buttons;
    }

    private Control BuildWarnings()
    {
        var warnings = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0)
        };
        _dirtyLabel.AutoSize = true;
        _dirtyLabel.Padding = new Padding(0, 6, 16, 0);
        _restartLabel.AutoSize = true;
        _restartLabel.Padding = new Padding(0, 6, 0, 0);
        _restartLabel.ForeColor = Color.DarkOrange;
        warnings.Controls.Add(_dirtyLabel);
        warnings.Controls.Add(_restartLabel);
        return warnings;
    }

    private Button MakeActionButton(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += (_, _) => action();
        return button;
    }

    private Button MakeAsyncButton(string text, Func<Task> action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task ConnectAsync()
    {
        try
        {
            _connection.SetBusy(true);
            _connection.ReadInto(_settings);
            _settings.Save();

            _store.Connect(_settings.BuildConnectionString());
            _connection.SetStatus("连接正常", healthy: true);
            await ReadAllAsync();
            await ConnectCharacterPanelAsync();
            await ConnectPetPanelAsync();
            await ConnectFarmPanelAsync();
            await ConnectQuestPanelAsync();
        }
        catch (Exception ex)
        {
            _connection.SetStatus("连接失败", healthy: false);
            MessageBox.Show(
                this,
                $"连接失败：{ex.Message}",
                "错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _connection.SetBusy(false);
        }
    }

    /// <summary>The "one click read": monster list, item catalogue, names.</summary>
    /// <summary>
    /// The pet tab reads its own connection so a pet-specific problem (missing
    /// bounds table, pet content not published in this database) can never break
    /// the loot workflow that shipped first.
    /// </summary>
    private async Task ConnectPetPanelAsync()
    {
        try
        {
            await _petPanel.ConnectAndReadAsync(
                _settings.BuildConnectionString(),
                _settings.ClientRoot);
        }
        catch (Exception ex)
        {
            _petPanel.SetStatus($"宠物页读取失败：{ex.Message}");
        }
    }

    /// <summary>
    /// The farm tab reads its own connection too, so a database that has not run
    /// the farm score migration cannot break the loot or pet workflows.
    /// </summary>
    private async Task ConnectFarmPanelAsync()
    {
        try
        {
            await _farmPanel.ConnectAndReadAsync(
                _settings.BuildConnectionString(),
                _settings.ClientRoot);
        }
        catch (Exception ex)
        {
            _farmPanel.SetStatus($"农场页读取失败：{ex.Message}");
        }
    }

    /// <summary>
    /// The quest reward tab reads its own connection as well; its two tables only
    /// exist once the server migration has run, and an old database must still
    /// leave every other tab usable.
    /// </summary>
    private async Task ConnectQuestPanelAsync()
    {
        try
        {
            // 任务列表来自客户端任务表，所以客户端目录也一起交过去
            _questPanel.SetClientRoot(_settings.ClientRoot);
            await _questPanel.ConnectAndReadAsync(_settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            _questPanel.SetStatus($"任务奖励页读取失败：{ex.Message}");
        }
    }

    /// <summary>
    /// The character/waypoint tab reads its own connection as well: the marker
    /// table only exists once the server migration has run, and a database
    /// without it must still leave every other tab usable.
    /// </summary>
    private async Task ConnectCharacterPanelAsync()
    {
        try
        {
            await _characterPanel.ConnectAndReadAsync(_settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            _characterPanel.SetStatus($"角色页读取失败：{ex.Message}");
        }

        try
        {
            await _monsterPanel.ConnectAndReadAsync(_settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            _monsterPanel.SetStatus($"刷怪页读取失败：{ex.Message}");
        }

        try
        {
            await _exportPanel.ConnectAndReadAsync(_settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            _exportPanel.SetStatus($"导出页读取失败：{ex.Message}");
        }

        try
        {
            _npcPanel.SetClientRoot(_settings.ClientRoot);
            await _npcPanel.ConnectAndReadAsync(_settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            _npcPanel.SetStatus($"NPC 页读取失败：{ex.Message}");
        }
    }

    private async Task ReadAllAsync()    {
        if (!_store.IsConnected)
        {
            MessageBox.Show(this, "请先点「连接数据库」。", "提示");
            return;
        }

        try
        {
            _connection.SetBusy(true);
            _connection.ReadInto(_settings);
            _settings.Save();

            _monsters = await _store.LoadMonstersAsync();
            _catalog = ClientTextCatalog.Load(_settings.ClientRoot);
            ApplyChineseNames();
            await LoadItemsAsync();
            // 「拾取后」和 12 列「物品属性」都是服务端迁移加的：旧库没有就把对应列藏起来
            _boundColumnAvailable = await _store.HasBoundOnPickupColumnAsync();
            _ruleGrid?.SetBoundColumnVisible(_boundColumnAvailable);
            _attributeColumnsAvailable = await _store.HasItemAttributeColumnsAsync();
            _ruleGrid?.SetAttributeColumnsVisible(_attributeColumnsAvailable);
            _questPanel.SetAttributeColumnsVisible(_attributeColumnsAvailable);
            await LoadItemAttributesAsync();
            RefreshMonsterGrid();
            try
            {
                await _characterPanel.ReadAsync();
            }
            catch (Exception ex)
            {
                _characterPanel.SetStatus($"角色页读取失败：{ex.Message}");
            }

            try
            {
                await _monsterPanel.ReadAsync();
            }
            catch (Exception ex)
            {
                _monsterPanel.SetStatus($"刷怪页读取失败：{ex.Message}");
            }

            try
            {
                await _exportPanel.RefreshAsync();
            }
            catch (Exception ex)
            {
                _exportPanel.SetStatus($"导出页读取失败：{ex.Message}");
            }

            try
            {
                _npcPanel.SetClientRoot(_settings.ClientRoot);
                await _npcPanel.ReadAsync();
            }
            catch (Exception ex)
            {
                _npcPanel.SetStatus($"NPC 页读取失败：{ex.Message}");
            }

            try
            {
                await _petPanel.ReadAsync();
            }
            catch (Exception ex)
            {
                _petPanel.SetStatus($"宠物页读取失败：{ex.Message}");
            }

            try
            {
                await _farmPanel.ReadAsync();
            }
            catch (Exception ex)
            {
                _farmPanel.SetStatus($"农场页读取失败：{ex.Message}");
            }

            try
            {
                await _questPanel.ReadAsync();
            }
            catch (Exception ex)
            {
                _questPanel.SetStatus($"任务奖励页读取失败：{ex.Message}");
            }

            var configured = _monsters.Count(static m => m.HasLootTable);
            _connection.SetStatus("连接正常", healthy: true);
            _statusLabel.Text =
                $"数据库 {_settings.Database}｜怪物模板 {_monsters.Count} 个（已配置掉落 {configured} 个）" +
                $"｜物品 {_items.Count} 个｜中文名：怪物 {_catalog.MonsterNameCount} 条 / 物品 {_catalog.ItemNameCount} 条" +
                (_boundColumnAvailable
                    ? string.Empty
                    : "｜⚠ monster_loot_rules 缺 bound_on_pickup 列（服务端迁移未应用），「拾取后」列已隐藏") +
                (_attributeColumnsAvailable
                    ? string.Empty
                    : "｜⚠ 缺 12 列物品属性（迁移 20260927_214 未应用），「属性…」已隐藏") +
                (_attributeCatalog.Attributes.Count > 0
                    ? $"｜附加属性表 {_attributeCatalog.Attributes.Count} 条" +
                      $"（中文名 {_attributeCatalog.ChineseNameCount} 条）"
                    : "｜⚠ 没读到附加属性表，「属性…」只能填数字 id");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"读取失败：{ex.Message}",
                "错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            _connection.SetBusy(false);
        }
    }

    private void ApplyChineseNames()
    {
        _monsters = _monsters
            .Select(monster => monster with
            {
                ChineseName = _catalog.MonsterName(monster.TemplateKey, string.Empty)
            })
            .ToList();
    }

    private async Task LoadItemsAsync()
    {
        var items = await _store.LoadItemsAsync();
        _items = items.ToDictionary(
            static item => item.Id,
            item => item with
            {
                ChineseName = _catalog.ItemName(item.NameKey, string.Empty)
            });
        // 任务奖励页签也用这份物品目录（中文名来自同一个 ClientTextCatalog）
        _questPanel.SetItems(_items.Values.ToList());
    }

    /// <summary>
    /// 附加属性表：属性 id/等级上限取服务端 item_attribute_templates，
    /// 中文名取客户端 Text\EquipDescription.dat。两边都交给「属性…」弹窗用。
    /// </summary>
    private async Task LoadItemAttributesAsync()
    {
        _itemAttributeTemplates = await _store.LoadItemAttributeTemplatesAsync();
        RefreshAttributeCatalog();
    }

    /// <summary>换客户端目录后也要重来一遍（中文名来自客户端）。</summary>
    private void RefreshAttributeCatalog()
    {
        _attributeCatalog = ItemAttributeCatalog.Load(
            _settings.ClientRoot,
            _itemAttributeTemplates);
        _questPanel.SetAttributeCatalog(_attributeCatalog);
    }

    /// <summary>「属性…」弹窗：返回 null 表示取消（调用方保持原值）。</summary>
    private ItemAttributeValues? EditItemAttributes(
        string itemDescription,
        ItemAttributeValues values)
    {
        using var dialog = new ItemAttributeDialog(
            _attributeCatalog,
            itemDescription.Length > 0 ? itemDescription : "（未选择物品）",
            values);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Values : null;
    }

    private void BrowseClientRoot()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择游戏客户端根目录（含 Localization 文件夹）",
            SelectedPath = _settings.ClientRoot
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings.ClientRoot = dialog.SelectedPath;
        _connection.LoadFrom(_settings);
        _settings.Save();
        _catalog = ClientTextCatalog.Load(_settings.ClientRoot);
        ApplyChineseNames();
        foreach (var id in _items.Keys.ToList())
        {
            _items[id] = _items[id] with
            {
                ChineseName = _catalog.ItemName(_items[id].NameKey, string.Empty)
            };
        }

        RefreshMonsterGrid();
        _ruleGrid?.SetItemDescriber(DescribeItem);
        _questPanel.SetItems(_items.Values.ToList());
        // 换了客户端：任务表与属性中文名都要按新客户端重来
        _questPanel.SetClientRoot(_settings.ClientRoot);
        RefreshAttributeCatalog();
    }

    private void RefreshMonsterGrid()
    {
        if (_monsterGrid.Columns.Count == 0)
        {
            return;
        }

        var query = _filterBox.Text.Trim();
        var mode = _filterMode.SelectedIndex;
        IEnumerable<MonsterRow> rows = _monsters;

        if (mode == 1)
        {
            rows = rows.Where(static m => m.IsSpawned);
        }
        else if (mode == 2)
        {
            rows = rows.Where(static m => !m.HasLootTable);
        }
        else if (mode == 3)
        {
            rows = rows.Where(static m => m.HasLootTable);
        }

        if (query.Length > 0)
        {
            rows = rows.Where(m =>
                m.TemplateKey.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.ChineseName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                m.EnglishName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = rows.ToList();
        _suppressSelection = true;
        try
        {
            _monsterGrid.SuspendLayout();
            _monsterGrid.Rows.Clear();
            foreach (var monster in list)
            {
                var index = _monsterGrid.Rows.Add();
                var row = _monsterGrid.Rows[index];
                row.Tag = monster.TemplateKey;
                row.Cells[0].Value = monster.DisplayName;
                row.Cells[1].Value = monster.EnglishName;
                row.Cells[2].Value = monster.TemplateKey;
                row.Cells[3].Value = monster.Rank;
                row.Cells[4].Value = _catalog.MonsterRegion(monster.TemplateKey) is { Length: > 0 } region
                    ? $"{monster.Scenes}（{region}）"
                    : monster.Scenes;
                row.Cells[5].Value = monster.Maps;
                row.Cells[6].Value = monster.SpawnSummary;
                row.Cells[7].Value = monster.LootSummary;
                if (!monster.IsSpawned)
                {
                    row.Cells[6].Style.ForeColor = Color.Firebrick;
                }
            }

            _monsterGrid.ResumeLayout();
        }
        finally
        {
            _suppressSelection = false;
        }

        _monsterSummary.Text =
            $"显示 {list.Count} / {_monsters.Count} 个怪物模板" +
            $"（其中能刷出来 {list.Count(static m => m.IsSpawned)} 个；" +
            "掉落按 template_key 绑定，同一 key 跨地图共享）";
    }

    /// <summary>
    /// "搜索怪物…" button: find a monster by Chinese or English name and jump
    /// straight to it, instead of scrolling a list of 883 templates.
    /// </summary>
    private async Task SearchMonsterAsync()
    {
        if (_monsters.Count == 0)
        {
            MessageBox.Show(this, "请先连接数据库并点「一键读取数据」。", "提示");
            return;
        }

        using var picker = new MonsterPickerForm(_monsters, _filterBox.Text.Trim());
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var key = picker.SelectedTemplateKey;
        if (!IsRowVisible(key))
        {
            _filterBox.Text = string.Empty;
            _filterMode.SelectedIndex = 0;
            RefreshMonsterGrid();
        }

        SelectMonsterRow(key);
        await LoadSelectedLootAsync();
    }

    private bool IsRowVisible(string templateKey)
    {
        foreach (DataGridViewRow row in _monsterGrid.Rows)
        {
            if (row.Tag as string == templateKey)
            {
                return true;
            }
        }

        return false;
    }

    private async Task LoadSelectedLootAsync()
    {
        var key = _monsterGrid.CurrentRow?.Tag as string;
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (_dirty)
        {
            var answer = MessageBox.Show(
                this,
                "当前怪物有未保存的改动，切换后将丢失。继续吗？",
                "未保存的改动",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes)
            {
                SelectMonsterRow(_currentKey);
                return;
            }
        }

        try
        {
            _currentKey = key;
            var monster = _monsters.FirstOrDefault(m => m.TemplateKey == key);
            _detailTitle.Text = monster is null
                ? key
                : $"{monster.DisplayName}　（{monster.EnglishName}）";
            _detailInfo.Text = monster is null
                ? key
                : $"template_key：{key}　｜　档位：{monster.Rank}　｜　区域：{monster.Scenes}" +
                  $"　｜　地图：{monster.Maps}　｜　刷怪点：{monster.SpawnCount}" +
                  (monster.IsSpawned ? string.Empty : "（不会掉落）");

            var header = await _store.LoadHeaderAsync(key);
            List<LootRule> rules = header is null
                ? new List<LootRule>()
                : await _store.LoadRulesAsync(key);

            _suppressHeaderEvents = true;
            try
            {
                _headerEnabled.Checked = header?.Enabled ?? true;
                _maximumDrops.Value = header?.MaximumDrops ?? 1;
            }
            finally
            {
                _suppressHeaderEvents = false;
            }

            _ruleGrid?.Bind(rules);
            var hint = header is null
                ? "该怪物尚未配置掉落：新增规则后点「确认修改」即可创建掉落表。"
                : $"已加载 {rules.Count} 条规则。概率是每条独立掷骰、按序号从小到大，" +
                  "凑够「单次最多掉落件数」后立刻停止；所以序号越靠后，实际到手率越低。";
            if (!_boundColumnAvailable)
            {
                // 这一列是服务端迁移加的；旧库没有它时整列已隐藏，保存不会去写它
                hint += "「拾取后」列因为库里还没有 bound_on_pickup 字段已隐藏" +
                        "（重启一次服务端跑迁移后回来点「一键读取数据」即可出现）。";
            }

            if (!_attributeColumnsAvailable)
            {
                hint += "「属性…」按钮因为库里还没有那 12 列物品属性已隐藏" +
                        "（迁移 20260927_214，重启服务端后点「一键读取数据」即可出现）。";
            }

            if (monster is not null && !monster.IsSpawned)
            {
                _tableHint.ForeColor = Color.Firebrick;
                _tableHint.Text =
                    "⚠ 该怪在已发布的世界内容里刷怪点为 0，掉落不会生效（先把它加进世界内容才行）。" +
                    hint;
            }
            else
            {
                _tableHint.ForeColor = Color.DimGray;
                _tableHint.Text = hint;
            }

            _dirty = false;
            UpdateIndicators();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"读取掉落表失败：{ex.Message}",
                "错误",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void SelectMonsterRow(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        _suppressSelection = true;
        try
        {
            foreach (DataGridViewRow row in _monsterGrid.Rows)
            {
                if (row.Tag as string == key)
                {
                    row.Selected = true;
                    _monsterGrid.CurrentCell = row.Cells[0];
                    _monsterGrid.FirstDisplayedScrollingRowIndex = row.Index;
                    return;
                }
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void PickItem()
    {
        if (_ruleGrid is null || _items.Count == 0)
        {
            MessageBox.Show(this, "请先连接数据库并读取数据。", "提示");
            return;
        }

        if (_ruleGrid.IsEmpty)
        {
            _ruleGrid.AddRow(DefaultItemId(), 2500);
        }

        using var picker = new ItemPickerForm(_items.Values.ToList(), _ruleGrid.CurrentItemId);
        if (picker.ShowDialog(this) == DialogResult.OK)
        {
            _ruleGrid.TrySetCurrentRowItem(picker.SelectedItemId);
            UpdateIndicators();
        }
    }

    private void ApplyFactorToCurrent()
    {
        if (_ruleGrid is null || _ruleGrid.IsEmpty)
        {
            MessageBox.Show(this, "当前怪物没有规则。", "提示");
            return;
        }

        _ruleGrid.MultiplyPercents((double)_factor.Value);
        UpdateIndicators();
    }

    private async Task ApplyFactorToAllAsync()
    {
        var factor = (double)_factor.Value;
        var answer = MessageBox.Show(
            this,
            $"将数据库中【所有】掉落表的概率乘以 {factor} 倍（结果限制在 0.01%-100%），继续吗？",
            "批量调整所有怪",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _connection.SetBusy(true);
            var affected = await _store.MultiplyChancesAsync(factor);
            _pendingRestart = true;
            UpdateIndicators();
            await ReloadAfterBulkAsync();
            MessageBox.Show(
                this,
                $"已更新 {affected} 条规则。\n\n需要重启游戏服务器才会生效。",
                "完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"批量调整失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connection.SetBusy(false);
        }
    }

    private void ApplyChanceToCurrent()
    {
        if (_ruleGrid is null || _ruleGrid.IsEmpty)
        {
            MessageBox.Show(this, "当前怪物没有规则。", "提示");
            return;
        }

        _ruleGrid.SetPercents((double)_uniformChance.Value);
        UpdateIndicators();
    }

    private async Task ApplyChanceToAllAsync()
    {
        var percent = (double)_uniformChance.Value;
        var answer = MessageBox.Show(
            this,
            $"把数据库中【所有】掉落规则的概率统一改成 {percent}%，继续吗？",
            "批量修改所有怪",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _connection.SetBusy(true);
            var basisPoints = (int)Math.Round(percent * 100d, MidpointRounding.AwayFromZero);
            var affected = await _store.SetChancesAsync(basisPoints);
            _pendingRestart = true;
            UpdateIndicators();
            await ReloadAfterBulkAsync();
            MessageBox.Show(
                this,
                $"已更新 {affected} 条规则。\n\n需要重启游戏服务器才会生效。",
                "完成",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"批量修改失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connection.SetBusy(false);
        }
    }

    private async Task ReloadAfterBulkAsync()
    {
        var key = _currentKey;
        _monsters = await _store.LoadMonstersAsync();
        ApplyChineseNames();
        RefreshMonsterGrid();
        SelectMonsterRow(key);
        await LoadSelectedLootAsync();
    }

    private async Task SaveAsync()
    {
        if (_ruleGrid is null || string.IsNullOrEmpty(_currentKey))
        {
            MessageBox.Show(this, "请先选择一只怪物。", "提示");
            return;
        }

        try
        {
            _connection.SetBusy(true);
            var rules = _ruleGrid.ReadRules();
            var maximumDrops = (short)_maximumDrops.Value;
            await _store.SaveLootAsync(
                _currentKey,
                maximumDrops,
                _headerEnabled.Checked,
                rules);

            _dirty = false;
            _pendingRestart = true;
            UpdateIndicators();

            var key = _currentKey;
            _monsters = await _store.LoadMonstersAsync();
            ApplyChineseNames();
            RefreshMonsterGrid();
            SelectMonsterRow(key);

            MessageBox.Show(
                this,
                $"已保存 {key}：{rules.Count} 条规则，单次最多 {maximumDrops} 件。\n\n" +
                "注意：掉落内容只在游戏服务器启动时读取，需要重启服务器才会生效。",
                "保存成功",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (LootValidationException ex)
        {
            MessageBox.Show(this, ex.Message, "校验未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connection.SetBusy(false);
        }
    }

    private async Task DeleteTableAsync()
    {
        if (string.IsNullOrEmpty(_currentKey))
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"确定删除 {_currentKey} 的整张掉落表吗？\n该怪物将不再掉落任何物品（规则会一并级联删除）。",
            "删除掉落表",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            _connection.SetBusy(true);
            await _store.DeleteLootTableAsync(_currentKey);
            _pendingRestart = true;
            _dirty = false;
            UpdateIndicators();
            var key = _currentKey;
            _monsters = await _store.LoadMonstersAsync();
            ApplyChineseNames();
            RefreshMonsterGrid();
            SelectMonsterRow(key);
            await LoadSelectedLootAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"删除失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connection.SetBusy(false);
        }
    }

    private async Task RunSelfCheckAsync()
    {
        if (!_store.IsConnected)
        {
            MessageBox.Show(this, "请先连接数据库。", "提示");
            return;
        }

        try
        {
            _connection.SetBusy(true);
            var problems = await _store.SelfCheckAsync();
            var text = problems.Count == 0
                ? "自检通过：结构、不变量与启停状态都没有问题。"
                : string.Join(
                    Environment.NewLine,
                    problems.Select(static problem => problem.TemplateKey.Length > 0
                        ? $"[{problem.Severity}] {problem.TemplateKey}：{problem.Message}"
                        : $"[{problem.Severity}] {problem.Message}"));
            ShowTextDialog("掉落表自检结果", text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"自检失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _connection.SetBusy(false);
        }
    }

    private void ShowTextDialog(string title, string text)
    {
        using var dialog = new Form
        {
            Text = title,
            Width = 780,
            Height = 520,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false
        };
        dialog.Controls.Add(new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Text = text
        });
        dialog.ShowDialog(this);
    }

    private void MarkDirty()
    {
        if (_suppressHeaderEvents)
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
            ? "⚠ 已写入数据库，但必须重启游戏服务器才会生效（掉落内容只在启动时读取）"
            : string.Empty;
        var count = _ruleGrid?.RowCount ?? 0;
        _ruleCountLabel.Text = _ruleGrid is null
            ? string.Empty
            : $"当前规则数 {count}（必须 ≥ 单次最多掉落件数）";
    }

    private string DescribeItem(int itemId)
    {
        if (itemId <= 0)
        {
            return "（未选择）";
        }

        if (!_items.TryGetValue(itemId, out var item))
        {
            return $"⚠ 未知物品 {itemId}（item_templates 中不存在，保存会被外键拒绝）";
        }

        var name = string.IsNullOrWhiteSpace(item.ChineseName)
            ? item.DisplayName
            : $"{item.ChineseName} / {item.DisplayName}";
        return $"{name}（{item.Kind}）";
    }

    private int DefaultItemId() =>
        _items.Count == 0 ? 0 : _items.Keys.Min();
}
