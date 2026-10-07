using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>一棵 GM 对话树（NPC 的多级窗口）。</summary>
internal sealed record GmNpcDialogue(
    string DialogueKey,
    string DisplayName,
    int FunctionFlag,
    int EntryPage,
    string Note,
    bool Enabled);

/// <summary>一页 = 客户端的一个窗口。</summary>
internal sealed record GmNpcDialoguePage(
    string DialogueKey,
    int PageIndex,
    string BodyText,
    string Note);

/// <summary>一页上的一个按钮。<c>NextPageIndex</c> 为 null 表示这一页是终点。</summary>
internal sealed record GmNpcDialogueButton(
    string DialogueKey,
    int PageIndex,
    short Slot,
    string Label,
    int NextPageIndex)
{
    public string SlotText => $"#{Slot}";
}

/// <summary>一棵树 + 它的页 + 它的按钮，导出/生成补丁时按这个整体走。</summary>
internal sealed record GmNpcTree(
    GmNpcDialogue Dialogue,
    IReadOnlyList<GmNpcDialoguePage> Pages,
    IReadOnlyList<GmNpcDialogueButton> Buttons)
{
    /// <summary>服务端要发出去的每个 (页, 条目) —— 也是生成补丁的唯一依据。</summary>
    public IEnumerable<(int Page, int SubId, string Text, short? Slot, int? NextPage)>
        Entries()
    {
        foreach (var page in Pages.OrderBy(static page => page.PageIndex))
        {
            var buttons = Buttons
                .Where(button => button.PageIndex == page.PageIndex)
                .OrderBy(static button => button.Slot)
                .ToList();
            // 101 是客户端既有的"正文"约定（见 NpcFunFallback.lua 的 {1,101,TEXT,...}），
            // 这里把它抬到 页号*1000+101，让多页多按钮的回传值互不冲突。
            yield return (
                page.PageIndex,
                NpcDialogueStore.BodySubId(page.PageIndex),
                page.BodyText,
                null,
                null);
            foreach (var button in buttons)
            {
                yield return (
                    page.PageIndex,
                    NpcDialogueStore.ButtonSubId(page.PageIndex, button.Slot),
                    button.Label,
                    button.Slot,
                    button.NextPageIndex);
            }
        }
    }
}

/// <summary>该图上已被发布内容用过的 NPC 外观模板（GM NPC 只能借这些外观）。</summary>
internal sealed record NpcTemplateChoice(
    string TemplateKey,
    uint AppearanceType,
    int UsedCount,
    uint SampleObjectId,
    float SampleX,
    float SampleZ)
{
    public override string ToString() =>
        $"{TemplateKey}（外观 {AppearanceType}，该图已在用 {UsedCount} 个）";
}

/// <summary>工具写进 <c>gm_npc_spawns</c> 的一个放置，读回来的样子。</summary>
internal sealed record GmNpcSpawnRow(
    long Id,
    string Name,
    short MapId,
    string NpcKey,
    string TemplateKey,
    string? DialogueKey,
    long? WaypointId,
    uint ObjectId,
    uint AppearanceType,
    float X,
    float Z,
    float Facing,
    bool Enabled,
    string Note)
{
    public string PositionText => $"{X:F1}, {Z:F1}";
}

/// <summary>要写进 <c>gm_npc_spawns</c> 的内容（名字唯一，重名即覆盖）。</summary>
internal sealed record GmNpcSpawnInput(
    string Name,
    short MapId,
    string NpcKey,
    string TemplateKey,
    string? DialogueKey,
    long? WaypointId,
    uint ObjectId,
    uint AppearanceType,
    float X,
    float Z,
    float Facing,
    bool Enabled,
    string Note);

