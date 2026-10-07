using System.Data;
using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Loads the three GM monster tables into a validated
/// <see cref="MonsterOverrideSnapshot"/>.
/// </summary>
/// <remarks>
/// Same shape as the monster-loot reader: one repeatable-read transaction so the
/// three tables are read from a single point in time, explicit row bounds, and a
/// graceful empty result when the migration has not run yet - an operator's
/// older database must still boot.
/// </remarks>
internal static class PostgresMonsterOverrideSnapshotReader
{
    /// <summary>PostgreSQL <c>undefined_table</c>.</summary>
    private const string UndefinedTableSqlState = "42P01";

    private const int MaximumSpawnRows = 20_000;
    private const int MaximumEditRows = 200_000;
    private const int MaximumAttributeRows = 20_000;

    public static async Task<MonsterOverrideSnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        try
        {
            var spawns = await ReadSpawnsAsync(connection, transaction, cancellationToken);
            var edits = await ReadEditsAsync(connection, transaction, cancellationToken);
            var attributes = await ReadAttributesAsync(
                connection,
                transaction,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MonsterOverrideSnapshot.Create(spawns, edits, attributes);
        }
        catch (PostgresException error) when (error.SqlState == UndefinedTableSqlState)
        {
            await transaction.RollbackAsync(cancellationToken);
            Console.WriteLine(
                "[monster-overrides] GM monster tables are absent; no operator " +
                "overrides are loaded. Start the server once more after the " +
                "migration, or ignore this on an older database.");
            return MonsterOverrideSnapshot.Empty;
        }
    }

    private static async Task<List<MonsterSpawnOverrideRow>> ReadSpawnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, waypoint_id, map_id, scene_key, template_key,
                   display_name, object_id, pos_x, pos_z, clear_bytes, enabled
            FROM gm_monster_spawns
            ORDER BY map_id, object_id
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumSpawnRows);
        var rows = new List<MonsterSpawnOverrideRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new MonsterSpawnOverrideRow(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                WaypointId: reader.IsDBNull(2) ? null : reader.GetInt64(2),
                MapId: reader.GetInt16(3),
                SceneKey: reader.GetString(4),
                TemplateKey: reader.GetString(5),
                DisplayName: reader.GetString(6),
                ObjectId: checked((uint)reader.GetInt64(7)),
                X: reader.GetFloat(8),
                Z: reader.GetFloat(9),
                Packet: (byte[])reader[10],
                Enabled: reader.GetBoolean(11)));
        }

        return rows;
    }

    private static async Task<
        List<((short MapId, uint ObjectId) Key, MonsterSpawnEdit Edit)>>
        ReadEditsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, object_id, enabled, pos_x, pos_z, facing,
                   level, current_health, maximum_health,
                   physical_attack, magic_attack, physical_defense, magic_defense,
                   hit, dodge, critical, critical_resistance
            FROM gm_monster_spawn_edits
            ORDER BY map_id, object_id
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumEditRows);
        var rows = new List<((short, uint), MonsterSpawnEdit)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetInt16(0), checked((uint)reader.GetInt64(1)));
            rows.Add((key, new MonsterSpawnEdit(
                Enabled: reader.GetBoolean(2),
                X: OptionalFloat(reader, 3),
                Z: OptionalFloat(reader, 4),
                Facing: OptionalFloat(reader, 5),
                Attributes: new MonsterAttributeOverride(
                    Level: OptionalInt(reader, 6),
                    CurrentHealth: OptionalInt(reader, 7),
                    MaximumHealth: OptionalInt(reader, 8),
                    PhysicalAttack: OptionalInt(reader, 9),
                    MagicAttack: OptionalInt(reader, 10),
                    PhysicalDefense: OptionalInt(reader, 11),
                    MagicDefense: OptionalInt(reader, 12),
                    Hit: OptionalInt(reader, 13),
                    Dodge: OptionalInt(reader, 14),
                    Critical: OptionalInt(reader, 15),
                    CriticalResistance: OptionalInt(reader, 16)))));
        }

        return rows;
    }

    private static async Task<
        List<((short MapId, string TemplateKey) Key, MonsterAttributeOverride Override)>>
        ReadAttributesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, template_key,
                   level, current_health, maximum_health,
                   physical_attack, magic_attack, physical_defense, magic_defense,
                   hit, dodge, critical, critical_resistance
            FROM gm_monster_attributes
            ORDER BY map_id, template_key
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumAttributeRows);
        var rows = new List<((short, string), MonsterAttributeOverride)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetInt16(0), reader.GetString(1));
            rows.Add((key, new MonsterAttributeOverride(
                Level: OptionalInt(reader, 2),
                CurrentHealth: OptionalInt(reader, 3),
                MaximumHealth: OptionalInt(reader, 4),
                PhysicalAttack: OptionalInt(reader, 5),
                MagicAttack: OptionalInt(reader, 6),
                PhysicalDefense: OptionalInt(reader, 7),
                MagicDefense: OptionalInt(reader, 8),
                Hit: OptionalInt(reader, 9),
                Dodge: OptionalInt(reader, 10),
                Critical: OptionalInt(reader, 11),
                CriticalResistance: OptionalInt(reader, 12))));
        }

        return rows;
    }

    /// <summary>NULL means "not configured" and must stay distinct from zero.</summary>
    private static int? OptionalInt(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static float? OptionalFloat(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFloat(ordinal);
}
