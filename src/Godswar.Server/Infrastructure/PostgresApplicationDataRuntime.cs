using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.Talents;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Application.World;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Infrastructure.Accounts;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.FactionCrier;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.Infrastructure.OnlineAwards;
using Godswar.Server.Infrastructure.Pets;
using Godswar.Server.Infrastructure.Progression;
using Godswar.Server.Infrastructure.Quests;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.Infrastructure.Reconciliation;
using Godswar.Server.Infrastructure.Realms;
using Godswar.Server.Infrastructure.Talents;
using Godswar.Server.Infrastructure.Zodiac;
using Godswar.Server.Infrastructure.World;
using Godswar.Server.Infrastructure.Warehouse;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.Infrastructure.ZeusGift;
using Godswar.Server.Infrastructure.WishingPool;
using Godswar.Server.Infrastructure.LuckyGods;
using Godswar.Server.Infrastructure.Guilds;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure;

/// <summary>
/// Owns one shared PostgreSQL pool for extracted application data paths.
/// The legacy broad store retains its existing pool until later backlog
/// slices migrate its remaining operations.
/// </summary>
internal sealed class PostgresApplicationDataRuntime :
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RealmId _realmId;
    private readonly PostgresOutboxDispatcher _outboxDispatcher;
    private readonly PostgresReconciliationWorker
        _reconciliationWorker;

    public PostgresApplicationDataRuntime(
        string connectionString,
        PostgresOutboxDispatcherOptions outboxOptions,
        ZodiacEnergyPolicy zodiacEnergyPolicy,
        GameplayItemContent itemContent,
        string gameplayContentRevision,
        RealmId realmId,
        RealmCalendar realmCalendar,
        IPetContentCatalog petContent,
        IPetOwnerMergeContentCatalog ownerMergeContent,
        IPetLearnedSkillContentCatalog learnedSkillContent,
        HolySpiritBalanceSnapshot holySpiritBalance,
        WarehouseExpansionPolicySnapshot warehouseExpansionPolicy,
        FactionCrierBalanceSnapshot factionCrierBalance,
        OnlineAwardBalanceSnapshot onlineAwardBalance,
        ReconciliationOptions? reconciliationOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(outboxOptions);
        ArgumentNullException.ThrowIfNull(itemContent);
        ArgumentNullException.ThrowIfNull(realmCalendar);
        if (realmCalendar.RealmId != realmId)
        {
            throw new ArgumentException(
                "The selected calendar must belong to the process realm.",
                nameof(realmCalendar));
        }
        _realmId = realmId;
        ArgumentNullException.ThrowIfNull(petContent);
        ArgumentNullException.ThrowIfNull(ownerMergeContent);
        ArgumentNullException.ThrowIfNull(learnedSkillContent);
        ArgumentNullException.ThrowIfNull(holySpiritBalance);
        holySpiritBalance.Validate();
        ArgumentNullException.ThrowIfNull(warehouseExpansionPolicy);
        warehouseExpansionPolicy.Validate();
        WarehouseExpansionPolicy = warehouseExpansionPolicy;
        ArgumentNullException.ThrowIfNull(factionCrierBalance);
        factionCrierBalance.Validate();
        FactionCrierBalance = factionCrierBalance;
        ArgumentNullException.ThrowIfNull(onlineAwardBalance);
        onlineAwardBalance.Validate();
        OnlineAwardBalance = onlineAwardBalance;
        gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateRequired(
                gameplayContentRevision);
        outboxOptions.Validate();

        _dataSource = NpgsqlDataSource.Create(connectionString);
        Accounts = new PostgresAccountStore(_dataSource);
        WishingPoolUsage = new PostgresWishingPoolUsageStore(_dataSource);
        QuestAppraisal = new PostgresQuestAppraisalStore(_dataSource);
        LuckyGodsWish = new PostgresLuckyGodsWishStore(_dataSource);
        ZeusGift = new PostgresZeusGiftStore(_dataSource);
        Guilds = new PostgresGuildStore(
            _dataSource,
            new PostgresGuildAltarContent(_dataSource));
        RealmCatalog = new PostgresRealmCatalogReader(_dataSource);
        var characterReader =
            new PostgresCharacterSnapshotReader(
                _dataSource,
                itemContent.Templates,
                gameplayContentRevision,
                learnedSkillContent.Revision.Sha256,
                holySpiritBalance);
        CharacterSnapshots = characterReader;
        CharacterRuntimeProjections = characterReader;
        OwnedPetSnapshots = characterReader;
        SealedPetSnapshots = characterReader;
        ExperienceBoosts =
            new PostgresExperienceBoostStateReader(
                _dataSource,
                gameplayContentRevision);
        var worldBossState =
            new PostgresWorldBossAreaControlStore(
                _dataSource,
                realmId,
                gameplayContentRevision);
        WorldBossAreaControl = worldBossState;
        WorldBossRespawns = worldBossState;
        ZodiacLevels = new PostgresZodiacLevelStore(_dataSource);
        CharacterCheckpoints =
            new PostgresCharacterCheckpointStore(_dataSource);
        CharacterLifecycleCommands =
            new PostgresCharacterLifecycleCommandExecutor(
                _dataSource,
                outboxOptions,
                gameplayContentRevision);
        TalentUpgradeCommands =
            new PostgresTalentUpgradeCommandExecutor(
                _dataSource,
                outboxOptions,
                gameplayContentRevision);
        DeveloperItemGrantCommands =
            new PostgresDeveloperItemGrantCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        DeveloperBagClearCommands =
            new PostgresDeveloperBagClearCommandExecutor(
                _dataSource,
                outboxOptions);
        MakeAttributeStoneCommands =
            new PostgresMakeAttributeStoneCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        var materialConversionCommands =
            new PostgresGearMentorMaterialConversionCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        MaterialConversionCommands = materialConversionCommands;
        ClassSuitCommands = materialConversionCommands;
        DecomposeGearCommands =
            new PostgresGearMentorDecomposeCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        GearEnhancementCommands =
            new PostgresGearEnhancementCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        EquipmentForgeCommands =
            new PostgresEquipmentForgeCommandExecutor(
                _dataSource,
                outboxOptions);
        KitBagItemDeleteCommands =
            new PostgresKitBagItemDeleteCommandExecutor(
                _dataSource,
                outboxOptions);
        KitBagItemMoveCommands =
            new PostgresKitBagItemMoveCommandExecutor(
                _dataSource,
                outboxOptions);
        EquipmentBagTransferCommands =
            new PostgresEquipmentBagTransferCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent);
        HolyStoneCommands =
            new PostgresHolyStoneCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent,
                holySpiritBalance: holySpiritBalance);
        HolySuitCommands =
            new PostgresHolySuitCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent,
                realmCalendar);
        ZodiacSkillGridActivationCommands =
            new PostgresZodiacSkillGridActivationCommandExecutor(
                _dataSource,
                outboxOptions);
        ZodiacSkillGridUpgradeCommands =
            new PostgresZodiacSkillGridUpgradeCommandExecutor(
                _dataSource,
                outboxOptions);
        ZodiacSkillGridSelectionCommands =
            new PostgresZodiacSkillGridSelectionCommandExecutor(
                _dataSource,
                outboxOptions,
                gameplayContentRevision);
        MonsterDeathRewardCommands =
            new PostgresMonsterDeathRewardCommandExecutor(
                _dataSource,
                outboxOptions);
        ProgressionIntervalSettlementCommands =
            new PostgresProgressionIntervalSettlementCommandExecutor(
                _dataSource,
                outboxOptions,
                zodiacEnergyPolicy,
                realmCalendar);
        DeveloperProgressionCommands =
            new PostgresDeveloperProgressionCommandExecutor(
                _dataSource);
        PetDurableCommands =
            new PostgresPetDurableCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent,
                petContent,
                ownerMergeContent,
                learnedSkillContent,
                gameplayContentRevision:
                    gameplayContentRevision);
        FactionCrierCommands =
            new PostgresFactionCrierCommandExecutor(
                _dataSource,
                outboxOptions,
                factionCrierBalance,
                realmCalendar,
                itemContent.Templates.Revision.Sha256);
        OnlineAwardCommands =
            new PostgresOnlineAwardCommandExecutor(
                _dataSource,
                outboxOptions,
                onlineAwardBalance,
                realmCalendar,
                itemContent.Templates,
                petContent);
        WarehouseSnapshots =
            new PostgresWarehouseSnapshotReader(_dataSource);
        WarehouseTransferCommands =
            new PostgresWarehouseTransferCommandExecutor(
                _dataSource,
                outboxOptions,
                itemContent.Templates);
        WarehouseExpansionCommands =
            new PostgresWarehouseExpansionCommandExecutor(
                _dataSource,
                outboxOptions,
                warehouseExpansionPolicy);
        MedusaDailyEntries =
            new PostgresMedusaDailyEntryClaimStore(_dataSource);
        LegacyInstanceDailyEntries =
            new PostgresLegacyInstanceDailyEntryClaimStore(_dataSource);
        LegacyInstanceOpalPayments =
            new PostgresLegacyInstanceOpalPaymentStore(
                _dataSource,
                outboxOptions);
        MedusaCompletionRewards =
            new PostgresMedusaCompletionRewardStore(_dataSource);
        AtlantisCompletionRewards = new PostgresAtlantisCompletionRewardStore(_dataSource);
        TitleSelections = new PostgresCharacterTitleSelectionStore(_dataSource);
        WonderlandTitles = new PostgresWonderlandTitleStore(_dataSource);
        WonderlandChests = new PostgresWonderlandChestClaimStore(_dataSource);
        WonderlandBlackmarket = new PostgresWonderlandBlackmarketStore(_dataSource);
        var outboxConsumers =
            PostgresOutboxConsumerCatalog.Create();
        _outboxDispatcher = new PostgresOutboxDispatcher(
            _dataSource,
            outboxConsumers,
            outboxOptions);
        OutboxEnabled = outboxOptions.Enabled;
        var effectiveReconciliationOptions =
            reconciliationOptions ?? new ReconciliationOptions();
        effectiveReconciliationOptions.Validate();
        var reconciliationMetrics = new ReconciliationMetrics();
        _reconciliationWorker = new PostgresReconciliationWorker(
            new ReconciliationRunner(
                new PostgresReconciliationReader(
                    _dataSource,
                    itemContent.Templates.Revision.Sha256,
                    outboxConsumers),
                effectiveReconciliationOptions,
                reconciliationMetrics),
            effectiveReconciliationOptions,
            reconciliationMetrics);
        ReconciliationRepair =
            new PostgresExpiredOutboxLeaseRepairer(
                _outboxDispatcher,
                reconciliationMetrics);
        ReconciliationEnabled =
            effectiveReconciliationOptions.Enabled;
    }

    public PostgresAccountStore Accounts { get; }

    /// <summary>
    /// The Wishing Pool's per-character free-wish counter.
    /// </summary>
    public PostgresWishingPoolUsageStore WishingPoolUsage { get; }

    /// <summary>
    /// The permanent quest experience appraisal behind the quest window's
    /// 经验加成 tab.
    /// </summary>
    public PostgresQuestAppraisalStore QuestAppraisal { get; }

    /// <summary>
    /// The divine wish's per-character streak and unclaimed prize pool.
    /// </summary>
    public PostgresLuckyGodsWishStore LuckyGodsWish { get; }

    /// <summary>
    /// The Zeus gift event's per-character delivery, praying-stone and luck state.
    /// </summary>
    public PostgresZeusGiftStore ZeusGift { get; }

    /// <summary>
    /// The guild tables behind the guild registrar.
    /// </summary>
    public PostgresGuildStore Guilds { get; }

    public IRealmCatalogReader RealmCatalog { get; }

    public ICharacterSnapshotReader CharacterSnapshots { get; }

    public ICharacterRuntimeProjectionReader
        CharacterRuntimeProjections
    { get; }

    public IOwnedPetSnapshotReader OwnedPetSnapshots { get; }

    public ISealedPetSnapshotReader SealedPetSnapshots { get; }

    public IExperienceBoostStateReader ExperienceBoosts { get; }

    public IWorldBossAreaControlStore WorldBossAreaControl { get; }

    public IWorldBossRespawnReader WorldBossRespawns { get; }

    public IZodiacLevelStore ZodiacLevels { get; }

    public ICharacterCheckpointStore CharacterCheckpoints { get; }

    public ICharacterLifecycleCommandExecutor
        CharacterLifecycleCommands
    { get; }

    public ITalentUpgradeCommandExecutor TalentUpgradeCommands { get; }

    public IDeveloperItemGrantCommandExecutor
        DeveloperItemGrantCommands
    { get; }

    public IDeveloperBagClearCommandExecutor
        DeveloperBagClearCommands
    { get; }

    public IMakeAttributeStoneCommandExecutor
        MakeAttributeStoneCommands
    { get; }

    public IGearMentorMaterialConversionCommandExecutor
        MaterialConversionCommands
    { get; }

    public IClassSuitCommandExecutor ClassSuitCommands { get; }

    public IGearMentorDecomposeGearCommandExecutor
        DecomposeGearCommands
    { get; }

    public IGearEnhancementCommandExecutor GearEnhancementCommands
    { get; }

    public IEquipmentForgeCommandExecutor EquipmentForgeCommands
    { get; }

    public IKitBagItemDeleteCommandExecutor KitBagItemDeleteCommands
    { get; }

    public IKitBagItemMoveCommandExecutor KitBagItemMoveCommands
    { get; }

    public IEquipmentBagTransferCommandExecutor
        EquipmentBagTransferCommands
    { get; }

    public IHolyStoneCommandExecutor HolyStoneCommands { get; }

    public IHolySuitCommandExecutor HolySuitCommands { get; }

    public IZodiacSkillGridActivationCommandExecutor
        ZodiacSkillGridActivationCommands
    { get; }

    public IZodiacSkillGridUpgradeCommandExecutor
        ZodiacSkillGridUpgradeCommands
    { get; }

    public IZodiacSkillGridSelectionCommandExecutor
        ZodiacSkillGridSelectionCommands
    { get; }

    public IMonsterDeathRewardCommandExecutor
        MonsterDeathRewardCommands
    { get; }

    public IProgressionIntervalSettlementCommandExecutor
        ProgressionIntervalSettlementCommands
    { get; }

    public IDeveloperProgressionCommandExecutor
        DeveloperProgressionCommands
    { get; }

    public IPetDurableCommandExecutor PetDurableCommands { get; }

    public IFactionCrierCommandExecutor FactionCrierCommands { get; }

    public FactionCrierBalanceSnapshot FactionCrierBalance { get; }

    public IOnlineAwardCommandExecutor OnlineAwardCommands { get; }

    public OnlineAwardBalanceSnapshot OnlineAwardBalance { get; }

    public WarehouseExpansionPolicySnapshot WarehouseExpansionPolicy
    { get; }

    public IWarehouseSnapshotReader WarehouseSnapshots { get; }

    public IWarehouseTransferCommandExecutor WarehouseTransferCommands
    { get; }

    public IWarehouseExpansionCommandExecutor WarehouseExpansionCommands
    { get; }

    public IMedusaDailyEntryClaimStore MedusaDailyEntries { get; }

    public ILegacyInstanceDailyEntryClaimStore LegacyInstanceDailyEntries
    { get; }

    public ILegacyInstanceOpalPaymentStore LegacyInstanceOpalPayments
    { get; }

    public IMedusaCompletionRewardStore MedusaCompletionRewards { get; }

    public IAtlantisCompletionRewardStore AtlantisCompletionRewards { get; }

    public ICharacterTitleSelectionStore TitleSelections { get; }

    /// <summary>
    /// Per-island Wonderland clear evidence. The run's teleporters, treasure
    /// chests and final exit all read the settlement this store owns, so an
    /// unconfigured store silently disables every Wonderland NPC service.
    /// </summary>
    public IWonderlandTitleStore WonderlandTitles { get; }

    public IWonderlandChestClaimStore WonderlandChests { get; }

    public IWonderlandBlackmarketStore WonderlandBlackmarket { get; }

    public bool OutboxEnabled { get; }

    public bool ReconciliationEnabled { get; }

    public IReconciliationRepairer ReconciliationRepair { get; }

    public Task RunOutboxAsync(
        CancellationToken cancellationToken = default) =>
        _outboxDispatcher.RunAsync(cancellationToken);

    public async Task RunLegacyInstanceOpalRecoveryAsync(
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var staleBefore = DateTimeOffset.UtcNow.Subtract(
                TimeSpan.FromMinutes(10));
            var recoveredPayments = await LegacyInstanceOpalPayments
                .RecoverPendingAsync(
                    _realmId,
                    staleBefore,
                    cancellationToken);
            var recoveredClaims = await LegacyInstanceDailyEntries
                .RecoverPendingAsync(
                    _realmId,
                    staleBefore,
                    cancellationToken);
            if (recoveredPayments != 0 || recoveredClaims != 0)
            {
                Console.WriteLine(
                    "[instance-caller] recovered stale instance state " +
                    $"realm={_realmId.Value} " +
                    $"payments={recoveredPayments} " +
                    $"claims={recoveredClaims}");
            }
            await Task.Delay(
                TimeSpan.FromMinutes(1),
                cancellationToken);
        }
    }

    public Task RunReconciliationAsync(
        CancellationToken cancellationToken = default) =>
        _reconciliationWorker.RunAsync(cancellationToken);

    public ReconciliationWorkerSnapshot
        GetReconciliationSnapshot() =>
        _reconciliationWorker.GetSnapshot();

    public async Task<bool> CheckHealthAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command =
            new NpgsqlCommand("SELECT 1;", connection)
            {
                CommandTimeout = 1
            };
        return await command.ExecuteScalarAsync(cancellationToken)
            is int and 1;
    }

    public ValueTask DisposeAsync() =>
        _dataSource.DisposeAsync();
}