/// <summary>
/// 读写服务端迁移 <c>20261004_229_gm_npc_content</c> 建的 GM NPC 表。
/// </summary>
/// <remarks>
/// 这里只做"存"；把它变成游戏里的 NPC 由服务端做，把它变成能显示的窗口由
/// <see cref="ClientNpcPatch"/> 生成客户端补丁做 —— 两边都需要，缺一不可。
/// </remarks>
internal sealed class NpcDialogueStore : IAsyncDisposable
{
    private const string UndefinedTableSqlState = "42P01";

    /// <summary>客户端 <c>Set_NpcFun_Text</c> 里空着的那个分派值（0..120 里只有 85/86 没人用）。</summary>
    public const int DefaultFunctionFlag = 85;

    /// <summary>客户端只有 <c>FirstWin_Button1..12</c>。</summary>
    public const int MaximumSlots = 12;

    /// <summary>
    /// 线路上的 <c>SubID</c> 编码：<c>页号 * 1000 + 条目</c>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么不能直接用 <c>1..12</c> 当按钮的 SubID：客户端的对话框协议是**无状态**的 ——
    /// 它把按钮的 SubID 原样回传，服务端拿这个数去一张扁平表里查下一步
    /// （见 <c>ScriptedNpcDialogue.Steps</c>）。所以回传值必须在**整棵对话内唯一**，
    /// 否则第 1 页的 1 号按钮和第 2 页的 1 号按钮会撞成同一个数。
    /// </para>
    /// <para>
    /// 正文条目占 <c>+101</c>（沿用客户端既有的 101=正文的习惯），按钮占 <c>+1..+12</c>，
    /// 两者天然不冲突。服务端 <c>GmNpcDialoguePolicy</c> 里有一份同样的公式，
    /// 两边各有一个检查/自检钉住具体数值，谁改了都会红。
    /// </para>
    /// </remarks>
    public const int SubIdStride = 1000;

    /// <summary>正文条目的线路 SubID。</summary>
    public static int BodySubId(int pageIndex) => (pageIndex * SubIdStride) + 101;

    /// <summary>某个按钮的线路 SubID；它同时也是客户端点击后回传的数。</summary>
    public static int ButtonSubId(int pageIndex, int slot) =>
        (pageIndex * SubIdStride) + slot;

    private NpgsqlDataSource? _dataSource;

    private NpgsqlDataSource Source =>
        _dataSource ?? throw new InvalidOperationException("尚未连接数据库。");

    public bool IsConnected => _dataSource is not null;

    public string DatabaseName { get; private set; } = string.Empty;

    public void Connect(string connectionString)
    {
        Disconnect();
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
        DatabaseName = new NpgsqlConnectionStringBuilder(connectionString).Database
            ?? string.Empty;
    }

