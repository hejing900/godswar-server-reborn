using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.FactionCrier;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.OnlineAwards;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.Talents;
using Godswar.Server.Application.World;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    public GameClientHandler(
        ClientSession session,
        IGameStore gameStore,
        GameSessionRegistry registry,
        ICharacterSnapshotReader characterSnapshots,
        IWorldContentReader worldContent,
        DeveloperCommandOptions? developerCommands = null,
        SecurePhase4AcceptanceFaults?
            phase4AcceptanceFaults = null,
        TimeSpan? mapTransitionReadyTimeout = null,
        TimeSpan? backhaulSkillCastTime = null,
        LegacyAuthenticationAccess?
            legacyAuthenticationAccess = null,
        ITalentUpgradeCommandExecutor?
            talentUpgradeCommands = null,
        IDeveloperItemGrantCommandExecutor?
            developerItemGrantCommands = null,
        IDeveloperBagClearCommandExecutor?
            developerBagClearCommands = null,
        IDeveloperProgressionCommandExecutor?
            developerProgressionCommands = null,
        IMakeAttributeStoneCommandExecutor?
            makeAttributeStoneCommands = null,
        IGearMentorMaterialConversionCommandExecutor?
            gearMentorMaterialConversionCommands = null,
        IClassSuitCommandExecutor?
            classSuitCommands = null,
        IGearMentorDecomposeGearCommandExecutor?
            gearMentorDecomposeGearCommands = null,
        IGearEnhancementCommandExecutor?
            gearEnhancementCommands = null,
        IEquipmentForgeCommandExecutor?
            equipmentForgeCommands = null,
        IKitBagItemDeleteCommandExecutor?
            kitBagItemDeleteCommands = null,
        IKitBagItemMoveCommandExecutor?
            kitBagItemMoveCommands = null,
        IEquipmentBagTransferCommandExecutor?
            equipmentBagTransferCommands = null,
        IHolyStoneCommandExecutor?
            holyStoneCommands = null,
        IHolySuitCommandExecutor?
            holySuitCommands = null,
        IZodiacSkillGridActivationCommandExecutor?
            zodiacSkillGridActivationCommands = null,
        IZodiacSkillGridUpgradeCommandExecutor?
            zodiacSkillGridUpgradeCommands = null,
        IZodiacSkillGridSelectionCommandExecutor?
            zodiacSkillGridSelectionCommands = null,
        ICharacterCheckpointCoordinator?
            characterCheckpoints = null,
        ICharacterLifecycleCommandExecutor?
            characterLifecycleCommands = null,
        IMonsterDeathRewardCommandExecutor?
            monsterDeathRewardCommands = null,
        IPetDurableCommandExecutor?
            petDurableCommands = null,
        IFactionCrierCommandExecutor?
            factionCrierCommands = null,
        FactionCrierBalanceSnapshot?
            factionCrierBalance = null,
        IOnlineAwardCommandExecutor?
            onlineAwardCommands = null,
        OnlineAwardBalanceSnapshot?
            onlineAwardBalance = null,
        ICharacterRuntimeProjectionReader?
            characterRuntimeProjections = null,
        IOwnedPetSnapshotReader?
            ownedPetSnapshots = null,
        IWorldBossAreaControlStore?
            worldBossAreaControl = null,
        IWorldBossRespawnReader?
            worldBossRespawns = null,
        IPlayerCoordinationLeaseIssuer?
            playerCoordination = null,
        IAccountDirectory? accountDirectory = null,
        IAccountPresenceWriter? accountPresence = null,
        bool requiresDurableMonsterRewardCommands = false,
        bool requiresDurablePlayerCommands = false,
        GameplayRuntimeCatalogs? gameplayCatalogs = null,
        GameplayItemContent? itemContent = null,
        IPetContentCatalog? petContent = null,
        TimeSpan? petOwnerMergeEnergyInterval = null,
        ISealedPetSnapshotReader? sealedPetSnapshots = null,
        TimeSpan? petOwnerMergeRechargeInterval = null,
        IRealmCatalogReader? realmCatalog = null,
        RealmId? processRealmId = null,
        RealmCalendar? realmCalendar = null,
        IWarehouseSnapshotReader? warehouseSnapshots = null,
        IWarehouseTransferCommandExecutor? warehouseTransferCommands = null,
        IWarehouseExpansionCommandExecutor? warehouseExpansionCommands = null,
        WarehouseExpansionPolicySnapshot? warehouseExpansionPolicy = null,
        IMedusaDailyEntryClaimStore? medusaDailyEntries = null,
        ILegacyInstanceDailyEntryClaimStore?
            legacyInstanceDailyEntries = null,
        ILegacyInstanceOpalPaymentStore?
            legacyInstanceOpalPayments = null,
        Godswar.Server.Infrastructure.WishingPool
            .PostgresWishingPoolUsageStore? wishingPoolUsage = null,
        Godswar.Server.Infrastructure.Quests
            .PostgresQuestAppraisalStore? questAppraisal = null,
        Godswar.Server.Application.LuckyGods
            .ILuckyGodsWishStore? luckyGodsWish = null,
        Godswar.Server.Infrastructure.Guilds
            .PostgresGuildStore? guilds = null,
        Godswar.Server.Infrastructure.ZeusGift
            .PostgresZeusGiftStore? zeusGift = null,
        TimeProvider? flameBlastTimeProvider = null)
    {
        if (backhaulSkillCastTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(backhaulSkillCastTime));
        }
        if (petOwnerMergeEnergyInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(petOwnerMergeEnergyInterval));
        }
        if (petOwnerMergeRechargeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(petOwnerMergeRechargeInterval));
        }

        _session = session;
        _flameBlastTimeProvider = flameBlastTimeProvider ?? TimeProvider.System;
        _store = gameStore;
        _capitalShopPurchases = gameStore as ICapitalShopPurchaseStore ??
            UnsupportedGameplayFeatures.Instance;
        _monsterRewardExtras = gameStore as IMonsterRewardExtrasStore ??
            UnsupportedGameplayFeatures.Instance;
        _fighterLevelSeals = gameStore as IFighterLevelSealStore;
        _farmPoints = gameStore as ILelantineFarmPointsStore ??
            UnsupportedGameplayFeatures.Instance;
        _weekendExperienceClaims = gameStore as IWeekendExperienceClaimStore;
        _realmCatalog = realmCatalog;
        _processRealmId = processRealmId ?? RealmId.Tempest;
        if (!_processRealmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(processRealmId));
        }
        if (requiresDurablePlayerCommands && realmCalendar is null)
        {
            throw new InvalidOperationException(
                "Production player composition requires the persisted " +
                "process-realm calendar.");
        }
        _realmCalendar = realmCalendar ??
            RealmCalendar.CreateForTesting(
                _processRealmId,
                "Asia/Manila");
        if (_realmCalendar.RealmId != _processRealmId)
        {
            throw new ArgumentException(
                "The game handler realm calendar must match the process realm.",
                nameof(realmCalendar));
        }
        _accountDirectory = accountDirectory ??
            gameStore as IAccountDirectory ??
            throw new ArgumentException(
                "An account directory is required.",
                nameof(accountDirectory));
        _accountPresence = accountPresence ??
            gameStore as IAccountPresenceWriter ??
            throw new ArgumentException(
                "An account presence writer is required.",
                nameof(accountPresence));
        _registry = registry;
        _characterSnapshots =
            characterSnapshots ?? throw new ArgumentNullException(
                nameof(characterSnapshots));
        _worldContent =
            worldContent ?? throw new ArgumentNullException(
                nameof(worldContent));
        _gameplayCatalogs = gameplayCatalogs ??
            GameplayRuntimeCatalogs.Create(worldContent.Gameplay);
        _itemContent = itemContent;
        _petContent = petContent;
        _talentUpgradeCommands = talentUpgradeCommands;
        _developerItemGrantCommands = developerItemGrantCommands;
        _developerBagClearCommands = developerBagClearCommands;
        _developerProgressionCommands =
            developerProgressionCommands;
        _makeAttributeStoneCommands = makeAttributeStoneCommands;
        _gearMentorMaterialConversionCommands =
            gearMentorMaterialConversionCommands;
        _classSuitCommands = classSuitCommands;
        _gearMentorDecomposeGearCommands =
            gearMentorDecomposeGearCommands;
        _gearEnhancementCommands = gearEnhancementCommands;
        _equipmentForgeCommands = equipmentForgeCommands;
        _kitBagItemDeleteCommands = kitBagItemDeleteCommands;
        _kitBagItemMoveCommands = kitBagItemMoveCommands;
        _equipmentBagTransferCommands = equipmentBagTransferCommands;
        _holyStoneCommands = holyStoneCommands;
        _holySuitCommands = holySuitCommands;
        _zodiacSkillGridActivationCommands =
            zodiacSkillGridActivationCommands;
        _zodiacSkillGridUpgradeCommands =
            zodiacSkillGridUpgradeCommands;
        _zodiacSkillGridSelectionCommands =
            zodiacSkillGridSelectionCommands;
        _characterCheckpoints = characterCheckpoints;
        _characterLifecycleCommands =
            characterLifecycleCommands;
        _monsterDeathRewardCommands =
            monsterDeathRewardCommands;
        _requiresDurableMonsterRewardCommands =
            requiresDurableMonsterRewardCommands;
        _requiresDurablePlayerCommands =
            requiresDurablePlayerCommands;
        _petDurableCommands = petDurableCommands;
        _factionCrierCommands = factionCrierCommands;
        _factionCrierBalance = factionCrierBalance;
        _onlineAwardCommands = onlineAwardCommands;
        _onlineAwardBalance = onlineAwardBalance;
        _warehouseSnapshots = warehouseSnapshots;
        _warehouseTransferCommands = warehouseTransferCommands;
        _warehouseExpansionCommands = warehouseExpansionCommands;
        _warehouseExpansionPolicy = warehouseExpansionPolicy;
        _medusaDailyEntries = medusaDailyEntries;
        _legacyInstanceDailyEntries = legacyInstanceDailyEntries;
        _legacyInstanceOpalPayments = legacyInstanceOpalPayments;
        _wishingPoolUsage = wishingPoolUsage;
        _questAppraisal = questAppraisal;
        _luckyGodsWish = luckyGodsWish;
        _guilds = guilds;
        _zeusGift = zeusGift;
        _petOwnerMergeEnergyInterval =
            petOwnerMergeEnergyInterval ?? TimeSpan.FromSeconds(3);
        _petOwnerMergeRechargeInterval =
            petOwnerMergeRechargeInterval ?? TimeSpan.FromSeconds(6);
        _characterRuntimeProjections =
            characterRuntimeProjections ??
            gameStore as ICharacterRuntimeProjectionReader ??
            throw new ArgumentException(
                "A character runtime projection reader is required.",
                nameof(characterRuntimeProjections));
        _ownedPetSnapshots =
            ownedPetSnapshots ??
            gameStore as IOwnedPetSnapshotReader ??
            throw new ArgumentException(
                "An owned-pet snapshot reader is required.",
                nameof(ownedPetSnapshots));
        _sealedPetSnapshots = sealedPetSnapshots ??
            characterSnapshots as ISealedPetSnapshotReader;
        _worldBossAreaControl =
            worldBossAreaControl ??
            gameStore as IWorldBossAreaControlStore ??
            throw new ArgumentException(
                "A world-boss area-control store is required.",
                nameof(worldBossAreaControl));
        _worldBossRespawns =
            worldBossRespawns ??
            gameStore as IWorldBossRespawnReader ??
            throw new ArgumentException(
                "A world-boss respawn reader is required.",
                nameof(worldBossRespawns));
        _playerCoordination = playerCoordination;
        if (_requiresDurablePlayerCommands &&
            new object?[]
            {
                _talentUpgradeCommands,
                _developerItemGrantCommands,
                _developerBagClearCommands,
                _makeAttributeStoneCommands,
                _gearMentorMaterialConversionCommands,
                _classSuitCommands,
                _gearMentorDecomposeGearCommands,
                _gearEnhancementCommands,
                _equipmentForgeCommands,
                _kitBagItemDeleteCommands,
                _kitBagItemMoveCommands,
                _equipmentBagTransferCommands,
                _holyStoneCommands,
                _holySuitCommands,
                _zodiacSkillGridActivationCommands,
                _zodiacSkillGridUpgradeCommands,
                _zodiacSkillGridSelectionCommands,
                _characterLifecycleCommands,
                _petDurableCommands,
                _factionCrierCommands,
                _factionCrierBalance,
                _onlineAwardCommands,
                _onlineAwardBalance,
                _warehouseSnapshots,
                _warehouseTransferCommands,
                _warehouseExpansionCommands,
                _warehouseExpansionPolicy,
                _characterCheckpoints,
                _sealedPetSnapshots
            }.Any(static provider => provider is null))
        {
            throw new InvalidOperationException(
                "Production player mutation composition requires every " +
                "extracted durable command executor and the character " +
                "checkpoint coordinator.");
        }

        _developerCommands =
            developerCommands ?? new DeveloperCommandOptions();
        if (_requiresDurablePlayerCommands &&
            _developerCommands.Enabled &&
            _developerProgressionCommands is null)
        {
            throw new InvalidOperationException(
                "Enabled production developer commands require the " +
                "durable developer progression executor.");
        }
        _legacyAuthenticationAccess = legacyAuthenticationAccess;
        _phase4AcceptanceFaults = phase4AcceptanceFaults;
        _mapTransitionReadyTimeout =
            mapTransitionReadyTimeout ??
            DefaultMapTransitionReadyTimeout;
        _backhaulSkillCastTime = backhaulSkillCastTime;
    }

    private GameplayItemContent RequireItemContent() =>
        _itemContent ?? throw new InvalidOperationException(
            "This gameplay operation requires a pinned item-content revision.");

    private IPetContentCatalog RequirePetContent() =>
        _petContent ?? throw new InvalidOperationException(
            "This gameplay operation requires a pinned pet-content revision.");

}
