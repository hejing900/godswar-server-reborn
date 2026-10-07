using Npgsql;
using NpgsqlTypes;

namespace Godswar.LootTool;

/// <summary>角色列表的一行：名字、阵营、等级，以及**当前地图与坐标**。</summary>
/// <remarks>
/// 位置取的是服务端最近一次落库的值。服务端最多每 2 秒写一次
/// （<c>GameClientHandler.PositionPersistInterval</c>），所以跑动中的角色会有
/// 一个身位的偏差；站定时就是准的。
/// </remarks>
internal sealed record CharacterRow(
    int Id,
    string Name,
    string Account,
    short Profession,
    short Camp,
    int Level,
    int MapId,
    float X,
    float Z,
    int LoginStatus,
    DateTime LastLogin)
{
    /// <summary>职业名取自服务端自己的班级种子（0 warrior / 1 champion / 2 priest / 3 mage）。</summary>
    public string ProfessionName => Profession switch
    {
        0 => "0 战士",
        1 => "1 斗士",
        2 => "2 祭司",
        3 => "3 法师",
        _ => Profession.ToString()
    };

    /// <summary>阵营：0 斯巴达 / 1 雅典（与服务端 <c>GameDefaults</c> 一致）。</summary>
    public string CampName => Camp switch
    {
        0 => "斯巴达",
        1 => "雅典",
        _ => Camp.ToString()
    };

    public string PositionText => $"{X:F1}, {Z:F1}";
}

/// <summary><c>gm_waypoints</c> 的一行：一个存下来的地图 + 坐标。</summary>
internal sealed record WaypointRow(
    long Id,
    string Name,
    string Note,
    string CharacterName,
    short MapId,
    string SceneKey,
    string MapDisplayName,
    float X,
    float Z,
    float Facing,
    string Source,
    DateTime UpdatedAt,
    string UpdatedBy)
{
    public string SourceName => Source == "manual" ? "手动" : "角色";

    public string PositionText => $"{X:F1}, {Z:F1}";

    /// <summary>下拉框里要能一眼看出是哪个点。</summary>
    public override string ToString() => $"{Name} · map{MapId} · {PositionText}";
}

/// <summary>要写进 <c>gm_waypoints</c> 的内容（名字是天然主键，重名即覆盖）。</summary>
internal sealed record WaypointInput(
    string Name,
    string? Note,
    string? CharacterName,
    short MapId,
    float X,
    float Z,
    float Facing,
    string Source,
    string UpdatedBy);

/// <summary>地图选择项，来自 <c>map_templates</c>。</summary>
internal sealed record MapChoice(short MapId, string SceneKey, string DisplayName)
{
    public override string ToString() => $"map{MapId} · {DisplayName}";
}

/// <summary>
/// 直连 PostgreSQL：按名字查角色（含地图/坐标），以及读写 GM 标记点表
/// <c>gm_waypoints</c>（服务端迁移 <c>20261004_227_gm_waypoints</c> 建的）。
/// </summary>
/// <remarks>
/// 与掉落/任务奖励页同一个约定：工具直连库，<b>不通知正在跑的服务端</b>。
/// 标记点目前只是"存档"，服务端还没有读它 —— 后续的刷怪 / 加 NPC 才会以它为挂载处。
/// 表可能不存在（旧库还没跑迁移），所以读取路径先问一次
/// <see cref="HasWaypointSchemaAsync"/>，并在缺失时给出可操作的提示。
/// </remarks>
internal sealed class CharacterStore : IAsyncDisposable
{
    /// <summary>PostgreSQL <c>undefined_table</c>。</summary>
    private const string UndefinedTableSqlState = "42P01";

    private NpgsqlDataSource? _dataSource;

    private NpgsqlDataSource Source =>
        _dataSource ?? throw new InvalidOperationException("尚未连接数据库。");

    public bool IsConnected => _dataSource is not null;

