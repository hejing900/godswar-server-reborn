using Godswar.Server.Domain.World.Content;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresQuestRewardItemIntegrationChecks
{
    private static async Task CheckQuestItemTransactionsAsync(NpgsqlDataSource dataSource, string connectionString,
        int accountId, int characterId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO character_items(user_id,item_location,slot_index,prop_id,stack)
            VALUES (@character,1,95,3820,2);
            """, connection))
        {
            seed.Parameters.AddWithValue("character", characterId);
            await seed.ExecuteNonQueryAsync();
        }
        await using var questStore = new PostgresGameStore(connectionString);
        Check.True(!await questStore.ConsumeQuestBagItemAsync(accountId,characterId,95,3820,3,[],[],1649),
            "stale stack refuses acceptance without consuming an item");
        Check.True(await questStore.ConsumeQuestBagItemAsync(accountId,characterId,95,3820,2,[],[],1649),
            "acceptance and consumption commit together");
        Check.True(!await questStore.ConsumeQuestBagItemAsync(accountId,characterId,95,3820,1,[],[],1649),
            "duplicate accept cannot consume the remaining item");
        var before = new CharacterQuest { QuestId = 1649 };
        var after = before.Clone();
        for (var slot = 0; slot < 4; slot++)
            after.Progress = StarterQuestObjectives.WithCounter(after.Progress,slot,5000 + slot);
        var stale = before.Clone(); stale.Progress = 1;
        Check.True(!await questStore.ConsumeQuestBagItemAsync(accountId,characterId,95,3820,1,[stale],[after]),
            "stale quest progress rolls back bag consumption");
        Check.True(await questStore.ConsumeQuestBagItemAsync(accountId,characterId,95,3820,1,[before],[after]),
            "capture item and wide quest progress commit together");
        await using (var read = new NpgsqlCommand("""
            SELECT q.progress,(SELECT count(*) FROM character_items WHERE user_id=@character
                AND item_location=1 AND slot_index=95) FROM character_quests q
            WHERE q.character_id=@character AND q.quest_id=1649;
            """, connection))
        {
            read.Parameters.AddWithValue("character",characterId);
            await using var reader = await read.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(),"committed quest survives a new connection");
            Check.Equal(after.Progress,reader.GetInt64(0),"database preserves all four 5000 counters");
            Check.Equal(0L,reader.GetInt64(1),"last item is consumed exactly once");
        }
        await questStore.SaveCharacterQuestStateAsync(accountId,characterId,[],[]);
    }
}
