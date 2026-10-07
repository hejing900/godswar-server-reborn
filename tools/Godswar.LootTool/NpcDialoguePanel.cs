using System.Globalization;

namespace Godswar.LootTool;

/// <summary>
/// 「NPC 对话」页签：配多级窗口（每页正文 + 按钮），把 NPC 摆到标记点上，
/// 最后**一键生成客户端补丁**。
/// </summary>
/// <remarks>
/// <para>
/// 三个 grid 都是**可直接编辑**的，保存时整棵树读回来过一遍
/// <see cref="NpcDialogueStore.Validate"/>，所以界面上敲错了会在保存时被挡下，
/// 而不是等到生成补丁、甚至等到进游戏才发现。
/// </para>
/// <para>
/// "生成客户端补丁"会改客户端目录下的三个文件（改前自动备份），所以 click 之后就一句
/// 确认框，确认内容里写明目录与备份策略。
/// </para>
/// </remarks>
internal sealed class NpcDialoguePanel : UserControl, IAsyncDisposable
{
    private const string NotConnectedMessage = "请先在顶部连接数据库。";

    private readonly NpcDialogueStore _store = new();
    private readonly ComboBox _mapBox = new();
    private readonly ListBox _dialogueList = new();
    private readonly NumericUpDown _entryPage = new();
    private readonly DataGridView _pageGrid = new();
    private readonly DataGridView _buttonGrid = new();
    private readonly ComboBox _templateBox = new();
    private readonly ComboBox _waypointBox = new();
    private readonly TextBox _spawnNameBox = new();
    private readonly TextBox _npcKeyBox = new();
    private readonly TextBox _objectIdBox = new();
    private readonly TextBox _xBox = new();
    private readonly TextBox _zBox = new();
    private readonly TextBox _facingBox = new();
    private readonly DataGridView _spawnGrid = new();
    private readonly StatusLabel _status = new();

    private List<MapChoice> _maps = [];
    private List<GmNpcTree> _trees = [];
    private List<NpcTemplateChoice> _templates = [];
    private List<WaypointRow> _waypoints = [];
    private List<GmNpcSpawnRow> _spawns = [];
    private string _clientRoot = string.Empty;
    private bool _connected;
    private bool _busy;

    public NpcDialoguePanel()
    {
        Dock = DockStyle.Fill;
        Controls.Add(BuildUi());
    }

    public void SetClientRoot(string clientRoot) => _clientRoot = clientRoot ?? string.Empty;

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
            if (!await _store.HasSchemaAsync())
            {
                SetStatus(NpcDialogueStore.MissingSchemaMessage(_store.DatabaseName), error: true);
                return;
            }

