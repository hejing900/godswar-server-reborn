using Godswar.Server.Application.Progression;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Application.Characters;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE character_base
            SET "Map" = @currentMap,
                "Pos_X" = @positionX,
                "Pos_Z" = @positionZ
            WHERE id = @characterId
              AND account_id = @accountId;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("currentMap", (short)currentMap);
        command.Parameters.AddWithValue("positionX", positionX);
        command.Parameters.AddWithValue("positionZ", positionZ);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE character_base
            SET "curHP" = GREATEST(0, @currentHp),
                "curMP" = GREATEST(0, @currentMp),
                vitals_revision = @vitalsRevision
            WHERE id = @characterId
              AND account_id = @accountId
              AND vitals_revision < @vitalsRevision;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("currentHp", currentHp);
        command.Parameters.AddWithValue("currentMp", currentMp);
        command.Parameters.AddWithValue("vitalsRevision", vitalsRevision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
        int accountId,
        int characterId,
        int experience,
        int talentExperience,
        CancellationToken cancellationToken = default)
    {
        experience = Math.Max(0, experience);
        talentExperience = Math.Max(0, talentExperience);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int previousLevel;
        long previousExperience;
        bool fighterLevelSealed;
        int previousTalentExperience;
        int previousTalentPoints;
        await using (var command = new NpgsqlCommand("""
            SELECT fighter_job_lv, fighter_job_exp, fighter_level_sealed,
                   "SkillExp", "SkillPoint"
            FROM character_base
            WHERE id = @characterId
              AND account_id = @accountId
            FOR UPDATE;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            previousLevel = reader.GetInt32(0);
            previousExperience = reader.GetInt64(1);
            fighterLevelSealed = reader.GetBoolean(2);
            previousTalentExperience = reader.GetInt32(3);
            previousTalentPoints = reader.GetInt32(4);
        }

        var fighterProgression = PlayerExperienceCatalog.Apply(
            previousLevel,
            previousExperience,
            experience,
            fighterLevelSealed);
        var talentProgression = TalentExperienceCatalog.Apply(
            previousTalentExperience, previousTalentPoints, talentExperience);

        await using (var command = new NpgsqlCommand("""
            UPDATE character_base
            SET fighter_job_lv = @level,
                fighter_job_exp = @experience,
                "SkillPoint" = @talentPoints,
                "SkillExp" = @talentExperience
            WHERE id = @characterId
              AND account_id = @accountId;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("level", fighterProgression.Level);
            command.Parameters.AddWithValue("experience", fighterProgression.Experience);
            command.Parameters.AddWithValue("talentPoints", talentProgression.Points);
            command.Parameters.AddWithValue("talentExperience", talentProgression.Experience);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new CharacterProgressionResult(
            fighterProgression.ExperienceGained,
            previousLevel,
            fighterProgression.Level,
            fighterProgression.Experience,
            PlayerExperienceCatalog.GetNextLevelExperience(fighterProgression.Level),
            fighterProgression.LevelUps,
            talentProgression.ExperienceGained,
            talentProgression.Experience,
            talentProgression.PointsGained,
            talentProgression.Points);
    }

    public async Task<ZodiacLevelUpgradeResult?> UpgradeZodiacLevelAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        var result = await _zodiacLevelStore.UpgradeAsync(
            accountId,
            characterId,
            ownership,
            cancellationToken);
        return result is null
            ? null
            : FocusedGameplayProjectionCompatibility.ToLegacy(result);
    }


    public Task<CapitalShopPurchaseResult> PurchaseCapitalShopItemAsync(
        int accountId, int characterId, Guid purchaseId, CapitalShopOffer offer,
        int quantity, CancellationToken cancellationToken = default) =>
        _capitalShopPurchases.PurchaseCapitalShopItemAsync(accountId, characterId,
            purchaseId, offer, quantity, cancellationToken);

    // Without this forward the interface default silently answers
    // UnsupportedItem, so every shop sale looks unimplemented.
    public Task<CapitalShopSaleResult> SellCapitalShopItemAsync(
        int accountId, int characterId, Guid saleId, int sourceSlot,
        CancellationToken cancellationToken = default) =>
        _capitalShopPurchases.SellCapitalShopItemAsync(accountId, characterId,
            saleId, sourceSlot, cancellationToken);

    public Task<MonsterLootPickupResult> PickupMonsterLootAsync(
        int accountId, int characterId, Guid deathEventId, int lootIndex,
        uint itemId, int quantity, bool? boundOnPickup = null,
        ItemGrantAttributes? attributes = null,
        CancellationToken cancellationToken = default) =>
        _monsterRewardExtras.PickupMonsterLootAsync(accountId, characterId,
            deathEventId, lootIndex, itemId, quantity, boundOnPickup,
            attributes, cancellationToken);

    /// <summary>
    /// Writes a quest reward item into the character's kit bag.
    /// </summary>
    /// <remarks>
    /// This store implements <see cref="IMonsterRewardExtrasStore"/> by forwarding
    /// to the dedicated reward store, and a member it forgets to forward silently
    /// falls back to the interface's default implementation - which refuses the
    /// work. A quest reward was answered with <c>Unsupported</c> and never written
    /// for exactly that reason, so every member of the interface is forwarded here.
    /// </remarks>
    public Task<QuestRewardItemGrantResult> GrantQuestRewardItemAsync(
        int accountId, int characterId, uint questId, int slotIndex,
        uint itemId, int quantity, ItemGrantAttributes attributes,
        CancellationToken cancellationToken = default,
        Guid rewardClaimId = default) =>
        _monsterRewardExtras.GrantQuestRewardItemAsync(accountId, characterId,
            questId, slotIndex, itemId, quantity, attributes,
            cancellationToken, rewardClaimId);

    public Task<PetMonsterExperienceResult> ApplyPetMonsterKillExperienceAsync(
        int accountId, int characterId, Guid deathEventId, int experience,
        CancellationToken cancellationToken = default) =>
        _monsterRewardExtras.ApplyPetMonsterKillExperienceAsync(accountId, characterId,
            deathEventId, experience, cancellationToken);

    public Task<FighterLevelSealChangeResult> ChangeFighterLevelSealAsync(
        int accountId, int characterId, RealmId realmId, PlayerOwnershipFence ownership,
        Guid operationId, bool desiredSealed, CancellationToken cancellationToken = default) =>
        _fighterLevelSeals.ChangeFighterLevelSealAsync(accountId, characterId, realmId,
            ownership, operationId, desiredSealed, cancellationToken);

    public Task<WeekendExperienceClaimStatus> ClaimWeekendExperienceAsync(
        int accountId, int characterId, RealmId realmId, DateOnly claimDay,
        DateTimeOffset claimedAtUtc, CancellationToken cancellationToken = default) =>
        _weekendExperienceClaims.ClaimWeekendExperienceAsync(accountId, characterId,
            realmId, claimDay, claimedAtUtc, cancellationToken);
}