    public void Disconnect()
    {
        _dataSource?.Dispose();
        _dataSource = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
            _dataSource = null;
        }
    }

    public static string MissingSchemaMessage(string databaseName) =>
        $"数据库 {databaseName} 缺少 GM NPC 表（gm_npc_dialogues / _pages / _buttons / " +
        "gm_npc_spawns），请先启动一次游戏服务端让它跑数据库迁移，再重试。";

    public async Task<bool> HasSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT to_regclass('public.gm_npc_dialogues') IS NOT NULL
               AND to_regclass('public.gm_npc_dialogue_pages') IS NOT NULL
               AND to_regclass('public.gm_npc_dialogue_buttons') IS NOT NULL
               AND to_regclass('public.gm_npc_spawns') IS NOT NULL;
            """);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<List<GmNpcDialogue>> LoadDialoguesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = new List<GmNpcDialogue>();
        try
        {
            await using var command = Source.CreateCommand(
                """
                SELECT dialogue_key, display_name, function_flag, entry_page, note, enabled
                FROM gm_npc_dialogues
                ORDER BY dialogue_key;
                """);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new GmNpcDialogue(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetString(4),
                    reader.GetBoolean(5)));
            }
        }
        catch (PostgresException error) when (error.SqlState == UndefinedTableSqlState)
        {
            throw new InvalidOperationException(MissingSchemaMessage(DatabaseName), error);
        }

        return rows;
    }

    public async Task<List<GmNpcDialoguePage>> LoadPagesAsync(
        string dialogueKey,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT dialogue_key, page_index, body_text, note
            FROM gm_npc_dialogue_pages
            WHERE dialogue_key = @key
            ORDER BY page_index;
            """);
        command.Parameters.AddWithValue("key", dialogueKey);
        var rows = new List<GmNpcDialoguePage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GmNpcDialoguePage(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return rows;
    }

    public async Task<List<GmNpcDialogueButton>> LoadButtonsAsync(
        string dialogueKey,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT dialogue_key, page_index, slot, label, next_page_index
            FROM gm_npc_dialogue_buttons
            WHERE dialogue_key = @key
            ORDER BY page_index, slot;
            """);
        command.Parameters.AddWithValue("key", dialogueKey);
        var rows = new List<GmNpcDialogueButton>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GmNpcDialogueButton(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4)));
        }

        return rows;
    }

    /// <summary>把所有树整棵读出来（生成补丁要一次拿全）。</summary>
    public async Task<List<GmNpcTree>> LoadTreesAsync(
        CancellationToken cancellationToken = default)
    {
        var trees = new List<GmNpcTree>();
        foreach (var dialogue in await LoadDialoguesAsync(cancellationToken))
        {
            var pages = await LoadPagesAsync(dialogue.DialogueKey, cancellationToken);
            var buttons = await LoadButtonsAsync(dialogue.DialogueKey, cancellationToken);
            trees.Add(new GmNpcTree(dialogue, pages, buttons));
        }

        return trees;
    }

    /// <summary>
    /// 整棵树一次写完（先删页/按钮再插，保证"编辑器里看到的就是库里存的"）。
    /// </summary>
    public async Task SaveTreeAsync(
        GmNpcTree tree,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        Validate(tree);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var upsert = new NpgsqlCommand(
                         """
                         INSERT INTO gm_npc_dialogues (
                             dialogue_key, display_name, function_flag, entry_page,
                             note, enabled, updated_by)
                         VALUES (
                             @key, @name, @flag, @entry, @note, @enabled, @updated_by)
                         ON CONFLICT (dialogue_key) DO UPDATE SET
                             display_name = EXCLUDED.display_name,
                             function_flag = EXCLUDED.function_flag,
                             entry_page = EXCLUDED.entry_page,
                             note = EXCLUDED.note,
                             enabled = EXCLUDED.enabled,
                             updated_at = now(),
                             updated_by = EXCLUDED.updated_by;
                         """,
                         connection,
                         transaction))
        {
            upsert.Parameters.AddWithValue("key", tree.Dialogue.DialogueKey);
            upsert.Parameters.AddWithValue("name", tree.Dialogue.DisplayName ?? string.Empty);
            upsert.Parameters.AddWithValue("flag", tree.Dialogue.FunctionFlag);
            upsert.Parameters.AddWithValue("entry", tree.Dialogue.EntryPage);
            upsert.Parameters.AddWithValue("note", tree.Dialogue.Note ?? string.Empty);
            upsert.Parameters.AddWithValue("enabled", tree.Dialogue.Enabled);
            upsert.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var clear = new NpgsqlCommand(
                         "DELETE FROM gm_npc_dialogue_pages WHERE dialogue_key = @key;",
                         connection,
                         transaction))
        {
            clear.Parameters.AddWithValue("key", tree.Dialogue.DialogueKey);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var page in tree.Pages)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO gm_npc_dialogue_pages (dialogue_key, page_index, body_text, note)
                VALUES (@key, @page, @body, @note);
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("key", tree.Dialogue.DialogueKey);
            insert.Parameters.AddWithValue("page", page.PageIndex);
            insert.Parameters.AddWithValue("body", page.BodyText ?? string.Empty);
            insert.Parameters.AddWithValue("note", page.Note ?? string.Empty);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var button in tree.Buttons)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO gm_npc_dialogue_buttons (
                    dialogue_key, page_index, slot, label, result_number, next_page_index)
                VALUES (@key, @page, @slot, @label, @result, @next);
                """,
                connection,
                transaction);
            insert.Parameters.AddWithValue("key", button.DialogueKey);
            insert.Parameters.AddWithValue("page", button.PageIndex);
            insert.Parameters.Add(new NpgsqlParameter("slot", NpgsqlDbType.Smallint)
            {
                Value = button.Slot
            });
            insert.Parameters.AddWithValue("label", button.Label ?? string.Empty);
            // result_number 是客户端点击后回传的线路 SubID，必须在整棵对话内唯一
            // （客户端协议是无状态的，服务端只靠这个数查下一步）。
            insert.Parameters.AddWithValue(
                "result",
                ButtonSubId(button.PageIndex, button.Slot));
            insert.Parameters.Add(new NpgsqlParameter("next", NpgsqlDbType.Integer)
            {
                Value = button.NextPageIndex > 0 ? button.NextPageIndex : DBNull.Value
            });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteDialogueAsync(
        string dialogueKey,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            "DELETE FROM gm_npc_dialogues WHERE dialogue_key = @key;");
        command.Parameters.AddWithValue("key", dialogueKey);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"对话不存在：{dialogueKey}");
        }
    }

    /// <summary>全部地图，给页签顶部的选择器用。</summary>
    public async Task<List<MapChoice>> LoadMapsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            "SELECT map_id, scene_key, display_name FROM map_templates ORDER BY map_id;");
        var maps = new List<MapChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            maps.Add(new MapChoice(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return maps;
    }

    /// <summary>该图的标记点，给「取标记点坐标」用。</summary>
    public async Task<List<WaypointRow>> LoadWaypointsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT w.id, w.name, w.note, w.character_name, w.map_id,
                   COALESCE(m.scene_key, ''), COALESCE(m.display_name, ''),
                   w.pos_x, w.pos_z, w.facing, w.source, w.updated_at, w.updated_by
            FROM gm_waypoints w
            LEFT JOIN map_templates m ON m.map_id = w.map_id
            WHERE w.map_id = @map_id
            ORDER BY w.name;
            """);
        command.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Smallint)
        {
            Value = mapId
        });

        var rows = new List<WaypointRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new WaypointRow(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                Note: reader.GetString(2),
                CharacterName: reader.GetString(3),
                MapId: reader.GetInt16(4),
                SceneKey: reader.GetString(5),
                MapDisplayName: reader.GetString(6),
                X: reader.GetFloat(7),
                Z: reader.GetFloat(8),
                Facing: reader.GetFloat(9),
                Source: reader.GetString(10),
                UpdatedAt: reader.GetDateTime(11),
                UpdatedBy: reader.GetString(12)));
        }

        return rows;
    }

    /// <summary>
    /// 该图上已经被发布内容用过的 NPC 外观模板。
    /// </summary>
    /// <remarks>
    /// GM NPC 的外观包是服务端按 <c>appearance_type + template_key</c> **合成**的
    /// （不存抓包字节），客户端靠模板名加载模型，所以模板必须是这个图已经认得的，
    /// 否则摆上去是个看不见/点不到的 NPC。和刷怪那条规矩一样。
    /// </remarks>
    public async Task<List<NpcTemplateChoice>> LoadNpcTemplatesAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            WITH published AS (
                SELECT d.template_key,
                       d.appearance_type,
                       d.object_id,
                       d.pos_x,
                       d.pos_z,
                       count(*) OVER (PARTITION BY d.template_key) AS used_count,
                       row_number() OVER (
                           PARTITION BY d.template_key
                           ORDER BY d.object_id) AS ordinal
                FROM npc_spawn_definitions d
                JOIN npc_content_publication p
                  ON p.family = 'npcs' AND p.revision = d.revision
                WHERE d.map_id = @map_id
            )
            SELECT template_key, appearance_type, used_count, object_id, pos_x, pos_z
            FROM published
            WHERE ordinal = 1
            ORDER BY template_key;
            """);
        command.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Smallint)
        {
            Value = mapId
        });

        var rows = new List<NpcTemplateChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new NpcTemplateChoice(
                reader.GetString(0),
                checked((uint)reader.GetInt32(1)),
                checked((int)reader.GetInt64(2)),
                checked((uint)reader.GetInt64(3)),
                reader.GetFloat(4),
                reader.GetFloat(5)));
        }

        return rows;
    }

    /// <summary>该图上工具已经摆过的 NPC。</summary>
    public async Task<List<GmNpcSpawnRow>> LoadSpawnRowsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT id, name, map_id, npc_key, template_key, dialogue_key,
                   waypoint_id, object_id, appearance_type, pos_x, pos_z, facing,
                   enabled, note
            FROM gm_npc_spawns
            WHERE map_id = @map_id
            ORDER BY object_id;
            """);
        command.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Smallint)
        {
            Value = mapId
        });

        var rows = new List<GmNpcSpawnRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GmNpcSpawnRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                checked((uint)reader.GetInt64(7)),
                checked((uint)reader.GetInt32(8)),
                reader.GetFloat(9),
                reader.GetFloat(10),
                reader.GetFloat(11),
                reader.GetBoolean(12),
                reader.GetString(13)));
        }

        return rows;
    }

    /// <summary>按名字新增/覆盖一个 NPC 放置。</summary>
    public async Task SaveSpawnAsync(
        GmNpcSpawnInput input,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new InvalidOperationException("NPC 放置要有一个名字（唯一）。");
        }

        if (input.ObjectId == 0)
        {
            throw new InvalidOperationException("对象 ID 不能是 0。");
        }

        await using var command = Source.CreateCommand(
            """
            INSERT INTO gm_npc_spawns (
                name, map_id, npc_key, template_key, dialogue_key, waypoint_id,
                object_id, appearance_type, pos_x, pos_z, facing, enabled, note,
                updated_by)
            VALUES (
                @name, @map_id, @npc_key, @template_key, @dialogue_key, @waypoint_id,
                @object_id, @appearance_type, @pos_x, @pos_z, @facing, @enabled, @note,
                @updated_by)
            ON CONFLICT (name) DO UPDATE SET
                map_id = EXCLUDED.map_id,
                npc_key = EXCLUDED.npc_key,
                template_key = EXCLUDED.template_key,
                dialogue_key = EXCLUDED.dialogue_key,
                waypoint_id = EXCLUDED.waypoint_id,
                object_id = EXCLUDED.object_id,
                appearance_type = EXCLUDED.appearance_type,
                pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                facing = EXCLUDED.facing,
                enabled = EXCLUDED.enabled,
                note = EXCLUDED.note,
                updated_at = now(),
                updated_by = EXCLUDED.updated_by;
            """);
        command.Parameters.AddWithValue("name", input.Name.Trim());
        command.Parameters.AddWithValue("map_id", input.MapId);
        command.Parameters.AddWithValue("npc_key", input.NpcKey);
        command.Parameters.AddWithValue("template_key", input.TemplateKey);
        command.Parameters.Add(new NpgsqlParameter("dialogue_key", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrEmpty(input.DialogueKey) ? DBNull.Value : input.DialogueKey
        });
        command.Parameters.Add(new NpgsqlParameter("waypoint_id", NpgsqlDbType.Bigint)
        {
            Value = input.WaypointId.HasValue ? input.WaypointId.Value : DBNull.Value
        });
        command.Parameters.AddWithValue("object_id", (long)input.ObjectId);
        command.Parameters.AddWithValue("appearance_type", checked((int)input.AppearanceType));
        command.Parameters.AddWithValue("pos_x", input.X);
        command.Parameters.AddWithValue("pos_z", input.Z);
        command.Parameters.AddWithValue("facing", input.Facing);
        command.Parameters.AddWithValue("enabled", input.Enabled);
        command.Parameters.AddWithValue("note", input.Note ?? string.Empty);
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteSpawnAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            "DELETE FROM gm_npc_spawns WHERE id = @id;");
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"NPC 放置不存在：id={id}");
        }
    }

    /// <summary>
    /// 生成客户端补丁前必须过的校验。这些不是"建议"，每一条都对应客户端的一个硬限制。
    /// </summary>
    /// <remarks>
    /// 尤其是<b>终点页不能和按钮同页</b>：客户端收到正文条目会立刻
    /// <c>NPCFUN:EndMessage(true)</c> 把窗口关掉，同一包里既有正文又有按钮时窗口会一闪而过。
    /// </remarks>
    public static void Validate(GmNpcTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var dialogue = tree.Dialogue;
        if (string.IsNullOrWhiteSpace(dialogue.DialogueKey))
        {
            throw new InvalidOperationException("对话必须有一个 key。");
        }

        if (tree.Pages.Count == 0)
        {
            throw new InvalidOperationException(
                $"对话「{dialogue.DialogueKey}」一页都没有，客户端没有东西可显示。");
        }

        var pages = tree.Pages.Select(static page => page.PageIndex).ToHashSet();
        if (!pages.Contains(dialogue.EntryPage))
        {
            throw new InvalidOperationException(
                $"入口页 {dialogue.EntryPage} 不在页列表里（有 {string.Join("、", pages.OrderBy(x => x))}）。");
        }

        foreach (var button in tree.Buttons)
        {
            if (button.Slot is < 1 or > MaximumSlots)
            {
                throw new InvalidOperationException(
                    $"按钮槽位 {button.Slot} 越界：客户端只有 FirstWin_Button1..{MaximumSlots}。");
            }

            if (!pages.Contains(button.PageIndex))
            {
                throw new InvalidOperationException(
                    $"按钮落在不存在的第 {button.PageIndex} 页上。");
            }

            if (button.NextPageIndex > 0 && !pages.Contains(button.NextPageIndex))
            {
                throw new InvalidOperationException(
                    $"第 {button.PageIndex} 页的按钮指向不存在的第 {button.NextPageIndex} 页。");
            }

            if (string.IsNullOrWhiteSpace(button.Label))
            {
                throw new InvalidOperationException(
                    $"第 {button.PageIndex} 页 #{button.Slot} 没有按钮文字。");
            }
        }

        var duplicate = tree.Buttons
            .GroupBy(static button => (button.PageIndex, button.Slot))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"第 {duplicate.Key.PageIndex} 页的 #{duplicate.Key.Slot} 槽位被配了 {duplicate.Count()} 次。");
        }

        // 可达性：入口页出发，沿按钮能不能走到每一页。
        var reachable = new HashSet<int> { dialogue.EntryPage };
        var queue = new Queue<int>();
        queue.Enqueue(dialogue.EntryPage);
        while (queue.Count > 0)
        {
            var page = queue.Dequeue();
            foreach (var button in tree.Buttons.Where(button => button.PageIndex == page))
            {
                if (button.NextPageIndex > 0 && reachable.Add(button.NextPageIndex))
                {
                    queue.Enqueue(button.NextPageIndex);
                }
            }
        }

        var unreachable = pages.Where(page => !reachable.Contains(page)).OrderBy(x => x).ToList();
        if (unreachable.Count > 0)
        {
            throw new InvalidOperationException(
                $"第 {string.Join("、", unreachable)} 页从入口页走不到（按钮没连过去）。");
        }
    }
}
