using System.Data;
using Godswar.Server.Application.World.Content;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Loads one repeatable-read snapshot of the GM-owned quest reward overrides.
/// Schema creation remains exclusively migration-owned, exactly like the monster
/// loot content reader.
/// </summary>
internal sealed class PostgresQuestRewardContentSnapshotReader
{
    private const int MaximumSlots = 65_536;
    private const int MaximumValues = 65_536;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresQuestRewardContentSnapshotReader(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
    }

    public static async Task<QuestRewardContentSnapshot> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        return await new PostgresQuestRewardContentSnapshotReader(dataSource)
            .ReadAsync(cancellationToken);
    }

    public async Task<QuestRewardContentSnapshot> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);
        var slots = await ReadSlotsAsync(
            connection,
            transaction,
            cancellationToken);
        var values = await ReadValuesAsync(
            connection,
            transaction,
            cancellationToken);
        var snapshot = new QuestRewardContentSnapshot(slots, values);
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    private static async Task<IReadOnlyList<QuestRewardSlotOverride>>
        ReadSlotsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT quest_id, slot_index, item_id,
                   item_quality, item_grade,
                   attribute1, attribute_level1,
                   attribute2, attribute_level2,
                   attribute3, attribute_level3,
                   attribute4, attribute_level4,
                   attribute5, attribute_level5
            FROM public.quest_reward_slots
            ORDER BY quest_id, slot_index
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumSlots);
        var slots = new List<QuestRewardSlotOverride>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            // A row that configures no attributes keeps the plain item every
            // grant produced before the columns existed. An empty slot stays
            // NULL: attribute id 0 is the real AttackA, not "no attribute".
            // Quality and star both fall back to 1 when the operator leaves those
            // boxes alone: quality has a floor of 1 (加固), it is never 0. The GM
            // tool clamps its own writes to 1 as well, so a row written either way
            // means the same thing.
            var attributes = new ItemGrantAttributes(
                reader.IsDBNull(3) || reader.GetInt16(3) < 1 ? (short)1 : reader.GetInt16(3),
                reader.IsDBNull(4) ? (short)1 : reader.GetInt16(4),
                Optional(reader, 5), Optional(reader, 6),
                Optional(reader, 7), Optional(reader, 8),
                Optional(reader, 9), Optional(reader, 10),
                Optional(reader, 11), Optional(reader, 12),
                Optional(reader, 13), Optional(reader, 14));
            slots.Add(new(
                checked((uint)reader.GetInt32(0)),
                reader.GetInt16(1),
                checked((uint)reader.GetInt32(2)),
                attributes));
        }

        return slots;
    }

    /// <summary>An attribute column, where <c>NULL</c> means "not configured".</summary>
    private static short? Optional(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);

    private static async Task<IReadOnlyList<QuestRewardValueOverride>>
        ReadValuesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT quest_id, experience, talent_points, silver, gold
            FROM public.quest_reward_values
            ORDER BY quest_id
            LIMIT @limit;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("limit", MaximumValues);
        var values = new List<QuestRewardValueOverride>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(new(
                checked((uint)reader.GetInt32(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return values;
    }
}
