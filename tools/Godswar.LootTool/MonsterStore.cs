using System.Buffers.Binary;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>怪物可选模板：该图上**已经有正式刷怪点**的模板。</summary>
/// <remarks>
/// 只列已发布的模板是有意的：新刷怪点的包体是从同图同模板的已发布点**拷来的**
/// （44 字节之后的外观数据没有逆向），所以没有已发布样本的模板根本造不出点。
/// </remarks>
internal sealed record MonsterTemplateChoice(
    string TemplateKey,
    string DisplayName,
    int SpawnCount,
    uint SampleObjectId,
    float SampleX,
    float SampleZ)
{
    public string DisplayText =>
        $"{DisplayName}（{TemplateKey}）· 该图已有点 {SpawnCount} 个";

    public override string ToString() => DisplayText;
}

/// <summary>工具写进 <c>gm_monster_spawns</c> 的一个点，读回来的样子。</summary>
internal sealed record GmMonsterSpawnRow(
    long Id,
    string Name,
    short MapId,
    string TemplateKey,
    string DisplayName,
    uint ObjectId,
    float X,
    float Z,
    bool Enabled,
    string Note,
    DateTime UpdatedAt)
{
    public string PositionText => $"{X:F1}, {Z:F1}";
}

/// <summary>一次「生成刷怪点」要写的东西。</summary>
internal sealed record SpawnBatchRequest(
    short MapId,
    long? WaypointId,
    string NamePrefix,
    string TemplateKey,
    string DisplayName,
    int Count,
    float X,
    float Z,
    float Spread,
    float Facing,
    int? Level,
    int? CurrentHealth,
    int? MaximumHealth,
    MonsterAttributeValues? Attributes,
    string UpdatedBy);

/// <summary>可选覆盖的怪物属性；<c>null</c> = 不覆盖（**不是 0**）。</summary>
internal sealed record MonsterAttributeValues(
    int? Level = null,
    int? CurrentHealth = null,
    int? MaximumHealth = null,
    int? PhysicalAttack = null,
    int? MagicAttack = null,
    int? PhysicalDefense = null,
    int? MagicDefense = null,
    int? Hit = null,
    int? Dodge = null,
    int? Critical = null,
    int? CriticalResistance = null)
{
    public bool IsEmpty =>
        Level is null && CurrentHealth is null && MaximumHealth is null &&
        PhysicalAttack is null && MagicAttack is null &&
        PhysicalDefense is null && MagicDefense is null &&
        Hit is null && Dodge is null && Critical is null &&
        CriticalResistance is null;
}

