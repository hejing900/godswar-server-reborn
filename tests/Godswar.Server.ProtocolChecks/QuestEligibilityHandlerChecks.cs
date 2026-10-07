using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class QuestEligibilityHandlerChecks
{
    public const string CheckName = "Quest handler enforces eligibility and daily reacceptance";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync()
    {
        // Exercise the actual acceptance method, rather than only the lookup predicate.
        foreach (var scenario in new[] { "underlevel", "outgrown", "wrongcamp", "quota", "completed", "locked" })
        {
            var character = new GameCharacter { Id = 73, AccountId = 72, Camp = GameDefaults.SpartaCamp, Level = 33 };
            uint questId = 209;
            switch (scenario)
            {
                case "underlevel": character.Level = 27; break;
                case "outgrown": character.Level = 43; break;
                case "wrongcamp": questId = 1209; break;
                case "quota": character.RecordQuestCompletion(209, QuestDailyState.Today()); break;
                case "completed": questId = 535; character.QuestCompletedIds = [535]; break;
                case "locked": questId = 519; break;
            }

            var fixture = CreateFixture(character);
            await InvokeAsync(fixture.Handler, "AcceptQuestAsync", questId, CancellationToken.None, true);
            Check.Equal(0, character.Quests.Count, $"acceptance rejects {scenario}");
            Check.Equal(0, fixture.Transport.WriteCount, $"rejected {scenario} sends no acceptance frames");
            Check.Equal(0, fixture.Store.Saves, $"rejected {scenario} writes no quest state");
            await fixture.Session.DisposeAsync();
        }

        var daily = new GameCharacter
        {
            Id = 73, AccountId = 72, Camp = GameDefaults.SpartaCamp, Level = 28,
            CurrentMap = 0, QuestCompletedIds = [209]
        };
        daily.RecordQuestCompletion(209, QuestDailyState.Today().AddDays(-1));
        var accepted = CreateFixture(daily);
        await InvokeAsync(accepted.Handler, "AcceptQuestAsync", 209u, CancellationToken.None, true);
        Check.True(daily.Quests.Any(quest => quest.QuestId == 209),
            "a completed daily quest is reaccepted at the five-level lead boundary");
        Check.Equal(1, accepted.Store.Saves, "daily reacceptance is persisted");
        Check.True(accepted.Transport.WriteCount > 0, "daily reacceptance returns real acceptance frames");
        Check.True(accepted.Handler.AcceptableQuests().Select(step => step.QuestId)
            .SequenceEqual(GameClientHandler.AcceptableQuestIds(daily)),
            "NPC menus and lookup use the same character eligibility rules");

        var remoteStep = StarterQuestChain.Find(209)!.Value;
        var remoteId = (uint)(await InvokeAsyncResult(accepted.Handler,
            "ResolveQuestNpcIdForSnapshotAsync", remoteStep.ResponderKey, CancellationToken.None))!;
        Check.True(remoteId != 0, "a cross-map responder resolves from its published source map");

        var refreshed = new GameCharacter
        {
            Id = daily.Id, AccountId = daily.AccountId,
            RealmId = daily.RealmId, Camp = daily.Camp
        };
        Invoke(accepted.Handler, "InstallUpdatedCharacter", refreshed);
        Check.Equal(1, refreshed.QuestCompletionsOn(209, QuestDailyState.Today().AddDays(-1)),
            "equipment/bag refresh preserves the quota counts");
        Check.True(refreshed.Quests.Any(quest => quest.QuestId == 209), "refresh preserves the reaccepted quest");

        var skippedBranches = Enumerable.Range(518, 34)
            .Where(id => id is not (535 or 536)).Select(id => (uint)id).ToArray();
        refreshed.Level = 33;
        refreshed.QuestCompletedIds = skippedBranches;
        Check.Equal(559u, ((StarterQuestChain.Step)Invoke(accepted.Handler, "NextMainLineQuest", 551u)!).QuestId,
            "automatic follow-up prefers level-32 main line over optional branches");
        refreshed.QuestCompletedIds = [.. skippedBranches, 559];
        Check.Equal(560u, ((StarterQuestChain.Step)Invoke(accepted.Handler, "NextMainLineQuest", 559u)!).QuestId,
            "unfinished branches do not pull the follow-up back from level 33");

        // Unaccepted dialogue quests used to reach the reward store with zero progress.
        var handIn = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(handIn, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(handIn.AsSpan(2), 10084);
        BinaryPrimitives.WriteUInt32LittleEndian(handIn.AsSpan(8), 518);
        await InvokeAsync(accepted.Handler, "HandleQuestHandInAsync", new GamePacket(handIn), CancellationToken.None);
        Check.Equal(0, accepted.Store.PayoutAttempts, "a quest that is not carried cannot be rewarded");

        try
        {
            QuestRewardContentCatalog.Install(new QuestRewardContentSnapshot(
                [new QuestRewardSlotOverride(207u, 0, 3876u, ItemGrantAttributes.None)], []));
            Invoke(accepted.Handler, "OfferQuestRewardItems", 207u);
            var firstClaim = ReadRewardClaim(accepted.Handler);
            Check.True(firstClaim.HasValue && firstClaim != Guid.Empty,
                "a daily item offer names this completion");
            Check.True(firstClaim == ReadRewardClaim(accepted.Handler),
                "replaying an announcement uses the same pending completion identity");
            Invoke(accepted.Handler, "OfferQuestRewardItems", 207u);
            Check.True(firstClaim != ReadRewardClaim(accepted.Handler),
                "a later daily completion gets a distinct reward identity");

            QuestRewardContentCatalog.Install(new QuestRewardContentSnapshot(
                [new QuestRewardSlotOverride(518u, 0, 9999u, ItemGrantAttributes.None)],
                [new QuestRewardValueOverride(518u, 0, 0, 0, 0)]));
            Invoke(accepted.Handler, "OfferQuestRewardItems", 518u);
            var offer = typeof(GameClientHandler).GetField("_pendingQuestRewardOffer", PrivateInstance)!
                .GetValue(accepted.Handler)!;
            var items = (IReadOnlyList<QuestRewardItem>)offer.GetType().GetProperty("Items")!.GetValue(offer)!;
            Check.True(items.Count == 1 && items[0].ItemId == 9999,
                "the actual reward offer contains GM items without original-item fallback");
            Check.True(QuestRewardContentCatalog.Current.Resolve(518u, new QuestRewardPayout(200, 2, 3, 4))
                == new QuestRewardPayout(0, 0, 0, 0), "a zero GM payout is an override, not a missing value");

            Invoke(accepted.Handler, "OfferQuestRewardItems", 518u);
            Check.True(Guid.Empty == ReadRewardClaim(accepted.Handler),
                "one-time main-line reward keeps its lifetime claim identity");
        }
        finally
        {
            QuestRewardContentCatalog.Install(QuestRewardContentSnapshot.Empty);
        }
        await accepted.Session.DisposeAsync();

        await CheckIndependentHandInFollowUpsAsync();
        await CheckExactLevelNpcRefreshAsync();
        await CheckCarryLimitAndDeleteAsync();

        // Capture what the actual hand-in sends to the experience and currency
        // stores, stopping before state changes or reward acknowledgement.
        foreach (var gmOverride in new[] { true, false })
        {
            var payoutFixture = CreateFixture(new GameCharacter
            {
                Id = 73, AccountId = 72, Camp = GameDefaults.SpartaCamp, Level = 3,
                Quests = [new CharacterQuest { QuestId = 518 }]
            });
            payoutFixture.Store.ProbeReward = true;
            try
            {
                QuestRewardContentCatalog.Install(gmOverride
                    ? new QuestRewardContentSnapshot([], [new QuestRewardValueOverride(518, 777, 9, 11, 13)])
                    : QuestRewardContentSnapshot.Empty);
                try
                {
                    await InvokeAsync(payoutFixture.Handler, "HandleQuestHandInAsync",
                        new GamePacket(handIn), CancellationToken.None);
                    throw new InvalidOperationException("The hand-in did not reach the currency probe.");
                }
                catch (OperationCanceledException)
                {
                    // The fixture stops exactly after observing the currency arguments.
                }

                Check.Equal(gmOverride ? 777 : 200, payoutFixture.Store.RewardExperience,
                    "hand-in uses GM experience first, otherwise original experience");
                Check.Equal(gmOverride ? 9 : 2, payoutFixture.Store.RewardTalent,
                    "hand-in uses GM talent first, otherwise original talent");
                Check.Equal(gmOverride ? 11 : 0, payoutFixture.Store.RewardSilver,
                    "hand-in uses GM silver first, otherwise original silver");
                Check.Equal(gmOverride ? 13 : 0, payoutFixture.Store.RewardGold,
                    "hand-in uses GM gold first, otherwise original gold");
            }
            finally
            {
                QuestRewardContentCatalog.Install(QuestRewardContentSnapshot.Empty);
                await payoutFixture.Session.DisposeAsync();
            }
        }
    }

    private static async Task CheckCarryLimitAndDeleteAsync()
    {
        foreach (var offset in new[] { 0u, 1000u })
        {
            foreach (var baseId in new[] { 519u, 535u, 573u, 371u, 581u, 328u, 345u })
            {
                var id = baseId + offset;
                var step = StarterQuestChain.Find(id)!.Value;
                var character = new GameCharacter
                {
                    Id = 73, AccountId = 72, Level = 140,
                    Camp = offset == 0 ? GameDefaults.SpartaCamp : GameDefaults.AthensCamp,
                    CurrentMap = checked((byte)step.ResponderMapId),
                    QuestCompletedIds = [518u + offset],
                    Quests = [new CharacterQuest { QuestId = id, Progress = 3 },
                        new CharacterQuest { QuestId = 602u + offset, Progress = 7 }]
                };
                character.RecordQuestCompletion(581u + offset, QuestDailyState.Today().AddDays(-1));
                var fixture = CreateFixture(character);
                var request = new byte[20];
                BinaryPrimitives.WriteUInt16LittleEndian(request, 20);
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), Opcodes.QuestSceneQuery);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 0x001af720);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), id);
                try
                {
                    await InvokeAsync(fixture.Handler, "HandleQuestSceneQueryAsync",
                        new GamePacket(request), CancellationToken.None);
                    Check.True(character.Quests.Count == 1 &&
                        character.Quests[0].QuestId == 602u + offset && character.Quests[0].Progress == 7,
                        "Del removes only the selected quest and preserves other progress");
                    Check.True(character.QuestCompletedIds.SequenceEqual(new[] { 518u + offset }) &&
                        character.QuestCompletionsOn(581u + offset, QuestDailyState.Today().AddDays(-1)) == 1,
                        "Del changes neither completed history nor daily quota");
                    var cipher = new PacketCipher();
                    var frames = fixture.Transport.WrittenChunks.Select(chunk =>
                    {
                        var frame = (byte[])chunk.Clone();
                        cipher.Transform(frame);
                        return frame;
                    }).ToArray();
                    var ack = frames.Single(frame =>
                        BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)) == Opcodes.QuestSceneQuery);
                    Check.True(BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)) == id &&
                        BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(12)) == id,
                        "Del acknowledges its selected ID despite another unlocked main line");
                    Check.True(frames.Any(frame =>
                        BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)) == Opcodes.PlayerAcceptedQuests),
                        "Del republishes the authoritative carried list");
                    await InvokeAsync(fixture.Handler, "HandleQuestSceneQueryAsync",
                        new GamePacket(request), CancellationToken.None);
                    Check.Equal(1, fixture.Store.Saves, "duplicate Del does not write or remove another quest");
                }
                finally { await fixture.Session.DisposeAsync(); }
            }

            var capped = new GameCharacter
            {
                Id = 73, AccountId = 72, Level = 140, CurrentMap = 9,
                Camp = offset == 0 ? GameDefaults.SpartaCamp : GameDefaults.AthensCamp,
                Quests = Enumerable.Range(581, 19)
                    .Select(id => new CharacterQuest { QuestId = (uint)id + offset }).ToList(),
                QuestCompletedIds = [573u + offset]
            };
            var capFixture = CreateFixture(capped);
            try
            {
                await InvokeAsync(capFixture.Handler, "AcceptQuestAsync", 574u + offset, CancellationToken.None, true);
                Check.Equal(20, capped.Quests.Count, "the twentieth quest can be accepted");
                var writes = capFixture.Transport.WriteCount;
                await InvokeAsync(capFixture.Handler, "AcceptQuestAsync", 371u + offset, CancellationToken.None, true);
                Check.True(capped.Quests.Count == 20 && capFixture.Store.Saves == 1 &&
                    capFixture.Transport.WriteCount == writes,
                    "the twenty-first quest has neither state nor successful reply");
            }
            finally { await capFixture.Session.DisposeAsync(); }
        }
    }

    private static async Task CheckIndependentHandInFollowUpsAsync()
    {
        foreach (var offset in new[] { 0u, 1000u })
        {
            // Keep starter quest 519 open to reproduce the original cross-route popup.
            foreach (var (id, level, nextId) in new[]
            {
                (573u, 130, 574u), (371u, 120, 372u),
                (376u, 124, 0u), (376u, 125, 377u), (377u, 125, 378u),
                (580u, 130, 0u), (382u, 125, 0u),
                (602u, 136, 0u), // accepted at 135; must remain handable after leveling
                (535u, 140, 0u), (207u, 140, 0u)
            })
            {
                var questId = id + offset;
                var step = StarterQuestChain.Find(questId)!.Value;
                var character = new GameCharacter
                {
                    Id = 73, AccountId = 72,
                    Camp = offset == 0 ? GameDefaults.SpartaCamp : GameDefaults.AthensCamp,
                    Level = level, CurrentMap = checked((byte)step.ResponderMapId),
                    QuestCompletedIds = [518u + offset],
                    Quests = [new CharacterQuest { QuestId = questId,
                        Progress = StarterQuestObjectives.For(questId)
                            .Select((objective, index) => Math.Min(objective.Required,
                                StarterQuestObjectives.CounterMaximum) * (1L << (index * StarterQuestObjectives.CounterBits)))
                            .Aggregate(0L, (progress, counter) => progress | counter) }]
                };
                var fixture = CreateFixture(character);
                fixture.Store.CompleteHandIn = true;
                var handIn = new byte[16];
                BinaryPrimitives.WriteUInt16LittleEndian(handIn, 16);
                BinaryPrimitives.WriteUInt16LittleEndian(handIn.AsSpan(2), 10084);
                BinaryPrimitives.WriteUInt32LittleEndian(handIn.AsSpan(8), questId);
                try
                {
                    await InvokeAsync(fixture.Handler, "HandleQuestHandInAsync", new GamePacket(handIn), CancellationToken.None);
                    Check.True(character.QuestCompletedIds.Contains(questId) && character.Quests.Count == 0,
                        "a carried quest completes even above its publishing level");
                    var offered = (uint)typeof(GameClientHandler).GetField("_offeredQuestId", PrivateInstance)!.GetValue(fixture.Handler)!;
                    Check.Equal(nextId == 0 ? 0u : nextId + offset, offered,
                        "actual hand-in opens only the completed route's follow-up");
                    var details = new List<uint>();
                    var cipher = new PacketCipher();
                    foreach (var packetBytes in fixture.Transport.WrittenChunks)
                    {
                        cipher.Transform(packetBytes);
                        if (packetBytes.Length >= 12 && BinaryPrimitives.ReadUInt16LittleEndian(packetBytes.AsSpan(2, 2)) ==
                            BinaryPrimitives.ReadUInt16LittleEndian(PacketBuilder.QuestNextDetail(1, 574).AsSpan(2, 2)))
                        {
                            details.Add(BinaryPrimitives.ReadUInt32LittleEndian(packetBytes.AsSpan(8, 4)));
                        }
                    }
                    Check.True(details.SequenceEqual(nextId == 0 ? [] : new[] { nextId + offset }),
                        "follow-up details never advertise the unrelated starter quest");
                    if (step.IsDaily)
                    {
                        Check.Equal(1, character.QuestCompletionsOn(questId, QuestDailyState.Today()), "daily main line retains its quota after hand-in");
                        Check.True(fixture.Store.DailySaves > 0, "daily completion is persisted");
                    }
                }
                finally
                {
                    await fixture.Session.DisposeAsync();
                }
            }
        }
    }

    private static async Task CheckExactLevelNpcRefreshAsync()
    {
        var character = new GameCharacter
        {
            Id = 73, AccountId = 72, Camp = GameDefaults.SpartaCamp,
            Level = 135, CurrentMap = 9,
            Quests = [new CharacterQuest { QuestId = 602 }]
        };
        var fixture = CreateFixture(character);
        try
        {
            var giver = (uint)Invoke(fixture.Handler, "ResolveQuestNpcId", "Thebes_All_007")!;
            Check.True(giver != 0, "old-level giver resolves on the current map");
            var before = (List<(uint QuestId, uint Available)>)Invoke(fixture.Handler,
                "QuestMarkerEntries", giver, fixture.Handler.AcceptableQuests())!;
            Check.True(before.Contains((604u, 1u)), "135-level quest is published before leveling");
            character.Level = 136;
            await InvokeAsync(fixture.Handler, "SendQuestLevelUpRefreshAsync", CancellationToken.None);
            var cipher = new PacketCipher();
            var oldGiverCleared = false;
            foreach (var packetBytes in fixture.Transport.WrittenChunks)
            {
                cipher.Transform(packetBytes);
                if (packetBytes.Length < 12 || BinaryPrimitives.ReadUInt16LittleEndian(packetBytes.AsSpan(2, 2)) !=
                    Opcodes.QuestMarkerList || BinaryPrimitives.ReadUInt32LittleEndian(packetBytes.AsSpan(4, 4)) != giver)
                {
                    continue;
                }
                for (var cursor = 12; cursor + 8 <= packetBytes.Length; cursor += 8)
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(packetBytes.AsSpan(cursor, 4)) == 604)
                    {
                        oldGiverCleared = BinaryPrimitives.ReadUInt32LittleEndian(packetBytes.AsSpan(cursor + 4, 4)) == 0;
                    }
                }
            }
            Check.True(oldGiverCleared, "level-up clears old NPC publishing flags even when the giver has no new quests");
            var responder = (uint)Invoke(fixture.Handler, "ResolveQuestNpcId", "Thebes_All_008")!;
            var handable = (List<uint>)Invoke(fixture.Handler, "QuestHandInEntries", responder)!;
            Check.True(handable.Contains(602u), "an old-level carried quest keeps its NPC hand-in entry");
            await InvokeAsync(fixture.Handler, "AcceptQuestAsync", 604u, CancellationToken.None, true);
            Check.True(character.Quests.All(quest => quest.QuestId != 604), "the real accept path rejects old-level publication");
            await InvokeAsync(fixture.Handler, "AcceptQuestAsync", 608u, CancellationToken.None, true);
            Check.True(character.Quests.Any(quest => quest.QuestId == 608), "new-level tasks can be accepted independently");
        }
        finally
        {
            await fixture.Session.DisposeAsync();
        }
    }

    private static Guid? ReadRewardClaim(GameClientHandler handler)
    {
        var offer = typeof(GameClientHandler).GetField("_pendingQuestRewardOffer", PrivateInstance)!.GetValue(handler);
        return offer is null ? null : (Guid)offer.GetType().GetProperty("RewardClaimId")!.GetValue(offer)!;
    }

    private static (GameClientHandler Handler, ClientSession Session,
        ScriptedLegacyByteTransport Transport, QuestStore Store) CreateFixture(GameCharacter character)
    {
        var store = new QuestStore();
        var transport = new ScriptedLegacyByteTransport();
        var session = new ClientSession(transport);
        var npcs = NpcContentBaselineV11.LoadDefinitions();
        var handler = new GameClientHandler(session, store, new GameSessionRegistry(store),
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Create(npcs.Select(npc => npc.MapId).Distinct(), npcs));
        typeof(GameClientHandler).GetField("_character", PrivateInstance)!.SetValue(handler, character);
        typeof(GameClientHandler).GetField("_account", PrivateInstance)!.SetValue(handler,
            new AccountIdentity(character.AccountId, "quest-check"));
        var byId = (Dictionary<uint, NpcSpawnDefinition>)typeof(GameClientHandler)
            .GetField("_mapNpcsByInteractionId", PrivateInstance)!.GetValue(handler)!;
        foreach (var npc in CapturedNpcPlacementPolicy.ApplyToMap(
            npcs.Where(npc => npc.MapId == character.CurrentMap).ToArray()))
        {
            byId[npc.InteractionId] = npc;
        }

        return (handler, session, transport, store);
    }

    private static object? Invoke(GameClientHandler handler, string method, params object[] arguments) =>
        typeof(GameClientHandler).GetMethod(method, PrivateInstance)!.Invoke(handler, arguments);

    private static async Task InvokeAsync(GameClientHandler handler, string method, params object[] arguments) =>
        await (Task)Invoke(handler, method, arguments)!;

    private static async Task<object?> InvokeAsyncResult(GameClientHandler handler, string method, params object[] arguments)
    {
        var task = (Task)Invoke(handler, method, arguments)!;
        await task;
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private sealed class QuestStore : GameStoreTestStub
    {
        public int Saves { get; private set; }
        public int PayoutAttempts { get; private set; }
        public bool ProbeReward { get; set; }
        public bool CompleteHandIn { get; set; }
        public int DailySaves { get; private set; }
        public int RewardExperience { get; private set; }
        public int RewardTalent { get; private set; }
        public int RewardSilver { get; private set; }
        public int RewardGold { get; private set; }
        public override Task SaveCharacterQuestStateAsync(int accountId, int characterId,
            IReadOnlyList<CharacterQuest> quests, IReadOnlyList<uint> completedQuestIds,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }

        public override Task<CharacterProgressionResult?> ApplyMonsterKillRewardAsync(
            int accountId, int characterId, int experience, int talentExperience,
            CancellationToken cancellationToken = default)
        {
            PayoutAttempts++;
            if (ProbeReward || CompleteHandIn)
            {
                RewardExperience = experience;
                RewardTalent = talentExperience;
                return Task.FromResult<CharacterProgressionResult?>(null);
            }

            throw new InvalidOperationException("An invalid hand-in reached the reward store.");
        }

        public override Task<CharacterWalletResult?> GrantQuestCurrencyAsync(
            int accountId, int characterId, int silver, int gold,
            CancellationToken cancellationToken = default)
        {
            RewardSilver = silver;
            RewardGold = gold;
            if (CompleteHandIn)
            {
                return Task.FromResult<CharacterWalletResult?>(null);
            }
            throw new OperationCanceledException("The payout arguments were observed by the test fixture.");
        }

        public override Task SaveQuestDailyCompletionsAsync(int accountId, int characterId,
            IReadOnlyDictionary<uint, GameCharacter.QuestDailyCount> counts,
            CancellationToken cancellationToken = default)
        {
            DailySaves++;
            return Task.CompletedTask;
        }
    }
}
