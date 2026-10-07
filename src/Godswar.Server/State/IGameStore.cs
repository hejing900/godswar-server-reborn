using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.State;

internal sealed record GearEnhancementTransactionResult(
    GearEnhancementResult? Enhancement,
    GameCharacter? Character)
{
    public bool CharacterFound => Character is not null;

    public bool Committed => Enhancement?.Committed == true;
}

internal sealed record GearMentorTransactionResult(
    GearMentorResult? Result,
    GameCharacter? Character)
{
    public bool CharacterFound => Character is not null;

    public bool Committed => Result?.Committed == true;
}

internal interface IGameStore : IAsyncDisposable
{
    Task<bool> ConsumeQuestBagItemAsync(int accountId, int characterId, int slot,
        uint itemId, int expectedStack, IReadOnlyList<CharacterQuest> before,
        IReadOnlyList<CharacterQuest> after, uint newQuestId = 0,
        CancellationToken cancellationToken = default) => Task.FromResult(false);

    Task EnsureSeedDataAsync(CancellationToken cancellationToken = default);

    Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default);

    Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default);

    Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
        int accountId,
        int characterId,
        int experience,
        int talentExperience,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists every quest the character carries, plus the ones it has finished.
    /// </summary>
    Task SaveCharacterQuestStateAsync(
        int accountId,
        int characterId,
        IReadOnlyList<CharacterQuest> quests,
        IReadOnlyList<uint> completedQuestIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the character's per-day quest completion counts.
    /// </summary>
    /// <remarks>
    /// The day stamp comes back with the count so the caller can apply the 12:00
    /// rollover: a row stamped with another day counts as zero.
    /// </remarks>
    Task<IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount>>
        LoadQuestDailyCompletionsAsync(
            int characterId,
            CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the character's per-day quest completion counts.
    /// </summary>
    /// <remarks>
    /// Called from the hand-in path, so the count is durable the moment the quest
    /// is paid out and a restart cannot reset it.
    /// </remarks>
    Task SaveQuestDailyCompletionsAsync(
        int accountId,
        int characterId,
        IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount> counts,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Credits a quest's silver and gold reward and returns the new balances.
    /// </summary>
    Task<CharacterWalletResult?> GrantQuestCurrencyAsync(
        int accountId,
        int characterId,
        int silver,
        int gold,
        CancellationToken cancellationToken = default);

    Task<ZodiacSkillGridActivationResult?> ActivateZodiacSkillGridAsync(
        int accountId,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken = default);

    Task<ZodiacSkillGridUpgradeResult?> UpgradeZodiacSkillGridAsync(
        int accountId,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken = default);

    Task<ZodiacSkillGridSelectionResult?> SelectZodiacSkillGridAsync(
        int accountId,
        int characterId,
        int gridIndex,
        int selectedSkillKind,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(int accountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default) =>
        realmId == RealmId.Tempest
            ? GetCharactersAsync(accountId, cancellationToken)
            : throw new NotSupportedException(
                "This game store is Tempest-only.");

    Task<GameCharacter?> GetFirstCharacterAsync(int accountId, CancellationToken cancellationToken = default);

    Task<GameCharacter?> GetFirstCharacterAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default) =>
        realmId == RealmId.Tempest
            ? GetFirstCharacterAsync(accountId, cancellationToken)
            : throw new NotSupportedException(
                "This game store is Tempest-only.");

    Task<GameCharacter> CreateCharacterAsync(int accountId, GameCharacter character, CancellationToken cancellationToken = default);

    Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        RealmId realmId,
        GameCharacter character,
        CancellationToken cancellationToken = default) =>
        realmId == RealmId.Tempest
            ? CreateCharacterAsync(accountId, character, cancellationToken)
            : throw new NotSupportedException(
                "This game store is Tempest-only.");

    Task<bool> DeleteCharacterAsync(int accountId, string characterName, CancellationToken cancellationToken = default);

    Task<bool> DeleteCharacterAsync(
        int accountId,
        RealmId realmId,
        string characterName,
        CancellationToken cancellationToken = default) =>
        realmId == RealmId.Tempest
            ? DeleteCharacterAsync(
                accountId,
                characterName,
                cancellationToken)
            : throw new NotSupportedException(
                "This game store is Tempest-only.");

    Task<GameCharacter?> MoveEquipmentToKitBagAsync(
        int accountId,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> MoveKitBagToEquipmentAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        int requestedEquipmentSlot,
        CancellationToken cancellationToken = default,
        bool requireEmptyEquipmentSlot = false);

    Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> DeleteKitBagItemAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> ClearKitBagAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<KitBagItemGrantResult> AddForgingMaterialAsync(
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken = default);

    Task<KitBagItemGrantResult> AddDeveloperMountAsync(
        int accountId,
        int characterId,
        uint itemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants one Wishing Pool skill book. Separate from the developer grant so the
    /// wishing pool does not depend on the developer allowlist or channel.
    /// <paramref name="goldCost"/> is debited from the character's own gold in the
    /// same transaction; pass zero for the free wish.
    /// </summary>
    Task<KitBagItemGrantResult> AddWishingPoolSkillBookAsync(
        int accountId,
        int characterId,
        uint itemId,
        int goldCost,
        CancellationToken cancellationToken = default);

    Task<ForgeTransactionResult> ForgeEquipmentAsync(
        int accountId,
        int characterId,
        ForgeTransactionRequest request,
        CancellationToken cancellationToken = default);

    Task<GearEnhancementTransactionResult> EnhanceGearAsync(
        int accountId,
        int characterId,
        GearEnhancementRequest request,
        CancellationToken cancellationToken = default);

    Task<GearMentorTransactionResult> ProcessGearMentorAsync(
        int accountId,
        int characterId,
        GearMentorRequest request,
        CancellationToken cancellationToken = default);

    Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
        int accountId,
        int characterId,
        HolyStoneOperation operation,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TalentState>> GetTalentStatesAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default);

    Task<TalentUpgradeResult?> UpgradeTalentAsync(
        int accountId,
        int characterId,
        int talentId,
        int clientRank,
        int clientTalentPoints,
        CancellationToken cancellationToken = default);

}