/// <summary>按 map+模板的属性覆盖，读回来的样子。</summary>
internal sealed record MonsterAttributeRow(
    short MapId,
    string TemplateKey,
    string DisplayName,
    MonsterAttributeValues Values,
    DateTime UpdatedAt,
    string UpdatedBy)
{
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            void Add(string label, int? value)
            {
                if (value is { } configured)
                {
                    parts.Add($"{label}={configured}");
                }
            }

            Add("等级", Values.Level);
            Add("血", Values.MaximumHealth);
            Add("物攻", Values.PhysicalAttack);
            Add("魔攻", Values.MagicAttack);
            Add("物防", Values.PhysicalDefense);
            Add("魔防", Values.MagicDefense);
            Add("命中", Values.Hit);
            Add("闪避", Values.Dodge);
            Add("暴击", Values.Critical);
            Add("暴抗", Values.CriticalResistance);
            return parts.Count == 0 ? "（空覆盖）" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 直连 PostgreSQL 读写服务端迁移 <c>20261004_228_gm_monster_overrides</c> 建的三张
/// GM 刷怪表，并读取它们的参照物（已发布的刷怪点 / NPC 占用的对象 ID）。
/// </summary>
/// <remarks>
/// <para>
/// 服务端**只在启动时**读这三张表（和掉落表同一个约定），所以保存之后必须重启
/// <c>godswar-server</c> 才会生效，界面里会一直提示这一点。
/// </para>
/// <para>
/// 对象 ID：客户端只认 8000..49999 这一段，而 40000/42000/43000/44000 已经被
/// 美杜莎/亚特兰蒂斯/温泉关/斯巴达新手这几批自制怪占用，NPC 也大量落在 32000 以上，
/// 所以工具固定从 <b>46000..49999</b> 里按图分配空闲 ID（避开该图已发布的怪、该图的
/// NPC、以及本工具已经写过的点）。
/// </para>
/// </remarks>
internal sealed class MonsterStore : IAsyncDisposable
{
    /// <summary>PostgreSQL <c>undefined_table</c>。</summary>
    private const string UndefinedTableSqlState = "42P01";

    /// <summary>GM 刷怪点的对象 ID 起点（客户端怪物注册表上限 49999）。</summary>
    public const uint FirstGmObjectId = 46_000;

    /// <inheritdoc cref="FirstGmObjectId"/>
    public const uint LastGmObjectId = 49_999;

    private const int CurrentHealthOffset = 20;
    private const int MaximumHealthOffset = 24;
    private const int TierOffset = 12;
    private const int ObjectIdOffset = 8;
    private const int XOffset = 28;
    private const int ZOffset = 36;
    private const int FacingOffset = 40;
    private const int MinimumPacketLength = 108;
    private const int MaximumPacketLength = 1200;

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
        $"数据库 {databaseName} 缺少 GM 刷怪表（gm_monster_spawns / gm_monster_spawn_edits / " +
        "gm_monster_attributes），请先启动一次游戏服务端让它跑数据库迁移，再重试。";

    public async Task<bool> HasOverrideSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT to_regclass('public.gm_monster_spawns') IS NOT NULL
               AND to_regclass('public.gm_monster_spawn_edits') IS NOT NULL
               AND to_regclass('public.gm_monster_attributes') IS NOT NULL;
            """);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
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
    /// 该图上可以拿来当底稿的怪物模板：已发布的刷怪点里出现过的组合。
    /// </summary>
    public async Task<List<MonsterTemplateChoice>> LoadTemplatesAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            WITH published AS (
                SELECT d.template_key,
                       d.display_name,
                       d.object_id,
                       d.pos_x,
                       d.pos_z,
                       count(*) OVER (PARTITION BY d.template_key) AS spawn_count,
                       row_number() OVER (
                           PARTITION BY d.template_key
                           ORDER BY d.object_id) AS ordinal
                FROM monster_spawn_definitions d
                JOIN monster_content_publication p
                  ON p.family = 'monsters' AND p.revision = d.revision
                WHERE d.map_id = @map_id
            )
            SELECT template_key, display_name, spawn_count, object_id, pos_x, pos_z
            FROM published
            WHERE ordinal = 1
            ORDER BY template_key;
            """);
        command.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Smallint)
        {
            Value = mapId
        });

        var rows = new List<MonsterTemplateChoice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MonsterTemplateChoice(
                reader.GetString(0),
                reader.GetString(1),
                checked((int)reader.GetInt64(2)),
                checked((uint)reader.GetInt64(3)),
                reader.GetFloat(4),
                reader.GetFloat(5)));
        }

        return rows;
    }

    /// <summary>该图上工具已经写过的刷怪点。</summary>
    public async Task<List<GmMonsterSpawnRow>> LoadSpawnRowsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT id, name, map_id, template_key, display_name, object_id,
                   pos_x, pos_z, enabled, note, updated_at
            FROM gm_monster_spawns
            WHERE map_id = @map_id
            ORDER BY object_id;
            """);
        command.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Smallint)
        {
            Value = mapId
        });

        var rows = new List<GmMonsterSpawnRow>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new GmMonsterSpawnRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    checked((uint)reader.GetInt64(5)),
                    reader.GetFloat(6),
                    reader.GetFloat(7),
                    reader.GetBoolean(8),
                    reader.GetString(9),
                    reader.GetDateTime(10)));
            }
        }
        catch (PostgresException error) when (error.SqlState == UndefinedTableSqlState)
        {
            throw new InvalidOperationException(MissingSchemaMessage(DatabaseName), error);
        }

        return rows;
    }

    /// <summary>该图的按模板属性覆盖。</summary>
    public async Task<List<MonsterAttributeRow>> LoadTemplateAttributesAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT map_id, template_key, display_name,
                   level, current_health, maximum_health,
                   physical_attack, magic_attack, physical_defense, magic_defense,
                   hit, dodge, critical, critical_resistance,
                   updated_at, updated_by
            FROM gm_monster_attributes
            WHERE map_id = @map_id
            ORDER BY template_key;
            """);
        command.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Smallint)
        {
            Value = mapId
        });

        var rows = new List<MonsterAttributeRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MonsterAttributeRow(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                new MonsterAttributeValues(
                    Level: OptionalInt(reader, 3),
                    CurrentHealth: OptionalInt(reader, 4),
                    MaximumHealth: OptionalInt(reader, 5),
                    PhysicalAttack: OptionalInt(reader, 6),
                    MagicAttack: OptionalInt(reader, 7),
                    PhysicalDefense: OptionalInt(reader, 8),
                    MagicDefense: OptionalInt(reader, 9),
                    Hit: OptionalInt(reader, 10),
                    Dodge: OptionalInt(reader, 11),
                    Critical: OptionalInt(reader, 12),
                    CriticalResistance: OptionalInt(reader, 13)),
                reader.GetDateTime(14),
                reader.GetString(15)));
        }

        return rows;
    }

    /// <summary>
    /// 写一批新刷怪点：包体从该图同模板的已发布点拷来，改写等级/血量/坐标/朝向/对象 ID。
    /// </summary>
    /// <remarks>
    /// 同时给每个新点在 <c>gm_monster_spawn_edits</c> 里写一行属性覆盖（除了等级/血量
    /// 之外的部分，例如攻防命闪暴）——因为攻击这些是**代码公式**，包体里没有它们，
    /// 只能靠覆盖表让服务端在结算时替换。
    /// </remarks>
    public async Task<List<GmMonsterSpawnRow>> CreateSpawnsAsync(
        SpawnBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Count is < 1 or > 200)
        {
            throw new InvalidOperationException("一次生成 1~200 个刷怪点。");
        }

        var basePacket = await ReadBasePacketAsync(
            request.MapId,
            request.TemplateKey,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"该图（map{request.MapId}）上没有模板 {request.TemplateKey} 的已发布刷怪点，" +
                "没有底稿就造不出点。先在游戏里让这种怪出现过，或换一个模板。");

        var freeIds = await AllocateObjectIdsAsync(
            request.MapId,
            request.Count,
            cancellationToken);
        if (freeIds.Count < request.Count)
        {
            throw new InvalidOperationException(
                $"GM 对象 ID 段（{FirstGmObjectId}..{LastGmObjectId}）在该图上只剩 " +
                $"{freeIds.Count} 个空闲，本次要 {request.Count} 个。");
        }

        var created = new List<GmMonsterSpawnRow>(request.Count);
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var sceneKey = await ReadSceneKeyAsync(
            connection,
            transaction,
            request.MapId,
            request.TemplateKey,
            cancellationToken);
        var note = $"GM 生成（底稿模板 {request.TemplateKey}）";
        for (var index = 0; index < request.Count; index++)
        {
            var objectId = freeIds[index];
            var (x, z) = Spread(request, index);
            var packet = BuildPacket(basePacket, objectId, x, z, request);
            var name = $"{request.NamePrefix}-{index + 1:00}";
            await using (var command = new NpgsqlCommand(
                             """
                             INSERT INTO gm_monster_spawns (
                                 name, waypoint_id, map_id, scene_key, template_key,
                                 display_name, object_id, pos_x, pos_z, clear_bytes,
                                 enabled, note, updated_by)
                             VALUES (
                                 @name, @waypoint_id, @map_id, @scene_key, @template_key,
                                 @display_name, @object_id, @pos_x, @pos_z, @clear_bytes,
                                 true, @note, @updated_by)
                             RETURNING id, updated_at;
                             """,
                             connection,
                             transaction))
            {
                command.Parameters.AddWithValue("name", name);
                command.Parameters.Add(new NpgsqlParameter("waypoint_id", NpgsqlDbType.Bigint)
                {
                    Value = request.WaypointId.HasValue ? request.WaypointId.Value : DBNull.Value
                });
                command.Parameters.AddWithValue("map_id", request.MapId);
                command.Parameters.AddWithValue("scene_key", sceneKey);
                command.Parameters.AddWithValue("template_key", request.TemplateKey);
                command.Parameters.AddWithValue("display_name", request.DisplayName);
                command.Parameters.AddWithValue("object_id", (long)objectId);
                command.Parameters.AddWithValue("pos_x", x);
                command.Parameters.AddWithValue("pos_z", z);
                command.Parameters.Add(new NpgsqlParameter("clear_bytes", NpgsqlDbType.Bytea)
                {
                    Value = packet
                });
                command.Parameters.AddWithValue("note", note);
                command.Parameters.AddWithValue("updated_by", request.UpdatedBy ?? string.Empty);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                var id = reader.GetInt64(0);
                var updatedAt = reader.GetDateTime(1);
                await reader.CloseAsync();
                created.Add(new GmMonsterSpawnRow(
                    id,
                    name,
                    request.MapId,
                    request.TemplateKey,
                    request.DisplayName,
                    objectId,
                    x,
                    z,
                    true,
                    note,
                    updatedAt));
            }

            await WriteSpawnAttributesAsync(
                connection,
                transaction,
                request,
                objectId,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return created;
    }

    public async Task SetSpawnEnabledAsync(
        long id,
        bool enabled,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            UPDATE gm_monster_spawns
            SET enabled = @enabled, updated_at = now(), updated_by = @updated_by
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException($"刷怪点不存在：id={id}");
        }
    }

    /// <summary>
    /// 删掉一个刷怪点，连同它自己的逐点属性覆盖。
    /// </summary>
    /// <remarks>
    /// 两件事必须在同一个事务里：属性覆盖是按 (map_id, object_id) 定位的，只删刷怪点会
    /// 留下一条孤儿覆盖，将来任何撞上这个对象 ID 的怪都会莫名继承它的攻防。
    /// </remarks>
    public async Task DeleteSpawnAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        short mapId;
        uint objectId;
        await using (var locate = new NpgsqlCommand(
                         "SELECT map_id, object_id FROM gm_monster_spawns WHERE id = @id;",
                         connection,
                         transaction))
        {
            locate.Parameters.AddWithValue("id", id);
            await using var reader = await locate.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"刷怪点不存在：id={id}");
            }

            mapId = reader.GetInt16(0);
            objectId = checked((uint)reader.GetInt64(1));
        }

        await using (var removeAttributes = new NpgsqlCommand(
                         """
                         DELETE FROM gm_monster_spawn_edits
                         WHERE map_id = @map_id AND object_id = @object_id;
                         """,
                         connection,
                         transaction))
        {
            removeAttributes.Parameters.AddWithValue("map_id", mapId);
            removeAttributes.Parameters.AddWithValue("object_id", (long)objectId);
            await removeAttributes.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var removeSpawn = new NpgsqlCommand(
                         "DELETE FROM gm_monster_spawns WHERE id = @id;",
                         connection,
                         transaction))
        {
            removeSpawn.Parameters.AddWithValue("id", id);
            await removeSpawn.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>按 map+模板保存属性覆盖（全空则删掉那一行）。</summary>
    public async Task SaveTemplateAttributesAsync(
        short mapId,
        string templateKey,
        string displayName,
        MonsterAttributeValues values,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.IsEmpty)
        {
            await using var delete = Source.CreateCommand(
                """
                DELETE FROM gm_monster_attributes
                WHERE map_id = @map_id AND template_key = @template_key;
                """);
            delete.Parameters.AddWithValue("map_id", mapId);
            delete.Parameters.AddWithValue("template_key", templateKey);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var command = Source.CreateCommand(
            """
            INSERT INTO gm_monster_attributes (
                map_id, template_key, display_name,
                level, current_health, maximum_health,
                physical_attack, magic_attack, physical_defense, magic_defense,
                hit, dodge, critical, critical_resistance, updated_by)
            VALUES (
                @map_id, @template_key, @display_name,
                @level, @current_health, @maximum_health,
                @physical_attack, @magic_attack, @physical_defense, @magic_defense,
                @hit, @dodge, @critical, @critical_resistance, @updated_by)
            ON CONFLICT (map_id, template_key) DO UPDATE SET
                display_name = EXCLUDED.display_name,
                level = EXCLUDED.level,
                current_health = EXCLUDED.current_health,
                maximum_health = EXCLUDED.maximum_health,
                physical_attack = EXCLUDED.physical_attack,
                magic_attack = EXCLUDED.magic_attack,
                physical_defense = EXCLUDED.physical_defense,
                magic_defense = EXCLUDED.magic_defense,
                hit = EXCLUDED.hit,
                dodge = EXCLUDED.dodge,
                critical = EXCLUDED.critical,
                critical_resistance = EXCLUDED.critical_resistance,
                updated_at = now(),
                updated_by = EXCLUDED.updated_by;
            """);
        command.Parameters.AddWithValue("map_id", mapId);
        command.Parameters.AddWithValue("template_key", templateKey);
        command.Parameters.AddWithValue("display_name", displayName ?? string.Empty);
        AddNullable(command, "level", values.Level);
        AddNullable(command, "current_health", values.CurrentHealth);
        AddNullable(command, "maximum_health", values.MaximumHealth);
        AddNullable(command, "physical_attack", values.PhysicalAttack);
        AddNullable(command, "magic_attack", values.MagicAttack);
        AddNullable(command, "physical_defense", values.PhysicalDefense);
        AddNullable(command, "magic_defense", values.MagicDefense);
        AddNullable(command, "hit", values.Hit);
        AddNullable(command, "dodge", values.Dodge);
        AddNullable(command, "critical", values.Critical);
        AddNullable(command, "critical_resistance", values.CriticalResistance);
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /* ------------------------------------------------------------------ 内部 */

    private static void AddNullable(NpgsqlCommand command, string name, int? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static int? OptionalInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private async Task<byte[]?> ReadBasePacketAsync(
        short mapId,
        string templateKey,
        CancellationToken cancellationToken)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT d.clear_bytes
            FROM monster_spawn_definitions d
            JOIN monster_content_publication p
              ON p.family = 'monsters' AND p.revision = d.revision
            WHERE d.map_id = @map_id AND d.template_key = @template_key
            ORDER BY d.object_id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("map_id", mapId);
        command.Parameters.AddWithValue("template_key", templateKey);
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    private async Task<string> ReadSceneKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        short mapId,
        string templateKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT d.scene_key
            FROM monster_spawn_definitions d
            JOIN monster_content_publication p
              ON p.family = 'monsters' AND p.revision = d.revision
            WHERE d.map_id = @map_id AND d.template_key = @template_key
            ORDER BY d.object_id
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("map_id", mapId);
        command.Parameters.AddWithValue("template_key", templateKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string ?? "unknown";
    }

    /// <summary>该图上空闲的 GM 对象 ID（避开已发布怪、该图 NPC、已写过的 GM 点）。</summary>
    private async Task<List<uint>> AllocateObjectIdsAsync(
        short mapId,
        int count,
        CancellationToken cancellationToken)
    {
        await using var command = Source.CreateCommand(
            """
            WITH used AS (
                SELECT d.object_id
                FROM monster_spawn_definitions d
                JOIN monster_content_publication p
                  ON p.family = 'monsters' AND p.revision = d.revision
                WHERE d.map_id = @map_id
                UNION
                SELECT n.object_id
                FROM npc_spawn_definitions n
                JOIN npc_content_publication q
                  ON q.family = 'npcs' AND q.revision = n.revision
                WHERE n.map_id = @map_id
                UNION
                SELECT g.object_id FROM gm_monster_spawns g WHERE g.map_id = @map_id
            )
            SELECT candidate
            FROM generate_series(@first::bigint, @last::bigint) AS candidate
            WHERE candidate NOT IN (SELECT object_id FROM used)
            ORDER BY candidate
            LIMIT @count;
            """);
        command.Parameters.AddWithValue("map_id", mapId);
        command.Parameters.AddWithValue("first", (long)FirstGmObjectId);
        command.Parameters.AddWithValue("last", (long)LastGmObjectId);
        command.Parameters.AddWithValue("count", count);

        var ids = new List<uint>(count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(checked((uint)reader.GetInt64(0)));
        }

        return ids;
    }

    private static (float X, float Z) Spread(SpawnBatchRequest request, int index)
    {
        if (request.Spread <= 0f || request.Count <= 1)
        {
            return (request.X, request.Z);
        }

        // 一圈等分撒开，半径就是操作者填的散布半径。
        var angle = (MathF.PI * 2f * index) / request.Count;
        return (
            request.X + (MathF.Cos(angle) * request.Spread),
            request.Z + (MathF.Sin(angle) * request.Spread));
    }

    /// <summary>
    /// 拷一份底稿包体并改写：对象 ID(8)、等级(12)、当前/最大血量(20/24)、坐标(28/36)、朝向(40)。
    /// 44 字节之后的外观数据原样保留 —— 那是没逆向的部分。
    /// </summary>
    private static byte[] BuildPacket(
        byte[] basePacket,
        uint objectId,
        float x,
        float z,
        SpawnBatchRequest request)
    {
        if (basePacket.Length is < MinimumPacketLength or > MaximumPacketLength)
        {
            throw new InvalidOperationException(
                $"底稿包体长度 {basePacket.Length} 不在 {MinimumPacketLength}..{MaximumPacketLength} 之间。");
        }

        var packet = (byte[])basePacket.Clone();
        var maximumHealth = request.MaximumHealth is { } configuredMaximum
            ? checked((uint)configuredMaximum)
            : BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(MaximumHealthOffset, 4));
        var currentHealth = request.CurrentHealth is { } configuredCurrent
            ? Math.Min(checked((uint)configuredCurrent), maximumHealth)
            : maximumHealth;
        var level = request.Level is { } configuredLevel
            ? checked((uint)configuredLevel)
            : BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(TierOffset, 4));
        if (level == 0)
        {
            throw new InvalidOperationException("等级不能是 0（服务端会拒收）。");
        }

        if (maximumHealth == 0)
        {
            throw new InvalidOperationException("最大血量不能是 0（服务端会拒收）。");
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(ObjectIdOffset, 4), objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(TierOffset, 4), level);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(MaximumHealthOffset, 4),
            maximumHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(CurrentHealthOffset, 4),
            currentHealth);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(XOffset, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(ZOffset, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(FacingOffset, 4),
            float.IsFinite(request.Facing) ? request.Facing : 0f);
        return packet;
    }

    /// <summary>
    /// 攻击/防御/命中/闪避/暴击这些是服务端的**代码公式**，包体里没有，
    /// 所以逐点写进 <c>gm_monster_spawn_edits</c>，服务端在结算时用覆盖值替换。
    /// </summary>
    private static async Task WriteSpawnAttributesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        SpawnBatchRequest request,
        uint objectId,
        CancellationToken cancellationToken)
    {
        var values = request.Attributes ?? new MonsterAttributeValues();
        if (values.IsEmpty)
        {
            return;
        }

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gm_monster_spawn_edits (
                map_id, object_id, enabled,
                level, current_health, maximum_health,
                physical_attack, magic_attack, physical_defense, magic_defense,
                hit, dodge, critical, critical_resistance, note, updated_by)
            VALUES (
                @map_id, @object_id, true,
                @level, @current_health, @maximum_health,
                @physical_attack, @magic_attack, @physical_defense, @magic_defense,
                @hit, @dodge, @critical, @critical_resistance, @note, @updated_by)
            ON CONFLICT (map_id, object_id) DO UPDATE SET
                physical_attack = EXCLUDED.physical_attack,
                magic_attack = EXCLUDED.magic_attack,
                physical_defense = EXCLUDED.physical_defense,
                magic_defense = EXCLUDED.magic_defense,
                hit = EXCLUDED.hit,
                dodge = EXCLUDED.dodge,
                critical = EXCLUDED.critical,
                critical_resistance = EXCLUDED.critical_resistance,
                updated_at = now(),
                updated_by = EXCLUDED.updated_by;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("map_id", request.MapId);
        command.Parameters.AddWithValue("object_id", (long)objectId);
        // 等级与血量已经在包体里，这里只写公式类属性，避免两处打架。
        AddNullable(command, "level", null);
        AddNullable(command, "current_health", null);
        AddNullable(command, "maximum_health", null);
        AddNullable(command, "physical_attack", values.PhysicalAttack);
        AddNullable(command, "magic_attack", values.MagicAttack);
        AddNullable(command, "physical_defense", values.PhysicalDefense);
        AddNullable(command, "magic_defense", values.MagicDefense);
        AddNullable(command, "hit", values.Hit);
        AddNullable(command, "dodge", values.Dodge);
        AddNullable(command, "critical", values.Critical);
        AddNullable(command, "critical_resistance", values.CriticalResistance);
        command.Parameters.AddWithValue("note", "GM 生成的逐点属性覆盖");
        command.Parameters.AddWithValue("updated_by", request.UpdatedBy ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>给界面用的一句话摘要（避免界面自己拼 SQL 说明）。</summary>
    public static string DescribeBatch(IReadOnlyList<GmMonsterSpawnRow> rows) =>
        rows.Count == 0
            ? "没有写入任何点。"
            : $"已写入 {rows.Count} 个点：" +
              string.Join(
                  "、",
                  rows.Take(4).Select(static row =>
                      $"{row.Name}(id={row.ObjectId} {row.PositionText})")) +
              (rows.Count > 4 ? " …" : string.Empty) +
              $"（对象 ID {FirstGmObjectId}..{LastGmObjectId} 段）";
}
