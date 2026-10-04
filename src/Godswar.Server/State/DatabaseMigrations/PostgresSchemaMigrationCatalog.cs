namespace Godswar.Server.State;

/// <summary>
/// Explicit migrations owned by the server runtime.
/// Do not discover files from database/postgres here: those files are legacy
/// bootstrap material and include local test-character fixtures.
/// </summary>
internal static partial class PostgresSchemaMigrationCatalog
{
    public static readonly IReadOnlyList<PostgresSchemaMigration> All =
    [
        new(
            "20260723_000_legacy_schema_baseline",
            "Record the pre-runner schema as a metadata-only baseline",
            """
            -- The legacy schema was initialized before the forward-only runner.
            -- No numbered bootstrap or test-fixture script is replayed here.
            SELECT 1;
            """),
        new(
            "20260723_001_mount_ride_compatibility",
            "Grant the stock Riding skill to compatible existing characters",
            """
            INSERT INTO character_skills (user_id, skill_id, skill_level, source)
            SELECT cb.id, st.skill_id, 1, 'mount-compatibility'
            FROM character_base cb
            JOIN skill_templates st
              ON st.skill_id = 4904
             AND cb.profession = ANY(st.class_ids)
            ON CONFLICT (user_id, skill_id) DO NOTHING;
            """),
        new(
            "20260723_002_mount_rank_guard",
            "Exclude mounts and mount gear from the ordinary armor aura ladder",
            """
            CREATE OR REPLACE VIEW character_rank_summary AS
            WITH totals AS (
                SELECT
                    user_id,
                    COALESCE(SUM(item_score) FILTER (
                        WHERE kind = 'weapon'
                    ), 0)::integer AS weapon_score,
                    COALESCE(SUM(item_score) FILTER (
                        WHERE kind <> 'weapon'
                          AND kind NOT IN (
                              'mount',
                              'mounthead',
                              'mountarmor',
                              'mountsoul',
                              'mountornament',
                              'mountamulet'
                          )
                    ), 0)::integer AS armor_score
                FROM character_equipment_scores
                GROUP BY user_id
            )
            SELECT
                cb.id AS user_id,
                cb.name,
                COALESCE(t.weapon_score, 0) AS weapon_score,
                COALESCE(wr.rank_level, 0)::smallint AS weapon_rank,
                COALESCE(wr.aura_effect, 0) AS weapon_aura_effect,
                COALESCE(t.armor_score, 0) AS armor_score,
                COALESCE(ar.rank_level, 0)::smallint AS armor_rank,
                COALESCE(ar.aura_effect, 0) AS armor_aura_effect
            FROM character_base cb
            LEFT JOIN totals t ON t.user_id = cb.id
            LEFT JOIN LATERAL (
                SELECT rank_level, aura_effect
                FROM equipment_rank_rules
                WHERE rank_kind = 'weapon'
                  AND required_score <= COALESCE(t.weapon_score, 0)
                ORDER BY rank_level DESC
                LIMIT 1
            ) wr ON true
            LEFT JOIN LATERAL (
                SELECT rank_level, aura_effect
                FROM equipment_rank_rules
                WHERE rank_kind = 'armor'
                  AND required_score <= COALESCE(t.armor_score, 0)
                ORDER BY rank_level DESC
                LIMIT 1
            ) ar ON true;
            """),
        new(
            "20260723_003_erebus_lion_mount",
            "Seed the locally authored Erebus Lion mount family",
            """
            WITH tiers(id, required_level, speed, max_hp) AS (
                VALUES
                    (16200,  40, '0.20', '2500'),
                    (16201,  50, '0.21', '2800'),
                    (16202,  60, '0.22', '3100'),
                    (16203,  70, '0.23', '3400'),
                    (16204,  80, '0.24', '3700'),
                    (16205,  90, '0.25', '4000'),
                    (16206, 100, '0.26', '4300'),
                    (16207, 110, '0.27', '4650'),
                    (16208, 120, '0.28', '5000'),
                    (16209, 120, '0.50', '5000')
            )
            INSERT INTO item_templates (
                id, kind, name_key, display_name, equipment_slot, class_ids,
                min_level, max_level, hand, skill_flag, texture, icon, stats
            )
            SELECT
                id,
                'mount',
                'Ride' || id,
                'Erebus Lion',
                20,
                ARRAY[0, 1, 2, 3]::smallint[],
                required_level,
                200,
                NULL,
                20,
                './Localization/en_us/UI/Texture/Icon4.gwo',
                '396,0',
                jsonb_build_object(
                    'ID', id::text,
                    'Type', 'mount',
                    'Texture', './Localization/en_us/UI/Texture/Icon4.gwo',
                    'Icon', '396,0',
                    'Random', '0',
                    'Distribution', '0,0',
                    'Speed', array_to_string(array_fill(speed, ARRAY[20]), ','),
                    'MaxHP', array_to_string(array_fill(max_hp, ARRAY[20]), ','),
                    'Money', '0',
                    'Overlap', '1',
                    'Equip', '1',
                    'Use', '1',
                    'SkillFlag', '20',
                    'Class', '0,1,2,3',
                    'PlayLv', required_level::text || ',200'
                )
            FROM tiers
            ON CONFLICT (id) DO UPDATE
            SET kind = EXCLUDED.kind,
                name_key = EXCLUDED.name_key,
                display_name = EXCLUDED.display_name,
                equipment_slot = EXCLUDED.equipment_slot,
                class_ids = EXCLUDED.class_ids,
                min_level = EXCLUDED.min_level,
                max_level = EXCLUDED.max_level,
                hand = EXCLUDED.hand,
                skill_flag = EXCLUDED.skill_flag,
                texture = EXCLUDED.texture,
                icon = EXCLUDED.icon,
                stats = EXCLUDED.stats;
            """),
        new(
            "20260723_004_remove_redundant_indexes",
            "Normalize username uniqueness and remove only proven redundant indexes",
            """
            DO $normalize_accounts_username_uniqueness$
            DECLARE
                username_index regclass := to_regclass('public.ux_accounts_username');
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint unique_constraint
                    JOIN pg_attribute username_column
                      ON username_column.attrelid = unique_constraint.conrelid
                     AND username_column.attname = 'username'
                     AND unique_constraint.conkey =
                         ARRAY[username_column.attnum]::smallint[]
                    WHERE unique_constraint.conrelid = 'public.accounts'::regclass
                      AND unique_constraint.contype = 'u'
                ) THEN
                    IF username_index IS NULL THEN
                        ALTER TABLE public.accounts
                            ADD CONSTRAINT accounts_username_key UNIQUE (username);
                    ELSE
                        ALTER TABLE public.accounts
                            ADD CONSTRAINT accounts_username_key
                            UNIQUE USING INDEX ux_accounts_username;
                    END IF;
                ELSIF username_index IS NOT NULL
                      AND NOT EXISTS (
                          SELECT 1
                          FROM pg_constraint
                          WHERE conindid = username_index
                      ) THEN
                    DROP INDEX public.ux_accounts_username;
                END IF;
            END
            $normalize_accounts_username_uniqueness$;

            DROP INDEX IF EXISTS public.ix_character_items_user_location;
            """),
        CreateStarterConsumableTemplates(),
        CreateArchiveLegacyCharacterKitbag(),
        CreateCharacterItemTemplateForeignKey(),
        CreateZodiacSkillGridState(),
        CreateSkillCastInterruptOpcode(),
        CreatePetSystemFoundation(),
        CreatePetAptitudeRangeCorrection(),
        CreatePetAptitudeCatalog(),
        CreateOwnedPetBootstrapOpcode(),
        CreatePetPresenceProtocol(),
        CreatePetPresenceAuditOperation(),
        CreatePetGrowthPolicy(),
        CreatePetGrowthMidpointBackfill(),
        CreatePetGrowthPolicyV2(),
        CreatePetInitialSavvyPolicy(),
        CreatePetSavvySemanticsCorrection(),
        CreatePetSavvySemanticsHardening(),
        CreatePetLevelProgression(),
        CreateNpcContentRelease(),
        CreateNpcDialogueContentRelease(),
        CreateCommandInboxOutboxFoundation(),
        CreateCommandInboxOutboxHardening(),
        CreateEconomyLedgerFoundation(),
        CreateEconomyLedgerHardening(),
        CreateHolyStoneMaterialTemplates(),
        CreateCharacterCheckpointVersions(),
        CreateCharacterLifecycleFoundation(),
        CreateProgressionRewardFoundation(),
        CreateProgressionIntervalAuthority(),
        CreatePetDurabilityFoundation(),
        CreateTempestRealmAuthority(),
        CreateMonsterContentRelease(),
        CreateEnterBootstrapContentRelease(),
        CreateItemTemplateContentRelease(),
        CreateGameplayContentRelease(),
        CreateItemRuntimeProjectionCutover(),
        CreateItemPolicyContentRelease(),
        CreatePetContentRelease(),
        CreateItemContentHeaderSealGuard(),
        CreateItemMaterialContentRelease(),
        CreateItemMaterialRecipeContentRelease(),
        CreateHolySuitContentRelease(),
        CreateFighterLevelSeal(),
        CreateFighterExperienceUInt32(),
        CreateHolySuitSingaporeDayBoundary(),
        CreateHolySuitFixedDailyCap(),
        CreateNpcDialogueMultiRouteRelease(),
        CreateItemContentV6Release(),
        CreateClassSuitAttributeSlots(),
        CreateElementalAttributeSlots(),
        CreateItemContentV8Release(),
        CreateItemContentV9Release(),
        CreateSocketSpellItemTemplates(),
        ReconcileStockHolyStoneMaterialTemplates(),
        CreateHolySpiritEffectivenessValues(),
        CreateFashionSlotConsistency(),
        CreatePetSkillsAndTalentsFoundation(),
        CreatePetManagerDialogueCapability(),
        CreateFashionRankProjectionRepair(),
        ReconcileZephyrHolyStoneMaterialTemplates(),
        CreatePetShedCapacity(),
        CreatePetSkillCellProtocol(),
        CorrectPetSkillCellProtocol(),
        CreatePetPointResetDialogueCapability(),
        CorrectPetGrowthSavvySemanticsV2(),
        CreatePetInitialSavvyPolicyV3(),
        CreatePetPhoenixGrowthActivation(),
        ReconcileQualityDerivedPetTalents(),
        CreatePetOwnerMergeContentRelease(),
        CreatePetMergeBalanceContentRelease(),
        CreatePetDurableEvidenceV2(),
        NormalizePetConsumableMutableProjection(),
        CreatePetDurableEvidenceV3(),
        CreatePetScaledAddedValueSemanticsV3(),
        CreatePetGrowthPreview(),
        CreatePetBasicSavvyPreview(),
        CreatePetRankContentRelease(),
        CreatePetHatchEvidenceHardening(),
        CreatePetLearnedSkillContentRelease(),
        CreatePetMergeSavvyLookupContentRelease(),
        CreatePetMagicJadeAppearanceGroups(),
        CreatePetAppearanceChange(),
        CreatePetBind(),
        CreatePetSoulContract(),
        CreatePetManagerUtility(),
        CreateBagConsumableCooldownState(),
        CreatePetPhoenixRebirthBracket(),
        CreatePackedSealOwnershipHardening(),
        CreateMonsterCombatAuthority(),
        CreateMultiRealmCharacterAuthority(),
        CreateRealmScopedWorldBossControl(),
        CreateFencedAccountPresenceProjection(),
        CreatePetOwnerMergeRebalance(),
        CreateCooledHolyStoneBalance(),
        CreateHolySpiritBalanceSettings(),
        CreateFactionCrierFoundation(),
        CreateFactionCrierDialogueCapability(),
        CreateFactionCrierNzstCalendar(),
        CreateRealmCalendarAuthority(),
        CreateCooledHolyStoneSevenPercentReduction(),
        CreateOnlineAwardFoundation(),
        CreateOnlineAwardDialogueCapability(),
        CreateWarehouseFoundation(),
        CreateWarehouseDialogueCapability(),
        CreateInstanceCallerDialogueCapability(),
        CreateWarehouseNineBoxCapacity(),
        CreateWarehouseManagerStorageKeyCap(),
        CreateMedusaDailyEntry(),
        CreateMedusaCompletionRewards(),
        CreateMedusaTitleOwnership(),
        CreateMedusaPetCaptureRarity(),
        CreateMedusaDailyEntryLimit(),
        CreateMedusaRewardPolicy(),
        CreateMedusaMonsterContent(),
        CreateMedusaExternalScore(),
        CreateMedusaExternalHealth(),
        CreateCapitalNpcBindingGold(),
        CreateOutboxOrderedSparsePolicy(),
        CreateOutboxClaimCandidateIndex(),
        CreateFighterLevelSealAllLevels(),
        CreateFighterLevelSealRevision(),
        CreateCapitalVendorItems(),
        CreateMonsterRewardPolicy(),
        CreateDonatorTiers(),
        CreateGlobalExperienceMultiplier(),
        CreateTransporterDialogueCapability(),
        CreateBattlefieldInstanceDialogueCapability(),
        CreateLegacyInstanceDailyEntry(),
        CreateLegacyInstanceDailyEntryLimit(),
        CreateInstanceAttemptDialogueRelease(),
        CreateLegacyInstanceOpalPayment(),
        CreateLegacyInstancePaidRetries(),
        CreateInstanceFreeEntryDialogueRelease(),
        CreateDuelArenaNpcContentRelease(),
        CreateDuelArenaDialogueRelease(),
        CreateDuelArenaInitialActions(),
        CreateDuelArenaServices(),
        CreateRestoreItemViewAuthority(),
        CreateAtlantisCompletionRewards(),
        CreateExchangePointBalances(),
        CreateCharacterQuestState(),
        CreateCharacterQuestTable(),
        CreateMonsterLootPolicy(),
        CreateWonderlandTitles(),
        CreateWonderlandAllIslandTitles(),
        CreateHolySuitDivinium(),
        CreateHolySuitCombatProjection(),
        CreateWonderlandChestClaims(),
        CreateWonderlandBossLootClaims(),
        CreateMonsterCombatBalance(),
        CreateBloodfangPetSpecies(),
        CreateWishingPoolUsageState(),
        CreateGuildState(),
        CreateGuildBuildingState(),
        CreateGuildContent(),
        CreateCharacterGuildDuty(),
        CreateCharacterGuildDutyBackfill(),
        CreateLuckyGodsWishState(),
        CreateGuildAltarWorshipState(),
        CreateGuildWorshipImpact(),
        // Ascending id order is enforced at startup.
        CreateGuildAltarWorshipSettlement(),
        CreatePetCareConsumableTemplates(),
        CreateQuestExperienceAppraisal(),
        CreatePetPresenceDeleteOperation(),
        CreateLelantineFarmPoints(),
        CreateLelantineFarmPersonalScores(),
        CreateQuestRewardItemClaims(),
        CreateQuestRewardOverrides(),
        CreateMonsterLootBindOnPickup(),
        CreateRewardItemAttributes(),
        CreateWonderlandUnlimitedEntries(),
        CreateHarborAttackDailyEntry(),
        CreateAtlantisPartialRewards(),
        CreateAtlantisCompletionRosterConstraint(),
        CreateGuildRefuseApplications(),
        CreateZeusGiftDailyState(),
        CreateZeusGiftWeeklyState(),
        CreateZeusGiftRealmStoneTotal(),
        CreateZeusLuckEntries(),
        CreateZeusGiftLimitedStock(),
        CreateZeusGiftBankedReward(),
        CreateZeusGiftLimitedStockPerCamp()
    ];
}
