using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.World;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;
using Godswar.Server.State;

namespace Godswar.Server;

internal sealed class GameClientHandlerFactory(
    IGameStore store,
    IAccountDirectory accountDirectory,
    IAccountPresenceWriter accountPresence,
    GameSessionRegistry registry,
    ICharacterSnapshotReader characterSnapshots,
    ISealedPetSnapshotReader sealedPetSnapshots,
    IWorldContentReader worldContent,
    DeveloperCommandOptions developerCommands,
    ICharacterCheckpointCoordinator characterCheckpoints,
    ServerGameplayPersistenceProviders gameplayPersistence,
    PostgresApplicationDataRuntime? postgresRuntime,
    RealmCalendar realmCalendar,
    IPlayerCoordinationLeaseIssuer? playerCoordination = null,
    GameplayRuntimeCatalogs? gameplayCatalogs = null,
    GameplayItemContent? itemContent = null,
    IPetContentCatalog? petContent = null,
    RealmId? processRealmId = null)
{
    public GameClientHandler Create(
        ClientSession session,
        SecurePhase4AcceptanceFaults? phase4AcceptanceFaults = null,
        LegacyAuthenticationAccess? legacyAuthenticationAccess = null)
    {
        registry.ConfigureMedusaCompletionRewards(
            postgresRuntime?.MedusaCompletionRewards);
        registry.ConfigureAtlantisCompletionRewards(postgresRuntime?.AtlantisCompletionRewards,
            postgresRuntime?.LegacyInstanceDailyEntries);
        registry.ConfigureCharacterTitleSelections(postgresRuntime?.TitleSelections);
        // Wonderland's island titles gate the treasure chests, the final exit
        // and (through settlement) the run's retirement, so the title store is
        // configured together with the two claim stores it authorizes.
        registry.ConfigureWonderlandTitles(
            postgresRuntime?.WonderlandTitles,
            postgresRuntime?.LegacyInstanceDailyEntries);
        registry.ConfigureWonderlandChests(postgresRuntime?.WonderlandChests);
        registry.ConfigureWonderlandBlackmarket(postgresRuntime?.WonderlandBlackmarket);
        return new(
            session,
            store,
            registry,
            characterSnapshots,
            worldContent,
            developerCommands,
            phase4AcceptanceFaults,
            legacyAuthenticationAccess:
                legacyAuthenticationAccess,
            talentUpgradeCommands:
                postgresRuntime?.TalentUpgradeCommands,
            developerItemGrantCommands:
                postgresRuntime?.DeveloperItemGrantCommands,
            developerBagClearCommands:
                postgresRuntime?.DeveloperBagClearCommands,
            developerProgressionCommands:
                postgresRuntime?.DeveloperProgressionCommands,
            makeAttributeStoneCommands:
                postgresRuntime?.MakeAttributeStoneCommands,
            gearMentorMaterialConversionCommands:
                postgresRuntime?.MaterialConversionCommands,
            classSuitCommands:
                postgresRuntime?.ClassSuitCommands,
            gearMentorDecomposeGearCommands:
                postgresRuntime?.DecomposeGearCommands,
            gearEnhancementCommands:
                postgresRuntime?.GearEnhancementCommands,
            equipmentForgeCommands:
                postgresRuntime?.EquipmentForgeCommands,
            kitBagItemDeleteCommands:
                postgresRuntime?.KitBagItemDeleteCommands,
            kitBagItemMoveCommands:
                postgresRuntime?.KitBagItemMoveCommands,
            equipmentBagTransferCommands:
                postgresRuntime?.EquipmentBagTransferCommands,
            holyStoneCommands:
                postgresRuntime?.HolyStoneCommands,
            holySuitCommands:
                postgresRuntime?.HolySuitCommands,
            zodiacSkillGridActivationCommands:
                postgresRuntime?.ZodiacSkillGridActivationCommands,
            zodiacSkillGridUpgradeCommands:
                postgresRuntime?.ZodiacSkillGridUpgradeCommands,
            zodiacSkillGridSelectionCommands:
                postgresRuntime?.ZodiacSkillGridSelectionCommands,
            characterCheckpoints:
                characterCheckpoints,
            characterLifecycleCommands:
                postgresRuntime?.CharacterLifecycleCommands,
            monsterDeathRewardCommands:
                postgresRuntime?.MonsterDeathRewardCommands,
            petDurableCommands:
                postgresRuntime?.PetDurableCommands,
            factionCrierCommands:
                postgresRuntime?.FactionCrierCommands,
            factionCrierBalance:
                postgresRuntime?.FactionCrierBalance,
            onlineAwardCommands:
                postgresRuntime?.OnlineAwardCommands,
            onlineAwardBalance:
                postgresRuntime?.OnlineAwardBalance,
            characterRuntimeProjections:
                gameplayPersistence.CharacterRuntime,
            ownedPetSnapshots:
                gameplayPersistence.OwnedPets,
            worldBossAreaControl:
                gameplayPersistence.WorldBossAreaControl,
            worldBossRespawns:
                gameplayPersistence.WorldBossRespawns,
            playerCoordination:
                playerCoordination,
            accountDirectory:
                accountDirectory,
            accountPresence:
                accountPresence,
            requiresDurableMonsterRewardCommands:
                postgresRuntime is not null,
            requiresDurablePlayerCommands:
                postgresRuntime is not null,
            gameplayCatalogs:
                gameplayCatalogs ??
                GameplayRuntimeCatalogs.Create(worldContent.Gameplay),
            itemContent:
                itemContent,
            petContent:
                petContent,
            sealedPetSnapshots:
                sealedPetSnapshots,
            realmCatalog:
                postgresRuntime?.RealmCatalog,
            processRealmId:
                processRealmId,
            realmCalendar:
                realmCalendar ?? throw new ArgumentNullException(
                    nameof(realmCalendar)),
            warehouseSnapshots:
                postgresRuntime?.WarehouseSnapshots,
            warehouseTransferCommands:
                postgresRuntime?.WarehouseTransferCommands,
            warehouseExpansionCommands:
                postgresRuntime?.WarehouseExpansionCommands,
            warehouseExpansionPolicy:
                postgresRuntime?.WarehouseExpansionPolicy,
            medusaDailyEntries:
                postgresRuntime?.MedusaDailyEntries,
            legacyInstanceDailyEntries:
                postgresRuntime?.LegacyInstanceDailyEntries,
            legacyInstanceOpalPayments:
                postgresRuntime?.LegacyInstanceOpalPayments,
            wishingPoolUsage:
                postgresRuntime?.WishingPoolUsage,
            questAppraisal:
                postgresRuntime?.QuestAppraisal,
            luckyGodsWish:
                postgresRuntime?.LuckyGodsWish,
            guilds:
                postgresRuntime?.Guilds,
            zeusGift:
                postgresRuntime?.ZeusGift);
    }
}
