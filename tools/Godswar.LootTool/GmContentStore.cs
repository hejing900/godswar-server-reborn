using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>
/// 一份可搬走的 GM 配置：标记点 + GM 刷怪点 + 属性覆盖。
/// </summary>
/// <remarks>
/// 包体用 base64 一起带走，所以换库/换机器导入不需要那边先有同样的已发布内容 ——
/// 但**模板仍然必须在该图已发布**，否则服务端会拒绝这个点（这是内容侧的不变量，
/// 不是工具能绕过的）。
/// </remarks>
internal sealed record GmContentBundle(
    int Version,
    DateTime ExportedAt,
    string Database,
    IReadOnlyList<GmWaypointEntry> Waypoints,
    IReadOnlyList<GmSpawnEntry> Spawns,
    IReadOnlyList<GmSpawnAttributeEntry> SpawnAttributes,
    IReadOnlyList<GmTemplateAttributeEntry> TemplateAttributes)
{
    public const int CurrentVersion = 1;

    public string Summary =>
        $"标记点 {Waypoints.Count} 个｜GM 刷怪点 {Spawns.Count} 个｜" +
        $"逐点属性覆盖 {SpawnAttributes.Count} 条｜模板属性覆盖 {TemplateAttributes.Count} 条";
}

internal sealed record GmWaypointEntry(
    string Name,
    string Note,
    string CharacterName,
    short MapId,
    float X,
    float Z,
    float Facing,
    string Source);

internal sealed record GmSpawnEntry(
    string Name,
    string? WaypointName,
    short MapId,
    string SceneKey,
    string TemplateKey,
    string DisplayName,
    uint ObjectId,
    float X,
    float Z,
    string ClearBytesBase64,
    bool Enabled,
    string Note);

internal sealed record GmSpawnAttributeEntry(
    short MapId,
    uint ObjectId,
    int? PhysicalAttack,
    int? MagicAttack,
    int? PhysicalDefense,
    int? MagicDefense,
    int? Hit,
    int? Dodge,
    int? Critical,
    int? CriticalResistance);

internal sealed record GmTemplateAttributeEntry(
    short MapId,
    string TemplateKey,
    string DisplayName,
    int? Level,
    int? CurrentHealth,
    int? MaximumHealth,
    int? PhysicalAttack,
    int? MagicAttack,
    int? PhysicalDefense,
    int? MagicDefense,
    int? Hit,
    int? Dodge,
    int? Critical,
    int? CriticalResistance);

/// <summary>导入结果，给界面念的一行。</summary>
internal sealed record GmImportResult(
    int Waypoints,
    int Spawns,
    int SpawnAttributes,
    int TemplateAttributes)
{
    public string Summary =>
        $"导入完成：标记点 {Waypoints} 个、刷怪点 {Spawns} 个、" +
        $"逐点属性 {SpawnAttributes} 条、模板属性 {TemplateAttributes} 条。";
}

/// <summary>
/// 把三张 GM 表整体导出成一份 JSON，或把一份 JSON 整份写回去（按天然键 upsert）。
/// </summary>
/// <remarks>
/// 用 <b>天然键</b>覆盖而不是按自增 id 写：标记点按 <c>name</c>、刷怪点按 <c>name</c>、
/// 属性按 <c>(map_id, object_id)</c> / <c>(map_id, template_key)</c>。这样同一份文件导入
/// 两次结果一样（幂等），也不会把目标库的自增序列搞乱。
/// </remarks>
internal sealed class GmContentStore : IAsyncDisposable
{
    private const string UndefinedTableSqlState = "42P01";

    /// <summary>导出的 JSON 用缩进 + 中文原样，操作者要能直接看懂/改。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

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

