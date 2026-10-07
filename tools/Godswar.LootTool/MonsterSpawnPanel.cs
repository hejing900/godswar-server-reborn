namespace Godswar.LootTool;

/// <summary>
/// 「刷怪」页签：挑一个标记点（地图 + 坐标）→ 选一个该图已发布的怪物模板 →
/// 填数量、等级、血量与攻防等属性 → 生成一组 GM 刷怪点；下方管理已生成的点
/// 与该图的按模板属性覆盖。
/// </summary>
/// <remarks>
/// <para>
/// 生成出来的点写在服务端迁移 <c>20261004_228_gm_monster_overrides</c> 建的表里。
/// 服务端**只在启动时**读它们，所以保存之后必须重启 <c>godswar-server</c> 才会生效；
/// 界面顶部会一直用橙字提醒。
/// </para>
/// <para>
/// 等级与血量写进刷怪包体（服务端启动时会校验，0 会被拒收），而物攻/魔攻/物防/魔防/
/// 命中/闪避/暴击/暴抗这些是服务端的**代码公式**，包体里没有它们，所以工具把它们写进
/// 逐点属性覆盖表，服务端在结算时替换。**留空 = 不覆盖**，不是 0。
/// </para>
/// </remarks>
internal sealed class MonsterSpawnPanel : UserControl, IAsyncDisposable
{
    private const string NotConnectedMessage = "请先在顶部连接数据库。";

    private readonly MonsterStore _store = new();
    private readonly ComboBox _mapBox = new();
    private readonly ComboBox _waypointBox = new();
    private readonly ComboBox _templateBox = new();
    private readonly TextBox _namePrefixBox = new();
    private readonly TextBox _countBox = new();
    private readonly TextBox _spreadBox = new();
    private readonly TextBox _facingBox = new();
    private readonly TextBox _levelBox = new();
    private readonly TextBox _maximumHealthBox = new();
    private readonly TextBox _currentHealthBox = new();
    private readonly TextBox _physicalAttackBox = new();
    private readonly TextBox _magicAttackBox = new();
    private readonly TextBox _physicalDefenseBox = new();
    private readonly TextBox _magicDefenseBox = new();
    private readonly TextBox _hitBox = new();
    private readonly TextBox _dodgeBox = new();
    private readonly TextBox _criticalBox = new();
    private readonly TextBox _criticalResistanceBox = new();
    private readonly TextBox _xBox = new();
    private readonly TextBox _zBox = new();
    private readonly DataGridView _spawnGrid = new();
    private readonly DataGridView _attributeGrid = new();
    private readonly Label _restartLabel = new();
    private readonly StatusLabel _status = new();

    private List<MapChoice> _maps = [];
    private List<WaypointRow> _waypoints = [];
    private List<MonsterTemplateChoice> _templates = [];
    private List<GmMonsterSpawnRow> _spawns = [];
    private List<MonsterAttributeRow> _attributes = [];
    private bool _connected;
    private bool _busy;

    public MonsterSpawnPanel()
    {
        Dock = DockStyle.Fill;
        Controls.Add(BuildUi());
    }

    public void SetStatus(string text) => SetStatus(text, error: false);

    public void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    public async Task ConnectAndReadAsync(string connectionString)
    {
        _store.Connect(connectionString);
        _connected = true;
        await ReadAsync();
    }

