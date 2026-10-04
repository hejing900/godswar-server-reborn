using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckForwardOnlyCatalog()
    {
        Check.Equal(
            187,
            PostgresSchemaMigrationCatalog.All.Count,
            "migration catalog entry count");
        var baseline = PostgresSchemaMigrationCatalog.All[0];
        Check.Equal(
            "20260723_000_legacy_schema_baseline",
            baseline.Id,
            "legacy database receives one explicit metadata baseline");
        Check.True(
            !baseline.Sql.Contains(
                "050_test_character",
                StringComparison.OrdinalIgnoreCase) &&
            !baseline.Sql.Contains(
                "character_base",
                StringComparison.OrdinalIgnoreCase) &&
            !baseline.Sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase),
            "baseline cannot replay legacy bootstrap or test-character mutations");
        Check.Throws<ArgumentException>(
            () => new PostgresSchemaMigration(
                "050_test_character_fixture",
                "legacy fixture",
                "SELECT 1;"),
            "legacy numbered script IDs cannot enter the forward-only catalog");
        Check.True(
            PostgresSchemaMigrationCatalog.All
                .Select(static migration => migration.Id)
                .SequenceEqual(ExpectedMigrationIds),
            "explicit migration catalog remains ordered and complete");
        Check.True(
            PostgresSchemaMigrationCatalog.All.All(migration =>
                !migration.Sql.Contains(
                    "test_character",
                    StringComparison.OrdinalIgnoreCase)),
            "production migration catalog cannot contain local character fixtures");
        var indexCleanup = PostgresSchemaMigrationCatalog.All.Single(
            migration =>
                migration.Id == "20260723_004_remove_redundant_indexes");
        Check.True(
            indexCleanup.Sql.Contains(
                "UNIQUE USING INDEX ux_accounts_username",
                StringComparison.Ordinal) &&
            indexCleanup.Sql.Contains(
                "WHERE conindid = username_index",
                StringComparison.Ordinal),
            "fresh and existing databases retain an authoritative username uniqueness constraint");
        var petEvidenceV3 = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260811_077_pet_durable_evidence_v3");
        Check.True(
            petEvidenceV3.Sql.Contains(
                "'pet_to_pet_merge'",
                StringComparison.Ordinal) &&
            petEvidenceV3.Sql.Contains(
                "'pet_rebirth'",
                StringComparison.Ordinal),
            "pet durable evidence exposes native Merge and Rebirth commands");
        var monsterCombat = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260814_093_monster_combat_authority");
        Check.Equal(
            "054B080ABAEEFCC3E88085CF5CF3C288D34E08E86BE3D90890C880D31F0E7803",
            monsterCombat.Checksum,
            "monster-combat authority migration checksum is pinned");
        var onlineAwardFoundation = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260821_105_online_award_foundation");
        Check.Equal(
            "D532139C093BEFFE15D176A094AA512BF80C997EC58CF0BCE4E354F44CAD89FF",
            onlineAwardFoundation.Checksum,
            "Online Award foundation migration checksum is pinned");
        var onlineAwardDialogue = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260821_106_online_award_dialogue_v7");
        Check.Equal(
            "748DA26AAFC6FDE85F914F2BB36F570A1AB4E0ABB38EC256A97D22959D8DFC84",
            onlineAwardDialogue.Checksum,
            "Online Award dialogue migration checksum is pinned");
        var warehouse = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260822_107_warehouse_foundation");
        Check.Equal(
            "6126E5629D6CBA2D165780A38317E881930E8FF469DF4C2570E50287DE2AF962",
            warehouse.Checksum,
            "warehouse foundation migration checksum is pinned");
        var warehouseDialogue = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260822_108_warehouse_dialogue_v8");
        Check.Equal(
            "7679146CFFA67221885A5D77C7CE593935712DDD182D76CAAD6B7C0EB8AFA5C6",
            warehouseDialogue.Checksum,
            "warehouse dialogue migration checksum is pinned");
        var instanceCallerDialogue = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260822_109_instance_caller_dialogue_v9");
        Check.Equal(
            "19FF256FBC00F9670D2A7E13E67BCDEA228B1171EBDA4AA559056A4807AC1E3A",
            instanceCallerDialogue.Checksum,
            "Instance Caller dialogue migration checksum is pinned");
        Check.True(
            instanceCallerDialogue.Sql.Contains(
                "behavior BETWEEN 1 AND 11",
                StringComparison.Ordinal),
            "Instance Caller migration extends only the finite behavior domain");
        var warehouseNineBox = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260824_110_warehouse_nine_box_capacity");
        Check.Equal(
            "93671839CBA49565A2B360BC797B7F39B61D9AEF9F1C7C5F3FFAED519B327548",
            warehouseNineBox.Checksum,
            "nine-box warehouse migration checksum is pinned");
        Check.True(
            warehouseNineBox.Sql.Contains(
                "warehouse_capacity BETWEEN 40 AND 360",
                StringComparison.Ordinal) &&
            warehouseNineBox.Sql.Contains(
                "slot_index BETWEEN 0 AND 359",
                StringComparison.Ordinal) &&
            warehouseNineBox.Sql.Contains(
                "level_count BETWEEN 1 AND 9",
                StringComparison.Ordinal) &&
            warehouseNineBox.Sql.Contains(
                "FROM generate_series(1, NEW.level_count)",
                StringComparison.Ordinal) &&
            warehouseNineBox.Sql.Contains(
                "publication_version = 2",
                StringComparison.Ordinal),
            "nine-box limits are durable while expansion values remain database-owned");
        var warehouseManagerKeyCap = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260824_111_warehouse_manager_storage_key_cap");
        Check.Equal(
            "30E405F8D88F28B34E148C1871DC689D7F5C8481AD6771EDB0201BC5779D41E3",
            warehouseManagerKeyCap.Checksum,
            "Warehouse Manager Storage Box Key cap migration checksum is pinned");
        Check.True(
            warehouseManagerKeyCap.Sql.Contains(
                "level_count, source, created_by)",
                StringComparison.Ordinal) &&
            warehouseManagerKeyCap.Sql.Contains(
                "(3, 160, 3, 4102)",
                StringComparison.Ordinal) &&
            !warehouseManagerKeyCap.Sql.Contains(
                "(3, 200,",
                StringComparison.Ordinal) &&
            warehouseManagerKeyCap.Sql.Contains(
                "publication_version = 3",
                StringComparison.Ordinal) &&
            warehouseManagerKeyCap.Sql.Contains(
                "AND revision = 2",
                StringComparison.Ordinal),
            "Warehouse Manager keys stop at SB4 without lowering the structural nine-box capacity");
        Check.True(
            warehouse.Sql.Contains(
                "warehouse_capacity IN (40, 80, 120, 160)",
                StringComparison.Ordinal) &&
            warehouse.Sql.Contains(
                "item_location IN (0, 1, 2, 3)",
                StringComparison.Ordinal) &&
            warehouse.Sql.Contains(
                "DEFERRABLE INITIALLY DEFERRED",
                StringComparison.Ordinal) &&
            warehouse.Sql.Contains(
                "warehouse_expansion_settlements",
                StringComparison.Ordinal),
            "warehouse capacity, item slots, and expansion evidence are durable");
        var medusaTitles = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260827_114_medusa_title_ownership");
        Check.True(
            medusaTitles.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS character_title_ownership",
                StringComparison.Ordinal) &&
            medusaTitles.Sql.Contains(
                "selected_title_id",
                StringComparison.Ordinal) &&
            medusaTitles.Sql.Contains(
                "awarded_title_id",
                StringComparison.Ordinal) &&
            medusaTitles.Sql.Contains(
                "hard_points >= 0",
                StringComparison.Ordinal) &&
            medusaTitles.Sql.Contains(
                "camp IN (0, 1)",
                StringComparison.Ordinal) &&
            medusaTitles.Sql.Contains(
                "title = 6 AND title_id = 5152",
                StringComparison.Ordinal),
            "Medusa titles have durable ownership, selected projection, replay evidence, and title-only Mythic settlement");
        var medusaCaptureRarity =
            PostgresSchemaMigrationCatalog.All.Single(
                migration => migration.Id ==
                    "20260827_115_medusa_pet_capture_rarity");
        Check.True(
            medusaCaptureRarity.Sql.Contains(
                "difficulty IN (2, 3)",
                StringComparison.Ordinal) &&
            medusaCaptureRarity.Sql.Contains(
                "weight_basis_points",
                StringComparison.Ordinal) &&
            medusaCaptureRarity.Sql.Contains(
                "current_total <> 10000",
                StringComparison.Ordinal) &&
            medusaCaptureRarity.Sql.Contains(
                "(2, 10150,  1, 2000)",
                StringComparison.Ordinal) &&
            medusaCaptureRarity.Sql.Contains(
                "(2, 10150, 14,   50)",
                StringComparison.Ordinal) &&
            medusaCaptureRarity.Sql.Contains(
                "(3, 10150,  1,  400)",
                StringComparison.Ordinal) &&
            medusaCaptureRarity.Sql.Contains(
                "(3, 10150, 14,  300)",
                StringComparison.Ordinal),
            "Medusa Rock Elf rarity is database-owned for Advanced and Mythic only");
        var medusaDailyEntryLimit =
            PostgresSchemaMigrationCatalog.All.Single(
                migration => migration.Id ==
                    "20260827_116_medusa_daily_entry_limit");
        Check.True(
            medusaDailyEntryLimit.Sql.Contains(
                "daily_entry_limit smallint NOT NULL",
                StringComparison.Ordinal) &&
            medusaDailyEntryLimit.Sql.Contains(
                "VALUES ('medusa', 3)",
                StringComparison.Ordinal) &&
            medusaDailyEntryLimit.Sql.Contains(
                "ADD CONSTRAINT medusa_daily_entries_pkey PRIMARY KEY",
                StringComparison.Ordinal) &&
            medusaDailyEntryLimit.Sql.Contains(
                "reservation_id);",
                StringComparison.Ordinal),
            "Medusa daily attempts are database-owned and allow repeated reservations up to the configured limit");
        var medusaRewardPolicy =
            PostgresSchemaMigrationCatalog.All.Single(
                migration => migration.Id ==
                    "20260827_117_medusa_reward_policy");
        Check.True(
            medusaRewardPolicy.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS medusa_reward_title_definitions",
                StringComparison.Ordinal) &&
            medusaRewardPolicy.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS medusa_completion_reward_rules",
                StringComparison.Ordinal) &&
            medusaRewardPolicy.Sql.Contains(
                "physical_attack_basis_points",
                StringComparison.Ordinal) &&
            medusaRewardPolicy.Sql.Contains(
                "(3, 2,  600, 3375, 6)",
                StringComparison.Ordinal) &&
            medusaRewardPolicy.Sql.Contains(
                "(3, 2, 2400, 2700, NULL)",
                StringComparison.Ordinal) &&
            medusaRewardPolicy.Sql.Contains(
                "fk_character_title_reward_definition",
                StringComparison.Ordinal),
            "Medusa points, title thresholds, client metadata, and title attributes are database-owned");
        var medusaMonsterContent =
            PostgresSchemaMigrationCatalog.All.Single(
                migration => migration.Id ==
                    "20260828_118_medusa_monster_content");
        Check.True(
            medusaMonsterContent.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS public.medusa_monster_rules",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS public.medusa_monster_loot_rules",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS public.monster_loot_pickup_claims",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.Contains(
                "CREATE TABLE IF NOT EXISTS public.monster_death_pet_experience",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.Contains(
                "('boss-stheno','stheno',200,1000,5000,1116)",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.Contains(
                "('boss-medusa',2,9916,10000,6,6)",
                StringComparison.Ordinal),
            "Medusa levels, HP, scores, movement, corpses, loot, and pet EXP are database-owned");
        var medusaLootSeedIndex = medusaMonsterContent.Sql.IndexOf(
            "('boss-medusa',2,9916,10000,6,6)",
            StringComparison.Ordinal);
        Check.True(
            medusaMonsterContent.Sql.Contains(
                "(9916, 'consume item', 'Rmaterial16', 'Punishment Dust'",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.Contains(
                "(9940, 'consume item', 'Material10', 'Accuracy Stone'",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.Contains(
                "(9941, 'consume item', 'Material11', 'Psychic Stone'",
                StringComparison.Ordinal) &&
            medusaMonsterContent.Sql.IndexOf(
                "(9916, 'consume item', 'Rmaterial16', 'Punishment Dust'",
                StringComparison.Ordinal) < medusaLootSeedIndex,
            "Medusa boss loot item templates are seeded before foreign-key-bound loot rules");
        var medusaExternalScore =
            PostgresSchemaMigrationCatalog.All.Single(
                migration => migration.Id ==
                    "20260828_119_medusa_external_score");
        Check.True(
            medusaExternalScore.Sql.Contains(
                "medusa_completion_rewards_final_score_check",
                StringComparison.Ordinal) &&
            medusaExternalScore.Sql.Contains(
                "CHECK (final_score >= 0)",
                StringComparison.Ordinal) &&
            medusaExternalScore.Sql.Contains(
                "CHECK (final_score >= 3000)",
                StringComparison.Ordinal),
            "external-style completion scores remain durable without rewriting the actual total");
        var medusaExternalHealth =
            PostgresSchemaMigrationCatalog.All.Single(
                migration => migration.Id ==
                    "20260828_120_medusa_external_health");
        Check.True(
            medusaExternalHealth.Sql.Contains(
                "('normal-mud-crocodile',800000)",
                StringComparison.Ordinal) &&
            medusaExternalHealth.Sql.Contains(
                "('elite-priest-a-012',250000)",
                StringComparison.Ordinal) &&
            medusaExternalHealth.Sql.Contains(
                "('elite-gorgon-wizard',8000000)",
                StringComparison.Ordinal) &&
            medusaExternalHealth.Sql.Contains(
                "('boss-euryale',5000000)",
                StringComparison.Ordinal) &&
            medusaExternalHealth.Sql.Contains(
                "('boss-stheno',3000000)",
                StringComparison.Ordinal) &&
            medusaExternalHealth.Sql.Contains(
                "('boss-medusa',3500000)",
                StringComparison.Ordinal) &&
            medusaExternalHealth.Sql.Contains(
                "VALUES (1,1), (2,2), (3,5)",
                StringComparison.Ordinal),
            "captured Normal health applies exact 2x Advanced and 5x Mythic scaling");
        Check.True(
            monsterCombat.Sql.Contains(
                "ADD COLUMN attack_type smallint",
                StringComparison.Ordinal) &&
            monsterCombat.Sql.Contains(
                "attack_type IN (1, 2, 3)",
                StringComparison.Ordinal) &&
            monsterCombat.Sql.Contains(
                "ADD COLUMN map_mode smallint",
                StringComparison.Ordinal) &&
            monsterCombat.Sql.Contains(
                "map_mode BETWEEN 0 AND 5",
                StringComparison.Ordinal),
            "sealed gameplay content retains constrained monster attack and map-mode PvP authority");

        CheckMonsterRewardPolicyMigration();

        CheckDonatorTierMigration();

        var orderedSparse = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260830_122_outbox_ordered_sparse");
        Check.True(
            orderedSparse.Sql.Contains(
                "'ordered_sparse'",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "event.lease_token IS NOT NULL",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "position.inflight_event_id IS NOT NULL",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "current_setting('session_replication_role') <> 'origin'",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "'trg_outbox_events_guard'",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "'trg_outbox_positions_lease_consistency'",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "ck_outbox_events_sparse_consumer_policy",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "ck_outbox_positions_sparse_consumer_policy",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "DISABLE TRIGGER trg_outbox_events_guard",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "DISABLE TRIGGER trg_outbox_consumer_positions_guard",
                StringComparison.Ordinal) &&
            orderedSparse.Sql.Contains(
                "SET CONSTRAINTS ALL IMMEDIATE",
                StringComparison.Ordinal) &&
            !orderedSparse.Sql.Contains(
                "'replica'",
                StringComparison.Ordinal),
            "ordered-sparse conversion refuses active streams, preserves guards, and prevents policy regression");

        CheckTransporterDialogueCatalogMigration();
        CheckDuelArenaCatalogMigrations();

        var rosterConstraint = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20261001_218_atlantis_completion_roster_constraint");
        Check.True(
            rosterConstraint.Sql.Contains(
                "DROP CONSTRAINT IF EXISTS ck_atlantis_completion_rewards_characters_admitted",
                StringComparison.Ordinal) &&
            rosterConstraint.Sql.Contains(
                "CHECK (admitted_character_ids <@ character_ids)",
                StringComparison.Ordinal) &&
            // The reversed predicate 217 shipped is what refused a party
            // settlement; it must not come back.
            !rosterConstraint.Sql.Contains(
                "CHECK (character_ids <@ admitted_character_ids)",
                StringComparison.Ordinal),
            "the Atlantis roster constraint requires every admitted member on " +
            "the settlement roster instead of restricting the roster to the " +
            "registered party");
    }

}
