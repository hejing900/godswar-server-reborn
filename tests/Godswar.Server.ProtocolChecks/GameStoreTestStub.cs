using System.Collections.Immutable;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Application.World;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal abstract class GameStoreTestStub :
    IGameStore,
    ICharacterRuntimeProjectionReader,
    IOwnedPetSnapshotReader,
    IExperienceBoostStateReader,
    IZodiacLevelStore,
    IWorldBossAreaControlStore,
    IWorldBossRespawnReader,
    IAccountCredentialStore,
    IAccountDirectory,
    IAccountPresenceWriter,
    ILegacyAccountLoginStore
{
    async Task<ExperienceBoostSnapshot>
        IExperienceBoostStateReader.ReadAsync(
            ExperienceBoostReadRequest request,
            CancellationToken cancellationToken)
    {
        ExperienceBoostContract.ValidateRequest(request);
        var state = await GetExperienceBoostStateAsync(
            request.AccountId,
            request.CharacterId,
            request.Camp,
            checked((byte)request.MapId),
            request.ReadAtUtc,
            cancellationToken);
        return FocusedGameplayProjectionCompatibility.ToApplication(
            state,
            request.ReadAtUtc);
    }

    async Task<WorldBossAreaActivationResult>
        IWorldBossAreaControlStore.ActivateAsync(
            WorldBossAreaActivation activation,
            CancellationToken cancellationToken)
    {
        if (!WorldBossPersistenceContract.IsValid(activation))
        {
            return WorldBossAreaActivationResult.Invalid();
        }

        var control = await ActivateWorldBossAreaAsync(
            activation.MapId,
            activation.BossTemplateKey,
            activation.ControllingCamp,
            activation.KilledAtUtc,
            activation.DeathToken,
            cancellationToken);
        return control is null
            ? WorldBossAreaActivationResult.NotConfigured()
            : WorldBossAreaActivationResult.Committed(
                FocusedGameplayProjectionCompatibility.ToApplication(
                    control));
    }

    async Task<WorldBossRespawnSnapshot?>
        IWorldBossRespawnReader.ReadActiveAsync(
            WorldBossRespawnReadRequest request,
            CancellationToken cancellationToken)
    {
        var respawn = await GetActiveWorldBossRespawnAsync(
            request.MapId,
            request.ReadAtUtc,
            cancellationToken);
        return respawn is null
            ? null
            : new WorldBossRespawnSnapshot(
                respawn.MapId,
                respawn.BossTemplateKey,
                respawn.RespawnAt);
    }

    async Task<ZodiacLevelUpgradeStoreResult?>
        IZodiacLevelStore.UpgradeAsync(
            int accountId,
            int characterId,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken)
    {
        var result = await UpgradeZodiacLevelAsync(
            accountId,
            characterId,
            ownership,
            cancellationToken);
        return result is null
            ? null
            : FocusedGameplayProjectionCompatibility.ToApplication(result);
    }

    async Task<CharacterCalculatedStatsSnapshot?>
        ICharacterRuntimeProjectionReader.ReadCalculatedStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken)
    {
        var stats = await GetCharacterStatsAsync(
            accountId,
            characterId,
            cancellationToken);
        return stats is null
            ? null
            : FocusedGameplayProjectionCompatibility.ToApplication(stats);
    }

    async Task<bool> ICharacterRuntimeProjectionReader.IsSkillLearnedAsync(
        int accountId,
        int characterId,
        int skillId,
        CancellationToken cancellationToken) =>
        (await GetSkillStatesAsync(
            accountId,
            characterId,
            cancellationToken)).Any(skill => skill.SkillId == skillId);

    async Task<System.Collections.Immutable.ImmutableArray<
        CharacterPetSnapshot>> IOwnedPetSnapshotReader.ReadOwnedPetsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken) =>
        (await GetOwnedPetsAsync(
            accountId,
            characterId,
            cancellationToken))
        .Select(FocusedGameplayProjectionCompatibility.ToApplication)
        .ToImmutableArray();

    public virtual Task EnsureSeedDataAsync(
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public virtual Task<GameAccount> LoginOrCreateAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<Godswar.Server.State.StoredAccountCredential?>
        FindAccountCredentialAsync(
        string username,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameAccount?> FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameAccount?> FindAccountByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameAccount?> TryCreateAccountWithCredentialAsync(
        string username,
        string versionedVerifier,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<bool> TryReplaceAccountCredentialAsync(
        int accountId,
        string expectedVerifier,
        string versionedVerifier,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task MarkAccountOnlineAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task MarkAccountOfflineAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task MarkAccountPlayerOnlineAsync(
        int accountId,
        Guid presenceToken,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<bool> TryMarkAccountPlayerOfflineAsync(
        int accountId,
        Guid presenceToken,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task SaveCharacterPositionAsync(
        int accountId,
        int characterId,
        byte currentMap,
        float positionX,
        float positionZ,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task SaveCharacterVitalsAsync(
        int accountId,
        int characterId,
        int currentHp,
        int currentMp,
        long vitalsRevision,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<CharacterProgressionResult?>
        ApplyMonsterKillRewardAsync(
            int accountId,
            int characterId,
            int experience,
            int talentExperience,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task SaveCharacterQuestStateAsync(
        int accountId,
        int characterId,
        IReadOnlyList<CharacterQuest> quests,
        IReadOnlyList<uint> completedQuestIds,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount>>
        LoadQuestDailyCompletionsAsync(
            int characterId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task SaveQuestDailyCompletionsAsync(
        int accountId,
        int characterId,
        IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount> counts,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<CharacterWalletResult?> GrantQuestCurrencyAsync(
        int accountId,
        int characterId,
        int silver,
        int gold,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacLevelUpgradeResult?>
        UpgradeZodiacLevelAsync(
            int accountId,
            int characterId,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacSkillGridActivationResult?>
        ActivateZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacSkillGridUpgradeResult?>
        UpgradeZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ExperienceBoostState>
        GetExperienceBoostStateAsync(
            int accountId,
            int characterId,
            byte camp,
            byte mapId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ZodiacSkillGridSelectionResult?>
        SelectZodiacSkillGridAsync(
            int accountId,
            int characterId,
            int gridIndex,
            int selectedSkillKind,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<FactionAreaExperienceControl?>
        ActivateWorldBossAreaAsync(
            short mapId,
            string bossTemplateKey,
            byte controllingCamp,
            DateTimeOffset killedAt,
            string deathToken,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<WorldBossRespawnState?>
        GetActiveWorldBossRespawnAsync(
            short mapId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<GameCharacter>>
        GetCharactersAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> GetFirstCharacterAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<PetBootstrapSnapshot>>
        GetOwnedPetsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PetBootstrapSnapshot>>([]);

    public virtual Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        GameCharacter character,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<bool> DeleteCharacterAsync(
        int accountId,
        string characterName,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> MoveEquipmentToKitBagAsync(
        int accountId,
        int characterId,
        int equipmentSlot,
        int kitBagSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> MoveKitBagToEquipmentAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        int requestedEquipmentSlot,
        CancellationToken cancellationToken = default,
        bool requireEmptyEquipmentSlot = false) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> MoveKitBagItemAsync(
        int accountId,
        int characterId,
        int sourceSlot,
        int destinationSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> DeleteKitBagItemAsync(
        int accountId,
        int characterId,
        int kitBagSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> ClearKitBagAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<KitBagItemGrantResult> AddForgingMaterialAsync(
        int accountId,
        int characterId,
        uint itemId,
        int quantity,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<KitBagItemGrantResult> AddDeveloperMountAsync(
        int accountId,
        int characterId,
        uint itemId,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<KitBagItemGrantResult> AddWishingPoolSkillBookAsync(
        int accountId,
        int characterId,
        uint itemId,
        int goldCost,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<ForgeTransactionResult> ForgeEquipmentAsync(
        int accountId,
        int characterId,
        ForgeTransactionRequest request,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GearEnhancementTransactionResult>
        EnhanceGearAsync(
            int accountId,
            int characterId,
            GearEnhancementRequest request,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GearMentorTransactionResult>
        ProcessGearMentorAsync(
            int accountId,
            int characterId,
            GearMentorRequest request,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
        int accountId,
        int characterId,
        HolyStoneOperation operation,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<TalentState>>
        GetTalentStatesAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<IReadOnlyList<SkillState>>
        GetSkillStatesAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual Task<TalentUpgradeResult?> UpgradeTalentAsync(
        int accountId,
        int characterId,
        int talentId,
        int clientRank,
        int clientTalentPoints,
        CancellationToken cancellationToken = default) =>
        throw Unsupported();

    public virtual ValueTask DisposeAsync() =>
        ValueTask.CompletedTask;

    async Task<AccountIdentity>
        ILegacyAccountLoginStore.LoginOrCreateLegacyAccountAsync(
            string username,
            string password,
            CancellationToken cancellationToken)
    {
        var account = await LoginOrCreateAccountAsync(
            username,
            password,
            cancellationToken);
        return ToIdentity(account);
    }

    async Task<AccountIdentity?> IAccountDirectory.FindAccountByIdAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        var account = await FindAccountByIdAsync(
            accountId,
            cancellationToken);
        return account is null ? null : ToIdentity(account);
    }

    async Task<AccountIdentity?>
        IAccountDirectory.FindAccountByUsernameAsync(
            string username,
            CancellationToken cancellationToken)
    {
        var account = await FindAccountByUsernameAsync(
            username,
            cancellationToken);
        return account is null ? null : ToIdentity(account);
    }

    async Task<Godswar.Server.Application.Accounts.StoredAccountCredential?>
        IAccountCredentialStore.FindAccountCredentialAsync(
            string username,
            CancellationToken cancellationToken)
    {
        var stored = await FindAccountCredentialAsync(
            username,
            cancellationToken);
        return stored is null
            ? null
            : new Godswar.Server.Application.Accounts.StoredAccountCredential(
                ToIdentity(stored.Account),
                stored.Verifier);
    }

    async Task<AccountIdentity?>
        IAccountCredentialStore.TryCreateAccountWithCredentialAsync(
            string username,
            string versionedVerifier,
            CancellationToken cancellationToken)
    {
        var account = await TryCreateAccountWithCredentialAsync(
            username,
            versionedVerifier,
            cancellationToken);
        return account is null ? null : ToIdentity(account);
    }

    private static AccountIdentity ToIdentity(GameAccount account) =>
        new(account.Id, account.Username);

    private static NotSupportedException Unsupported() =>
        new("This test store method is not used.");
}