    public async Task ReadAsync()
    {
        if (!_connected || _busy)
        {
            return;
        }

        _busy = true;
        try
        {
            _maps = await _store.LoadMapsAsync();
            FillMaps();
            if (!await _store.HasOverrideSchemaAsync())
            {
                _restartLabel.Text = string.Empty;
                SetStatus(MonsterStore.MissingSchemaMessage(_store.DatabaseName), error: true);
                return;
            }

            await ReloadForSelectedMapAsync();
            SetStatus($"已连接 {_store.DatabaseName}：地图 {_maps.Count} 张。");
        }
        catch (Exception error)
        {
            SetStatus($"读取失败：{error.Message}", error: true);
        }
        finally
        {
            _busy = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    /* ------------------------------------------------------------------ 界面 */

    private Control BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = NotConnectedMessage;
        root.Controls.Add(_status, 0, 2);
        return root;
    }

    private Control BuildHeader()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        row.Controls.Add(new Label
        {
            Text = "地图",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0)
        });
        _mapBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _mapBox.Width = 240;
        _mapBox.Margin = new Padding(0, 4, 8, 0);
        _mapBox.SelectedIndexChanged += async (_, _) => await ReloadForSelectedMapAsync();
        row.Controls.Add(_mapBox);
        row.Controls.Add(MakeButton("刷新", ReadAsync, 70));
        _restartLabel.AutoSize = true;
        _restartLabel.ForeColor = Color.DarkOrange;
        _restartLabel.Margin = new Padding(12, 8, 0, 0);
        _restartLabel.Text = "保存后需重启 godswar-server 才生效。";
        row.Controls.Add(_restartLabel);
        return row;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 620,
            Orientation = Orientation.Vertical
        };
        split.Panel1.Controls.Add(BuildEditor());
        split.Panel2.Controls.Add(BuildLists());
        return split;
    }

    private Control BuildEditor()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            Padding = new Padding(0, 0, 8, 0)
        };
        for (var row = 0; row < 6; row++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        }

        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var anchor = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        anchor.Controls.Add(Label("标记点"));
        _waypointBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _waypointBox.Width = 260;
        _waypointBox.Margin = new Padding(0, 4, 8, 0);
        anchor.Controls.Add(_waypointBox);
        anchor.Controls.Add(MakeButton("取标记点坐标", FillFromWaypoint, 130));
        layout.Controls.Add(anchor, 0, 0);

        var templateRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        templateRow.Controls.Add(Label("怪物模板"));
        _templateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _templateBox.Width = 360;
        _templateBox.Margin = new Padding(0, 4, 0, 0);
        templateRow.Controls.Add(_templateBox);
        layout.Controls.Add(templateRow, 0, 1);

        var placement = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        placement.Controls.Add(Label("名字前缀"));
        AddBox(placement, _namePrefixBox, 150, "例如 诅咒之地二-巡逻");
        placement.Controls.Add(Label("X"));
        AddBox(placement, _xBox, 80, "X");
        placement.Controls.Add(Label("Z"));
        AddBox(placement, _zBox, 80, "Z");
        placement.Controls.Add(Label("散布半径"));
        AddBox(placement, _spreadBox, 60, "0");
        placement.Controls.Add(Label("朝向"));
        AddBox(placement, _facingBox, 60, "0");
        layout.Controls.Add(placement, 0, 2);

        var vitals = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        vitals.Controls.Add(Label("数量"));
        AddBox(vitals, _countBox, 50, "1");
        vitals.Controls.Add(Label("等级"));
        AddBox(vitals, _levelBox, 60, "继承");
        vitals.Controls.Add(Label("最大血量"));
        AddBox(vitals, _maximumHealthBox, 80, "继承");
        vitals.Controls.Add(Label("当前血量"));
        AddBox(vitals, _currentHealthBox, 80, "满");
        layout.Controls.Add(vitals, 0, 3);

        var attack = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        attack.Controls.Add(Label("物攻"));
        AddBox(attack, _physicalAttackBox, 60, "继承");
        attack.Controls.Add(Label("魔攻"));
        AddBox(attack, _magicAttackBox, 60, "继承");
        attack.Controls.Add(Label("物防"));
        AddBox(attack, _physicalDefenseBox, 60, "继承");
        attack.Controls.Add(Label("魔防"));
        AddBox(attack, _magicDefenseBox, 60, "继承");
        layout.Controls.Add(attack, 0, 4);

        var ratings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        ratings.Controls.Add(Label("命中"));
        AddBox(ratings, _hitBox, 60, "继承");
        ratings.Controls.Add(Label("闪避"));
        AddBox(ratings, _dodgeBox, 60, "继承");
        ratings.Controls.Add(Label("暴击"));
        AddBox(ratings, _criticalBox, 60, "继承");
        ratings.Controls.Add(Label("暴抗"));
        AddBox(ratings, _criticalResistanceBox, 60, "继承");
        layout.Controls.Add(ratings, 0, 5);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("生成刷怪点", CreateSpawnsAsync, 130));
        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(12, 8, 0, 0),
            Text = "等级/血量写进包体；攻防命闪暴写属性覆盖表。留空 = 不覆盖（不是 0）。"
        };
        actions.Controls.Add(hint);
        layout.Controls.Add(actions, 0, 6);
        return layout;
    }

    private Control BuildLists()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8, 0, 0, 0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

        layout.Controls.Add(new Label
        {
            Text = "本图 GM 刷怪点（工具写的，重启后生效）",
            Dock = DockStyle.Fill
        }, 0, 0);
        ConfigureGrid(_spawnGrid);
        _spawnGrid.Columns.Add(Column("名字", 130));
        _spawnGrid.Columns.Add(Column("模板", 190));
        _spawnGrid.Columns.Add(Column("对象ID", 70));
        _spawnGrid.Columns.Add(Column("坐标", 110));
        _spawnGrid.Columns.Add(Column("启用", 50));
        _spawnGrid.Columns.Add(Column("更新时间", 110));
        layout.Controls.Add(_spawnGrid, 0, 1);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("启用/停用", ToggleSelectedSpawnAsync, 100));
        actions.Controls.Add(MakeButton("删除刷怪点", DeleteSelectedSpawnAsync, 110));
        layout.Controls.Add(actions, 0, 2);

        var attributeHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2
        };
        attributeHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        attributeHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        attributeHost.Controls.Add(new Label
        {
            Text = "本图按模板属性覆盖（对所有该模板的怪生效）",
            Dock = DockStyle.Fill
        }, 0, 0);
        ConfigureGrid(_attributeGrid);
        _attributeGrid.Columns.Add(Column("模板", 230));
        _attributeGrid.Columns.Add(Column("覆盖内容", 330));
        _attributeGrid.Columns.Add(Column("更新时间", 110));
        attributeHost.Controls.Add(_attributeGrid, 0, 1);
        layout.Controls.Add(attributeHost, 0, 3);
        return layout;
    }

    private static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(6, 9, 4, 0)
    };

    private static void AddBox(
        FlowLayoutPanel row,
        TextBox box,
        int width,
        string placeholder)
    {
        box.Width = width;
        box.Margin = new Padding(0, 4, 8, 0);
        box.PlaceholderText = placeholder;
        row.Controls.Add(box);
    }

    private Button MakeButton(string text, Func<Task> action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception error)
            {
                SetStatus(error.Message, error: true);
            }
        };
        button.Margin = new Padding(0, 4, 8, 0);
        return button;
    }

    private static Button MakeButton(string text, Action action, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 28 };
        button.Click += (_, _) => action();
        button.Margin = new Padding(0, 4, 8, 0);
        return button;
    }

    private static void ConfigureGrid(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.BackgroundColor = SystemColors.Window;
    }

    private static DataGridViewTextBoxColumn Column(string header, int width) =>
        new()
        {
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

    /* ------------------------------------------------------------------ 数据 */

    private void FillMaps()
    {
        var previous = (_mapBox.SelectedItem as MapChoice)?.MapId;
        _mapBox.Items.Clear();
        foreach (var map in _maps)
        {
            _mapBox.Items.Add(map);
        }

        if (_mapBox.Items.Count == 0)
        {
            return;
        }

        var index = previous is null
            ? 0
            : Math.Max(0, _maps.FindIndex(m => m.MapId == previous.Value));
        _mapBox.SelectedIndex = index;
    }

    private short SelectedMapId =>
        (_mapBox.SelectedItem as MapChoice)?.MapId ?? (short)0;

    private async Task ReloadForSelectedMapAsync()
    {
        if (!_connected || _busy || _mapBox.SelectedItem is not MapChoice map)
        {
            return;
        }

        _busy = true;
        try
        {
            _templates = await _store.LoadTemplatesAsync(map.MapId);
            _templateBox.Items.Clear();
            foreach (var template in _templates)
            {
                _templateBox.Items.Add(template);
            }

            if (_templateBox.Items.Count > 0)
            {
                _templateBox.SelectedIndex = 0;
            }

            _waypoints = await _store.LoadWaypointsAsync(map.MapId);
            _waypointBox.Items.Clear();
            foreach (var waypoint in _waypoints)
            {
                _waypointBox.Items.Add(waypoint);
            }

            _spawns = await _store.LoadSpawnRowsAsync(map.MapId);
            _attributes = await _store.LoadTemplateAttributesAsync(map.MapId);
            RenderSpawns();
            RenderAttributes();
            SetStatus(
                $"map{map.MapId}：可选模板 {_templates.Count} 个、标记点 {_waypoints.Count} 个、" +
                $"已写 GM 刷怪点 {_spawns.Count} 个、模板属性覆盖 {_attributes.Count} 条。");
        }
        finally
        {
            _busy = false;
        }
    }

    private void RenderSpawns()
    {
        _spawnGrid.Rows.Clear();
        foreach (var spawn in _spawns)
        {
            _spawnGrid.Rows.Add(
                spawn.Name,
                spawn.TemplateKey,
                spawn.ObjectId,
                spawn.PositionText,
                spawn.Enabled ? "是" : "否",
                spawn.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"));
        }
    }

    private void RenderAttributes()
    {
        _attributeGrid.Rows.Clear();
        foreach (var row in _attributes)
        {
            _attributeGrid.Rows.Add(
                row.TemplateKey,
                row.Summary,
                row.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"));
        }
    }

    private void FillFromWaypoint()
    {
        if (_waypointBox.SelectedItem is not WaypointRow waypoint)
        {
            SetStatus("这张图还没有标记点：先去「角色与标记点」存一个。", error: true);
            return;
        }

        _xBox.Text = waypoint.X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _zBox.Text = waypoint.Z.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        _facingBox.Text = waypoint.Facing.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        if (_namePrefixBox.Text.Length == 0)
        {
            _namePrefixBox.Text = waypoint.Name;
        }

        SetStatus($"已取标记点「{waypoint.Name}」的坐标：{waypoint.PositionText}。");
    }

    private async Task CreateSpawnsAsync()
    {
        if (_templateBox.SelectedItem is not MonsterTemplateChoice template)
        {
            SetStatus("先选一个怪物模板（只列该图已发布的模板）。", error: true);
            return;
        }

        if (!TryReadFloat(_xBox, required: true, out var x) ||
            !TryReadFloat(_zBox, required: true, out var z))
        {
            SetStatus("X / Z 必须填数字（可以先点「取标记点坐标」）。", error: true);
            return;
        }

        TryReadFloat(_spreadBox, required: false, out var spread);
        TryReadFloat(_facingBox, required: false, out var facing);
        var count = TryReadInt(_countBox, out var parsedCount) && parsedCount > 0
            ? parsedCount
            : 1;
        var prefix = _namePrefixBox.Text.Trim();
        if (prefix.Length == 0)
        {
            prefix = $"{template.TemplateKey}@{SelectedMapId}";
        }

        var attributes = new MonsterAttributeValues(
            PhysicalAttack: ReadOptional(_physicalAttackBox),
            MagicAttack: ReadOptional(_magicAttackBox),
            PhysicalDefense: ReadOptional(_physicalDefenseBox),
            MagicDefense: ReadOptional(_magicDefenseBox),
            Hit: ReadOptional(_hitBox),
            Dodge: ReadOptional(_dodgeBox),
            Critical: ReadOptional(_criticalBox),
            CriticalResistance: ReadOptional(_criticalResistanceBox));

        var created = await _store.CreateSpawnsAsync(new SpawnBatchRequest(
            SelectedMapId,
            (_waypointBox.SelectedItem as WaypointRow)?.Id,
            prefix,
            template.TemplateKey,
            template.DisplayName,
            count,
            x,
            z,
            spread,
            facing,
            ReadOptional(_levelBox),
            ReadOptional(_currentHealthBox),
            ReadOptional(_maximumHealthBox),
            attributes,
            Environment.UserName));

        _spawns = await _store.LoadSpawnRowsAsync(SelectedMapId);
        RenderSpawns();
        SetStatus(MonsterStore.DescribeBatch(created) + " 现在重启 godswar-server 就会出现在游戏里。");
    }

    private async Task ToggleSelectedSpawnAsync()
    {
        var selected = SelectedSpawn();
        if (selected is null)
        {
            SetStatus("先在右边选中一个刷怪点。", error: true);
            return;
        }

        await _store.SetSpawnEnabledAsync(selected.Id, !selected.Enabled, Environment.UserName);
        _spawns = await _store.LoadSpawnRowsAsync(SelectedMapId);
        RenderSpawns();
        SetStatus($"刷怪点「{selected.Name}」已{(selected.Enabled ? "停用" : "启用")}。");
    }

    private async Task DeleteSelectedSpawnAsync()
    {
        var selected = SelectedSpawn();
        if (selected is null)
        {
            SetStatus("先在右边选中一个刷怪点。", error: true);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"删除刷怪点「{selected.Name}」（对象 {selected.ObjectId}）？",
            "确认删除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        await _store.DeleteSpawnAsync(selected.Id);
        _spawns = await _store.LoadSpawnRowsAsync(SelectedMapId);
        RenderSpawns();
        SetStatus($"刷怪点「{selected.Name}」已删除。");
    }

    private GmMonsterSpawnRow? SelectedSpawn()
    {
        if (_spawnGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        var objectId = Convert.ToUInt32(_spawnGrid.SelectedRows[0].Cells[2].Value);
        return _spawns.FirstOrDefault(spawn => spawn.ObjectId == objectId);
    }

    private static bool TryReadFloat(TextBox box, bool required, out float value)
    {
        var text = box.Text.Trim();
        if (text.Length == 0)
        {
            value = 0f;
            return !required;
        }

        if (float.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        value = 0f;
        return false;
    }

    private static bool TryReadInt(TextBox box, out int value) =>
        int.TryParse(
            box.Text.Trim(),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);

    /// <summary>空的输入框 = 不覆盖（<c>null</c>），不是 0。</summary>
    private static int? ReadOptional(TextBox box)
    {
        var text = box.Text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (!int.TryParse(
                text,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new InvalidOperationException($"「{text}」不是整数（留空表示不覆盖）。");
        }

        return value < 0
            ? throw new InvalidOperationException("属性不能是负数。")
            : value;
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
