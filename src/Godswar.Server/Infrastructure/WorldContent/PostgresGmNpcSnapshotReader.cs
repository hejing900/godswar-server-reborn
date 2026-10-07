using System.Data;
using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Loads the four GM NPC tables into a validated
/// <see cref="GmNpcOverrideSnapshot"/>.
/// </summary>
/// <remarks>
/// Same shape as the GM monster reader: one repeatable-read transaction, explicit
/// row bounds, and a graceful empty result when the migration has not run, so an
/// older database still boots.
/// </remarks>
internal static class PostgresGmNpcSnapshotReader
{
    /// <summary>PostgreSQL <c>undefined_table</c>.</summary>
    private const string UndefinedTableSqlState = "42P01";

    private const int MaximumSpawnRows = 2_000;
    private const int MaximumDialogueRows = 5_000;

    public static async Task<GmNpcOverrideSnapshot> LoadAsync(
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
            var dialogues = await ReadDialoguesAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return GmNpcOverrideSnapshot.Create(spawns, dialogues);
        }
        catch (PostgresException error) when (error.SqlState == UndefinedTableSqlState)
        {
            await transaction.RollbackAsync(cancellationToken);
            Console.WriteLine(
                "[gm-npc] GM NPC tables are absent; no operator NPCs are loaded. " +
                "Start the server once more after the migration, or ignore this " +
                "on an older database.");
            return GmNpcOverrideSnapshot.Empty;
        }
    }

    private static async Task<List<GmNpcSpawnOverride>> ReadSpawnsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, map_id, npc_key, template_key, dialogue_key,
                   object_id, appearance_type, pos_x, pos_z, facing, enabled
            FROM gm_npc_spawns
            ORDER BY map_id, object_id
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumSpawnRows);
        var rows = new List<GmNpcSpawnOverride>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GmNpcSpawnOverride(
                Id: reader.GetInt64(0),
                Name: reader.GetString(1),
                MapId: reader.GetInt16(2),
                NpcKey: reader.GetString(3),
                TemplateKey: reader.GetString(4),
                DialogueKey: reader.IsDBNull(5) ? null : reader.GetString(5),
                ObjectId: checked((uint)reader.GetInt64(6)),
                AppearanceType: checked((uint)reader.GetInt32(7)),
                X: reader.GetFloat(8),
                Z: reader.GetFloat(9),
                Facing: reader.GetFloat(10),
                Enabled: reader.GetBoolean(11)));
        }

        return rows;
    }

    private static async Task<List<GmNpcDialogue>> ReadDialoguesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var headers = new List<(string Key, string Name, int Flag, int Entry, bool Enabled)>();
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT dialogue_key, display_name, function_flag,
                                entry_page, enabled
                         FROM gm_npc_dialogues
                         ORDER BY dialogue_key
                         LIMIT @limit;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("limit", MaximumDialogueRows);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                headers.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetBoolean(4)));
            }
        }

        var pages = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT dialogue_key, page_index, body_text
                         FROM gm_npc_dialogue_pages
                         ORDER BY dialogue_key, page_index
                         LIMIT @limit;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("limit", MaximumDialogueRows);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);
                if (!pages.TryGetValue(key, out var map))
                {
                    map = [];
                    pages[key] = map;
                }

                map[reader.GetInt32(1)] = reader.GetString(2);
            }
        }

        var buttons = new Dictionary<string, List<GmNpcDialogueButton>>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT dialogue_key, page_index, slot, label,
                                result_number, next_page_index
                         FROM gm_npc_dialogue_buttons
                         ORDER BY dialogue_key, page_index, slot
                         LIMIT @limit;
                         """,
                         connection,
                         transaction))
        {
            command.Parameters.AddWithValue("limit", MaximumDialogueRows);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);
                if (!buttons.TryGetValue(key, out var list))
                {
                    list = [];
                    buttons[key] = list;
                }

                list.Add(new GmNpcDialogueButton(
                    PageIndex: reader.GetInt32(1),
                    Slot: reader.GetInt16(2),
                    Label: reader.GetString(3),
                    ResultNumber: reader.GetInt32(4),
                    NextPageIndex: reader.IsDBNull(5) ? null : reader.GetInt32(5)));
            }
        }

        var rows = new List<GmNpcDialogue>(headers.Count);
        foreach (var (key, name, flag, entry, enabled) in headers)
        {
            rows.Add(new GmNpcDialogue(
                key,
                name,
                flag,
                entry,
                enabled,
                pages.TryGetValue(key, out var pageMap)
                    ? pageMap
                    : new Dictionary<int, string>(),
                buttons.TryGetValue(key, out var list)
                    ? list
                    : (IReadOnlyList<GmNpcDialogueButton>)[]));
        }

        return rows;
    }
}
