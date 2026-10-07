namespace Godswar.LootTool;

/// <summary>
/// 「角色与标记点」页签：左边按名字查角色并显示它**当前的地图与坐标**，
/// 右边是存下来的标记点列表（地图 + 坐标的命名存档），可以新增 / 覆盖 / 改名 / 删除。
/// </summary>
/// <remarks>
/// <para>
/// 标记点本身不改变任何运行时内容：它是后续「在某个点刷怪 / 加 NPC」的挂载处，
/// 所以这一页只读写 <c>gm_waypoints</c>，不需要重启服务端。
/// </para>
/// <para>
/// 坐标的来源是角色的**最近一次落库位置**：服务端最多每 2 秒写一次，跑动中会有一个
/// 身位的偏差。要求精确时用右侧的「手动坐标」，或者站定再取。
/// </para>
/// </remarks>
internal sealed class CharacterPanel : UserControl, IAsyncDisposable
{
    private const int CharacterIdColumn = 0;
    private const int CharacterMapColumn = 6;
    private const int WaypointNameColumn = 0;

    private const string NotConnectedMessage = "请先在顶部连接数据库。";

    private readonly CharacterStore _store = new();
    private readonly TextBox _nameBox = new();
    private readonly DataGridView _characterGrid = new();
    private readonly Label _selectedLabel = new();
    private readonly TextBox _waypointNameBox = new();
    private readonly TextBox _waypointNoteBox = new();
    private readonly DataGridView _waypointGrid = new();
    private readonly ComboBox _manualMap = new();
    private readonly NumericUpDown _manualX = new();
    private readonly NumericUpDown _manualZ = new();
    private readonly NumericUpDown _manualFacing = new();
    private readonly StatusLabel _status = new();

    private List<CharacterRow> _characters = [];
    private List<WaypointRow> _waypoints = [];
    private List<MapChoice> _maps = [];
    private CharacterRow? _selected;
    private bool _connected;
    private bool _busy;

    public CharacterPanel()
    {
        Dock = DockStyle.Fill;
        Controls.Add(BuildUi());
        UpdateButtons();
    }

    public void SetStatus(string text) => SetStatus(text, error: false);

