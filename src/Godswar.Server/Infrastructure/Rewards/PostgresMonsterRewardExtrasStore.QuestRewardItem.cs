using static Godswar.Server.Infrastructure.Inventory.PostgresItemAcquisitionPolicy;
using Godswar.Server.State;
using System.Buffers.Binary;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Rewards;

/// <summary>
/// The quest half of the reward channel: the write that turns the item the
/// client announced on opcode 10056 into a row the character keeps.
/// </summary>
/// <remarks>
/// The client pays a quest reward into its own bag and then announces it, so
/// before this path existed the item lived only in the client and the next relog
/// overwrote it with the server's bag. The bag mutation itself is the same one
/// ground loot uses - the pinned stack cap and bind flag of the item, the kit-bag
/// planner, the inventory revision and the ledger - so a quest reward lands
/// exactly like a picked-up drop, and the claim row makes each reward slot
/// payable once.
/// </remarks>
internal sealed partial class PostgresMonsterRewardExtrasStore
{
    /// <summary>The command family of a quest reward item grant.</summary>
    private const string QuestRewardCommandFamily = "quest_reward_item";

    public async Task<QuestRewardItemGrantResult> GrantQuestRewardItemAsync(
        int accountId,
        int characterId,
        uint questId,
        int slotIndex,
        uint itemId,
        int quantity,
        ItemGrantAttributes attributes,
        CancellationToken cancellationToken = default,
        Guid rewardClaimId = default)
    {
        if (accountId <= 0 || characterId <= 0 || questId == 0 ||
            slotIndex < 0 ||
            itemId == 0 ||
            quantity is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var itemPolicy = await ReadLootItemPolicyAsync(
            connection,
            transaction,
            itemId,
            cancellationToken);
        if (!itemPolicy.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(QuestRewardItemGrantStatus.Unsupported, null);
        }

        var (stackCap, bound) = itemPolicy.Value;
        await LockDeathIdentityAsync(
            connection,
            transaction,
            $"quest-reward:{characterId}:{questId}:{slotIndex}:{rewardClaimId}",
            cancellationToken);

        if (await ReadQuestRewardClaimAsync(
                connection,
                transaction,
                characterId,
                questId,
                slotIndex,
                rewardClaimId,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                QuestRewardItemGrantStatus.Duplicate,
                await GetCharacterByIdAsync(characterId, cancellationToken));
        }

        var inventoryRevision = await LockCharacterInventoryAsync(
            connection,
            transaction,
            accountId,
            characterId,
            cancellationToken);
        if (!inventoryRevision.HasValue)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(QuestRewardItemGrantStatus.CharacterNotFound, null);
        }

        var bag = await LockLootBagAsync(
            connection,
            transaction,
            characterId,
            checked((int)itemId),
            bound,
            stackCap,
            cancellationToken);
        var plan = PlanLootBagMutation(bag, quantity, stackCap);
        if (plan is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(
                QuestRewardItemGrantStatus.InsufficientCapacity,
                await GetCharacterByIdAsync(characterId, cancellationToken));
        }

        var mutations = await ApplyLootBagMutationAsync(
            connection,
            transaction,
            characterId,
            checked((int)itemId),
            bound,
            plan,
            attributes,
            cancellationToken);
        var nextRevision = checked(inventoryRevision.Value + 1);
        await AdvanceLootInventoryRevisionAsync(
            connection,
            transaction,
            accountId,
            characterId,
            inventoryRevision.Value,
            nextRevision,
            cancellationToken);
        var inboxId = await InsertQuestRewardEvidenceAsync(
            connection,
            transaction,
            accountId,
            characterId,
            questId,
            slotIndex,
            itemId,
            quantity,
            nextRevision,
            rewardClaimId,
            cancellationToken);
        await InsertQuestRewardInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            accountId,
            characterId,
            nextRevision,
            mutations,
            cancellationToken);
        await InsertQuestRewardClaimAsync(
            connection,
            transaction,
            characterId,
            questId,
            slotIndex,
            itemId,
            quantity,
            nextRevision,
            rewardClaimId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(
            QuestRewardItemGrantStatus.Added,
            await GetCharacterByIdAsync(characterId, cancellationToken));
    }