    public async Task<GmContentBundle> ExportAsync(CancellationToken cancellationToken = default)
    {
        var waypoints = new List<GmWaypointEntry>();
        await using (var command = Source.CreateCommand(
                         """
                         SELECT name, note, character_name, map_id, pos_x, pos_z, facing, source
                         FROM gm_waypoints
                         ORDER BY map_id, name;
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                waypoints.Add(new GmWaypointEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt16(3),
                    reader.GetFloat(4),
                    reader.GetFloat(5),
                    reader.GetFloat(6),
                    reader.GetString(7)));
            }
        }

        var spawns = new List<GmSpawnEntry>();
        await using (var command = Source.CreateCommand(
                         """
                         SELECT s.name, w.name, s.map_id, s.scene_key, s.template_key,
                                s.display_name, s.object_id, s.pos_x, s.pos_z,
                                s.clear_bytes, s.enabled, s.note
                         FROM gm_monster_spawns s
                         LEFT JOIN gm_waypoints w ON w.id = s.waypoint_id
                         ORDER BY s.map_id, s.object_id;
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                spawns.Add(new GmSpawnEntry(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt16(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    checked((uint)reader.GetInt64(6)),
                    reader.GetFloat(7),
                    reader.GetFloat(8),
                    Convert.ToBase64String((byte[])reader[9]),
                    reader.GetBoolean(10),
                    reader.GetString(11)));
            }
        }

        var spawnAttributes = new List<GmSpawnAttributeEntry>();
        await using (var command = Source.CreateCommand(
                         """
                         SELECT map_id, object_id, physical_attack, magic_attack,
                                physical_defense, magic_defense, hit, dodge,
                                critical, critical_resistance
                         FROM gm_monster_spawn_edits
                         ORDER BY map_id, object_id;
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                spawnAttributes.Add(new GmSpawnAttributeEntry(
                    reader.GetInt16(0),
                    checked((uint)reader.GetInt64(1)),
                    OptionalInt(reader, 2),
                    OptionalInt(reader, 3),
                    OptionalInt(reader, 4),
                    OptionalInt(reader, 5),
                    OptionalInt(reader, 6),
                    OptionalInt(reader, 7),
                    OptionalInt(reader, 8),
                    OptionalInt(reader, 9)));
            }
        }

        var templateAttributes = new List<GmTemplateAttributeEntry>();
        await using (var command = Source.CreateCommand(
                         """
                         SELECT map_id, template_key, display_name,
                                level, current_health, maximum_health,
                                physical_attack, magic_attack, physical_defense,
                                magic_defense, hit, dodge, critical, critical_resistance
                         FROM gm_monster_attributes
                         ORDER BY map_id, template_key;
                         """))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                templateAttributes.Add(new GmTemplateAttributeEntry(
                    reader.GetInt16(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    OptionalInt(reader, 3),
                    OptionalInt(reader, 4),
                    OptionalInt(reader, 5),
                    OptionalInt(reader, 6),
                    OptionalInt(reader, 7),
                    OptionalInt(reader, 8),
                    OptionalInt(reader, 9),
                    OptionalInt(reader, 10),
                    OptionalInt(reader, 11),
                    OptionalInt(reader, 12),
                    OptionalInt(reader, 13)));
            }
        }

        return new GmContentBundle(
            GmContentBundle.CurrentVersion,
            DateTime.UtcNow,
            DatabaseName,
            waypoints,
            spawns,
            spawnAttributes,
            templateAttributes);
    }

    /// <summary>
    /// 整份写回（按天然键 upsert）。整件事在一个事务里：任何一行失败就全回滚，
    /// 不会留下"导入了一半"的库。
    /// </summary>
    public async Task<GmImportResult> ImportAsync(
        GmContentBundle bundle,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Version != GmContentBundle.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"这份文件是版本 {bundle.Version}，本工具只认版本 " +
                $"{GmContentBundle.CurrentVersion}。");
        }

        await using var connection = await _dataSource!.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var waypoint in bundle.Waypoints)
            {
                await UpsertWaypointAsync(connection, transaction, waypoint, updatedBy, cancellationToken);
            }

            foreach (var spawn in bundle.Spawns)
            {
                await UpsertSpawnAsync(connection, transaction, spawn, updatedBy, cancellationToken);
            }

            foreach (var attributes in bundle.SpawnAttributes)
            {
                await UpsertSpawnAttributesAsync(
                    connection,
                    transaction,
                    attributes,
                    updatedBy,
                    cancellationToken);
            }

            foreach (var attributes in bundle.TemplateAttributes)
            {
                await UpsertTemplateAttributesAsync(
                    connection,
                    transaction,
                    attributes,
                    updatedBy,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new GmImportResult(
                bundle.Waypoints.Count,
                bundle.Spawns.Count,
                bundle.SpawnAttributes.Count,
                bundle.TemplateAttributes.Count);
        }
        catch (PostgresException error) when (error.SqlState == UndefinedTableSqlState)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException(
                $"数据库 {DatabaseName} 缺少 GM 表，请先启动一次游戏服务端跑迁移。",
                error);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task UpsertWaypointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GmWaypointEntry waypoint,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gm_waypoints (
                name, note, character_name, map_id, pos_x, pos_z, facing, source, updated_by)
            VALUES (
                @name, @note, @character_name, @map_id, @pos_x, @pos_z, @facing, @source, @updated_by)
            ON CONFLICT (name) DO UPDATE SET
                note = EXCLUDED.note,
                character_name = EXCLUDED.character_name,
                map_id = EXCLUDED.map_id,
                pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                facing = EXCLUDED.facing,
                source = EXCLUDED.source,
                updated_at = now(),
                updated_by = EXCLUDED.updated_by;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("name", waypoint.Name);
        command.Parameters.AddWithValue("note", waypoint.Note ?? string.Empty);
        command.Parameters.AddWithValue("character_name", waypoint.CharacterName ?? string.Empty);
        command.Parameters.AddWithValue("map_id", waypoint.MapId);
        command.Parameters.AddWithValue("pos_x", waypoint.X);
        command.Parameters.AddWithValue("pos_z", waypoint.Z);
        command.Parameters.AddWithValue("facing", waypoint.Facing);
        command.Parameters.AddWithValue(
            "source",
            waypoint.Source == "manual" ? "manual" : "character");
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSpawnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GmSpawnEntry spawn,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        var packet = Convert.FromBase64String(spawn.ClearBytesBase64);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gm_monster_spawns (
                name, waypoint_id, map_id, scene_key, template_key, display_name,
                object_id, pos_x, pos_z, clear_bytes, enabled, note, updated_by)
            VALUES (
                @name,
                (SELECT id FROM gm_waypoints WHERE name = @waypoint_name),
                @map_id, @scene_key, @template_key, @display_name,
                @object_id, @pos_x, @pos_z, @clear_bytes, @enabled, @note, @updated_by)
            ON CONFLICT (name) DO UPDATE SET
                waypoint_id = EXCLUDED.waypoint_id,
                map_id = EXCLUDED.map_id,
                scene_key = EXCLUDED.scene_key,
                template_key = EXCLUDED.template_key,
                display_name = EXCLUDED.display_name,
                object_id = EXCLUDED.object_id,
                pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                clear_bytes = EXCLUDED.clear_bytes,
                enabled = EXCLUDED.enabled,
                note = EXCLUDED.note,
                updated_at = now(),
                updated_by = EXCLUDED.updated_by;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("name", spawn.Name);
        command.Parameters.Add(new NpgsqlParameter("waypoint_name", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrEmpty(spawn.WaypointName)
                ? DBNull.Value
                : spawn.WaypointName
        });
        command.Parameters.AddWithValue("map_id", spawn.MapId);
        command.Parameters.AddWithValue("scene_key", spawn.SceneKey);
        command.Parameters.AddWithValue("template_key", spawn.TemplateKey);
        command.Parameters.AddWithValue("display_name", spawn.DisplayName ?? string.Empty);
        command.Parameters.AddWithValue("object_id", (long)spawn.ObjectId);
        command.Parameters.AddWithValue("pos_x", spawn.X);
        command.Parameters.AddWithValue("pos_z", spawn.Z);
        command.Parameters.Add(new NpgsqlParameter("clear_bytes", NpgsqlDbType.Bytea)
        {
            Value = packet
        });
        command.Parameters.AddWithValue("enabled", spawn.Enabled);
        command.Parameters.AddWithValue("note", spawn.Note ?? string.Empty);
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertSpawnAttributesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GmSpawnAttributeEntry attributes,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gm_monster_spawn_edits (
                map_id, object_id, enabled,
                physical_attack, magic_attack, physical_defense, magic_defense,
                hit, dodge, critical, critical_resistance, note, updated_by)
            VALUES (
                @map_id, @object_id, true,
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
        command.Parameters.AddWithValue("map_id", attributes.MapId);
        command.Parameters.AddWithValue("object_id", (long)attributes.ObjectId);
        AddNullable(command, "physical_attack", attributes.PhysicalAttack);
        AddNullable(command, "magic_attack", attributes.MagicAttack);
        AddNullable(command, "physical_defense", attributes.PhysicalDefense);
        AddNullable(command, "magic_defense", attributes.MagicDefense);
        AddNullable(command, "hit", attributes.Hit);
        AddNullable(command, "dodge", attributes.Dodge);
        AddNullable(command, "critical", attributes.Critical);
        AddNullable(command, "critical_resistance", attributes.CriticalResistance);
        command.Parameters.AddWithValue("note", "导入");
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTemplateAttributesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        GmTemplateAttributeEntry attributes,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
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
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("map_id", attributes.MapId);
        command.Parameters.AddWithValue("template_key", attributes.TemplateKey);
        command.Parameters.AddWithValue("display_name", attributes.DisplayName ?? string.Empty);
        AddNullable(command, "level", attributes.Level);
        AddNullable(command, "current_health", attributes.CurrentHealth);
        AddNullable(command, "maximum_health", attributes.MaximumHealth);
        AddNullable(command, "physical_attack", attributes.PhysicalAttack);
        AddNullable(command, "magic_attack", attributes.MagicAttack);
        AddNullable(command, "physical_defense", attributes.PhysicalDefense);
        AddNullable(command, "magic_defense", attributes.MagicDefense);
        AddNullable(command, "hit", attributes.Hit);
        AddNullable(command, "dodge", attributes.Dodge);
        AddNullable(command, "critical", attributes.Critical);
        AddNullable(command, "critical_resistance", attributes.CriticalResistance);
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNullable(NpgsqlCommand command, string name, int? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Integer)
        {
            Value = value.HasValue ? value.Value : DBNull.Value
        });

    private static int? OptionalInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    public static string Serialize(GmContentBundle bundle) =>
        JsonSerializer.Serialize(bundle, JsonOptions);

    public static GmContentBundle Deserialize(string json) =>
        JsonSerializer.Deserialize<GmContentBundle>(json, JsonOptions)
        ?? throw new InvalidOperationException("这份 JSON 不是有效的 GM 配置文件。");
}