    public void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? Color.Firebrick : Color.DimGray;
    }

    /// <summary>连接并载入地图表 + 角色 + 标记点。</summary>
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
            _maps = await _store.LoadMapChoicesAsync();
            FillManualMaps();
            _characters = await _store.SearchCharactersAsync(_nameBox.Text);
            RenderCharacters();

            if (await _store.HasWaypointSchemaAsync())
            {
                _waypoints = await _store.LoadWaypointsAsync();
                RenderWaypoints();
                SetStatus(
                    $"已连接 {_store.DatabaseName}：角色 {_characters.Count} 个，" +
                    $"标记点 {_waypoints.Count} 个。");
            }
            else
            {
                _waypoints = [];
                RenderWaypoints();
                SetStatus(CharacterStore.MissingSchemaMessage(_store.DatabaseName), error: true);
            }
        }
        catch (Exception error)
        {
            SetStatus($"读取失败：{error.Message}", error: true);
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
    }

    /* ------------------------------------------------------------------ 界面 */

    private Control BuildUi()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 700,
            Orientation = Orientation.Vertical
        };

        split.Panel1.Controls.Add(BuildCharacterSide());
        split.Panel2.Controls.Add(BuildWaypointSide());

        var host = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8)
        };
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        host.Controls.Add(split, 0, 0);
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = NotConnectedMessage;
        host.Controls.Add(_status, 0, 1);
        return host;
    }

    private Control BuildCharacterSide()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 0, 8, 0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var search = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        search.Controls.Add(new Label
        {
            Text = "角色名",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0)
        });
        _nameBox.Width = 200;
        _nameBox.Margin = new Padding(0, 4, 8, 0);
        _nameBox.PlaceholderText = "支持模糊匹配，留空列出全部";
        _nameBox.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                await ReloadCharactersAsync();
            }
        };
        search.Controls.Add(_nameBox);
        search.Controls.Add(MakeButton("查询", ReloadCharactersAsync, 70));
        search.Controls.Add(MakeButton("列出全部", async () =>
        {
            _nameBox.Text = string.Empty;
            await ReloadCharactersAsync();
        }, 90));
        layout.Controls.Add(search, 0, 0);

        CharacterGrid(_characterGrid);
        _characterGrid.SelectionChanged += (_, _) =>
        {
            _selected = SelectedCharacter();
            UpdateButtons();
        };
        layout.Controls.Add(_characterGrid, 0, 1);

        _selectedLabel.Dock = DockStyle.Fill;
        _selectedLabel.Text = "未选中角色。";
        layout.Controls.Add(_selectedLabel, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("把这个位置存成标记点", SaveFromSelectedAsync, 190));
        actions.Controls.Add(MakeButton("把坐标填到右边手动框", FillManualFromSelected, 190));
        layout.Controls.Add(actions, 0, 3);
        return layout;
    }

    private Control BuildWaypointSide()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8, 0, 0, 0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var identity = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        identity.Controls.Add(new Label
        {
            Text = "名字",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0)
        });
        _waypointNameBox.Width = 170;
        _waypointNameBox.Margin = new Padding(0, 4, 8, 0);
        _waypointNameBox.PlaceholderText = "唯一，例如 诅咒之地二-入口";
        identity.Controls.Add(_waypointNameBox);
        identity.Controls.Add(new Label
        {
            Text = "备注",
            AutoSize = true,
            Margin = new Padding(4, 8, 6, 0)
        });
        _waypointNoteBox.Width = 160;
        _waypointNoteBox.Margin = new Padding(0, 4, 0, 0);
        identity.Controls.Add(_waypointNoteBox);
        layout.Controls.Add(identity, 0, 0);

        var manual = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        manual.Controls.Add(new Label
        {
            Text = "手动坐标",
            AutoSize = true,
            Margin = new Padding(0, 8, 6, 0)
        });
        _manualMap.DropDownStyle = ComboBoxStyle.DropDownList;
        _manualMap.Width = 210;
        _manualMap.Margin = new Padding(0, 4, 8, 0);
        manual.Controls.Add(_manualMap);
        manual.Controls.Add(MakeNumber(_manualX, 110, 1, -100000, 100000, 1));
        manual.Controls.Add(MakeNumber(_manualZ, 110, 1, -100000, 100000, 1));
        manual.Controls.Add(MakeNumber(_manualFacing, 80, 1, -360, 360, 1));
        manual.Controls.Add(MakeButton("按手动坐标新增/覆盖", SaveManualAsync, 180));
        layout.Controls.Add(manual, 0, 1);

        WaypointGrid(_waypointGrid);
        _waypointGrid.SelectionChanged += (_, _) =>
        {
            var row = SelectedWaypoint();
            if (row is not null)
            {
                _waypointNameBox.Text = row.Name;
                _waypointNoteBox.Text = row.Note;
            }

            UpdateButtons();
        };
        layout.Controls.Add(_waypointGrid, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("用选中角色位置覆盖", MoveSelectedWaypointAsync, 180));
        actions.Controls.Add(MakeButton("改名 / 备注", RenameSelectedWaypointAsync, 130));
        actions.Controls.Add(MakeButton("删除", DeleteSelectedWaypointAsync, 70));
        actions.Controls.Add(MakeButton("刷新", ReadAsync, 70));
        layout.Controls.Add(actions, 0, 3);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "标记点只是地图+坐标的存档，改完立即生效，不用重启服务端；" +
                   "后续的刷怪 / 加 NPC 会以它为挂载处。"
        };
        layout.Controls.Add(hint, 0, 4);
        return layout;
    }

    private static void CharacterGrid(DataGridView grid)
    {
        ConfigureGrid(grid, readOnly: true);
        grid.Columns.Add(Column("ID", 56));
        grid.Columns.Add(Column("角色名", 130));
        grid.Columns.Add(Column("账号", 110));
        grid.Columns.Add(Column("职业", 70));
        grid.Columns.Add(Column("等级", 56));
        grid.Columns.Add(Column("阵营", 70));
        grid.Columns.Add(Column("地图", 130));
        grid.Columns.Add(Column("坐标 (X, Z)", 130));
        grid.Columns.Add(Column("在线", 60));
        grid.Columns.Add(Column("最后登录", 140));
    }

    private static void WaypointGrid(DataGridView grid)
    {
        ConfigureGrid(grid, readOnly: true);
        grid.Columns.Add(Column("名字", 150));
        grid.Columns.Add(Column("地图", 140));
        grid.Columns.Add(Column("坐标 (X, Z)", 120));
        grid.Columns.Add(Column("朝向", 56));
        grid.Columns.Add(Column("来源", 60));
        grid.Columns.Add(Column("备注", 130));
        grid.Columns.Add(Column("更新时间", 140));
        grid.Columns.Add(Column("改自", 70));
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
    }

    private static DataGridViewTextBoxColumn Column(string header, int width) =>
        new() { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable };

    private static NumericUpDown MakeNumber(
        NumericUpDown box,
        int width,
        int decimals,
        decimal minimum,
        decimal maximum,
        int increment)
    {
        box.DecimalPlaces = decimals;
        box.Minimum = minimum;
        box.Maximum = maximum;
        box.Increment = increment;
        box.Width = width;
        box.Margin = new Padding(0, 4, 8, 0);
        return box;
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

    /* ------------------------------------------------------------------ 数据 */

    private void FillManualMaps()
    {
        var previous = (_manualMap.SelectedItem as MapChoice)?.MapId;
        _manualMap.Items.Clear();
        foreach (var map in _maps)
        {
            _manualMap.Items.Add(map);
        }

        if (_manualMap.Items.Count == 0)
        {
            return;
        }

        var index = previous is null
            ? 0
            : Math.Max(0, _maps.FindIndex(m => m.MapId == previous.Value));
        _manualMap.SelectedIndex = index;
    }

    private void RenderCharacters()
    {
        _characterGrid.Rows.Clear();
        foreach (var character in _characters)
        {
            var map = _maps.FirstOrDefault(m => m.MapId == character.MapId);
            var mapText = map is null
                ? $"map{character.MapId}"
                : $"map{character.MapId} · {map.DisplayName}";
            _characterGrid.Rows.Add(
                character.Id,
                character.Name,
                character.Account,
                character.ProfessionName,
                character.Level,
                character.CampName,
                mapText,
                character.PositionText,
                character.LoginStatus == 1 ? "在线" : "离线",
                character.LastLogin.ToString("MM-dd HH:mm"));
        }

        if (_characterGrid.Rows.Count == 0)
        {
            _selectedLabel.Text = "没有匹配的角色。";
        }
    }

    private void RenderWaypoints()
    {
        _waypointGrid.Rows.Clear();
        foreach (var waypoint in _waypoints)
        {
            var mapText = string.IsNullOrEmpty(waypoint.MapDisplayName)
                ? $"map{waypoint.MapId}"
                : $"map{waypoint.MapId} · {waypoint.MapDisplayName}";
            _waypointGrid.Rows.Add(
                waypoint.Name,
                mapText,
                waypoint.PositionText,
                waypoint.Facing.ToString("F2"),
                waypoint.SourceName,
                waypoint.Note,
                waypoint.UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm"),
                waypoint.UpdatedBy);
        }
    }

    private CharacterRow? SelectedCharacter()
    {
        if (_characterGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        var id = Convert.ToInt32(_characterGrid.SelectedRows[0].Cells[CharacterIdColumn].Value);
        return _characters.FirstOrDefault(c => c.Id == id);
    }

    private WaypointRow? SelectedWaypoint()
    {
        if (_waypointGrid.SelectedRows.Count == 0)
        {
            return null;
        }

        var name = _waypointGrid.SelectedRows[0].Cells[WaypointNameColumn].Value as string;
        return _waypoints.FirstOrDefault(w => w.Name == name);
    }

    private void UpdateButtons()
    {
        _selectedLabel.Text = _selected is null
            ? "未选中角色。"
            : $"选中：{_selected.Name}（{_selected.ProfessionName}，{_selected.Level} 级）· " +
              $"map{_selected.MapId} · {_selected.PositionText}" +
              $"（{(_selected.LoginStatus == 1 ? "在线" : "离线")}，最近存档）";
    }

    private async Task ReloadCharactersAsync()
    {
        if (!_connected)
        {
            SetStatus(NotConnectedMessage, error: true);
            return;
        }

        try
        {
            _characters = await _store.SearchCharactersAsync(_nameBox.Text);
            RenderCharacters();
            SetStatus($"查到 {_characters.Count} 个角色。");
        }
        catch (Exception error)
        {
            SetStatus($"查询角色失败：{error.Message}", error: true);
        }
    }

    private async Task SaveFromSelectedAsync()
    {
        if (_selected is null)
        {
            SetStatus("先在左边选中一个角色。", error: true);
            return;
        }

        var name = _waypointNameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("给标记点起个名字（右上角「名字」框）。", error: true);
            return;
        }

        await _store.SaveWaypointAsync(new WaypointInput(
            name,
            _waypointNoteBox.Text,
            _selected.Name,
            (short)_selected.MapId,
            _selected.X,
            _selected.Z,
            (float)_manualFacing.Value,
            "character",
            Environment.UserName));
        _waypoints = await _store.LoadWaypointsAsync();
        RenderWaypoints();
        SetStatus($"已保存标记点「{name}」：map{_selected.MapId} · {_selected.PositionText}。");
    }

    private void FillManualFromSelected()
    {
        if (_selected is null)
        {
            SetStatus("先在左边选中一个角色。", error: true);
            return;
        }

        var index = _maps.FindIndex(m => m.MapId == _selected.MapId);
        if (index >= 0)
        {
            _manualMap.SelectedIndex = index;
        }

        _manualX.Value = Clamp(_manualX, _selected.X);
        _manualZ.Value = Clamp(_manualZ, _selected.Z);
        SetStatus("已把选中角色的地图与坐标填到手动框。");
    }

    private async Task SaveManualAsync()
    {
        var name = _waypointNameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("给标记点起个名字。", error: true);
            return;
        }

        if (_manualMap.SelectedItem is not MapChoice map)
        {
            SetStatus("先选一张地图。", error: true);
            return;
        }

        await _store.SaveWaypointAsync(new WaypointInput(
            name,
            _waypointNoteBox.Text,
            null,
            map.MapId,
            (float)_manualX.Value,
            (float)_manualZ.Value,
            (float)_manualFacing.Value,
            "manual",
            Environment.UserName));
        _waypoints = await _store.LoadWaypointsAsync();
        RenderWaypoints();
        SetStatus($"已保存标记点「{name}」：map{map.MapId} · " +
                  $"{_manualX.Value:F1}, {_manualZ.Value:F1}。");
    }

    private async Task MoveSelectedWaypointAsync()
    {
        var waypoint = SelectedWaypoint();
        if (waypoint is null)
        {
            SetStatus("先在右边选中一个标记点。", error: true);
            return;
        }

        if (_selected is null)
        {
            SetStatus("先在左边选中一个角色，用它的坐标覆盖。", error: true);
            return;
        }

        await _store.MoveWaypointAsync(
            waypoint.Id,
            (short)_selected.MapId,
            _selected.X,
            _selected.Z,
            (float)_manualFacing.Value,
            Environment.UserName);
        _waypoints = await _store.LoadWaypointsAsync();
        RenderWaypoints();
        SetStatus($"已把「{waypoint.Name}」覆盖为 map{_selected.MapId} · {_selected.PositionText}。");
    }

    private async Task RenameSelectedWaypointAsync()
    {
        var waypoint = SelectedWaypoint();
        if (waypoint is null)
        {
            SetStatus("先在右边选中一个标记点。", error: true);
            return;
        }

        await _store.RenameWaypointAsync(
            waypoint.Id,
            _waypointNameBox.Text,
            _waypointNoteBox.Text,
            Environment.UserName);
        _waypoints = await _store.LoadWaypointsAsync();
        RenderWaypoints();
        SetStatus($"已更新标记点「{_waypointNameBox.Text.Trim()}」。");
    }

    private async Task DeleteSelectedWaypointAsync()
    {
        var waypoint = SelectedWaypoint();
        if (waypoint is null)
        {
            SetStatus("先在右边选中一个标记点。", error: true);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"删除标记点「{waypoint.Name}」？",
            "确认删除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        await _store.DeleteWaypointAsync(waypoint.Id);
        _waypoints = await _store.LoadWaypointsAsync();
        RenderWaypoints();
        SetStatus($"已删除标记点「{waypoint.Name}」。");
    }

    private static decimal Clamp(NumericUpDown box, float value)
    {
        var asDecimal = (decimal)value;
        return Math.Clamp(asDecimal, box.Minimum, box.Maximum);
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
