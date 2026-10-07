using Npgsql;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<bool> ConsumeQuestBagItemAsync(int accountId, int characterId, int slot,
        uint itemId, int expectedStack, IReadOnlyList<CharacterQuest> before,
        IReadOnlyList<CharacterQuest> after, uint newQuestId = 0,
        CancellationToken cancellationToken = default)
    {
        if (slot is < 0 or >= 96 || expectedStack <= 0 || before.Count != after.Count)
            return false;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var owner = new NpgsqlCommand("""
            SELECT id FROM character_base WHERE id=@character AND account_id=@account FOR UPDATE;
            """, connection, transaction))
        {
            owner.Parameters.AddWithValue("character", characterId);
            owner.Parameters.AddWithValue("account", accountId);
            if (await owner.ExecuteScalarAsync(cancellationToken) is null) return false;
        }
        long itemRow;
        await using (var item = new NpgsqlCommand("""
            SELECT id FROM character_items WHERE user_id=@character AND item_location=1
                AND slot_index=@slot AND prop_id=@item AND stack=@stack FOR UPDATE;
            """, connection, transaction))
        {
            item.Parameters.AddWithValue("character", characterId);
            item.Parameters.AddWithValue("slot", (short)slot);
            item.Parameters.AddWithValue("item", checked((int)itemId));
            item.Parameters.AddWithValue("stack", expectedStack);
            if (await item.ExecuteScalarAsync(cancellationToken) is not long row) return false;
            itemRow = row;
        }
        if (newQuestId != 0)
        {
            await using var accept = new NpgsqlCommand("""
                INSERT INTO character_quests(character_id, quest_id, state, progress)
                SELECT @character,@quest,0,0
                WHERE (SELECT count(*) FROM character_quests WHERE character_id=@character AND state=0)<20
                    AND NOT EXISTS(SELECT 1 FROM character_quests
                        WHERE character_id=@character AND quest_id=@quest AND state=0)
                ON CONFLICT(character_id,quest_id) DO UPDATE SET state=0,progress=0
                WHERE character_quests.state=1;
                """, connection, transaction);
            accept.Parameters.AddWithValue("character", characterId);
            accept.Parameters.AddWithValue("quest", checked((int)newQuestId));
            if (await accept.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }
        for (var index = 0; index < before.Count; index++)
        {
            if (before[index].QuestId != after[index].QuestId) return false;
            await using var progress = new NpgsqlCommand("""
                UPDATE character_quests SET progress=@after
                WHERE character_id=@character AND quest_id=@quest AND state=0 AND progress=@before;
                """, connection, transaction);
            progress.Parameters.AddWithValue("character", characterId);
            progress.Parameters.AddWithValue("quest", checked((int)before[index].QuestId));
            progress.Parameters.AddWithValue("before", before[index].Progress);
            progress.Parameters.AddWithValue("after", after[index].Progress);
            if (await progress.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }
        if (expectedStack == 1)
            await DeleteCharacterItemSlotAsync(connection, transaction, characterId,
                ItemLocationKitBag, slot, "quest-item-consumption", cancellationToken);
        else
        {
            await using var consume = new NpgsqlCommand("""
                WITH old AS (SELECT * FROM character_items WHERE id=@id),
                changed AS (UPDATE character_items SET stack=stack-1,updated_at=now() WHERE id=@id RETURNING *)
                INSERT INTO character_item_audit(source,action,user_id,item_location,slot_index,
                    prop_id,item_quality,item_grade,item_exp,old_item)
                SELECT 'quest-item-consumption','update',user_id,item_location,slot_index,
                    prop_id,item_quality,item_grade,item_exp,to_jsonb(old) FROM old;
                """, connection, transaction);
            consume.Parameters.AddWithValue("id", itemRow);
            await consume.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var revision = new NpgsqlCommand("""
            UPDATE character_base SET inventory_revision=inventory_revision+1
            WHERE id=@character AND account_id=@account;
            """, connection, transaction))
        {
            revision.Parameters.AddWithValue("character", characterId);
            revision.Parameters.AddWithValue("account", accountId);
            await revision.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
