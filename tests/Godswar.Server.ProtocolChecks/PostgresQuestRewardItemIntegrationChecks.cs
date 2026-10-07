using System.Text.RegularExpressions;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Rewards;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Proves the quest reward item grant against a real database: the item lands in
/// the kit bag bound and stacked like a picked-up drop, the claim makes the slot
/// payable once, and a second reward item persists alongside the first.
/// </summary>
/// <remarks>
/// Runs only against a disposable database, the same rule the capital-shop
/// integration check follows, and deletes its own account fixture afterwards -
/// deleting the account cascades through character_base into character_items and
/// the claim row.
/// </remarks>
internal static partial class PostgresQuestRewardItemIntegrationChecks
{
    public const string CheckName = "PostgreSQL quest reward item persistence";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b09_[a-z0-9_]{1,40}|b12_[a-z0-9_]{1,48})$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The revision the check publishes for itself when the database has no item
    /// publication yet. Revisions are shaped like content digests, so the fixture
    /// uses the SHA-256 of its own name.
    /// </summary>
    private const string FixtureContentRevision =
        "03152D2E6B953373B6203D69E22168C2720281AABF675A689DEEB62CDD14CA5A";

    /// <summary>
    /// A stackable bound item, so the reward lands bound with a stack of one.
    /// </summary>
    private const uint BoundRewardItemId = 3820;

    /// <summary>A single-slot unbound item, so the second reward needs its own slot.</summary>
    private const uint SecondRewardItemId = 3935;

    /// <summary>A stackable item, so three pickups have to leave a stack of three.</summary>
    private const uint PickupStackItemId = 4000;

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new CheckSkippedException(
                $"{CheckName} ({ConnectionStringVariable} is not set)");
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var databaseName = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(databaseName))
        {
            throw new CheckSkippedException(
                $"{CheckName} requires a disposable B03/B09/B12 database; " +
                $"received '{databaseName}'");
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var fixture = await CreateFixtureAsync(dataSource);
        await EnsureRewardItemContentAsync(dataSource);
        var store = new PostgresMonsterRewardExtrasStore(
            dataSource,
            static (_, _) => Task.FromResult<GameCharacter?>(null));
        try
        {
            await CheckQuestItemTransactionsAsync(dataSource, connectionString, fixture.AccountId, fixture.CharacterId);
            var revisionBefore = await ReadInventoryRevisionAsync(
                dataSource,
                fixture.CharacterId);

            // The bug this closes: the client pays a quest reward itself and the
            // server has to write it, or the next relog replaces the bag.
            var giftBag = await store.GrantQuestRewardItemAsync(
                fixture.AccountId,
                fixture.CharacterId,
                518u,
                0,
                BoundRewardItemId,
                1,
                ItemGrantAttributes.None);
            Check.True(
                giftBag.Status == QuestRewardItemGrantStatus.Added,
                "quest 518 slot 0 pays the newbie gift bag; status was " +
                $"{giftBag.Status}");

            var bag = await ReadBagAsync(dataSource, fixture.CharacterId);
            Check.True(
                bag.Count == 1 &&
                bag[0].ItemId == BoundRewardItemId &&
                bag[0].Bound == 1 &&
                bag[0].Stack == 1 &&
                bag[0].Slot == 0,
                "the reward lands in the kit bag, bound, exactly like a drop");

            var claim = await ReadClaimAsync(dataSource, fixture.CharacterId, 518, 0);
            Check.True(
                claim is { ItemId: (int)BoundRewardItemId, Quantity: 1 } &&
                claim.InventoryRevision == revisionBefore + 1,
                "the claim records the slot and the inventory revision it produced");

            // A relog replays the announcement, so the slot has to be payable
            // once and only once.
            var replay = await store.GrantQuestRewardItemAsync(
                fixture.AccountId,
                fixture.CharacterId,
                518u,
                0,
                BoundRewardItemId,
                1,
                ItemGrantAttributes.None);
            Check.True(
                replay.Status == QuestRewardItemGrantStatus.Duplicate,
                "the same reward slot cannot pay twice");
            bag = await ReadBagAsync(dataSource, fixture.CharacterId);
            Check.True(
                bag.Count == 1,
                "a replayed announcement adds no second item");

            // A second reward item has to persist next to the first: this is the
            // shape that used to leave only the first one in the bag.
            var longStaff = await store.GrantQuestRewardItemAsync(
                fixture.AccountId,
                fixture.CharacterId,
                519u,
                3,
                SecondRewardItemId,
                1,
                new ItemGrantAttributes(
                    10, 12, 24, 5, 133, null, 90, null, null, null, null, null));
            Check.True(
                longStaff.Status == QuestRewardItemGrantStatus.Added,
                "quest 519 slot 3 pays the long staff; status was " +
                $"{longStaff.Status}");
            bag = await ReadBagAsync(dataSource, fixture.CharacterId);
            Check.True(
                bag.Count == 2 &&
                bag[1].ItemId == SecondRewardItemId &&
                bag[1].Slot == 1,
                "both reward items persist, in separate slots");

            // The configured attributes have to reach the item instance, or the
            // reward is the bare item every grant used to produce.
            Check.True(
                bag[1].Quality == 10 &&
                bag[1].Grade == 12 &&
                bag[1].Attribute1 == 24 &&
                bag[1].AttributeLevel1 == 5 &&
                bag[1].Attribute2 == 133 &&
                bag[1].Attribute3 == 90,
                "the configured quality, grade and attributes land on the item");

            // An unconfigured slot has to be NULL and never 0: attribute id 0 is
            // the real AttackA, so a zeroed slot draws one phantom "physical
            // attack I" line on every item that configured fewer than five.
            Check.True(
                bag[1].Attribute4 is null &&
                bag[1].AttributeLevel4 is null &&
                bag[1].Attribute5 is null &&
                bag[1].AttributeLevel5 is null,
                "a slot the source left unconfigured is empty, not attribute 0");

            var revisionAfter = await ReadInventoryRevisionAsync(
                dataSource,
                fixture.CharacterId);
            Check.Equal(
                revisionBefore + 2,
                revisionAfter,
                "two grants advance the inventory revision exactly twice");

            var unsupported = await store.GrantQuestRewardItemAsync(
                fixture.AccountId,
                fixture.CharacterId,
                520u,
                0,
                999_999u,
                1,
                ItemGrantAttributes.None);
            Check.True(
                unsupported.Status == QuestRewardItemGrantStatus.Unsupported,
                "an item outside the pinned content is refused");
            bag = await ReadBagAsync(dataSource, fixture.CharacterId);
            Check.True(
                bag.Count == 2,
                "a refused item writes nothing");

            await CheckConfiguredSlotAttributesReachTheItemAsync(
                dataSource,
                store,
                fixture);

            await CheckRepeatedGroundPickupPersistsAsync(
                dataSource,
                store,
                fixture);

            // A daily quest's next completion pays again; retransmitting that
            // completion's announcement still pays only once.
            var firstCompletion = Guid.NewGuid();
            var secondCompletion = Guid.NewGuid();
            foreach (var completion in new[] { firstCompletion, secondCompletion })
            {
                var paid = await store.GrantQuestRewardItemAsync(
                    fixture.AccountId, fixture.CharacterId, 69u, 0,
                    BoundRewardItemId, 1, ItemGrantAttributes.None,
                    rewardClaimId: completion);
                Check.True(paid.Status == QuestRewardItemGrantStatus.Added,
                    "each daily completion can receive its reward item");
                var duplicate = await store.GrantQuestRewardItemAsync(
                    fixture.AccountId, fixture.CharacterId, 69u, 0,
                    BoundRewardItemId, 1, ItemGrantAttributes.None,
                    rewardClaimId: completion);
                Check.True(duplicate.Status == QuestRewardItemGrantStatus.Duplicate,
                    "a duplicate announcement does not pay that completion twice");
            }

            await using var questStore = new PostgresGameStore(connectionString);
            await questStore.SaveCharacterQuestStateAsync(
                fixture.AccountId, fixture.CharacterId,
                [new CharacterQuest { QuestId = 209u, Progress = 4 }],
                [209u, 535u, 535u]);
            await using (var saved = dataSource.CreateCommand("""
                SELECT state, progress FROM public.character_quests
                WHERE character_id = @characterId AND quest_id = 209;
                """))
            {
                saved.Parameters.AddWithValue("characterId", fixture.CharacterId);
                await using var reader = await saved.ExecuteReaderAsync();
                Check.True(await reader.ReadAsync() &&
                    reader.GetInt16(0) == CharacterQuestStatus.InProgress && reader.GetInt32(1) == 4,
                    "daily reacceptance persists active progress despite historical completion");
                Check.True(!await reader.ReadAsync(), "quest persistence emits one row per quest");
            }

            var quotaDay = QuestDailyState.Today();
            await questStore.SaveQuestDailyCompletionsAsync(
                fixture.AccountId, fixture.CharacterId,
                new Dictionary<uint, GameCharacter.QuestDailyCount>
                {
                    [209u] = new(quotaDay, 1)
                });
            var counts = await questStore.LoadQuestDailyCompletionsAsync(fixture.CharacterId);
            Check.True(counts.TryGetValue(209u, out var count) && count.CompletedOn(quotaDay) == 1,
                "a saved daily quota survives a fresh store read");
        }
        finally
        {
            await DeleteFixtureAsync(
                dataSource,
                fixture.AccountId,
                fixture.CharacterId);
        }
    }

    /// <summary>
    /// Publishes the three item ids this check grants, then proves the reward
    /// policy can read them.
    /// </summary>
    /// <remarks>
    /// A disposable database carries the migration templates but no publication,
    /// and the reward policy reads the published view - without this the grant
    /// answers <c>Unsupported</c> for every item. The publication is a minimal
    /// manifest version 1 revision: three definitions and nothing else, which is
    /// the one shape <c>validate_item_template_content_publication</c> accepts
    /// without the reviewed policy tables the real publication also carries.
    /// <para>
    /// The rows are deliberately not removed afterwards: the schema refuses to
    /// delete a publication or rewrite a published definition, and the check only
    /// runs against a disposable database that is dropped with it.
    /// </para>
    /// </remarks>
    private static async Task EnsureRewardItemContentAsync(
        NpgsqlDataSource dataSource)
    {
        var existingRevision = await ReadPublishedRevisionAsync(dataSource);
        if (existingRevision is null)
        {
            await PublishFixtureItemContentAsync(dataSource);
        }

        await using var command = dataSource.CreateCommand(
            """
            SELECT id FROM public.official_item_template_content
            WHERE id = ANY(@itemIds);
            """);
        command.Parameters.AddWithValue(
            "itemIds",
            new[]
            {
                checked((int)BoundRewardItemId),
                checked((int)SecondRewardItemId),
                checked((int)PickupStackItemId)
            });
        var published = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            published.Add(reader.GetInt32(0));
        }

        Check.True(
            published.Count == 3,
            "the published item content carries the three fixture items; found " +
            $"{published.Count}");
    }

    private static async Task<string?> ReadPublishedRevisionAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT revision FROM public.item_template_content_publication " +
            "WHERE family = 'items';");
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task PublishFixtureItemContentAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var revision = new NpgsqlCommand(
            """
            INSERT INTO public.item_template_content_revisions (
                revision, entry_count, source, manifest_version,
                attribute_count, equipment_rank_count, holy_suit_effect_count,
                material_policy_count, material_recipe_count,
                holy_suit_tier_count, holy_suit_upgrade_count,
                holy_suit_consumable_count, holy_suit_policy_count)
            VALUES (
                @revision, 3, 'quest-reward-item-check', 1,
                0, 0, 0, 0, 0, 0, 0, 0, 0);
            """,
            connection,
            transaction))
        {
            revision.Parameters.AddWithValue("revision", FixtureContentRevision);
            await revision.ExecuteNonQueryAsync();
        }

        await using (var definitions = new NpgsqlCommand(
            """
            INSERT INTO public.item_template_content_definitions (
                revision, id, kind, name_key, display_name, equipment_slot,
                class_ids, min_level, max_level, hand, skill_flag, texture,
                icon, stats)
            VALUES
                (@revision, 3820, 'consume item', '3820', '3820', 0,
                 ARRAY[0,1,2,3]::smallint[], 1, 200, 0, 0, '', '',
                 '{"ID":"3820","Overlap":"99","BindType":"1"}'::jsonb),
                (@revision, 3935, 'consume item', '3935', '3935', 0,
                 ARRAY[0,1,2,3]::smallint[], 1, 200, 0, 0, '', '',
                 '{"ID":"3935","Overlap":"1"}'::jsonb),
                (@revision, 4000, 'consume item', '4000', '4000', 0,
                 ARRAY[0,1,2,3]::smallint[], 1, 200, 0, 0, '', '',
                 '{"ID":"4000","Overlap":"99"}'::jsonb);
            """,
            connection,
            transaction))
        {
            definitions.Parameters.AddWithValue(
                "revision",
                FixtureContentRevision);
            await definitions.ExecuteNonQueryAsync();
        }

        // Publishing seals the revision and makes the view readable.
        await using (var publication = new NpgsqlCommand(
            """
            INSERT INTO public.item_template_content_publication (
                family, revision)
            VALUES ('items', @revision);
            """,
            connection,
            transaction))
        {
            publication.Parameters.AddWithValue(
                "revision",
                FixtureContentRevision);
            await publication.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    /// <summary>
    /// The whole chain the GM tool feeds: a configured
    /// <c>quest_reward_slots</c> row has to reach the item instance through the
    /// content snapshot the server reads at runtime.
    /// </summary>
    /// <remarks>
    /// The GM tool writes the twelve columns, the snapshot reader turns them into
    /// <see cref="ItemGrantAttributes"/>, and the grant writes them onto the item.
    /// Asserting only the store would leave the middle link - the one that decides
    /// whether an operator's configuration ever reaches the game - untested.
    /// </remarks>
    private static async Task CheckConfiguredSlotAttributesReachTheItemAsync(
        NpgsqlDataSource dataSource,
        PostgresMonsterRewardExtrasStore store,
        QuestRewardFixture fixture)
    {
        // A quest id no content uses, so the fixture cannot shadow an override.
        const uint fixtureQuestId = 999_983u;
        const int fixtureSlotIndex = 2;
        await DeleteConfiguredSlotAsync(dataSource, fixtureQuestId);
        await using (var command = dataSource.CreateCommand(
            """
            INSERT INTO public.quest_reward_slots (
                quest_id, slot_index, item_id, item_quality, item_grade,
                attribute1, attribute_level1, attribute2, attribute_level2,
                attribute3, attribute_level3, attribute4, attribute_level4,
                attribute5, attribute_level5)
            VALUES (
                @questId, @slotIndex, @itemId, 10, 12,
                24, 5, 133, NULL, 90, NULL, NULL, NULL, NULL, NULL);
            """))
        {
            command.Parameters.AddWithValue(
                "questId",
                checked((int)fixtureQuestId));
            command.Parameters.AddWithValue(
                "slotIndex",
                checked((short)fixtureSlotIndex));
            command.Parameters.AddWithValue(
                "itemId",
                checked((int)SecondRewardItemId));
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            var snapshot = await new PostgresQuestRewardContentSnapshotReader(
                dataSource).ReadAsync();
            var hasSlots = snapshot.TryGetSlots(fixtureQuestId, out var slots);
            Check.True(
                hasSlots,
                "the content snapshot carries the configured quest");
            var configured = slots.SingleOrDefault(slot =>
                slot.SlotIndex == fixtureSlotIndex);
            Check.True(
                configured is not null,
                "the content snapshot carries the configured reward slot");
            Check.True(
                configured is
                {
                    ItemId: SecondRewardItemId,
                    Attributes:
                    {
                        Quality: 10,
                        Grade: 12,
                        Attribute1: 24,
                        AttributeLevel1: 5,
                        Attribute2: 133,
                        AttributeLevel2: null,
                        Attribute3: 90
                    }
                },
                "the snapshot turns the configured columns into item attributes");

            var granted = await store.GrantQuestRewardItemAsync(
                fixture.AccountId,
                fixture.CharacterId,
                fixtureQuestId,
                fixtureSlotIndex,
                configured!.ItemId,
                1,
                configured.Attributes);
            Check.True(
                granted.Status == QuestRewardItemGrantStatus.Added,
                "the configured slot pays its item; status was " +
                $"{granted.Status}");

            var row = (await ReadBagAsync(dataSource, fixture.CharacterId))
                .SingleOrDefault(item => item.Slot == fixtureSlotIndex);
            Check.True(
                row is
                {
                    ItemId: checked((int)SecondRewardItemId),
                    Quality: 10,
                    Grade: 12,
                    Attribute1: 24,
                    AttributeLevel1: 5,
                    Attribute2: 133,
                    Attribute3: 90
                },
                "what the GM configured is what the character keeps");
        }
        finally
        {
            await DeleteConfiguredSlotAsync(dataSource, fixtureQuestId);
        }
    }

    private static async Task DeleteConfiguredSlotAsync(
        NpgsqlDataSource dataSource,
        uint questId)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM public.quest_reward_slots WHERE quest_id = @questId;");
        command.Parameters.AddWithValue("questId", checked((int)questId));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The monster-loot half of the same complaint: a second pickup of the very
    /// same item - the ordinary case for anything stackable, and the one the
    /// handler used to misread as a bag-to-equipment move and drop - has to land
    /// in the kit bag next to the first, from a different corpse and from a
    /// second drop of one corpse.
    /// </summary>
    private static async Task CheckRepeatedGroundPickupPersistsAsync(
        NpgsqlDataSource dataSource,
        PostgresMonsterRewardExtrasStore store,
        QuestRewardFixture fixture)
    {
        var bagBefore = await ReadBagAsync(dataSource, fixture.CharacterId);
        var firstKill = new Guid("11111111-1111-4111-8111-111111111111");
        var secondKill = new Guid("22222222-2222-4222-8222-222222222222");

        var first = await store.PickupMonsterLootAsync(
            fixture.AccountId,
            fixture.CharacterId,
            firstKill,
            0,
            PickupStackItemId,
            1);
        Check.True(
            first.Status == MonsterLootPickupStatus.Added,
            "the first drop of a stackable item persists");

        var second = await store.PickupMonsterLootAsync(
            fixture.AccountId,
            fixture.CharacterId,
            secondKill,
            0,
            PickupStackItemId,
            1);
        Check.True(
            second.Status == MonsterLootPickupStatus.Added,
            "a second pickup of the same item persists");

        var bagAfter = await ReadBagAsync(dataSource, fixture.CharacterId);
        var stack = bagAfter.SingleOrDefault(row => row.ItemId == PickupStackItemId);
        Check.True(
            stack is not null && stack.Stack == 2,
            "the two pickups share one stack of two in the kit bag");
        Check.True(
            bagAfter.Count == bagBefore.Count + 1,
            "the repeated pickup adds one row, not two");

        var replay = await store.PickupMonsterLootAsync(
            fixture.AccountId,
            fixture.CharacterId,
            firstKill,
            0,
            PickupStackItemId,
            1);
        Check.True(
            replay.Status == MonsterLootPickupStatus.Duplicate,
            "replaying one kill's drop pays it once");
        var bagReplayed = await ReadBagAsync(dataSource, fixture.CharacterId);
        Check.True(
            bagReplayed.Single(row => row.ItemId == PickupStackItemId).Stack == 2,
            "a replayed pickup does not stack a third time");

        var secondDropOfOneKill = await store.PickupMonsterLootAsync(
            fixture.AccountId,
            fixture.CharacterId,
            secondKill,
            1,
            PickupStackItemId,
            1);
        Check.True(
            secondDropOfOneKill.Status == MonsterLootPickupStatus.Added,
            "the same corpse's second drop is its own claim");
        var bagFinal = await ReadBagAsync(dataSource, fixture.CharacterId);
        Check.True(
            bagFinal.Single(row => row.ItemId == PickupStackItemId).Stack == 3,
            "three pickups of one item leave a stack of three");

        // A drop with no configured attributes must not carry a zeroed slot
        // either: zero is the real AttackA, so it would draw a phantom line on
        // every picked-up item exactly as it did on every reward.
        var drop = bagFinal.Single(row => row.ItemId == PickupStackItemId);
        Check.True(
            drop.Attribute1 is null &&
            drop.Attribute2 is null &&
            drop.Attribute3 is null &&
            drop.Attribute4 is null &&
            drop.Attribute5 is null &&
            drop.AttributeLevel1 is null &&
            drop.AttributeLevel5 is null,
            "a drop that configured no attributes keeps every slot empty");
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return await command.ExecuteScalarAsync() as string ?? string.Empty;
    }

    private static async Task<QuestRewardFixture> CreateFixtureAsync(
        NpgsqlDataSource dataSource)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue(
                "username",
                $"quest_reward_{token}");
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The quest reward fixture account has no identity."));
        }

        int realmId;
        await using (var realm = new NpgsqlCommand(
            "SELECT id FROM public.server ORDER BY id LIMIT 1;",
            connection,
            transaction))
        {
            realmId = Convert.ToInt32(
                await realm.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The quest reward fixture has no realm."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id, server_id, name, camp, profession,
                fighter_job_lv, "Money", "Stone", "BindingGold",
                wallet_revision, inventory_revision)
            VALUES (
                @accountId, @realmId, @name, 1, 0,
                80, 0, 0, 0, 0, 0)
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("realmId", realmId);
            character.Parameters.AddWithValue("name", $"Quest{token}");
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The quest reward fixture character has no identity."));
        }

        // Every inventory-ledger write carries the economy baseline of the
        // character, so the fixture needs one before the first grant - exactly as
        // a created character has one.
        await using (var baseline = new NpgsqlCommand(
            """
            INSERT INTO public.character_economy_baseline (
                character_id, account_id, wallet_revision, inventory_revision,
                silver, gold, item_count, baseline_source, binding_gold)
            VALUES (
                @characterId, @accountId, 0, 0,
                0, 0, 0, 'quest-reward-item-check', 0);
            """,
            connection,
            transaction))
        {
            baseline.Parameters.AddWithValue("characterId", characterId);
            baseline.Parameters.AddWithValue("accountId", accountId);
            await baseline.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new QuestRewardFixture(accountId, characterId);
    }

    /// <summary>
    /// Removes the fixture and everything the grants it made left behind.
    /// </summary>
    /// <remarks>
    /// Deleting the account cascades through <c>character_base</c> into
    /// <c>character_items</c>, the economy baseline and the reward claims, but a
    /// ground-loot claim points at the account directly and blocks the delete, so
    /// the pickup claims go first.
    /// </remarks>
    private static async Task DeleteFixtureAsync(
        NpgsqlDataSource dataSource,
        int accountId,
        int characterId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var claims = new NpgsqlCommand(
            """
            DELETE FROM public.monster_loot_pickup_claims
            WHERE account_id = @accountId OR character_id = @characterId;
            """,
            connection,
            transaction))
        {
            claims.Parameters.AddWithValue("accountId", accountId);
            claims.Parameters.AddWithValue("characterId", characterId);
            await claims.ExecuteNonQueryAsync();
        }

        await using (var account = new NpgsqlCommand(
            "DELETE FROM public.accounts WHERE id = @accountId;",
            connection,
            transaction))
        {
            account.Parameters.AddWithValue("accountId", accountId);
            await account.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task<long> ReadInventoryRevisionAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT inventory_revision FROM public.character_base WHERE id = @characterId;");
        command.Parameters.AddWithValue("characterId", characterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    private static async Task<IReadOnlyList<QuestRewardBagRow>> ReadBagAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT slot_index, prop_id, bound, stack,
                   item_quality, item_grade,
                   attribute1, attribute_level1,
                   attribute2, attribute_level2,
                   attribute3, attribute_level3,
                   attribute4, attribute_level4,
                   attribute5, attribute_level5
            FROM public.character_items
            WHERE user_id = @characterId AND item_location = 1
            ORDER BY slot_index;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        var rows = new List<QuestRewardBagRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new(
                reader.GetInt16(0),
                reader.GetInt32(1),
                reader.GetInt16(2),
                reader.GetInt16(3),
                reader.GetInt16(4),
                reader.GetInt16(5),
                Optional(reader, 6),
                Optional(reader, 7),
                Optional(reader, 8),
                Optional(reader, 9),
                Optional(reader, 10),
                Optional(reader, 11),
                Optional(reader, 12),
                Optional(reader, 13),
                Optional(reader, 14),
                Optional(reader, 15)));
        }

        return rows;
    }

    /// <summary>An attribute column, where <c>NULL</c> is "no attribute".</summary>
    private static short? Optional(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);

    private static async Task<QuestRewardClaimRow?> ReadClaimAsync(
        NpgsqlDataSource dataSource,
        int characterId,
        int questId,
        int slotIndex)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT item_id, quantity, inventory_revision
            FROM public.quest_reward_item_claims
            WHERE character_id = @characterId
              AND quest_id = @questId
              AND slot_index = @slotIndex;
            """);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("questId", checked((int)questId));
        command.Parameters.AddWithValue("slotIndex", checked((short)slotIndex));
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new QuestRewardClaimRow(
                reader.GetInt32(0),
                reader.GetInt16(1),
                reader.GetInt64(2))
            : null;
    }

    private sealed record QuestRewardFixture(int AccountId, int CharacterId);

    private sealed record QuestRewardBagRow(
        short Slot,
        int ItemId,
        short Bound,
        short Stack,
        short Quality = 1,
        short Grade = 1,
        short? Attribute1 = null,
        short? AttributeLevel1 = null,
        short? Attribute2 = null,
        short? AttributeLevel2 = null,
        short? Attribute3 = null,
        short? AttributeLevel3 = null,
        short? Attribute4 = null,
        short? AttributeLevel4 = null,
        short? Attribute5 = null,
        short? AttributeLevel5 = null);

    private sealed record QuestRewardClaimRow(
        int ItemId,
        short Quantity,
        long InventoryRevision);
}
