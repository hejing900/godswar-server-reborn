using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.ZeusGift;
using Godswar.Server.Infrastructure.ZeusGift;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The Zeus gift event's durable state; null when the storage provider does not
    /// expose it, in which case the event answers the level gate and nothing else.
    /// </summary>
    private readonly PostgresZeusGiftStore? _zeusGift;

    /// <summary>
    /// Opens the event window and starts a fresh page counter.
    /// </summary>
    /// <remarks>
    /// The window is opened once per NPC click, and the client's page counter - the
    /// <c>Index</c> the script receives - only restarts when it is opened again.
    /// Resetting it here is what keeps the first reply on page one.
    /// </remarks>
    private async Task SendZeusGiftFunctionMenuAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        _npcDialogPages.Opened(npc.InteractionId);
        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                ZeusGiftFunctionList,
                npc.NpcKey),
            cancellationToken,
            "ZeusGiftFunctionMenu");
        Console.WriteLine(
            $"[zeus] menu open npc={npc.InteractionId} key={npc.NpcKey} " +
            $"list=[{string.Join(',', ZeusGiftFunctionList)}]");
    }

    /// <summary>
    /// Answers one click on either endpoint of the Zeus gift event.
    /// </summary>
    /// <remarks>
    /// Both endpoints run the installed client's own <c>NpcFunZeus.lua</c> under
    /// function <c>26</c>, and the script's first page carries both of their entry
    /// groups on the same rows - the follower's delivery entries and the saint's
    /// stone, exchange and luck entries. Which half answers is therefore the only
    /// thing that keeps the two windows apart.
    /// <para>
    /// A reply may only carry numbers the script draws on page
    /// <c>replies + 1</c> (see <see cref="NpcDialogPages"/>), and every click has to
    /// be answered exactly once, because an unanswered click leaves the counter
    /// behind for every later step.
    /// </para>
    /// </remarks>
    private async Task HandleZeusGiftDialogueAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int selection,
        int[] args,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character || _zeusGift is not { } store)
        {
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftLevelReply, "ZeusGiftUnavailable", cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var page = _npcDialogPages.NextPage(npc.InteractionId);
        if (IsZeusPrayingSaint(npc))
        {
            await HandleZeusGiftSaintAsync(
                npc, dialogIndex, page, selection, args, character, store, now,
                cancellationToken);
            return;
        }

        await HandleZeusGiftFollowerAsync(
            npc, dialogIndex, page, selection, args, character, store, now,
            cancellationToken);
    }

    // ---------------------------------------------------------------- follower

    /// <summary>
    /// The believer half: the delivery loop.
    /// </summary>
    /// <remarks>
    /// The capture is the whole of this half. Page one is the requirement the
    /// follower is asking for, the three delivery entries and the progress line;
    /// page two is the basket, the offer to take ten crystals instead and the offer
    /// to swap the requirement; page three is the reward level and the swap's own
    /// answer.
    /// </remarks>
    private async Task HandleZeusGiftFollowerAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int page,
        int selection,
        int[] args,
        GameCharacter character,
        PostgresZeusGiftStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var calendar = _realmCalendar;
        var realmDay = calendar.GetDay(now);
        var state = await store.ReadDailyAsync(character.Id, calendar, now, cancellationToken);

        if (selection == ZeusGiftOpeningRequest)
        {
            if (character.Level < ZeusGiftPolicy.MinimumLevel)
            {
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftLevelReply, "ZeusGiftLevelGate", cancellationToken);
                return;
            }

            if (state.Round >= ZeusGiftPolicy.GiftLimit(realmDay))
            {
                // NF_L0_Z1301, "你今天的献礼次数已经用完了", a first-page line.
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, [1509], "ZeusGiftNoDeliveries", cancellationToken);
                return;
            }

            if (!state.HasRequirement)
            {
                var candidates = ZeusGiftPolicy.RequirementsForRound(state.Round + 1);
                var requirement = candidates[Random.Shared.Next(candidates.Count)];
                state = await store.AssignRequirementAsync(
                    character.Id,
                    calendar,
                    now,
                    state.Round,
                    state.Tier,
                    requirement.ItemId,
                    requirement.Count,
                    cancellationToken);
                Console.WriteLine(
                    $"[zeus] requirement character={character.Name} " +
                    $"round={state.Round + 1} item={requirement.ItemId} x{requirement.Count} " +
                    $"({requirement.Label})");
            }

            await SendZeusGiftReplyAsync(
                npc,
                dialogIndex,
                [
                    ZeusGiftPolicy.RequirementSubId(
                        new ZeusGiftPolicy.ZeusGiftRequirement(
                            state.RequiredItemId, state.RequiredCount, 0, string.Empty)),
                    .. ZeusGiftFollowerDeliveryEntries,
                    ZeusGiftPolicy.ProgressSubId(state.Round + 1, state.Tier + 1)
                ],
                "ZeusGiftRequirement",
                cancellationToken);
            return;
        }

        // Every follower click is logged with the whole argument array: the frame's
        // fixed block is the one place the basket's item can travel, and its
        // encoding is still being pinned.
        Console.WriteLine(
            $"[zeus] follower click character={character.Name} page={page} " +
            $"selection={selection} args=[{string.Join(',', args)}]");

        if (page == 2)
        {
            var reply = selection switch
            {
                1001 => ZeusGiftBasketPage,
                1002 => ZeusGiftLazyAnswerPage,
                1003 => ZeusGiftSwapQuestionPage,
                _ => ZeusGiftBasketPage
            };
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, reply, $"ZeusGiftFollowerPage2/{selection}", cancellationToken);
            return;
        }

        if (page == 3)
        {
            await HandleZeusGiftFollowerPage3Async(
                npc, dialogIndex, selection, args, character, store, calendar, now,
                cancellationToken);
            return;
        }

        // Pages four and five are not reachable on this half: the deepest captured
        // step is the third reply. Anything deeper repeats the swap answer rather
        // than drawing nothing.
        await SendZeusGiftReplyAsync(
            npc, dialogIndex, ZeusGiftSwapAcceptedPage, "ZeusGiftFollowerDeep", cancellationToken);
    }

    /// <summary>
    /// The argument slot the basket's confirm frame puts the placed item's kit-bag
    /// slot in.
    /// </summary>
    /// <remarks>
    /// The confirm frame's arguments are the first click slots followed by a fixed
    /// block, and the block's first word is the only value the capture carries on
    /// that frame that can be a bag slot: <c>2026-10-04 01:02:40.525</c> sends
    /// <c>[1001, 0, -1, -1, -1, -1, 18]</c>, and eighteen is inside the ninety-six
    /// slots. The whole frame is logged so the reading can be confirmed against a
    /// real placement.
    /// </remarks>
    private const int PlacedItemSlot = NpcFunctionClickSlotCount - 1;

    /// <summary>
    /// What a basket confirm frame said about the item the player put in.
    /// </summary>
    /// <param name="ItemId">The item the basket holds, or zero.</param>
    /// <param name="Encoding">Which reading produced it, for the log.</param>
    /// <param name="Decided">
    /// Whether the server can act on the reading. A word of zero or less means the
    /// basket is empty on every reading, so it is decided; a word that no reading
    /// resolves is not, and the caller then falls back to the bag rather than
    /// turning a working placement away.
    /// </param>
    private readonly record struct ZeusGiftPlacedItem(
        int ItemId,
        string Encoding,
        bool Decided);

    /// <summary>
    /// Reads the item the player put in the basket out of the word the confirm frame
    /// carries in <see cref="PlacedItemSlot"/>.
    /// </summary>
    /// <remarks>
    /// The field has not been pinned by a reference capture yet, and only two
    /// readings are supported by evidence: a kit-bag slot, which is what the one
    /// captured basket confirm carries (<c>18</c> on
    /// <c>2026-10-04 01:02:40.525</c>, inside the ninety-six slots), and the item id
    /// itself, for a client that sends the template rather than a slot. A live
    /// placement sent <c>109</c>, which is neither, and it stays unresolved rather
    /// than being bent onto a slot: an invented reading would compare the wrong item
    /// against the requirement and refuse a placement the player made correctly.
    /// Every unresolved word is logged with the whole frame, which is what will pin
    /// the encoding.
    /// </remarks>
    private static ZeusGiftPlacedItem ResolvePlacedItem(GameCharacter character, int word)
    {
        if (word <= 0)
        {
            return new ZeusGiftPlacedItem(0, "empty", Decided: true);
        }

        if (word < KitBagItemGrantPlanner.SlotCount)
        {
            var item = ReadBagSlot(character, word);
            return item != 0
                ? new ZeusGiftPlacedItem(item, "slot", Decided: true)
                : new ZeusGiftPlacedItem(0, "slot-empty", Decided: true);
        }

        return word >= MinimumItemId
            ? new ZeusGiftPlacedItem(word, "item-id", Decided: true)
            : new ZeusGiftPlacedItem(0, "unresolved", Decided: false);
    }

    private static int ReadBagSlot(GameCharacter character, int slot) =>
        slot is >= 0 and < KitBagItemGrantPlanner.SlotCount &&
        !KitBagSlots.GetItem(character.KitBag, slot).IsEmpty
            ? checked((int)KitBagSlots.GetItem(character.KitBag, slot).Id)
            : 0;

    /// <summary>
    /// The argument that carries the number a player typed into one of the
    /// dialogue's own edit boxes.
    /// </summary>
    /// <remarks>
    /// The word is the one this server already measured on the guild altar:
    /// <see cref="DialogAmountOffset"/> is <c>+0x38</c> from the payload, where ten,
    /// 123, 5000, 12345, 100000 and 123123 were each read, and a submission that
    /// typed nothing read <c>-1</c>. The argument array starts at <c>+0x10</c>, so
    /// the same word is argument ten.
    /// </remarks>
    private const int DialogAmountArgument = (DialogAmountOffset - 16) / 4;

    /// <summary>The client's first item template id.</summary>
    private const int MinimumItemId = 1000;

    /// <summary>
    /// Re-reads the character, announces everything the event just handed over and
    /// re-sends the bag, so the window and the game log show what the database
    /// already holds.
    /// </summary>
    /// <remarks>
    /// The event's own store writes the bag rows directly, which leaves the
    /// session's character snapshot - and therefore every packet built from it -
    /// showing the bag as it was before the delivery, the deposit or the exchange.
    /// Every other grant path in this server reloads the character and re-publishes
    /// it for the same reason. The announcement is the stock native acquisition the
    /// chest and online-award paths use, which is what raises "you obtained" in the
    /// lower-right log; it hydrates the item into the client's own bag first, so the
    /// scratch slot it lands in is evicted again before the authoritative pages.
    /// </remarks>
    private async Task RefreshZeusGiftInventoryAsync(
        GameCharacter character,
        IReadOnlyList<(int ItemId, int Quantity)> granted,
        CancellationToken cancellationToken)
    {
        if (_account is not { } account)
        {
            return;
        }

        var characters = await _store.GetCharactersAsync(account.Id, cancellationToken);
        var updated = characters.FirstOrDefault(candidate => candidate.Id == character.Id);
        if (updated is null)
        {
            Console.Error.WriteLine(
                $"[zeus] bag refresh lost character={character.Name} id={character.Id}");
            return;
        }

        InstallUpdatedCharacter(updated);
        _registry.UpdateCharacter(_session, _character!, advanceWorldRevision: false);

        foreach (var packet in BuildZeusGiftAcquisitionProjection(_character!, granted))
        {
            await _session.SendAsync(packet, cancellationToken, "ZeusGiftAcquisition");
        }
    }

    /// <summary>
    /// The packets that announce the event's grants and then restore the bag.
    /// </summary>
    /// <remarks>
    /// The shape is the chest path's, which is the chest path's because the stock
    /// client hydrates an acquisition into its own first bag slot and only then
    /// raises the notification: the slot has to be cleared between acquisitions, and
    /// the authoritative pages go last so the client ends up showing the server's
    /// bag rather than its optimistic one.
    /// </remarks>
    internal static IReadOnlyList<byte[]> BuildZeusGiftAcquisitionProjection(
        GameCharacter character,
        IReadOnlyList<(int ItemId, int Quantity)> granted)
    {
        var packets = new List<byte[]>();
        var announces = granted.Any(grant => grant.ItemId > 0 && grant.Quantity > 0);
        if (announces)
        {
            var scratchClear = PacketBuilder.StorageItemKitBagDelete(0);
            packets.Add(scratchClear);
            foreach (var (itemId, quantity) in granted)
            {
                if (itemId <= 0 || quantity <= 0)
                {
                    continue;
                }

                var item = Enumerable.Range(0, KitBagItemGrantPlanner.SlotCount)
                    .Select(slot => KitBagSlots.GetItem(character.KitBag, slot))
                    .FirstOrDefault(candidate => candidate.Id == itemId);
                // The event grants bound, and the planner writes quality and grade
                // one, so a grant that already merged into an existing stack still
                // has an entry to name.
                if (item.IsEmpty)
                {
                    item = CompactItemEntry.Empty with
                    {
                        Id = checked((uint)itemId),
                        Quality = 1,
                        Grade = 1,
                        Bound = 1
                    };
                }

                var remaining = quantity;
                while (remaining > 0)
                {
                    var chunk = Math.Min(remaining, 99);
                    packets.Add(PacketBuilder.SystemAddItemWithAcquisitionLog(
                        item with { Stack = checked((short)chunk) }));
                    packets.Add(scratchClear);
                    remaining -= chunk;
                }
            }
        }

        packets.AddRange(PacketBuilder.KitBagDetailPages(character));
        packets.AddRange(PacketBuilder.KitBagSlotIndexes(character));
        return packets;
    }

    /// <summary>
    /// The follower's three delivery entries, in the script's own rows.
    /// </summary>
    internal static readonly int[] ZeusGiftFollowerDeliveryEntries = [1001, 1002, 1003];

    /// <summary>
    /// Page three of the follower: the delivery itself, the swap and the two
    /// refusals.
    /// </summary>
    private async Task HandleZeusGiftFollowerPage3Async(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int selection,
        int[] args,
        GameCharacter character,
        PostgresZeusGiftStore store,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (selection)
        {
            case 1008:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftRefusedPage, "ZeusGiftRefused", cancellationToken);
                return;
            case 1009:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftReconsideredPage, "ZeusGiftReconsidered",
                    cancellationToken);
                return;
            case 1007:
            {
                // The swap: the reward level restarts, the round keeps counting.
                var current = await store.ReadDailyAsync(
                    character.Id, calendar, now, cancellationToken);
                var candidates = ZeusGiftPolicy.RequirementsForRound(current.Round + 1);
                var requirement = candidates[Random.Shared.Next(candidates.Count)];
                await store.AbandonRequirementAsync(
                    character.Id,
                    calendar,
                    now,
                    current.Round,
                    requirement.ItemId,
                    requirement.Count,
                    cancellationToken);
                Console.WriteLine(
                    $"[zeus] requirement abandoned character={character.Name} " +
                    $"round={current.Round} (tier reset) next-item={requirement.ItemId}");
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSwapAcceptedPage, "ZeusGiftSwap", cancellationToken);
                return;
            }
        }

        // A delivery, in one of the two shapes the script offers:
        //   * the confirm on the basket page, which the client reports as zero and
        //     which carries the item the player put in;
        //   * the "算了给你水晶" entry, which places nothing and spends ten crystals.
        var basketPath = selection == ZeusGiftConfirmSelection;
        var placedWord = basketPath && args.Length > PlacedItemSlot ? args[PlacedItemSlot] : int.MinValue;
        var placed = basketPath
            ? ResolvePlacedItem(character, placedWord)
            : default;
        var daily = basketPath
            ? await store.ReadDailyAsync(character.Id, calendar, now, cancellationToken)
            : default;

        var source = basketPath ? ZeusGiftDeliverySource.Basket : ZeusGiftDeliverySource.CrystalSubstitute;
        if (basketPath)
        {
            // The basket only accepts the follower's own requirement, which is the
            // client's own line NF_L0_Z1104.
            if (placed.Decided && placed.ItemId == 0)
            {
                Console.WriteLine(
                    $"[zeus] basket empty character={character.Name} word={placedWord} " +
                    $"read={placed.Encoding} required={daily.RequiredItemId}x{daily.RequiredCount}");
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftMissingGiftPage, "ZeusGiftBasketEmpty",
                    cancellationToken);
                return;
            }

            if (placed.Decided && placed.ItemId != daily.RequiredItemId)
            {
                Console.WriteLine(
                    $"[zeus] basket mismatch character={character.Name} word={placedWord} " +
                    $"read={placed.Encoding} placed={placed.ItemId} " +
                    $"required={daily.RequiredItemId}");
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftMissingGiftPage, "ZeusGiftBasketMismatch",
                    cancellationToken);
                return;
            }

            if (!placed.Decided)
            {
                // The frame's word is not one of the readings this server knows, so
                // the bag alone decides rather than turning a placement away. The
                // raw frame is logged so the encoding can be pinned from it.
                source = ZeusGiftDeliverySource.UnreadBasket;
                Console.WriteLine(
                    $"[zeus] basket unread character={character.Name} word={placedWord} " +
                    $"args=[{string.Join(',', args)}] " +
                    $"required={daily.RequiredItemId}x{daily.RequiredCount}");
            }
        }

        var delivery = await store.DeliverAsync(
            _account?.Id ?? 0,
            character.Id,
            calendar,
            now,
            source,
            placed.ItemId,
            character.Level,
            cancellationToken);
        switch (delivery.Outcome)
        {
            case ZeusGiftDeliveryOutcome.MissingRequirement:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftMissingGiftPage, "ZeusGiftMissingGift",
                    cancellationToken);
                return;
            case ZeusGiftDeliveryOutcome.NoDeliveriesLeft:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftNoDeliveriesPage, "ZeusGiftNoDeliveries",
                    cancellationToken);
                return;
            case ZeusGiftDeliveryOutcome.BagFull:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftMissingGiftPage, "ZeusGiftBagFull",
                    cancellationToken);
                return;
            case ZeusGiftDeliveryOutcome.Unavailable:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftLevelReply, "ZeusGiftUnavailable", cancellationToken);
                return;
        }

        var reward = delivery.Reward;
        await PayZeusGiftProgressionAsync(
            reward.Experience, reward.TalentExperience, "ZeusGiftDelivery", cancellationToken);
        await RefreshZeusGiftInventoryAsync(
            character,
            [
                new(ZeusGiftPolicy.PrayingStoneItemId, reward.PrayingStones),
                new(reward.Wine ? ZeusGiftPolicy.WineItemId : 0, reward.Wine ? 1 : 0)
            ],
            cancellationToken);
        await SendZeusGiftReplyAsync(
            npc,
            dialogIndex,
            [1313, ZeusGiftPolicy.RewardSubId(reward.Tier)],
            "ZeusGiftReward",
            cancellationToken);
    }

    // ------------------------------------------------------------------- saint

    /// <summary>
    /// The saint half: praying stones, the weekend dust exchange and the luck
    /// contest.
    /// </summary>
    /// <remarks>
    /// No capture covers this endpoint past the level gate, so every number is
    /// transcribed from <c>NpcFunZeus.lua</c> with the page it sits on. The event
    /// page supplies the windows: stones Monday to Friday, the exchange Saturday
    /// noon to Sunday night, the contest Saturday noon to Sunday noon, and the
    /// prizes claimable until Sunday 23:55.
    /// </remarks>
    private async Task HandleZeusGiftSaintAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int page,
        int selection,
        int[] args,
        GameCharacter character,
        PostgresZeusGiftStore store,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var calendar = _realmCalendar;
        var realmDay = calendar.GetDay(now);
        var realmTime = TimeOnly.FromDateTime(calendar.ToRealmTime(now).DateTime);

        if (selection == ZeusGiftOpeningRequest)
        {
            if (character.Level < ZeusGiftPolicy.MinimumLevel)
            {
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftLevelReply, "ZeusGiftLevelGate", cancellationToken);
                return;
            }
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftSaintOpeningPage, "ZeusGiftSaintMenu", cancellationToken);
            return;
        }

        if (page == 2)
        {
            await HandleZeusGiftSaintPage2Async(
                npc, dialogIndex, selection, args, character, store, calendar, realmDay, now,
                cancellationToken);
            return;
        }

        if (page == 3)
        {
            await HandleZeusGiftSaintPage3Async(
                npc, dialogIndex, selection, args, character, store, calendar, realmDay, realmTime,
                now, cancellationToken);
            return;
        }

        if (page == 4)
        {
            await HandleZeusGiftSaintPage4Async(
                npc, dialogIndex, selection, args, character, store, calendar, realmDay,
                realmTime, now, cancellationToken);
            return;
        }

        if (page == 5)
        {
            await HandleZeusGiftSaintPage5Async(
                npc, dialogIndex, selection, args, character, store, calendar, realmDay,
                realmTime, now, cancellationToken);
            return;
        }

        // The saint's script has five pages and no more. A sixth reply means the
        // client and the server already disagree about where the window is, and no
        // number on a page that does not exist would draw anything, so this leaves
        // the click unanswered and says so instead of inventing a step.
        Console.Error.WriteLine(
            $"[zeus] saint page overflow npc={npc.InteractionId} page={page} " +
            $"selection={selection}: NpcFunZeus.lua has five pages");
    }

    private async Task HandleZeusGiftSaintPage2Async(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int selection,
        int[] args,
        GameCharacter character,
        PostgresZeusGiftStore store,
        RealmCalendar calendar,
        DateOnly realmDay,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var week = await store.ReadWeeklyAsync(character.Id, calendar, now, cancellationToken);
        var delivered = await HasEverDeliveredAsync(
            character.Id, calendar, now, store, cancellationToken);

        switch (selection)
        {
            case 1200:
                if (!ZeusGiftPolicy.AcceptsPrayingStones(realmDay))
                {
                    await SendZeusGiftReplyAsync(
                        npc, dialogIndex, ZeusGiftSaintWeekdayOnlyPage, "ZeusGiftStoneWeekday",
                        cancellationToken);
                    return;
                }
                if (!delivered)
                {
                    await SendZeusGiftReplyAsync(
                        npc, dialogIndex, ZeusGiftSaintNotInEventPage, "ZeusGiftStoneNotInEvent",
                        cancellationToken);
                    return;
                }
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSaintDepositFormPage, "ZeusGiftStoneForm",
                    cancellationToken);
                return;

            case 1201:
                if (!ZeusGiftPolicy.IsWeekend(realmDay))
                {
                    await SendZeusGiftReplyAsync(
                        npc, dialogIndex, ZeusGiftSaintWeekendOnlyPage, "ZeusGiftExchangeWeekend",
                        cancellationToken);
                    return;
                }
                if (!delivered)
                {
                    await SendZeusGiftReplyAsync(
                        npc, dialogIndex, ZeusGiftSaintNotInEventPage, "ZeusGiftExchangeNotInEvent",
                        cancellationToken);
                    return;
                }
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSaintExchangePage, "ZeusGiftExchangeMenu",
                    cancellationToken);
                return;

            case 101:
                if (!delivered)
                {
                    await SendZeusGiftReplyAsync(
                        npc, dialogIndex, ZeusGiftSaintNotInEventPage, "ZeusGiftLuckNotInEvent",
                        cancellationToken);
                    return;
                }
                // The page leads with the line that prints the week's remaining
                // chances, which the client reads back out of the number.
                await SendZeusGiftReplyAsync(
                    npc,
                    dialogIndex,
                    ZeusGiftSaintLuckPage(week.LuckChances),
                    "ZeusGiftLuckMenu",
                    cancellationToken);
                return;
        }

        // Nothing else is drawn on this page: repeat the exchange entries rather
        // than leave the click unanswered, which would stall the page counter.
        await SendZeusGiftReplyAsync(
            npc, dialogIndex, ZeusGiftSaintExchangePage, "ZeusGiftSaintPage2Unknown",
            cancellationToken);
    }

    private async Task HandleZeusGiftSaintPage3Async(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int selection,
        int[] args,
        GameCharacter character,
        PostgresZeusGiftStore store,
        RealmCalendar calendar,
        DateOnly realmDay,
        TimeOnly realmTime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The confirm button on the deposit form. It answers on the third page,
        // because the form the player confirmed was itself the second reply. The
        // typed amount travels in the block the opening request also uses; the
        // capture leaves that slot unexplained (2026-10-04 01:02:40 carries 18
        // there), so the amount is read from it and validated against the script's
        // own 1-99 rule (NF_L0_Z1327).
        if (selection == ZeusGiftConfirmSelection)
        {
            // The window is checked here as well as on the form page: a click can be
            // sent without ever drawing the page, and the stones are only taken
            // Monday to Friday.
            if (!ZeusGiftPolicy.AcceptsPrayingStones(realmDay))
            {
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSaintWeekdayOnlyPage, "ZeusGiftStoneWeekday",
                    cancellationToken);
                return;
            }

            // The typed amount is the dialogue's own edit-box word, the one measured
            // at +0x38, not the item slot's word the basket uses.
            var amount = args.Length > DialogAmountArgument ? args[DialogAmountArgument] : 0;
            Console.WriteLine(
                $"[zeus] stone deposit character={character.Name} amount={amount} " +
                $"args=[{string.Join(',', args)}]");
            if (amount is < 1 or > 99)
            {
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, [1327], "ZeusGiftStoneAmount", cancellationToken);
                return;
            }

            var taken = await store.DepositPrayingStonesAsync(
                character.Id, calendar, _processRealmId.Value, now, amount, cancellationToken);
            if (taken != amount)
            {
                // NF_L0_Z1315, "你身上的祈祷石数量不足", a third-page line. The
                // deposit is all or nothing: a request for more than the bag holds
                // takes none of them and buys none of the luck chances.
                Console.WriteLine(
                    $"[zeus] stone deposit short character={character.Name} " +
                    $"asked={amount} taken={taken}");
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, [1315], "ZeusGiftStoneShort", cancellationToken);
                return;
            }

            // Nothing is paid here: each stone banks its experience and talent point
            // until the claim line hands the whole bank over.
            await RefreshZeusGiftInventoryAsync(character, [], cancellationToken);
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftSaintDepositDonePage, "ZeusGiftStoneDeposited",
                cancellationToken);
            return;
        }

        switch (selection)
        {
            case 1203:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSaintOrdinaryShelfPage, "ZeusGiftOrdinaryShelf",
                    cancellationToken);
                return;
            case 1204:
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSaintLimitedShelfPage, "ZeusGiftLimitedShelf",
                    cancellationToken);
                return;
            case 1205:
            {
                // The claim hands over everything the week's stones banked and
                // empties the bank in the same transaction, so a second click pays
                // nothing and answers with the client's own "already claimed" line.
                var claim = await store.ClaimBankedRewardAsync(
                    character.Id, calendar, now, cancellationToken);
                if (claim.Outcome != ZeusGiftExchangeOutcome.Won)
                {
                    // NF_L0_Z1506, "你已经领取过奖励了", which is also what a week
                    // with no stones handed in answers.
                    await SendZeusGiftReplyAsync(
                        npc, dialogIndex, [1506], "ZeusGiftBankEmpty", cancellationToken);
                    return;
                }

                Console.WriteLine(
                    $"[zeus] claim paid character={character.Name} exp={claim.Experience} " +
                    $"talent-points={claim.TalentPoints} claims={claim.ClaimsTaken}");
                await PayZeusGiftProgressionAsync(
                    checked((int)Math.Min(claim.Experience, int.MaxValue)),
                    checked(claim.TalentPoints * ZeusGiftPolicy.TalentExperiencePerPoint),
                    "ZeusGiftClaim",
                    cancellationToken);
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSaintFreeClaimedPage, "ZeusGiftBankClaimed",
                    cancellationToken);
                return;
            }
        }

        if (ZeusGiftPolicy.ResolveLuckGood(selection) is not { } good)
        {
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftSaintExchangePage, "ZeusGiftSaintPage3Unknown",
                cancellationToken);
            return;
        }

        if (!ZeusGiftPolicy.IsLuckWindow(realmDay, realmTime) &&
            !ZeusGiftPolicy.IsClaimWindow(realmDay, realmTime))
        {
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftLuckClosedPage, "ZeusGiftLuckClosed", cancellationToken);
            return;
        }

        var entries = await store.ReadLuckAsync(character.Id, calendar, now, cancellationToken);
        var own = entries.FirstOrDefault(entry => entry.ItemId == good.ItemId);
        var highest = await store.ReadHighestScoreAsync(
            character.Camp, calendar, now, good.ItemId, cancellationToken);

        if (ZeusGiftPolicy.IsClaimWindow(realmDay, realmTime))
        {
            // After the contest closes the page shows the settled result - the item's
            // final highest score and the player's own, the two text lines the script's
            // tail-5 and tail-4 branches draw - and the claim button goes with it every
            // time, whatever the scores are. The reference sends [4, 5, 302] for an
            // untouched board, and settles the verdict only once the button is pressed.
            await SendZeusGiftReplyAsync(
                npc,
                dialogIndex,
                BuildZeusGiftLuckSettlement(highest, own.Score),
                "ZeusGiftLuckSettled",
                cancellationToken);
            return;
        }

        // The contest is open: the item's current highest score on the first line and
        // the player's own score with the slot the dusts go in on the second, which
        // is exactly the pair NF_Z_T3011's trailing comma expects and the only
        // third-page branch that raises FirstWin_ItemBtn1.
        await SendZeusGiftReplyAsync(
            npc,
            dialogIndex,
            [ZeusGiftPolicy.LuckHighestScoreSubId(highest), ZeusGiftPolicy.LuckOwnScoreSubId(own.Score)],
            "ZeusGiftLuckEntry",
            cancellationToken);
    }

    private async Task HandleZeusGiftSaintPage4Async(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int selection,
        int[] args,
        GameCharacter character,
        PostgresZeusGiftStore store,
        RealmCalendar calendar,
        DateOnly realmDay,
        TimeOnly realmTime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (selection == ZeusGiftLuckClaimButton)
        {
            // The claim only exists inside its window: the page is drawn there, but a
            // click can be sent without it, and after Sunday 23:55 the prize is gone.
            if (!ZeusGiftPolicy.IsClaimWindow(realmDay, realmTime))
            {
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftLuckClosedPage, "ZeusGiftLuckClaimClosed",
                    cancellationToken);
                return;
            }

            var good = ResolveLuckGoodFromClick(args);
            var itemId = good?.ItemId ?? 0;
            if (itemId == 0)
            {
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftLuckNotWinnerPage, "ZeusGiftLuckNoItem",
                    cancellationToken);
                return;
            }

            var claim = await store.ClaimLuckPrizeAsync(
                _account?.Id ?? 0, character.Id, calendar, now, itemId, character.Camp,
                cancellationToken);
            var reply = claim.Outcome switch
            {
                ZeusGiftExchangeOutcome.Won => ZeusGiftLuckClaimedPage,
                ZeusGiftExchangeOutcome.OutOfStock => ZeusGiftLuckAlreadyClaimedPage,
                ZeusGiftExchangeOutcome.BagFull => ZeusGiftLuckBagFullPage,
                ZeusGiftExchangeOutcome.Tied => ZeusGiftLuckTiedPage,
                _ => ZeusGiftLuckNotWinnerPage
            };
            if (claim.Outcome == ZeusGiftExchangeOutcome.Won)
            {
                await RefreshZeusGiftInventoryAsync(
                    character, [(claim.ItemId, claim.Count)], cancellationToken);
            }
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, reply, $"ZeusGiftLuckClaim/{claim.Outcome}", cancellationToken);
            return;
        }

        // Both shelves put the dust box up on the fourth page; the exchange itself
        // is settled on the fifth, once the player has dropped the dusts in. The
        // limited shelf is checked for stock first, because the script's own line
        // for a spent shelf (5102, NF_L0_Z1329) is a fourth-page line.
        if (ZeusGiftPolicy.ResolveLimitedPrize(selection) is { } limited &&
            await store.IsLimitedShelfEmptyAsync(
                _processRealmId.Value, character.Camp, calendar, now, limited.ItemId,
                cancellationToken))
        {
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftSaintOutOfStockPage, "ZeusGiftLimitedOutOfStock",
                cancellationToken);
            return;
        }

        if (ZeusGiftPolicy.ResolveOrdinaryPrize(selection) is not null ||
            ZeusGiftPolicy.ResolveLimitedPrize(selection) is not null)
        {
            // The dust box is the weekend's, and the click can arrive without the
            // shelf page ever being drawn.
            if (!ZeusGiftPolicy.IsExchangeWindow(realmDay, realmTime))
            {
                await SendZeusGiftReplyAsync(
                    npc, dialogIndex, ZeusGiftSaintWeekendOnlyPage, "ZeusGiftExchangeClosed",
                    cancellationToken);
                return;
            }

            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftSaintDustBoxPage, "ZeusGiftDustBox", cancellationToken);
            return;
        }

        if (!ZeusGiftPolicy.IsLuckWindow(realmDay, realmTime))
        {
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftLuckClosedPage, "ZeusGiftLuckClosedDraw",
                cancellationToken);
            return;
        }

        // The confirm on the luck page: the item the character is throwing at is the
        // one its third-page entry showed, which the client echoes back somewhere in
        // the click path. The path starts with the dialog index and carries the menu
        // entries before the good, so the good is found by value rather than by
        // position: the reference's own claim click is [26, 101, 206, 302] and only
        // 206 is one of the eight.
        var chosen = ResolveLuckGoodFromClick(args) ?? ZeusGiftPolicy.Luck[0];
        var draw = await store.DrawLuckAsync(
            _account?.Id ?? 0,
            character.Id,
            calendar,
            now,
            chosen.ItemId,
            character.Camp,
            cancellationToken);
        var drawReply = draw.Outcome switch
        {
            ZeusLuckDrawOutcome.NoChances => ZeusGiftLuckNoChancesPage,
            ZeusLuckDrawOutcome.MissingDust => ZeusGiftLuckNoDustPage,
            ZeusLuckDrawOutcome.NotDust => ZeusGiftLuckNotDustPage,
            ZeusLuckDrawOutcome.Unavailable => ZeusGiftLuckNotDustPage,
            _ when draw.ItemId != 0 => ZeusGiftLuckSuperLuckyPage,
            _ when draw.RefundedDusts > 0 => ZeusGiftLuckLuckyNumberPage,
            _ => [ZeusGiftPolicy.LuckRolledSubId(draw.Score)]
        };
        if (draw.Outcome == ZeusLuckDrawOutcome.Rolled)
        {
            // The draw always moved dusts, and may have handed the item or the
            // ninety-nine back, so the window has to be re-sent either way.
            await RefreshZeusGiftInventoryAsync(
                character,
                draw.ItemCount > 0
                    ? [(draw.ItemId, draw.ItemCount)]
                    : [(draw.RefundedDustItemId, draw.RefundedDusts)],
                cancellationToken);
        }
        await SendZeusGiftReplyAsync(
            npc, dialogIndex, drawReply, $"ZeusGiftLuckDraw/{draw.Outcome}", cancellationToken);
    }

    /// <summary>
    /// Page five of the saint: the dust exchange is settled. The reply is the only
    /// place any of the script's results can be drawn, because <c>3002</c>–<c>3007</c>
    /// all sit in its fifth-page block.
    /// </summary>
    /// <remarks>
    /// Which shelf the player was buying from is read from the click that opened the
    /// dust box, which the client echoes as the first argument of the confirm frame.
    /// </remarks>
    private async Task HandleZeusGiftSaintPage5Async(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int selection,
        int[] args,
        GameCharacter character,
        PostgresZeusGiftStore store,
        RealmCalendar calendar,
        DateOnly realmDay,
        TimeOnly realmTime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The dust box is settled in the weekend window only, and the click can be sent
        // without the shelf page ever being drawn.
        if (!ZeusGiftPolicy.IsExchangeWindow(realmDay, realmTime))
        {
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftSaintWeekendOnlyPage, "ZeusGiftExchangeClosed",
                cancellationToken);
            return;
        }

        // The shelf is found by value: the click path starts with the dialog index and
        // carries every entry the player walked through, so it is not at a fixed
        // position - the reference's own exchange click is [26, 1201, 1203, 1207].
        var (shelfId, limited, ordinary) = ResolveShelfFromClick(args);
        var shelf = shelfId;
        if (!limited && !ordinary)
        {
            await SendZeusGiftReplyAsync(
                npc, dialogIndex, ZeusGiftSaintExchangeFailedPage, "ZeusGiftExchangeNoShelf",
                cancellationToken);
            return;
        }

        var result = await store.ExchangeDustAsync(
            _account?.Id ?? 0,
            character.Id,
            calendar,
            _processRealmId.Value,
            character.Camp,
            now,
            shelf,
            limited,
            cancellationToken);
        var reply = result.Outcome switch
        {
            ZeusGiftExchangeOutcome.Won => ZeusGiftSaintPrizeClaimedPage,
            ZeusGiftExchangeOutcome.Lost => ZeusGiftSaintExchangeLostPage,
            ZeusGiftExchangeOutcome.MissingDust => ZeusGiftSaintExchangeNoDustPage,
            ZeusGiftExchangeOutcome.NoExchangesLeft => ZeusGiftSaintExchangeSpentPage,
            ZeusGiftExchangeOutcome.OutOfStock => ZeusGiftSaintOutOfStockPage,
            ZeusGiftExchangeOutcome.BagFull => ZeusGiftSaintBagFullPage,
            _ => ZeusGiftSaintExchangeFailedPage
        };
        if (result.Outcome is ZeusGiftExchangeOutcome.Won or ZeusGiftExchangeOutcome.Lost)
        {
            // A settled exchange always spent the dusts, whether or not it paid.
            await RefreshZeusGiftInventoryAsync(
                character, [(result.ItemId, result.Count)], cancellationToken);
        }
        await SendZeusGiftReplyAsync(
            npc, dialogIndex, reply, $"ZeusGiftExchange/{result.Outcome}", cancellationToken);
    }

    /// <summary>
    /// Whether the character has ever delivered, which the saint half gates the
    /// stone, the exchange and the luck contest on
    /// (<c>NF_L0_Z1521</c>, <c>NF_L0_Z1330</c>, <c>NF_L0_Z1102</c>).
    /// </summary>
    private static async Task<bool> HasEverDeliveredAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        PostgresZeusGiftStore store,
        CancellationToken cancellationToken)
    {
        var state = await store.ReadDailyAsync(characterId, calendar, now, cancellationToken);
        // The row only carries the current realm day, so "ever" is read as "has
        // delivered at any point today or holds a requirement"; the week row's own
        // deposits are the durable half of the same fact.
        var week = await store.ReadWeeklyAsync(characterId, calendar, now, cancellationToken);
        return state.Round > 0 || state.HasRequirement || week.StonesDeposited > 0;
    }

    /// <summary>
    /// Pays experience and talent experience through the shared progression path,
    /// the same way the divine wish's claim does.
    /// </summary>
    private async Task PayZeusGiftProgressionAsync(
        int experience,
        int talentExperience,
        string reason,
        CancellationToken cancellationToken)
    {
        if (_character is not { } character || experience <= 0 && talentExperience <= 0)
        {
            return;
        }

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.ApplyMonsterKillReward);
        var progression = await _store.ApplyMonsterKillRewardAsync(
            _account?.Id ?? 0,
            character.Id,
            experience,
            talentExperience,
            cancellationToken);
        if (progression is null)
        {
            Console.Error.WriteLine(
                $"[zeus] {reason} lost character={character.Name} exp={experience} " +
                $"talent={talentExperience}: the store wrote nothing");
            return;
        }

        character.Level = progression.CurrentLevel;
        character.Experience = progression.CurrentExperience;
        character.TalentExperience = progression.CurrentTalentExperience;
        character.TalentPoints = progression.CurrentTalentPoints;
        if (progression.LevelUps.Count > 0)
        {
            await RefreshLevelUpStatsAsync(character, cancellationToken);
        }

        _registry.UpdateCharacter(_session, character, advanceWorldRevision: false);
        foreach (var levelUp in progression.LevelUps)
        {
            var experienceMaximum = PlayerExperienceCatalog.GetClientExperienceMaximum(
                levelUp.Level, character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    experienceMaximum,
                    levelUp.CurrentExperience,
                    character.MaxHp,
                    character.CurrentHp,
                    character.MaxMp,
                    character.CurrentMp),
                cancellationToken,
                $"{reason}LevelUp");
            await _registry.BroadcastToMapAsync(
                character.CurrentMap,
                PacketBuilder.PlayerLevelUp(
                    CurrentPlayerObjectId,
                    levelUp.Level,
                    experienceMaximum,
                    levelUp.CurrentExperience,
                    character.MaxHp,
                    character.CurrentHp,
                    character.MaxMp,
                    character.CurrentMp),
                cancellationToken,
                _session,
                $"{reason}LevelUpWorld");
        }

        if (experience > 0)
        {
            await _session.SendAsync(
                PacketBuilder.ExperienceGain(experience, character.Experience),
                cancellationToken,
                $"{reason}ExperienceGain");
        }
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            $"{reason}Status");
        Console.WriteLine(
            $"[zeus] {reason} character={character.Name} exp={experience} " +
            $"talent={talentExperience} level={character.Level}");
    }

    /// <summary>
    /// The shelf a click was aimed at, found by value in the click path.
    /// </summary>
    /// <remarks>
    /// The path starts with the dialog index and repeats the menu entries the player
    /// walked through, so the shelf is not at a fixed position: the reference's own
    /// exchange click is <c>[26, 1201, 1203, 1207]</c>, where the shelf is fourth and
    /// first is the dialog index. The seven limited buttons and the five ordinary ones
    /// are the only values in a path that resolve, which is what makes searching safe.
    /// </remarks>
    private static (int Shelf, bool Limited, bool Ordinary) ResolveShelfFromClick(int[] args)
    {
        foreach (var argument in args)
        {
            if (argument <= 0)
            {
                continue;
            }

            if (ZeusGiftPolicy.ResolveLimitedPrize(argument) is not null)
            {
                return (argument, true, false);
            }

            if (ZeusGiftPolicy.ResolveOrdinaryPrize(argument) is not null)
            {
                return (argument, false, true);
            }
        }

        return (0, false, false);
    }

    /// <summary>
    /// The luck good a click was aimed at, found by value in the click path.
    /// </summary>
    /// <remarks>
    /// The path a click carries starts with the dialog index and repeats the menu
    /// entries the player walked through before the one they just pressed, so the good
    /// is not at a fixed position: the reference's claim click is
    /// <c>[26, 101, 206, 302]</c> on 2026-10-04 04:09:41, where the good is third and
    /// the claim button fourth. The eight goods are the only values in the path that
    /// resolve, which is what makes searching safe.
    /// </remarks>
    private static ZeusGiftPolicy.ZeusGiftPrize? ResolveLuckGoodFromClick(int[] args)
    {
        foreach (var argument in args)
        {
            if (argument > 0 && ZeusGiftPolicy.ResolveLuckGood(argument) is { } good)
            {
                return good;
            }
        }

        return null;
    }

    /// <summary>
    /// The contest's settled page: the item's final highest score, the player's own
    /// score, and the claim button.
    /// </summary>
    /// <remarks>
    /// The button goes with the page every time, whatever the two scores are, which is
    /// what the reference answered for an untouched board on 2026-10-04 04:09:36:
    /// <c>[4, 5, 302]</c> for a final highest of zero and an own score of zero. The
    /// verdict is settled when the button is pressed, not when the page is drawn.
    /// </remarks>
    internal static int[] BuildZeusGiftLuckSettlement(int highestScore, int ownScore) =>
    [
        ZeusGiftPolicy.LuckFinalHighestSubId(highestScore),
        ZeusGiftPolicy.LuckFinalOwnSubId(ownScore),
        ZeusGiftLuckClaimButton
    ];

    /// <summary>
    /// Sends one reply and books it, which is what advances the client's page.
    /// </summary>
    private async Task SendZeusGiftReplyAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int[] reply,
        string reason,
        CancellationToken cancellationToken)
    {
        var page = _npcDialogPages.NextPage(npc.InteractionId);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npc.InteractionId,
                dialogIndex,
                reply),
            cancellationToken,
            reason);
        _npcDialogPages.Recorded(npc.InteractionId);
        Console.WriteLine(
            $"[zeus] reply npc={npc.InteractionId} dialog={dialogIndex} page={page} " +
            $"reason={reason} sent=[{string.Join(',', reply)}]");
    }
}