    public string DatabaseName { get; private set; } = string.Empty;

    public void Connect(string connectionString)
    {
        Disconnect();
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _dataSource = builder.Build();
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

    /// <summary>缺少表时给出的统一提示，界面和异常都用它。</summary>
    public static string MissingSchemaMessage(string databaseName) =>
        $"数据库 {databaseName} 缺少标记点表（gm_waypoints），" +
        "请先启动一次游戏服务端让它跑数据库迁移，再重试。";

    public async Task<bool> HasWaypointSchemaAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            "SELECT to_regclass('public.gm_waypoints') IS NOT NULL;");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>按名字模糊查角色（留空列出全部），带上地图与坐标。</summary>
    public async Task<List<CharacterRow>> SearchCharactersAsync(
        string? name,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT cb.id,
                   cb.name,
                   a.username,
                   cb.profession,
                   cb.camp,
                   cb.fighter_job_lv,
                   cb."Map"::integer,
                   cb."Pos_X",
                   cb."Pos_Z",
                   a.login_status::integer,
                   cb."LastLogin_time"
            FROM character_base cb
            JOIN accounts a ON a.id = cb.account_id
            WHERE (@like IS NULL OR cb.name ILIKE @like)
            ORDER BY cb.name
            LIMIT @limit;
            """);
        // 参数类型必须显式给：`@like IS NULL` 这种写法让 PostgreSQL 推断不出类型，
        // 会以 "could not determine data type of parameter" 直接报错。
        command.Parameters.Add(new NpgsqlParameter("like", NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(name) ? DBNull.Value : $"%{name.Trim()}%"
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer)
        {
            Value = Math.Clamp(limit, 1, 500)
        });

        var rows = new List<CharacterRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CharacterRow(
                Id: reader.GetInt32(0),
                Name: reader.GetString(1),
                Account: reader.GetString(2),
                Profession: reader.GetInt16(3),
                Camp: reader.GetInt16(4),
                Level: reader.GetInt32(5),
                MapId: reader.GetInt32(6),
                X: reader.GetFloat(7),
                Z: reader.GetFloat(8),
                LoginStatus: reader.GetInt32(9),
                LastLogin: reader.GetDateTime(10)));
        }

        return rows;
    }

    /// <summary>全部地图（客户端认得的那些），给标记点编辑器当选择器。</summary>
    public async Task<List<MapChoice>> LoadMapChoicesAsync(
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

    /// <summary>列出标记点；<paramref name="mapId"/> 为 null 时列全部。</summary>
    public async Task<List<WaypointRow>> LoadWaypointsAsync(
        long? mapId = null,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT w.id, w.name, w.note, w.character_name, w.map_id,
                   COALESCE(m.scene_key, ''), COALESCE(m.display_name, ''),
                   w.pos_x, w.pos_z, w.facing, w.source, w.updated_at, w.updated_by
            FROM gm_waypoints w
            LEFT JOIN map_templates m ON m.map_id = w.map_id
            WHERE (@map_id IS NULL OR w.map_id = @map_id)
            ORDER BY w.map_id, w.name;
            """);
        command.Parameters.Add(new NpgsqlParameter("map_id", NpgsqlDbType.Smallint)
        {
            Value = mapId.HasValue ? (short)mapId.Value : DBNull.Value
        });

        var rows = new List<WaypointRow>();
        try
        {
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
        }
        catch (PostgresException error) when (error.SqlState == UndefinedTableSqlState)
        {
            throw new InvalidOperationException(MissingSchemaMessage(DatabaseName), error);
        }