    private static async Task<bool> ReadQuestRewardClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        uint questId,
        int slotIndex,
        Guid rewardClaimId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT 1
            FROM public.quest_reward_item_claims
            WHERE character_id = @characterId
              AND quest_id = @questId
              AND slot_index = @slotIndex
              AND completion_id = @completionId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("questId", checked((int)questId));
        command.Parameters.AddWithValue("slotIndex", checked((short)slotIndex));
        command.Parameters.AddWithValue("completionId", rewardClaimId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task InsertQuestRewardClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        uint questId,
        int slotIndex,
        uint itemId,
        int quantity,
        long inventoryRevision,
        Guid rewardClaimId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.quest_reward_item_claims (
                character_id, quest_id, slot_index, item_id, quantity,
                inventory_revision, completion_id)
            VALUES (@characterId, @questId, @slotIndex, @itemId, @quantity,
                    @inventoryRevision, @completionId);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("questId", checked((int)questId));
        command.Parameters.AddWithValue("slotIndex", checked((short)slotIndex));
        command.Parameters.AddWithValue("itemId", checked((int)itemId));
        command.Parameters.AddWithValue("quantity", checked((short)quantity));
        command.Parameters.AddWithValue("inventoryRevision", inventoryRevision);
        command.Parameters.AddWithValue("completionId", rewardClaimId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "Quest reward claim was not inserted exactly once.");
        }
    }

    private static async Task<long> InsertQuestRewardEvidenceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        uint questId,
        int slotIndex,
        uint itemId,
        int quantity,
        long inventoryRevision,
        Guid rewardClaimId,
        CancellationToken cancellationToken)
    {
        // The operation is the reward slot itself, so a replay of the same
        // announcement carries the same identity instead of inventing a new
        // command for a claim the table would reject anyway.
        var operationKey = $"quest-reward:{characterId}:{questId}:{slotIndex}";
        if (rewardClaimId != Guid.Empty)
        {
            operationKey += $":{rewardClaimId}";
        }

        var operationHash = SHA256.HashData(Encoding.UTF8.GetBytes(operationKey));
        var operationId = operationHash.AsSpan(0, 20).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            questId,
            slotIndex,
            rewardClaimId,
            itemId,
            quantity,
            inventoryRevision
        });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var principalKey = $"account:{accountId}";
        var aggregateKey = $"character:{characterId}";
        long auditId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public.command_audit (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, outcome_code, detail_payload)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @commandFamily, @operationId, @hash,
                    'committed', @payload)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("principalKey", principalKey);
            command.Parameters.AddWithValue("aggregateKey", aggregateKey);
            command.Parameters.AddWithValue("commandFamily", QuestRewardCommandFamily);
            command.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
                operationId;
            command.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = hash;
            command.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value =
                payload;
            auditId = await command.ExecuteScalarAsync(cancellationToken)
                is long value && value > 0
                ? value
                : throw new InvalidDataException(
                    "Quest reward audit returned no identity.");
        }

        await using var inbox = new NpgsqlCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type, principal_key, aggregate_type,
                aggregate_key, command_family, operation_id,
                request_hash, result_contract_version, result_code,
                result_payload, result_hash, audit_id)
            VALUES ('account', @principalKey, 'character', @aggregateKey,
                    @commandFamily, @operationId, @hash,
                    1, 'committed', @payload, @hash, @auditId)
            RETURNING id;
            """,
            connection,
            transaction);
        inbox.Parameters.AddWithValue("principalKey", principalKey);
        inbox.Parameters.AddWithValue("aggregateKey", aggregateKey);
        inbox.Parameters.AddWithValue("commandFamily", QuestRewardCommandFamily);
        inbox.Parameters.Add("operationId", NpgsqlDbType.Bytea).Value =
            operationId;
        inbox.Parameters.Add("hash", NpgsqlDbType.Bytea).Value = hash;
        inbox.Parameters.Add("payload", NpgsqlDbType.Jsonb).Value = payload;
        inbox.Parameters.AddWithValue("auditId", auditId);
        return await inbox.ExecuteScalarAsync(cancellationToken)
            is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "Quest reward inbox returned no identity.");
    }

    private static async Task InsertQuestRewardInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        int accountId,
        int characterId,
        long inventoryRevision,
        IReadOnlyList<LootMutation> mutations,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < mutations.Count; index++)
        {
            var mutation = mutations[index];
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO public.character_inventory_ledger (
                    command_inbox_id, account_id, character_id,
                    inventory_revision, entry_ordinal, item_instance_id,
                    mutation_kind, before_state, after_state, reason_code)
                VALUES (@inboxId, @accountId, @characterId,
                        @revision, @ordinal, @itemId, @kind,
                        @before, @after, @reasonCode);
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("inboxId", inboxId);
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("revision", inventoryRevision);
            command.Parameters.AddWithValue("ordinal", checked((short)index));
            command.Parameters.AddWithValue("itemId", mutation.InstanceId);
            command.Parameters.AddWithValue("kind", mutation.Kind);
            command.Parameters.AddWithValue("reasonCode", QuestRewardCommandFamily);
            command.Parameters.Add("before", NpgsqlDbType.Jsonb).Value =
                mutation.BeforeState is null
                    ? DBNull.Value
                    : mutation.BeforeState;
            command.Parameters.Add("after", NpgsqlDbType.Jsonb).Value =
                mutation.AfterState is null
                    ? DBNull.Value
                    : mutation.AfterState;
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "Quest reward ledger row was not inserted once.");
            }
        }
    }
}