            _trees = await _store.LoadTreesAsync();
            RenderDialogues();
            await ReloadForSelectedMapAsync();
            SetStatus(
                $"已连接 {_store.DatabaseName}：对话 {_trees.Count} 棵" +
                (_clientRoot.Length > 0 ? $"｜客户端目录 {_clientRoot}" : "｜未设置客户端目录"));
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
        row.Controls.Add(Label("地图"));
        _mapBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _mapBox.Width = 220;
        _mapBox.Margin = new Padding(0, 4, 8, 0);
        _mapBox.SelectedIndexChanged += async (_, _) => await ReloadForSelectedMapAsync();
        row.Controls.Add(_mapBox);
        row.Controls.Add(MakeButton("刷新", ReadAsync, 70));
        row.Controls.Add(MakeButton("保存这棵对话", SaveTreeAsync, 120));
        row.Controls.Add(MakeButton("生成客户端补丁…", GeneratePatchAsync, 150));
        var hint = new Label
        {
            AutoSize = true,
            ForeColor = Color.DarkOrange,
            Margin = new Padding(12, 8, 0, 0),
            Text = "补丁会改客户端目录下的 3 个文件（改前备份）；服务端要重启才读新配置。"
        };
        row.Controls.Add(hint);
        return row;
    }

    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 300,
            Orientation = Orientation.Vertical
        };
        split.Panel1.Controls.Add(BuildDialogueSide());
        split.Panel2.Controls.Add(BuildEditorSide());
        return split;
    }

    private Control BuildDialogueSide()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0, 0, 8, 0)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.Controls.Add(Label("对话（一棵 = 一个 NPC 的多级窗口）"), 0, 0);
        _dialogueList.Dock = DockStyle.Fill;
        _dialogueList.IntegralHeight = false;
        _dialogueList.SelectedIndexChanged += (_, _) => RenderSelectedDialogue();
        layout.Controls.Add(_dialogueList, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("新建对话", CreateDialogueAsync, 90));
        actions.Controls.Add(MakeButton("删除对话", DeleteDialogueAsync, 90));
        layout.Controls.Add(actions, 0, 2);
        return layout;
    }

    private Control BuildEditorSide()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28));

        var entry = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        entry.Controls.Add(Label("入口页"));
        _entryPage.Minimum = 1;
        _entryPage.Maximum = 999_999;
        _entryPage.Width = 90;
        _entryPage.Margin = new Padding(0, 4, 12, 0);
        entry.Controls.Add(_entryPage);
        entry.Controls.Add(new Label
        {
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(8, 8, 0, 0),
            Text = "页号必须全局唯一（客户端只按页号找内容）；正文条目的线路号 = 页号×1000+101。"
        });
        layout.Controls.Add(entry, 0, 0);

        layout.Controls.Add(Label("页（一页 = 一个窗口；没有按钮的页 = 终点页，会自己关窗）"), 0, 1);
        ConfigureGrid(_pageGrid, readOnly: false);
        _pageGrid.Columns.Add(Column("页号", 80));
        _pageGrid.Columns.Add(Column("正文（自由文本）", 520));
        layout.Controls.Add(_pageGrid, 0, 2);

        layout.Controls.Add(Label("按钮（一页最多 12 个；「下一页」留空 = 点了不回应）"), 0, 3);
        ConfigureGrid(_buttonGrid, readOnly: false);
        _buttonGrid.Columns.Add(Column("页号", 80));
        _buttonGrid.Columns.Add(Column("槽位 1-12", 70));
        _buttonGrid.Columns.Add(Column("按钮文字", 240));
        _buttonGrid.Columns.Add(Column("下一页（留空=不回应）", 140));
        layout.Controls.Add(_buttonGrid, 0, 4);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.Add(MakeButton("加一页", AddPage, 80));
        actions.Controls.Add(MakeButton("删选中页", RemoveSelectedPage, 100));
        actions.Controls.Add(MakeButton("加按钮", AddButton, 80));
        actions.Controls.Add(MakeButton("删选中按钮", RemoveSelectedButton, 110));
        layout.Controls.Add(actions, 0, 5);

        layout.Controls.Add(BuildPlacement(), 0, 6);
        return layout;
    }

    private Control BuildPlacement()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 560));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var form = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        form.Controls.Add(Label("外观模板"));
        _templateBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _templateBox.Width = 260;
        _templateBox.Margin = new Padding(0, 4, 8, 0);
        form.Controls.Add(_templateBox);
        form.Controls.Add(Label("标记点"));
        _waypointBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _waypointBox.Width = 200;
        _waypointBox.Margin = new Padding(0, 4, 8, 0);
        form.Controls.Add(_waypointBox);
        form.Controls.Add(MakeButton("取标记点坐标", FillFromWaypoint, 110));
        form.Controls.Add(Label("名字"));
        AddBox(form, _spawnNameBox, 120, "唯一");
        form.Controls.Add(Label("npc_key"));
        AddBox(form, _npcKeyBox, 120, "gm_xxx");
        form.Controls.Add(Label("对象ID"));
        AddBox(form, _objectIdBox, 70, "47000");
        form.Controls.Add(Label("X"));
        AddBox(form, _xBox, 70, "X");
        form.Controls.Add(Label("Z"));
        AddBox(form, _zBox, 70, "Z");
        form.Controls.Add(Label("朝向"));
        AddBox(form, _facingBox, 50, "0");
        form.Controls.Add(MakeButton("添加/覆盖放置", SaveSpawnAsync, 120));
        form.Controls.Add(MakeButton("删除选中放置", DeleteSpawnAsync, 120));
        panel.Controls.Add(form, 0, 0);
        panel.SetColumnSpan(form, 2);

        ConfigureGrid(_spawnGrid, readOnly: true);
        _spawnGrid.Columns.Add(Column("名字", 120));
        _spawnGrid.Columns.Add(Column("npc_key", 120));
        _spawnGrid.Columns.Add(Column("模板", 200));
        _spawnGrid.Columns.Add(Column("对话", 110));
        _spawnGrid.Columns.Add(Column("对象ID", 70));
        _spawnGrid.Columns.Add(Column("坐标", 100));
        _spawnGrid.Columns.Add(Column("启用", 50));
        panel.Controls.Add(_spawnGrid, 0, 1);
        panel.SetColumnSpan(_spawnGrid, 2);
        return panel;
    }

    private static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(6, 9, 4, 0)
    };

    private static void AddBox(FlowLayoutPanel row, TextBox box, int width, string placeholder)
    {
        box.Width = width;
        box.Margin = new Padding(0, 4, 8, 0);
        box.PlaceholderText = placeholder;
        row.Controls.Add(box);
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
        grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
    }

    private static DataGridViewTextBoxColumn Column(string header, int width) =>
        new()
        {
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

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

        _mapBox.SelectedIndex = previous is null
            ? 0
            : Math.Max(0, _maps.FindIndex(m => m.MapId == previous.Value));
    }

    private short SelectedMapId => (_mapBox.SelectedItem as MapChoice)?.MapId ?? (short)0;

    private async Task ReloadForSelectedMapAsync()
    {
        if (!_connected || _busy || _mapBox.SelectedItem is not MapChoice map)
        {
            return;
        }

        _busy = true;
        try
        {
            _templates = await _store.LoadNpcTemplatesAsync(map.MapId);
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
            RenderSpawns();
        }
        finally
        {
            _busy = false;
        }
    }

    private void RenderDialogues()
    {
        _dialogueList.Items.Clear();
        foreach (var tree in _trees)
        {
            _dialogueList.Items.Add(tree.Dialogue);
        }

        if (_dialogueList.Items.Count > 0)
        {
            _dialogueList.SelectedIndex = 0;
        }
        else
        {
            _pageGrid.Rows.Clear();
            _buttonGrid.Rows.Clear();
        }
    }

    private void RenderSelectedDialogue()
    {
        _pageGrid.Rows.Clear();
        _buttonGrid.Rows.Clear();
        if (_dialogueList.SelectedItem is not GmNpcDialogue dialogue)
        {
            return;
        }

        var tree = _trees.FirstOrDefault(row => row.Dialogue.DialogueKey == dialogue.DialogueKey);
        if (tree is null)
        {
            return;
        }

        _entryPage.Value = Math.Clamp(tree.Dialogue.EntryPage, 1, 999_999);
        foreach (var page in tree.Pages.OrderBy(static page => page.PageIndex))
        {
            _pageGrid.Rows.Add(page.PageIndex, page.BodyText);
        }

        foreach (var button in tree.Buttons.OrderBy(static b => b.PageIndex).ThenBy(static b => b.Slot))
        {
            _buttonGrid.Rows.Add(
                button.PageIndex,
                button.Slot,
                button.Label,
                button.NextPageIndex > 0
                    ? button.NextPageIndex.ToString(CultureInfo.InvariantCulture)
                    : string.Empty);
        }
    }

    private void RenderSpawns()
    {
        _spawnGrid.Rows.Clear();
        foreach (var spawn in _spawns)
        {
            _spawnGrid.Rows.Add(
                spawn.Name,
                spawn.NpcKey,
                spawn.TemplateKey,
                spawn.DialogueKey ?? string.Empty,
                spawn.ObjectId,
                spawn.PositionText,
                spawn.Enabled ? "是" : "否");
        }
    }

    private GmNpcTree? ReadTree()
    {
        if (_dialogueList.SelectedItem is not GmNpcDialogue dialogue)
        {
            SetStatus("先在左边选中一棵对话。", error: true);
            return null;
        }

        var pages = new List<GmNpcDialoguePage>();
        foreach (DataGridViewRow row in _pageGrid.Rows)
        {
            var pageIndex = ReadInt(row.Cells[0].Value, "页号");
            pages.Add(new GmNpcDialoguePage(
                dialogue.DialogueKey,
                pageIndex,
                row.Cells[1].Value as string ?? string.Empty,
                string.Empty));
        }

        var buttons = new List<GmNpcDialogueButton>();
        foreach (DataGridViewRow row in _buttonGrid.Rows)
        {
            var pageIndex = ReadInt(row.Cells[0].Value, "按钮所在页号");
            var slot = ReadInt(row.Cells[1].Value, "槽位");
            var nextText = (row.Cells[3].Value as string ?? string.Empty).Trim();
            buttons.Add(new GmNpcDialogueButton(
                dialogue.DialogueKey,
                pageIndex,
                checked((short)slot),
                row.Cells[2].Value as string ?? string.Empty,
                nextText.Length == 0 ? 0 : ReadInt(nextText, "下一页")));
        }

        return new GmNpcTree(
            dialogue with { EntryPage = (int)_entryPage.Value },
            pages,
            buttons);
    }

    private static int ReadInt(object? value, string what)
    {
        var text = value?.ToString()?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new InvalidOperationException($"「{what}」不能空着。");
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"「{what}」必须是整数，实际是「{text}」。");
    }

    /// <summary>下一段没人用过的页号区间（每棵对话一段，保证全局唯一）。</summary>
    private int AllocatePageBase() =>
        ((_trees.SelectMany(static tree => tree.Pages)
                .Select(static page => page.PageIndex)
                .DefaultIfEmpty(1000)
                .Max() / 1000) + 1) * 1000;

    private async Task CreateDialogueAsync()
    {
        if (!_connected)
        {
            SetStatus(NotConnectedMessage, error: true);
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var key = $"gm_{stamp}";
        var page = AllocatePageBase() + 1;
        var tree = new GmNpcTree(
            new GmNpcDialogue(key, $"新对话 {stamp}", NpcDialogueStore.DefaultFunctionFlag, page, "", true),
            [new GmNpcDialoguePage(key, page, "在这里写正文（支持自由文本，会写进客户端补丁）。", "")],
            []);
        await _store.SaveTreeAsync(tree, Environment.UserName);
        _trees = await _store.LoadTreesAsync();
        RenderDialogues();
        _dialogueList.SelectedItem = _trees
            .FirstOrDefault(row => row.Dialogue.DialogueKey == key)?.Dialogue;
        SetStatus($"已新建对话 {key}（入口页 {page}）。改完点「保存这棵对话」。");
    }

    private async Task DeleteDialogueAsync()
    {
        if (_dialogueList.SelectedItem is not GmNpcDialogue dialogue)
        {
            SetStatus("先在左边选中一棵对话。", error: true);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"删除对话「{dialogue.DialogueKey}」及其全部页与按钮？\n" +
            "（引用它的 NPC 放置会被解绑，不会删除）",
            "确认删除",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        await _store.DeleteDialogueAsync(dialogue.DialogueKey);
        _trees = await _store.LoadTreesAsync();
        RenderDialogues();
        SetStatus($"已删除对话 {dialogue.DialogueKey}。");
    }

    private async Task SaveTreeAsync()
    {
        var tree = ReadTree();
        if (tree is null)
        {
            return;
        }

        // 校验先跑：界面上敲错的东西要在这里被挡下，而不是生成补丁时才发现。
        NpcDialogueStore.Validate(tree);
        await _store.SaveTreeAsync(tree, Environment.UserName);
        _trees = await _store.LoadTreesAsync();
        RenderDialogues();
        _dialogueList.SelectedItem = _trees
            .FirstOrDefault(row => row.Dialogue.DialogueKey == tree.Dialogue.DialogueKey)?.Dialogue;
        SetStatus(
            $"已保存 {tree.Dialogue.DialogueKey}：{tree.Pages.Count} 页、" +
            $"{tree.Buttons.Count} 按钮。");
    }

    private async Task GeneratePatchAsync()
    {
        if (!_connected)
        {
            SetStatus(NotConnectedMessage, error: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(_clientRoot) || !Directory.Exists(_clientRoot))
        {
            SetStatus("先在顶部填「客户端目录」（补丁要写进那个目录）。", error: true);
            return;
        }

        var enabled = _trees.Count(static tree => tree.Dialogue.Enabled);
        var answer = MessageBox.Show(
            this,
            $"把 {enabled} 棵启用中的对话生成成客户端补丁？\n\n" +
            $"客户端目录：{_clientRoot}\n" +
            "会写/改这 3 个文件（每个改动前先备份成 *_gm-backup-<时间>.bak 形式）：\n" +
            "  • Localization\\en_us\\UI\\XML\\NpcFun\\NpcFunGM.lua（整段重写）\n" +
            "  • Localization\\en_us\\UI\\XML\\NpcFun\\NpcFun.lua（只插 1 个分支）\n" +
            "  • Localization\\en_us\\UI\\XML\\NpcFunLoad.xml（只登记 1 行）",
            "确认生成客户端补丁",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            SetStatus("已取消生成补丁。");
            return;
        }

        var report = ClientNpcPatch.Apply(_clientRoot, _trees);
        SetStatus($"{report.Summary}｜{report.BackupSummary}");
        MessageBox.Show(
            this,
            $"{report.Summary}\n\n{report.BackupSummary}\n\n生成的文件：\n{report.LuaPath}",
            "补丁已生成",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void AddPage()
    {
        if (_dialogueList.SelectedItem is not GmNpcDialogue)
        {
            SetStatus("先在左边选中一棵对话。", error: true);
            return;
        }

        var used = _pageGrid.Rows
            .Cast<DataGridViewRow>()
            .Select(row => int.TryParse(row.Cells[0].Value?.ToString(), out var value) ? value : 0)
            .ToHashSet();
        var candidate = AllocatePageBase() + 1;
        while (used.Contains(candidate))
        {
            candidate++;
        }

        _pageGrid.Rows.Add(candidate, string.Empty);
    }

    private void RemoveSelectedPage()
    {
        if (_pageGrid.SelectedRows.Count == 0)
        {
            SetStatus("先在「页」表里选中一行。", error: true);
            return;
        }

        _pageGrid.Rows.RemoveAt(_pageGrid.SelectedRows[0].Index);
    }

    private void AddButton()
    {
        if (_dialogueList.SelectedItem is not GmNpcDialogue)
        {
            SetStatus("先在左边选中一棵对话。", error: true);
            return;
        }

        var page = _pageGrid.Rows.Count > 0
            ? ReadInt(_pageGrid.Rows[0].Cells[0].Value, "第一行的页号")
            : AllocatePageBase() + 1;
        var used = _buttonGrid.Rows
            .Cast<DataGridViewRow>()
            .Where(row => row.Cells[0].Value?.ToString() == page.ToString(CultureInfo.InvariantCulture))
            .Select(row => int.TryParse(row.Cells[1].Value?.ToString(), out var value) ? value : 0)
            .ToHashSet();
        var slot = 1;
        while (used.Contains(slot) && slot < NpcDialogueStore.MaximumSlots)
        {
            slot++;
        }

        _buttonGrid.Rows.Add(page, slot, "按钮文字", string.Empty);
    }

    private void RemoveSelectedButton()
    {
        if (_buttonGrid.SelectedRows.Count == 0)
        {
            SetStatus("先在「按钮」表里选中一行。", error: true);
            return;
        }

        _buttonGrid.Rows.RemoveAt(_buttonGrid.SelectedRows[0].Index);
    }

    private void FillFromWaypoint()
    {
        if (_waypointBox.SelectedItem is not WaypointRow waypoint)
        {
            SetStatus("这张图还没有标记点：先去「角色与标记点」存一个。", error: true);
            return;
        }

        _xBox.Text = waypoint.X.ToString("F2", CultureInfo.InvariantCulture);
        _zBox.Text = waypoint.Z.ToString("F2", CultureInfo.InvariantCulture);
        _facingBox.Text = waypoint.Facing.ToString("F2", CultureInfo.InvariantCulture);
        if (_spawnNameBox.Text.Length == 0)
        {
            _spawnNameBox.Text = waypoint.Name;
        }

        if (_npcKeyBox.Text.Length == 0)
        {
            _npcKeyBox.Text = $"gm_{_spawnNameBox.Text}";
        }
    }

    private async Task SaveSpawnAsync()
    {
        if (_templateBox.SelectedItem is not NpcTemplateChoice template)
        {
            SetStatus("先选一个外观模板（只列该图已发布内容用过的）。", error: true);
            return;
        }

        if (_dialogueList.SelectedItem is not GmNpcDialogue dialogue)
        {
            SetStatus("先在左边选中要绑定的对话。", error: true);
            return;
        }

        try
        {
            var name = _spawnNameBox.Text.Trim();
            if (name.Length == 0)
            {
                throw new InvalidOperationException("给这个 NPC 放置起个名字（唯一）。");
            }

            var objectId = _objectIdBox.Text.Trim().Length == 0
                ? 47_000L
                : ReadInt(_objectIdBox.Text, "对象ID");
            await _store.SaveSpawnAsync(
                new GmNpcSpawnInput(
                    name,
                    SelectedMapId,
                    _npcKeyBox.Text.Trim().Length > 0
                        ? _npcKeyBox.Text.Trim()
                        : $"gm_{name}",
                    template.TemplateKey,
                    dialogue.DialogueKey,
                    (_waypointBox.SelectedItem as WaypointRow)?.Id,
                    checked((uint)objectId),
                    template.AppearanceType,
                    ReadFloat(_xBox.Text, "X"),
                    ReadFloat(_zBox.Text, "Z"),
                    ReadFloat(_facingBox.Text, "朝向"),
                    true,
                    "GM 工具添加"),
                Environment.UserName);
            _spawns = await _store.LoadSpawnRowsAsync(SelectedMapId);
            RenderSpawns();
            SetStatus($"已保存放置「{name}」：模板 {template.TemplateKey}，对话 {dialogue.DialogueKey}。");
        }
        catch (Exception error)
        {
            SetStatus(error.Message, error: true);
        }
    }

    private async Task DeleteSpawnAsync()
    {
        if (_spawnGrid.SelectedRows.Count == 0)
        {
            SetStatus("先在放置表里选中一行。", error: true);
            return;
        }

        var name = _spawnGrid.SelectedRows[0].Cells[0].Value?.ToString() ?? string.Empty;
        var target = _spawns.FirstOrDefault(spawn => spawn.Name == name);
        if (target is null)
        {
            SetStatus("选中的放置已经不在列表里了，刷新一下。", error: true);
            return;
        }

        await _store.DeleteSpawnAsync(target.Id);
        _spawns = await _store.LoadSpawnRowsAsync(SelectedMapId);
        RenderSpawns();
        SetStatus($"已删除放置「{name}」。");
    }

    private static float ReadFloat(string text, string what) =>
        float.TryParse(
            text.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : throw new InvalidOperationException($"「{what}」必须是数字。");

    private sealed class StatusLabel : Label
    {
        public StatusLabel()
        {
            AutoSize = false;
            Height = 22;
        }
    }
}
