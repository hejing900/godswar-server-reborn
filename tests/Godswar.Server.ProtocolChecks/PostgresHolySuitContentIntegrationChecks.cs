using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresHolySuitContentIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL Holy Suit manifest-v6 publication";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException($"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var loaded = await PostgresItemTemplateContentBootstrapper.LoadAsync(
            connectionString);
        Check.True(
            loaded.Revision.ManifestVersion == 9 &&
            loaded.Revision.HolySuitTierCount == 8 &&
            loaded.Revision.HolySuitUpgradeCount == 70 &&
            loaded.Revision.HolySuitConsumableCount == 13 &&
            loaded.Revision.HolySuitPolicyCount == 1 &&
            loaded.HolySuit.IsAvailable &&
            loaded.HolySuit.ItemTemplates.Count == 13,
            "runtime pins one complete Holy Suit manifest-v6 catalog");
        Check.True(
            loaded.HolySuit.TryGetUpgrade(2, 1, out var silver) &&
            silver.RequiredItemExperience == 5_649_898 &&
            loaded.HolySuit.TryGetUpgrade(3, 2, out var gold) &&
            gold.RequiredItemExperience == 65_349_705,
            "runtime loads corrected EquipEffect upgrade costs");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await AssertPublishedRowsAsync(dataSource, loaded.Revision.Sha256);
        await AssertDurableStateAndPointRecomputationAsync(dataSource);
    }

    private static async Task AssertPublishedRowsAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                (SELECT count(*) FROM holy_suit_tier_content_definitions
                 WHERE revision = @revision),
                (SELECT count(*) FROM holy_suit_upgrade_content_definitions
                 WHERE revision = @revision),
                (SELECT count(*) FROM holy_suit_consumable_content_definitions
                 WHERE revision = @revision),
                (SELECT count(*)
                 FROM holy_suit_operation_policy_content_definitions
                 WHERE revision = @revision),
                (SELECT count(*) FROM official_holy_suit_upgrade_content),
                (SELECT daily_experience_per_player_level
                 FROM holy_suit_operation_policy_content_definitions
                 WHERE revision = @revision AND policy_key = 'alpha'),
                (SELECT daily_experience_per_player
                 FROM holy_suit_operation_policy_content_definitions
                 WHERE revision = @revision AND policy_key = 'alpha'),
                (SELECT per_operation_experience_maximum
                 FROM holy_suit_operation_policy_content_definitions
                 WHERE revision = @revision AND policy_key = 'alpha'),
                (SELECT realm_day_time_zone
                 FROM holy_suit_operation_policy_content_definitions
                 WHERE revision = @revision AND policy_key = 'alpha');
            """);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "Holy Suit publication query returns one row");
        Check.True(
            reader.GetInt64(0) == 8 &&
            reader.GetInt64(1) == 70 &&
            reader.GetInt64(2) == 13 &&
            reader.GetInt64(3) == 1 &&
            reader.GetInt64(4) == 70 &&
            reader.GetInt64(5) == 1_000_000 &&
            reader.GetInt64(6) == 2_000_000_000 &&
            reader.GetInt64(7) == 400_000_000 &&
            reader.GetString(8) == "Asia/Singapore",
            "sealed and official Holy Suit row counts match manifest v7");
    }

    private static async Task AssertDurableStateAndPointRecomputationAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        int accountId;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO accounts (username, password)
            VALUES (@username, 'integration-only')
            RETURNING id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("username", "holy_" + suffix);
            accountId = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        int characterId;
        await using (var command = new NpgsqlCommand("""
            INSERT INTO character_base (
                account_id, server_id, name, fighter_job_lv,
                holy_suit_points)
            VALUES (@accountId, 1, @name, 70, 99)
            RETURNING id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            command.Parameters.AddWithValue("name", "HS" + suffix);
            characterId = Convert.ToInt32(await command.ExecuteScalarAsync());
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id, holy_suit_code)
            VALUES
                (@characterId, 0, 0, 1000, 705),
                (@characterId, 0, 11, 1000, 210),
                (@characterId, 0, 12, 1000, 710),
                (@characterId, 1, 0, 1000, 710);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand("""
            INSERT INTO account_entitlements (
                account_id, entitlement_key, starts_at, source)
            VALUES (@accountId, 'battle_pass', now(), 'integration-test');

            INSERT INTO holy_suit_daily_exp_storage (
                account_id, realm_id, usage_day, stored_exp,
                operation_count)
            VALUES (@accountId, 1, (now() AT TIME ZONE 'UTC')::date,
                    100000000, 1);
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("accountId", accountId);
            await command.ExecuteNonQueryAsync();
        }

        await using (var command = new NpgsqlCommand(
            "SELECT public.recompute_character_holy_suit_points(@characterId);",
            connection,
            transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            Check.Equal(
                85, // Code 705 contributes 65 points; code 210 contributes 20.
                Convert.ToInt32(await command.ExecuteScalarAsync()),
                "explicit Holy Suit point recomputation returns derived total");
        }

        await using (var command = new NpgsqlCommand("""
            SELECT (SELECT holy_suit_points FROM character_base
                    WHERE id = @characterId),
                   (SELECT stored_exp FROM holy_suit_daily_exp_storage
                    WHERE account_id = @accountId AND realm_id = 1),
                   (SELECT count(*) FROM account_entitlements
                    WHERE account_id = @accountId
                      AND entitlement_key = 'battle_pass');
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("accountId", accountId);
            await using var reader = await command.ExecuteReaderAsync();
            Check.True(await reader.ReadAsync(),
                "Holy Suit durable-state query returns one row");
            Check.True(
                reader.GetInt32(0) == 85 &&
                reader.GetInt64(1) == 100_000_000 &&
                reader.GetInt64(2) == 1,
                "point recomputation excludes bag and non-regular slots");
        }

        await transaction.RollbackAsync();
    }
}
