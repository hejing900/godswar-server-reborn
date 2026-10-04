using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private const int ConcurrentPacketCount = 512;
    private const int ConcurrentPacketLength = 37;
    private const ushort ConcurrentPacketOpcode = 0x6F6F;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--schema-metadata")
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                migrationCount = PostgresSchemaMigrationCatalog.All.Count,
                migrationHead = PostgresSchemaMigrationCatalog.All[^1].Id
            }));
            return 0;
        }
        (string Name, Func<Task> Run)[] checks =
        [
            (ProtocolCheckRunnerChecks.CheckName, ProtocolCheckRunnerChecks.RunAsync),
            .. CoreRuntimeCheckCatalog.All,
            .. WorldTravelCheckCatalog.All,
            .. LegacyInstanceCheckCatalog.All,
            (HarborAttackEntryProtocolChecks.CheckName,
                HarborAttackEntryProtocolChecks.RunAsync),
            (ChatChannelChecks.CheckName, ChatChannelChecks.RunAsync),
            ("Native local-player scene-change packet", MapSceneChangePacketChecks.RunAsync),
            ("Player combat and committed-progression ECS parity", PlayerCombatEcsParityChecks.RunAsync),
            ("Live reversible player-combat ECS adapter", PlayerCombatEcsLiveAdapterChecks.RunAsync),
            ("Player movement ECS projection parity", PlayerMovementEcsParityChecks.RunAsync),
            ("Live reversible player-movement ECS adapter", PlayerMovementEcsLiveAdapterChecks.RunAsync),
            ("Monster-to-player damage ECS parity", MonsterPlayerDamageEcsParityChecks.RunAsync),
            ("Live reversible monster-to-player damage ECS adapter", MonsterPlayerDamageEcsLiveAdapterChecks.RunAsync),
            ("Pet Healing talent ECS policy", PetHealingTalentEcsChecks.RunAsync),
            ("Pet Healing live adapter lifecycle", PetHealingTalentLiveAdapterChecks.RunAsync),
            ("Pet Healing combat-text and vitals protocol", PetHealingTalentProtocolChecks.RunAsync),
            .. DataArchitectureIntegrationChecks(),
            .. PetProtocolCheckCatalog.All,
            .. IntonedCombatSkillCheckCatalog.All,
            // BagConsumableUseChecks is parked with the paused bag-consumable
            // feature: its frame builder does not reproduce the captured
            // opcode-10040 cast yet, and the runtime wiring is removed, so the
            // check is not registered until that work resumes.
            ("Character camp starting location", CheckCharacterCampStartingLocationAsync),
            ("Saved character location persistence", CheckSavedCharacterLocationPersistenceAsync),
            ("Persistent monster-kill progression", CheckMonsterKillProgressionAsync),
            (
                PlayerExperienceSealingChecks.CheckName,
                PlayerExperienceSealingChecks.RunAsync),
            (
                FighterLevelSealToolChecks.CheckName,
                FighterLevelSealToolChecks.RunAsync),
            (
                FighterExperienceFixtureToolChecks.CheckName,
                FighterExperienceFixtureToolChecks.RunAsync),
            (
                LegacyFighterExperienceWireChecks.CheckName,
                LegacyFighterExperienceWireChecks.RunAsync),
            ("Donator and Battle Pass progression benefits", CheckExperienceBoostStackingAsync),
            ("Pet EXP boost settlement boundary", PetExperienceRewardBoundaryChecks.RunAsync),
            ("Online-only EXP and Talent boost duration", CheckOnlineProgressionBoostDurationAsync),
            ("World-session owned boost clock", CheckWorldSessionOwnedBoostClockAsync),
            ("Working-original login bootstrap manifest", CheckAfterLoginManifestAsync),
            ("Working-original character preview layout", CheckCharacterPreviewAsync),
            ("EnterMain character identity and saved location", CheckEnterMainCharacterIdentityAsync),
            (
                TalentProgressionPolicyChecks.CheckName,
                TalentProgressionPolicyChecks.RunAsync),
            ("Warrior talent ID-zero upgrade protocol", CheckWarriorTalentIdZeroUpgradeAsync),
            ("JSON warrior talent persistence", CheckJsonWarriorTalentPersistenceAsync),
            ("Warrior starter skill packets", CheckWarriorStarterSkillPacketsAsync),
            ("JSON provider starter skill", CheckJsonProviderStarterSkillAsync),
            ("Skill combat catalog", CheckSkillCombatCatalogAsync),
            ("Skill combat cast and cooldown timing", SkillCombatTimingCatalogChecks.RunAsync),
            (
                PriestHealingSkillCatalogChecks.CheckName,
                PriestHealingSkillCatalogChecks.RunAsync),
            ("Native mount Ride status and spawn protocol", CheckMountRideProtocolAsync),
            ("Mount and mount-gear Q20/G25 stat progression", MountEquipmentProgressionChecks.RunAsync),
            ("Immediate mount Ride dismount toggle", CheckImmediateMountRideDismountAsync),
            ("Atomic mount Ride activation commit", CheckAtomicMountRideActivationAsync),
            ("Sacred Zeal runtime-status composition", CheckSacredZealStatusCompositionAsync),
            ("Holy Ward runtime-status mitigation", CheckHolyWardStatusCompositionAsync),
            ("Priest Gaia Care and Mana Shield statuses", CheckPriestDefensiveStatusCompositionAsync),
            ("Skill cast target and impact layout", CheckSkillCastTargetAndImpactAsync),
            ("Native skill-cast interruption packet", SkillCastInterruptPacketChecks.RunAsync),
            (
                "Skill cast lifecycle cancellation races",
                BackhaulSkillHandlerChecks.RunCastingLifecycleRacesAsync),
            (
                "Skill cast authoritative interruption boundaries",
                BackhaulSkillHandlerChecks
                    .RunAuthoritativeInterruptionBoundariesAsync),
            ("Basic and monster attack packet layouts", CheckAttackPacketLayoutsAsync),
            ("Dynamic original-server time response", CheckServerTimePacketAsync),
            ("Zodiac full-sync and accumulation protocol", CheckZodiacProtocolAsync),
            ("Zodiac online-energy cadence and day policy", CheckZodiacOnlineEnergyPolicyAsync),
            ("JSON Zodiac creation persistence", CheckJsonZodiacPersistenceAsync),
            ("Zodiac level-up policy and protocol", CheckZodiacLevelUpgradeAsync),
            ("JSON Zodiac level-up persistence", CheckJsonZodiacLevelUpgradePersistenceAsync),
            ("Serialized Zodiac accrual and level-up", CheckZodiacLevelUpgradeSerializationAsync),
            ("PostgreSQL Zodiac level-up race", PostgresZodiacLevelUpgradeIntegrationChecks.RunAsync),
            ("Zodiac skill-grid activation and persistence", CheckZodiacSkillGridActivationAsync),
            ("Zodiac skill-grid upgrade and persistence", CheckZodiacSkillGridUpgradeAsync),
            (
                ZodiacDefensiveSkillProjectionChecks.CheckName,
                ZodiacDefensiveSkillProjectionChecks.RunAsync),
            ("PostgreSQL Zodiac skill-grid race", PostgresZodiacSkillGridIntegrationChecks.RunAsync),
            (ZodiacOffensiveSkillProjectionChecks.CheckName, ZodiacOffensiveSkillProjectionChecks.RunAsync),
            ("Player passive recovery protocol", CheckPlayerRecoveryProtocolAsync),
            ("PlayerWorldSpawn layout", CheckPlayerWorldSpawnAsync),
            ("PlayerWorldSpawn captured appearance", CheckPlayerWorldAppearanceAsync),
            ("PlayerWorldSpawn full quality/grade extension", CheckPlayerWorldExtendedAppearanceAsync),
            ("PlayerWorldSpawn mount overflow priority", CheckPlayerWorldMountOverflowPriorityAsync),
            ("Player auxiliary appearance packets", CheckPlayerAuxiliaryAppearanceAsync),
            ("PlayerInspectEquipment packed slots and details", CheckPlayerInspectExtendedSlotsAsync),
            ("PlayerDetail vitals and wallet layout", CheckPlayerDetailAsync),
            ("PlayerStatusUpdate layout", CheckPlayerStatusUpdateAsync),
            (GuildPanelStatusChecks.CheckName, GuildPanelStatusChecks.RunAsync),
            (GuildListProtocolChecks.CheckName, GuildListProtocolChecks.RunAsync),
            (GuildProfileTitleChecks.CheckName, GuildProfileTitleChecks.RunAsync),
            (GuildApplicationNoticeChecks.CheckName, GuildApplicationNoticeChecks.RunAsync),
            ("Native status-effect sync layout", CheckPlayerStatusEffectsAsync),
            ("Post-enter UI-ready bootstrap gate", CheckPostEnterBootstrapGateAsync),
            ("Captured accepted-quest replay exclusion", CheckCapturedAcceptedQuestReplayExclusionAsync),
            ("Guarded bag-to-equipment persistence and snapshot", CheckGuardedEquipmentMoveAsync),
            ("Rejected right-click equip authoritative slot", CheckRejectedEquipRefreshSlotAsync),
            ("Genuine equipment-kind persistence guard", EquipmentKindGuardChecks.RunAsync),
            ("Holy-stone targeted authoritative-item preservation", CheckHolyStoneAuthoritativePersistencePlanAsync),
            ("Holy-stone stock-client level projection", CheckHolyStoneWireLevelProjectionAsync),
            ("Occupied ghost-slot bag move parsing", CheckOccupiedGhostSlotBagMoveParsingAsync),
            ("Confirmed bag-item deletion protocol and persistence", CheckBagItemDeletionAsync),
            ("Developer material item command", CheckDeveloperForgingMaterialCommandAsync),
            (
                DeveloperRubyCommandChecks.CheckName,
                DeveloperRubyCommandChecks.RunAsync),
            (
                DeveloperHolyBoxGrantChecks.CheckName,
                DeveloperHolyBoxGrantChecks.RunAsync),
            (
                DeveloperCostumeGrantChecks.CheckName,
                DeveloperCostumeGrantChecks.RunAsync),
            ("Developer mount catalog, command, and JSON grant", DeveloperMountCommandChecks.RunAsync),
            ("PostgreSQL developer mount grant and audit", PostgresDeveloperMountIntegrationChecks.RunAsync),
            ("PostgreSQL developer clear-bag scope and audit", PostgresKitBagClearIntegrationChecks.RunAsync),
            ("Equipment forging packet protocol", ForgeProtocolChecks.RunAsync),
            ("Equipment forging rule catalog and calculator", EquipmentForgeCatalogChecks.RunAsync),
            ("Atomic equipment-forge persistence", ForgeTransactionChecks.RunAsync),
            ("PostgreSQL equipment-forge race and preservation", PostgresForgeIntegrationChecks.RunAsync),
            ("Gear-enhancement material catalog and planner", GearEnhancementStateChecks.RunAsync),
            ("Gear Mentor material, planner, and protocol", GearMentorStateChecks.RunAsync),
            ("PostgreSQL Gear Mentor race and preservation", PostgresGearMentorIntegrationChecks.RunAsync),
            (ClassSuitWireProtocolChecks.CheckName, ClassSuitWireProtocolChecks.RunAsync),
            (ClassSuitCommandContractChecks.CheckName, ClassSuitCommandContractChecks.RunAsync),
            (ClassSuitExecutionContractChecks.CheckName, ClassSuitExecutionContractChecks.RunAsync),
            ("Class Suit conversion catalog and planner", ClassSuitConversionPlannerChecks.RunAsync),
            ("Class Suit attribute catalog and planner", ClassSuitAttributePlannerChecks.RunAsync),
            (
                ElementalClassSuitAttributeChecks.CheckName,
                ElementalClassSuitAttributeChecks.RunAsync),
            (ClassSuitHandlerChecks.CheckName, ClassSuitHandlerChecks.RunAsync),
            ("Atomic gear-enhancement persistence", GearEnhancementTransactionChecks.RunAsync),
            (
                PostgresGearEnhancementIntegrationChecks.CheckName,
                PostgresGearEnhancementIntegrationChecks.RunAsync),
            (
                PostgresEquipmentForgeCommandIntegrationChecks.CheckName,
                PostgresEquipmentForgeCommandIntegrationChecks.RunAsync),
            ("Gear-enhancer initial NPC protocol", CheckGearEnhancerInitialProtocolAsync),
            ("Holy-suit design original NPC protocol", CheckHolySuitDesignProtocolAsync),
            ("Holy-suit bounded stock-client wire protocol", HolySuitWireProtocolChecks.RunAsync),
            .. CapitalCommerceCheckCatalog.All,
            .. MedusaCheckCatalog.All,
            (FactionCrierHandlerChecks.CheckName, FactionCrierHandlerChecks.RunAsync),
            (FactionCrierProjectionChecks.CheckName, FactionCrierProjectionChecks.RunAsync),
            (
                CompactItemClassAttributeChecks.CheckName,
                CompactItemClassAttributeChecks.RunAsync),
            ("NPC definitions and spawn layout", CheckNpcDefinitionsAndSpawnLayoutAsync),            ("NPC multi-segment scene-key generation", NpcMultiSegmentSceneChecks.RunAsync),
            ("NPC movement-cell visibility", CheckNpcMovementCellVisibilityAsync),
            (
                LuckyGodsWishPolicyChecks.CheckName,
                LuckyGodsWishPolicyChecks.RunAsync),
            (
                ZeusGiftProtocolChecks.CheckName,
                ZeusGiftProtocolChecks.RunAsync),
            ("Monster movement-cell visibility and spawn layout", CheckMonsterMovementCellVisibilityAsync),
            ("World boss outdoor-area catalog", WorldBossCatalogChecks.RunAsync),
            (MonsterLootChannelChecks.CheckName, MonsterLootChannelChecks.RunAsync),
            ("Persisted world-boss respawn across restart", CheckPersistedWorldBossRespawnAsync),
            ("Monster ECS shadow parity", MonsterEcsParityChecks.RunAsync),
            ("Reversible monster runtime cutover", MonsterRuntimeCutoverChecks.RunAsync),
            ("Monster movement and lifecycle packet layouts", CheckMonsterMovementPacketLayoutsAsync),
            ("Monster runtime appearance patch", CheckMonsterRuntimeAppearancePatchAsync),
            ("Shared bounded monster runtime and lifecycle", CheckSharedBoundedMonsterRuntimeAsync),
            ("Warrior stun monster-control runtime", MonsterStunChecks.RunAsync),
            ("Passive monster retaliation state machine", CheckMonsterRetaliationRuntimeAsync),
            ("Monster smooth leash return and full-health replacement", CheckMonsterLeashReturnAsync),
            ("Monster return/replacement socket lifecycle", CheckMonsterReturnViewerPacketOrderAsync),
            ("Monster generation reconciliation across bootstrap", CheckMonsterGenerationReconciliationAsync),
            ("Monster old-generation event packet suppression", CheckMonsterOldGenerationEventSuppressionAsync),
            ("Monster same-generation activation refresh", CheckMonsterSameGenerationActivationRefreshAsync),
            ("Monster entering-viewer damage delivery lease", CheckMonsterEnteringViewerDamageLeaseAsync),
            ("Monster health-revision inverse and gap ordering", CheckMonsterHealthRevisionOrderingAsync),
            ("Monster self-viewer inverse damage ordering", CheckMonsterSelfViewerDamageOrderingAsync),
            ("Monster area-damage AOI revision delivery", CheckMonsterAreaDamageDeliveryAsync),
            ("Monster viewer registry AOI scoping", CheckMonsterViewerRegistryAsync),
            ("Map registry world-readiness gate", CheckMapRegistryWorldReadinessAsync),
            .. PlayerWorldObjectIdChecks.All,
            (
                "Password authentication primitives",
                PasswordAuthenticationPrimitiveChecks.RunAsync),
            (
                "Bounded password KDF scheduler",
                PasswordKdfSchedulerChecks.RunAsync),
            (
                "JSON account authentication and migration",
                AccountAuthenticationJsonChecks.RunAsync),
            (
                "Registration collision plaintext authentication",
                RegistrationCollisionAuthenticationChecks.RunAsync),
            ("Secure Phase 2 bounded protocol codecs", SecureProtocolCodecChecks.RunAsync),
            ("Secure Phase 2 legacy transport parity", LegacyByteTransportChecks.RunAsync),
            ("Secure Phase 2 bounded network lifecycle", NetworkRuntimeLifecycleChecks.RunAsync),
            ("Secure Phase 2 TLS mux transport", SecureTlsTransportChecks.RunAsync),
            (
                "Secure Phase 2 single-use game ticket authority",
                SecureGameTicketStoreChecks.RunAsync),
            (
                "Secure Phase 2 authenticated grant and principal flow",
                SecureLoginTicketFlowChecks.RunAsync),
            (
                "Secure Phase 2 authentication idle transition",
                ClientSessionRuntimeChecks.RunSecureAuthenticationIdleTransitionAsync),
            (
                "Mutually exclusive raw or secure listener profile",
                ServerListenerProfileChecks.RunAsync),
            (
                "Fail-closed server runtime and storage profiles",
                ServerRuntimeProfileChecks.RunAsync),
            (
                "Local-development-only legacy authentication",
                LegacyAuthenticationProfileChecks.RunAsync),
            (
                "Controlled-host exact Npgsql validation",
                ControlledHostValidationCommandChecks.RunAsync),
            (
                "Controlled-host exact TLS certificate policy",
                ControlledHostCertificateValidationChecks.RunAsync),
            (
                "Controlled-host exact acceptance options",
                ControlledHostAcceptancePolicyChecks.RunAsync),
            (
                "Controlled-host privacy-safe evidence",
                ControlledHostPrivacyEvidenceChecks.RunAsync),
            (
                "Controlled-host same-user graceful shutdown",
                ControlledHostShutdownControlChecks.RunAsync),
            (
                "Secure Phase 3 UDP Slice 9A/9B binding foundation",
                SecureUdpFoundationChecks.RunAsync),
            (
                "Secure Phase 3 UDP bounded loopback baseline",
                SecureUdpAdmissionBaselineChecks.RunAsync),
            (
                "Secure Phase 4 realtime movement protocol",
                SecureRealtimeMovementProtocolChecks.RunAsync),
            (
                "Secure Phase 4 realtime session authority",
                SecureRealtimeSessionAuthorityChecks.RunAsync),
            (
                "Secure Phase 4 authoritative movement policy",
                AuthoritativePlayerMovementSystemChecks.RunAsync),
            (
                "Secure Phase 4 deterministic network emulation and overload",
                SecurePhase4NetworkEmulationChecks.RunAsync),
            (
                "Secure Phase 4 controlled-host fault injection",
                SecurePhase4AcceptanceFaultChecks.RunAsync),
            (
                "Secure Phase 4 game-handler movement integration",
                SecureRealtimeHandlerIntegrationChecks.RunAsync),
            (
                "Secure Phase 5A deterministic movement replay",
                Phase5DeterministicMovementReplayChecks.RunAsync),
            (
                "Secure Phase 5A realtime decoder fuzz",
                Phase5RealtimeDecoderFuzzChecks.RunAsync),
            (
                "Secure Phase 5A simulation loop observability",
                SimulationLoopMetricsChecks.RunAsync),
            (
                "Secure Phase 5A operational state metrics",
                OperationalStateMetricsChecks.RunAsync),
            ("ClientSession concurrent send ordering", CheckConcurrentSendOrderingAsync)
        ];

        return await ProtocolCheckRunner.RunAsync(
            checks, args, Console.Out, Console.Error);
    }
}