        return rows;
    }

    /// <summary>新增或覆盖同名标记点。</summary>
    public async Task<WaypointRow> SaveWaypointAsync(
        WaypointInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = RequireName(input.Name);
        await using var command = Source.CreateCommand(
            """
            INSERT INTO gm_waypoints (
                name, note, character_name, map_id,
                pos_x, pos_z, facing, source, updated_by)
            VALUES (
                @name, @note, @character_name, @map_id,
                @pos_x, @pos_z, @facing, @source, @updated_by)
            ON CONFLICT (name) DO UPDATE SET
                note = EXCLUDED.note,
                character_name = EXCLUDED.character_name,
                map_id = EXCLUDED.map_id,
                pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                facing = EXCLUDED.facing,
                source = EXCLUDED.source,
                updated_at = now(),
                updated_by = EXCLUDED.updated_by
            RETURNING id;
            """);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("note", input.Note ?? string.Empty);
        command.Parameters.AddWithValue(
            "character_name",
            input.CharacterName ?? string.Empty);
        command.Parameters.AddWithValue("map_id", input.MapId);
        command.Parameters.AddWithValue("pos_x", input.X);
        command.Parameters.AddWithValue("pos_z", input.Z);
        command.Parameters.AddWithValue("facing", input.Facing);
        command.Parameters.AddWithValue(
            "source",
            input.Source == "manual" ? "manual" : "character");
        command.Parameters.AddWithValue("updated_by", input.UpdatedBy ?? string.Empty);
        var id = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
        return await ReadWaypointAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"标记点已写入但读不回来：id={id}");
    }

    /// <summary>只挪位置，不动名字/备注。</summary>
    public async Task<WaypointRow> MoveWaypointAsync(
        long id,
        short mapId,
        float x,
        float z,
        float facing,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        await using (var command = Source.CreateCommand(
                         """
                         UPDATE gm_waypoints
                         SET map_id = @map_id,
                             pos_x = @pos_x,
                             pos_z = @pos_z,
                             facing = @facing,
                             updated_at = now(),
                             updated_by = @updated_by
                         WHERE id = @id;
                         """))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("map_id", mapId);
            command.Parameters.AddWithValue("pos_x", x);
            command.Parameters.AddWithValue("pos_z", z);
            command.Parameters.AddWithValue("facing", facing);
            command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException($"标记点不存在：id={id}");
            }
        }

        return await ReadWaypointAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"标记点更新后读不回来：id={id}");
    }

    public async Task<bool> DeleteWaypointAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            "DELETE FROM gm_waypoints WHERE id = @id;");
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>重命名（名字是唯一键，所以要挡重名）。</summary>
    public async Task RenameWaypointAsync(
        long id,
        string name,
        string? note,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        await using var command = Source.CreateCommand(
            """
            UPDATE gm_waypoints
            SET name = @name, note = @note, updated_at = now(), updated_by = @updated_by
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", RequireName(name));
        command.Parameters.AddWithValue("note", note ?? string.Empty);
        command.Parameters.AddWithValue("updated_by", updatedBy ?? string.Empty);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException($"标记点不存在：id={id}");
            }
        }
        catch (PostgresException error) when (error.SqlState == "23505")
        {
            throw new InvalidOperationException(
                $"已经有叫「{name}」的标记点了，换个名字。", error);
        }
    }

    private async Task<WaypointRow?> ReadWaypointAsync(
        long id,
        CancellationToken cancellationToken)
    {
        await using var command = Source.CreateCommand(
            """
            SELECT w.id, w.name, w.note, w.character_name, w.map_id,
                   COALESCE(m.scene_key, ''), COALESCE(m.display_name, ''),
                   w.pos_x, w.pos_z, w.facing, w.source, w.updated_at, w.updated_by
            FROM gm_waypoints w
            LEFT JOIN map_templates m ON m.map_id = w.map_id
            WHERE w.id = @id;
            """);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new WaypointRow(
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
            UpdatedBy: reader.GetString(12));
    }

    private static string RequireName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("标记点必须有一个名字。");
        }

        if (trimmed.Length > 120)
        {
            throw new InvalidOperationException("标记点名字最长 120 个字符。");
        }

        return trimmed;
    }
}
