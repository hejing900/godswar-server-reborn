namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    /// <summary>
    /// Persists every quest the character carries, plus the ones it has finished.
    /// </summary>
    /// <remarks>
    /// A character can hold several quests at once, so the whole set is written in
    /// one statement: the rows that are no longer part of it are deleted, and the
    /// rest are upserted with their own state and progress. The character's
    /// current and completed quests are one list here - a finished quest is simply
    /// a row in the completed state, which is what stops it being offered again.
    /// </remarks>
    public async Task SaveCharacterQuestStateAsync(
        int accountId,
        int characterId,
        IReadOnlyList<CharacterQuest> quests,
        IReadOnlyList<uint> completedQuestIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(completedQuestIds);

        // Reaccepted daily quests also have a historical completion record.
        // PostgreSQL cannot upsert the same key twice in one statement; the
        // carried row must take precedence over that history.
        var carriedIds = quests.Select(quest => quest.QuestId).ToHashSet();
        var completedOnly = completedQuestIds.Distinct()
            .Where(questId => !carriedIds.Contains(questId)).ToArray();
        var questIds = new int[quests.Count + completedOnly.Length];
        var states = new short[questIds.Length];
        var progresses = new long[questIds.Length];
        for (var index = 0; index < quests.Count; index++)
        {
            questIds[index] = checked((int)quests[index].QuestId);
            states[index] = CharacterQuestStatus.InProgress;
            progresses[index] = quests[index].Progress;
        }

        for (var index = 0; index < completedOnly.Length; index++)
        {
            var target = quests.Count + index;
            questIds[target] = checked((int)completedOnly[index]);
            states[target] = CharacterQuestStatus.Completed;
            progresses[target] = 0;
        }

        await using var command = _dataSource.CreateCommand("""
            DELETE FROM character_quests quest
            USING character_base character
            WHERE quest.character_id = character.id
              AND character.id = @characterId
              AND character.account_id = @accountId
              AND NOT (quest.quest_id = ANY(@questIds));

            INSERT INTO character_quests
                (character_id, quest_id, state, progress)
            SELECT
                @characterId,
                incoming.quest_id,
                incoming.state,
                incoming.progress
            FROM unnest(@questIds, @states, @progresses)
                AS incoming(quest_id, state, progress)
            WHERE EXISTS (
                SELECT 1
                FROM character_base character
                WHERE character.id = @characterId
                  AND character.account_id = @accountId)
            ON CONFLICT (character_id, quest_id)
            DO UPDATE SET
                state = EXCLUDED.state,
                progress = EXCLUDED.progress;
            """);
        command.Parameters.AddWithValue("questIds", questIds);
        command.Parameters.AddWithValue("states", states);
        command.Parameters.AddWithValue("progresses", progresses);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the character's per-day quest completion counts.
    /// </summary>
    /// <remarks>
    /// The whole table for the character is read, day stamps included, and the
    /// caller compares them against the current quota day. Filtering in SQL would
    /// work too, but keeping the stamp lets <see cref="QuestDailyState"/> own the
    /// comparison, so the "a stale day counts as zero" rule has exactly one
    /// implementation.
    /// </remarks>
    public async Task<IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount>>
        LoadQuestDailyCompletionsAsync(
            int characterId,
            CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT daily.quest_id, daily.reset_day, daily.completions
            FROM character_quest_daily daily
            WHERE daily.character_id = @characterId
            ORDER BY daily.quest_id;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        var counts = new Dictionary<uint, GameCharacter.QuestDailyCount>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[checked((uint)reader.GetInt32(0))] =
                new GameCharacter.QuestDailyCount(
                    reader.GetFieldValue<DateOnly>(1),
                    reader.GetInt32(2));
        }

        return counts;
    }

    /// <summary>
    /// Writes the character's per-day quest completion counts.
    /// </summary>
    /// <remarks>
    /// The upsert is the hand-over's own rule: a row already stamped with today
    /// keeps counting up, and a row from another day restarts at one. A count that
    /// is not part of <paramref name="counts"/> is left alone rather than deleted,
    /// because the day it belongs to may still be the one the server is on for
    /// another character's quota.
    /// </remarks>
    public async Task SaveQuestDailyCompletionsAsync(
        int accountId,
        int characterId,
        IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount> counts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Count == 0)
        {
            return;
        }

        var questIds = new int[counts.Count];
        var days = new DateOnly[counts.Count];
        var completions = new int[counts.Count];
        var index = 0;
        foreach (var (questId, count) in counts)
        {
            questIds[index] = checked((int)questId);
            days[index] = count.Day;
            completions[index] = count.Completions;
            index++;
        }

        await using var command = _dataSource.CreateCommand("""
            INSERT INTO character_quest_daily
                (character_id, quest_id, reset_day, completions)
            SELECT
                @characterId,
                incoming.quest_id,
                incoming.reset_day,
                incoming.completions
            FROM unnest(@questIds, @days, @completions)
                AS incoming(quest_id, reset_day, completions)
            WHERE EXISTS (
                SELECT 1
                FROM character_base character
                WHERE character.id = @characterId
                  AND character.account_id = @accountId)
            ON CONFLICT (character_id, quest_id)
            DO UPDATE SET
                reset_day = EXCLUDED.reset_day,
                completions = EXCLUDED.completions;
            """);
        command.Parameters.AddWithValue("questIds", questIds);
        command.Parameters.AddWithValue("days", days);
        command.Parameters.AddWithValue("completions", completions);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("accountId", accountId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
