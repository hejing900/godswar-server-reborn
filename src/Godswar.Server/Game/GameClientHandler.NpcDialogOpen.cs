using System.Buffers.Binary;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.Infrastructure.WishingPool;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleNpcDialogOpenAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Payload.Length < sizeof(uint))
        {
            _warehouseAccessContext = null;
            Console.WriteLine("[npc] dialog open ignored: payload too short");
            return;
        }

        ClearGearEnhancerSelection();
        ClearInstanceCallerPageContext();
        ClearTransporterDialogueContext();
        ClearBattlefieldTransporterDialogueContext();
        ClearDuelArenaTransporterDialogueContext();
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..sizeof(uint)]);
        if (!TryResolveMapNpc(npcId, out var npc))
        {
            _warehouseAccessContext = null;
            QuestFrameTrace.Append(
                $"[npc] dialog open ignored: unknown npc={npcId} " +
                $"map={_character?.CurrentMap.ToString() ?? "<none>"} " +
                $"len={packet.Length}",
                []);
            Console.WriteLine(
                $"[npc] dialog open ignored: unknown npc={npcId} " +
                $"map={_character?.CurrentMap.ToString() ?? "<none>"}");
            return;
        }

        QuestFrameTrace.Append(
            $"[npc] dialog open received npc={npcId} key={npc.NpcKey} " +
            $"map={npc.MapId} len={packet.Length} buffer={packet.Buffer.Length} " +
            $"carried={_character?.Quests.Count ?? 0}",
            []);

        // Operator-authored GM NPCs answer next. They are injected into the map
        // roster by the GM layer at login, so the published route lookup below
        // finds nothing for their key and would leave every click unanswered.
        if (await TryHandleGmNpcDialogOpenAsync(npc, cancellationToken))
        {
            QuestFrameTrace.Append(
                $"[npc] dialog open branch=gm npc={npc.InteractionId} key={npc.NpcKey}",
                []);
            return;
        }

        // A Wonderland run's own actors answer first. The eight island
        // teleporters, the entrance blackmarket and the eight treasure chests are
        // injected into the instance roster rather than published as map content,
        // so the dialogue route lookup below finds nothing for them and would
        // leave every click unanswered. Both handlers refuse anything that is not
        // one of those actors, and only on map 207.
        if (await TryHandleWonderlandNpcOpenAsync(
                packet,
                npc,
                cancellationToken) ||
            await TryHandleWonderlandChestOpenAsync(
                packet,
                npc,
                cancellationToken))
        {
            QuestFrameTrace.Append(
                $"[npc] dialog open branch=wonderland npc={npc.InteractionId} " +
                $"key={npc.NpcKey}",
                []);
            return;
        }

        // The stock client can leave normal storage open while the related
        // manager dialogue is used. Preserve only an access lease that was
        // already issued by the normal Warehouse NPC; the manager never
        // grants one. Every unrelated NPC click still invalidates the lease.
        if (!WarehouseNpcProtocol.IsManagerEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            _warehouseAccessContext = null;
        }

        // The flags word is a bitmask, so an npc whose normal page is something
        // else keeps it and gains the quest page. Computed once here because every
        // branch below needs the same answer.
        var questFlags = HasQuestFunctionFor(npc.InteractionId)
            ? QuestContentBaseline.QuestOpenFlags
            : 0;

        if (WarehouseNpcProtocol.IsWarehouseEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            if (packet.Length == 48 && packet.Buffer.Length == 48 &&
                CanUseDuelArenaWarehouseNpc(npc))
            {
                await _session.SendAsync(
                    PacketBuilder.WarehouseDialogOpenAck(
                        npc.InteractionId,
                        WarehouseNpcProtocol.ClientScriptKey(
                            npc.NpcKey, npc.InteractionId),
                        questFlags),
                    cancellationToken,
                    "WarehouseDialogOpenAck");
            }
            else
            {
                Console.Error.WriteLine(
                    "[warehouse] rejected non-canonical NPC click " +
                    $"npc={npc.InteractionId} length={packet.Length}");
            }
            return;
        }

        if (QuestContentBaseline.IsNewbieGuide(npc.InteractionId))
        {
            // Replayed from the reference capture: the guide opens with the 10067
            // frame captured at packet 222, the responder (Sparta_106) with the
            // one captured at packet 1307. Both are this frame with the clicked
            // npc's own id and script key - which is what the capture holds - so
            // building it from the npc keeps Sparta byte-identical and stops the
            // Athens guide (5233, also named Acacia) from advertising Sparta_094.
            await _session.SendAsync(
                PacketBuilder.NpcQuestDialogOpenAck(
                    npc.InteractionId,
                    npc.NpcKey),
                cancellationToken,
                "NpcQuestDialogOpenAck");
            Console.WriteLine(
                $"[quest] guide dialog open npc={npc.InteractionId} " +
                $"key={npc.NpcKey} responder=" +
                $"{QuestContentBaseline.IsNewbieGuideResponder(npc.InteractionId)}");
            QuestFrameTrace.Append(
                $"[npc] dialog open branch=guide npc={npc.InteractionId} " +
                $"key={npc.NpcKey}",
                []);
            return;
        }

        // Every other npc that has a quest to give or take opens the same quest
        // page. The captured dialogs advertise it with flags 3; the generic
        // description window advertises 0, which is why the npc that receives the
        // second quest used to open a window with no quest entry in it at all.
        if (questFlags != 0)
        {
            await _session.SendAsync(
                PacketBuilder.NpcQuestDialogOpenAck(
                    npc.InteractionId,
                    npc.NpcKey),
                cancellationToken,
                "NpcQuestDialogOpenAck");
            Console.WriteLine(
                $"[quest] dialog open npc={npc.InteractionId} " +
                $"key={npc.NpcKey}");
            QuestFrameTrace.Append(
                $"[npc] dialog open branch=quest npc={npc.InteractionId} " +
                $"key={npc.NpcKey}",
                []);
            return;
        }

        if (IsWishingPool(npc))
        {
            await SendWishingPoolMenuAsync(npc, cancellationToken);
            return;
        }

        // The Cursed Land's in-map transport actors own a client script window
        // (function 96 = NPC_FLAG_SYS_RANDOM, 97 = NPC_FLAG_SYS_HOME) that draws
        // no menu at all: the reference advertised the function and moved the
        // character the moment the client asked for its entries. Answered before
        // the scripted-dialogue lookup because they carry no dialogue table.
        if (CursedLandTransportProtocol.TryGetInMapFunction(
                npc.NpcKey,
                out var cursedLandFunction))
        {
            await SendCursedLandTransporterOpenAsync(
                npc,
                cursedLandFunction,
                cancellationToken);
            return;
        }

        // The scripted NPCs own their whole window in client scripts and carry no
        // dialogue route, so they are answered before route resolution and leave the
        // versioned dialogue baseline untouched.
        if (ResolveScriptedNpcDialogues(npc) is { } scriptedDialogue)
        {
            await SendScriptedNpcDialogueMenuAsync(
                npc,
                scriptedDialogue,
                cancellationToken);
            return;
        }

        if (await TryHandleDuelArenaNpcDialogOpenAsync(
                packet, npc, cancellationToken))
        {
            QuestFrameTrace.Append(
                $"[npc] dialog open branch=duel-arena npc={npc.InteractionId} " +
                $"key={npc.NpcKey}",
                []);
            return;
        }

        if (await TryHandleCapitalNpcDialogOpenAsync(
                npc,
                questFlags,
                cancellationToken))
        {
            QuestFrameTrace.Append(
                $"[npc] dialog open branch=capital npc={npc.InteractionId} " +
                $"key={npc.NpcKey}",
                []);
            return;
        }

        // The guild registrar does not open a dialogue page at all: the reference
        // server answers with flags 0x80 and an empty function list, and the client
        // then raises its own guild window. That window's create entry is checked
        // locally - the shipped text "you need level 30" is the client's own, and a
        // refused attempt sends nothing - so the server's whole part of the create
        // conversation starts only once a character of level 30 or more tries.
        // Captured from the reference server: 10067 {5187, 0x80, 0, "Athens_049"}.
        if (IsGuildRegistrar(npc))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionDialogOpenAck(
                    npc.InteractionId,
                    GuildRegistrarOpenFlags,
                    packedDialog: 0,
                    npc.NpcKey),
                cancellationToken,
                "GuildRegistrarDialogOpenAck");
            Console.WriteLine(
                $"[npc] guild registrar open npc={npc.InteractionId} " +
                $"key={npc.NpcKey} flags=0x{GuildRegistrarOpenFlags:X}");
            QuestFrameTrace.Append(
                $"[npc] dialog open branch=guild-registrar " +
                $"npc={npc.InteractionId} key={npc.NpcKey}",
                []);
            await SendGuildListAsync(cancellationToken);
            await SendGuildWindowAsync(cancellationToken);
            return;
        }

        var (routes, text) = await ResolveNpcDialogueRoutesAsync(
            npc,
            cancellationToken);
        if (routes.Count == 0)
        {
            // A published NPC may carry dialogue text without any extended
            // function: the Wishing Pool is one, and the captured Duel Arena
            // Vendor and capital Teaching Manager are others. Advertise the
            // description-only window so the stock client opens its own
            // description page, exactly as those captured endpoints do. The
            // client resolves the visible text from the script key in its own
            // NPCDescription.dat, so no text travels in this packet.
            if (IsZeusGiftEndpoint(npc))
            {
                await SendZeusGiftFunctionMenuAsync(npc, cancellationToken);
                return;
            }

            if (text is { } description &&
                !string.IsNullOrWhiteSpace(description.Description))
            {
                await SendNpcDescriptionOpenAsync(npc, cancellationToken);
                return;
            }

            // Nothing matched: no function, no route and no description, so the
            // client is sent nothing at all and the click looks dead. Record it
            // because this is the one branch that leaves no other trace.
            QuestFrameTrace.Append(
                $"[npc] dialog open sent nothing npc={npc.InteractionId} " +
                $"key={npc.NpcKey} map={npc.MapId} questFlags={questFlags} " +
                $"warehouse={WarehouseNpcProtocol.IsWarehouseEndpoint(npc.NpcKey, npc.InteractionId)} " +
                $"text={(text is null ? "none" : "empty")} " +
                $"capital={CapitalNpcServiceProtocol.TryResolve(npc, out _)}",
                []);
            return;
        }

        var clientScriptKey = routes[0].ClientScriptKey;
        if (routes.Count > 3 || routes.Any(route =>
                !string.Equals(
                    route.ClientScriptKey,
                    clientScriptKey,
                    StringComparison.Ordinal)))
        {
            Console.Error.WriteLine(
                "[npc] dialog open rejected: routes cannot be represented " +
                $"npc={npc.InteractionId} routes={routes.Count}");
            return;
        }

        // One native advertisement carries up to three ordered top-level
        // functions in a base-1000 field. For Gear Mentor, [4, 37] becomes
        // 37004, so Gear Enhancement and Class Suit are sibling choices.
        var dialogIndices = routes
            .Select(static route => route.DialogIndex)
            .ToArray();
        if (routes.Any(DuelArenaCapturedTransportProtocol.IsCapturedRoute) &&
            (packet.Length != 48 || packet.Buffer.Length != 48 ||
                !TryIssueDuelArenaTransporterDialogueContext(npc)))
        {
            return;
        }
        if (routes.Any(static route =>
                route.Behavior == NpcDialogueBehavior.DuelArenaServices) &&
            (packet.Length != 48 || packet.Buffer.Length != 48 ||
                !CanUseCapturedArenaNpc(npc)))
        {
            return;
        }
        if (routes.Any(DuelArenaExitProtocol.IsRoute) &&
            !TryIssueDuelArenaTransporterDialogueContext(npc))
        {
            return;
        }
        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                dialogIndices,
                clientScriptKey),
            cancellationToken,
            "NpcDialogOpenAck");

        foreach (var route in routes)
        {
            Console.WriteLine(
                $"[npc] dialog open npc={npc.InteractionId} " +
                $"script={route.ClientScriptKey} " +
                $"behavior={route.Behavior} dialog={route.DialogIndex} " +
                $"order={route.RouteOrder}");
        }

        QuestFrameTrace.Append(
            $"[npc] dialog open branch=routes npc={npc.InteractionId} " +
            $"key={npc.NpcKey} script={clientScriptKey} routes={routes.Count}",
            []);
    }

    private async Task HandleNpcDialogPageRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Payload.Length < sizeof(uint))
        {
            Console.WriteLine("[npc] page request ignored: payload too short");
            return;
        }

        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..sizeof(uint)]);
        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine(
                $"[npc] page request ignored: unknown npc={npcId}");
            return;
        }

        if (WarehouseNpcProtocol.IsWarehouseEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            if (!CanUseDuelArenaWarehouseNpc(npc))
            {
                _warehouseAccessContext = null;
                return;
            }
            if (packet.Length == 8 && packet.Buffer.Length == 8)
            {
                await HandleWarehouseOpenAsync(npc, cancellationToken);
            }
            else if (packet.Length == 12 &&
                     packet.Buffer.Length == 12 &&
                     TryAuthorizeWarehouseTransfer(out var authorizedNpc) &&
                     authorizedNpc.InteractionId == npc.InteractionId)
            {
                var page = BinaryPrimitives.ReadInt32LittleEndian(
                    packet.Payload.Slice(sizeof(uint), sizeof(int)));
                await HandleWarehouseOpenAsync(
                    npc,
                    cancellationToken,
                    page,
                    issueAccess: false);
            }
            else
            {
                Console.Error.WriteLine(
                    "[warehouse] rejected non-canonical page request " +
                    $"npc={npc.InteractionId} length={packet.Length}");
            }
            return;
        }

        if (await TryHandleCapitalNpcPageRequestAsync(
                npc,
                cancellationToken))
        {
            return;
        }

        Console.WriteLine(
            $"[npc] page request npc={npcId} key={npc.NpcKey}");
    }

    /// <summary>
    /// Opens the stock client's plain description window for a published NPC
    /// that owns dialogue text but no extended function.
    /// </summary>
    /// <remarks>
    /// The advertised script key is the NPC key because the client indexes its
    /// own <c>NPCDescription.dat</c> by that key; the captured Duel Arena
    /// Vendor and the capital Teaching Manager both open this way. The packet
    /// is an ordinary 48-byte <see cref="Opcodes.NpcDialogOpen"/>
    /// acknowledgement with <c>flags = 0</c> and an empty function field, so it
    /// advertises no function the client would try to dispatch. It is unrelated
    /// to opcode 10090: no quest page is sent, and the quest refresh routine is
    /// never entered.
    /// </remarks>
    /// <summary>
    /// The dialog acknowledgement flags word that advertises the extended function
    /// list. It is what the captured Arena and mall acknowledgements carry together
    /// with the function number.
    /// </summary>
    private const int FunctionListOpenFlags = 0x200;

    /// <summary>
    /// The dialog acknowledgement flags word the reference server sends for the
    /// guild registrar. It is the whole of the server's part in opening that
    /// window: the function list stays empty and no dialogue number follows, which
    /// the capture of <c>10067 {5187, 0x80, 0, "Athens_049"}</c> shows.
    /// </summary>
    private const int GuildRegistrarOpenFlags = 0x80;

    /// <summary>
    /// Whether the NPC registers guilds. The client shows its own guild window for
    /// him, so he is answered with the flags word instead of a dialogue.
    /// </summary>
    private static bool IsGuildRegistrar(NpcSpawnDefinition npc) =>
        npc.NpcKey is "Athens_049" or "Sparta_049";

    /// <summary>
    /// Whether the NPC is one of the two endpoints of the Zeus gift event. Both
    /// are answered by the installed client's own <c>NpcFunZeus.lua</c> under the
    /// same function number, so both open the same function menu and differ only
    /// in which dialogue set they answer with.
    /// </summary>
    private static bool IsZeusGiftEndpoint(NpcSpawnDefinition npc) =>
        IsZeusLoyalBeliever(npc) || IsZeusPrayingSaint(npc);

    private static bool IsZeusLoyalBeliever(NpcSpawnDefinition npc) =>
        npc.NpcKey is "Athens_113" or "Sparta_113";

    /// <summary>
    /// The praying saint, the endpoint a gift is handed to. The client names him
    /// "[Event]Praying Saint" and he stands beside the believer in both capitals.
    /// </summary>
    private static bool IsZeusPrayingSaint(NpcSpawnDefinition npc) =>
        npc.NpcKey is "Athens_114" or "Sparta_114";


    /// <summary>
    /// The installed client's function advertisement for the Wishing Pool
    /// (<c>Athens_074</c>, "Wishing Pool"). The reference server sent this exact
    /// frame: <c>flags 0x200</c> with the packed page list <c>[16, 24, 50]</c>.
    /// </summary>
    private static readonly int[] WishingPoolPages = [16, 24, 50];

    private static bool IsWishingPool(uint npcId) => npcId is 5071u or 5213u;

    /// <summary>
    /// The client's own gate for the free wish: <c>NF_L0_JN100</c> reports that
    /// "players below level 30 cannot make wishes in the wishing pool".
    /// </summary>
    private const int WishingPoolMinimumLevel = 30;

    /// <summary>
    /// What the paid entry costs. The client script's own <c>JN103</c> offers to
    /// "spend 230 Gold in making another wish".
    /// </summary>
    private const int WishingPoolPaidWishGoldCost = 230;

    /// <summary>
    /// The client's two draw-result texts: <c>JN103</c> for an ordinary book
    /// ("it's only a common skill book") and <c>JN203</c> for an advanced one
    /// ("you are lucky to have obtained an advanced skill book").
    /// </summary>
    private const int WishingPoolOrdinaryResultSubId = 103;
    private const int WishingPoolAdvancedResultSubId = 203;

    private static bool IsWishingPool(NpcSpawnDefinition npc) =>
        npc.NpcKey is "Athens_074" or "Sparta_074";

    /// <summary>
    /// Opens the Wishing Pool with its three advertised pages.
    /// </summary>
    private async Task SendWishingPoolMenuAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                WishingPoolPages,
                npc.NpcKey),
            cancellationToken,
            "WishingPoolMenu");
        Console.WriteLine(
            $"[npc] wishing pool open npc={npc.InteractionId} key={npc.NpcKey}");
    }

    /// <summary>
    /// Replays the reference server's answer for the Wishing Pool. The captured
    /// exchange is page-keyed: the client sends the page it is on as the dialog
    /// field, and the server answers each page's selection with that page's own
    /// message numbers.
    /// </summary>
    /// <remarks>
    /// Transcribed from <c>docs/wishing-pool-capture-20260915.md</c> section 2:
    /// <c>10069 {5212, 16, 16, -1}</c> is answered <c>10070 {5212, 16, 101, 201}</c>
    /// and <c>10069 {5212, 24, 24, -1}</c> is answered <c>10070 {5212, 24, 101, 1, 2}</c>.
    /// Pages 16 and 50 are menu pages, so their answer lists that page's buttons;
    /// page 24 is an instruction page, whose <c>-1</c> answer opens its two
    /// choices. Only the dialogue is modelled - the wishing economy (level gate,
    /// cooldown, streak and exp reward) is not implemented.
    /// </remarks>
    private async Task HandleWishingPoolActionAsync(
        uint npcId,
        int page,
        int selection,
        CancellationToken cancellationToken)
    {
        // Page 16 is the wish page. Its two entries are the script's tail-1
        // buttons: 101 "Make wishes for free" (NF_L0_JN101) and 201 "Throw Gold
        // into the Wishing pool" (NF_L0_JN201). A class then runs the free wish.
        if (page == 16)
        {
            if (selection is 101 or 201)
            {
                // Both entries - 101 "Make wishes for free" and 201 "Throw Gold into
                // the Wishing pool" - ask which skill book to wish for. The class
                // choices are the script's own buttons 301 Warrior, 401 Champion, 501
                // Mage and 601 Priest.
                //
                // The tail-0 family (100/200/300/400/500) is deliberately absent.
                // Those are the script's result texts and each one ends with
                // NPCFUN:EndMessage(true); sending one alongside the class buttons
                // made the client treat the whole answer as a result and close the
                // window, so the class buttons could never be clicked.
                //
                // The class click that follows cannot tell the two entries apart -
                // both send the same 301-601 - so the paid entry is remembered until
                // that click arrives.
                _wishingPoolPaidWishPending = selection == 201;
                await SendWishingPoolPageAsync(
                    npcId, page, [301, 401, 501, 601],
                    "WishingPoolClasses",
                    cancellationToken);
                return;
            }

            if (selection is 301 or 401 or 501 or 601)
            {
                var paid = _wishingPoolPaidWishPending;
                _wishingPoolPaidWishPending = false;
                if (!WishingPoolCatalog.TryResolveClassButton(
                        selection,
                        out var characterClass))
                {
                    return;
                }

                await HandleWishingPoolWishAsync(
                    npcId,
                    page,
                    characterClass,
                    paid,
                    cancellationToken);
                return;
            }

            _wishingPoolPaidWishPending = false;
            await SendWishingPoolPageAsync(
                npcId, page, [101, 201], "WishingPoolWishPage",
                cancellationToken);
            return;
        }

        if (page == WishingPoolLostBookPage)
        {
            // The reference server's answer to this page is captured: it replies
            // [101, 1, 2]. Either of the two book buttons leads to the wish itself,
            // which this server does not run, so the answer is the script's own
            // missing-book line instead.
            var lostBookReply = selection is 1 or 2
                ? WishingPoolLostBookNoBook
                : WishingPoolLostBookMenu;
            await SendWishingPoolPageAsync(
                npcId,
                page,
                lostBookReply,
                selection is 1 or 2
                    ? "WishingPoolLostBookNoBook"
                    : "WishingPoolLostBookMenu",
                cancellationToken);
            return;
        }

        if (page == WishingPoolLuckyGodsPage)
        {
            // The divine wish is its own service with its own durable state, so it
            // is answered in the LuckyGods partial rather than on this page table.
            await HandleLuckyGodsActionAsync(
                npcId,
                page,
                selection,
                cancellationToken);
            return;
        }

        await SendWishingPoolPageAsync(
            npcId, page, [101, 1, 2], "WishingPoolPage", cancellationToken);
    }

    /// <summary>
    /// Runs one wish end to end: gate, tier, skill book, cost, dialog.
    /// </summary>
    /// <remarks>
    /// The outcome numbers are the client script's own: <c>JN100</c> is reported
    /// as <c>100</c> ("players below level 30 cannot make wishes"), <c>JN500</c> as
    /// <c>500</c> ("you have used up your free chances"), a wait is
    /// <c>minutes * 100 + seconds</c> with tail <c>2</c>, and the three results are
    /// <c>103</c> / <c>203</c> / <c>303</c> for common, advanced and ultimate.
    /// <para>
    /// The paid entry charges <see cref="WishingPoolPaidWishGoldCost"/> gold and does
    /// not consume the free allowance, matching the script's own wording: <c>JN103</c>
    /// offers "spend 230 Gold in making another wish" and <c>JN300</c> reports
    /// "you don't have enough Gold to throw into the wishing pool".
    /// </para>
    /// </remarks>
    private async Task HandleWishingPoolWishAsync(
        uint npcId,
        int page,
        byte characterClass,
        bool paid,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (!paid && _wishingPoolUsage is null)
        {
            Console.WriteLine("[wishing-pool] free wish unavailable: no usage store");
            return;
        }

        if (_character.Level < WishingPoolMinimumLevel)
        {
            await SendWishingPoolPageAsync(
                npcId, page, [100], "WishingPoolLevelGate", cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        WishingPoolUsage usage = default;
        if (!paid)
        {
            usage = await _wishingPoolUsage!.ReadAsync(
                _character.Id,
                _realmCalendar,
                now,
                cancellationToken);
            if (usage.Remaining <= 0)
            {
                await SendWishingPoolPageAsync(
                    npcId, page, [500], "WishingPoolExhausted", cancellationToken);
                return;
            }

            if (usage.LastUsedAt is { } lastUsedAt)
            {
                var readyAt = lastUsedAt + WishingPoolUsage.Interval;
                if (readyAt > now)
                {
                    var wait = readyAt - now;
                    var encoded = checked(
                        (int)wait.TotalMinutes * 100 + wait.Seconds);
                    await SendWishingPoolPageAsync(
                        npcId, page, [encoded * 100 + 2], "WishingPoolWait",
                        cancellationToken);
                    return;
                }
            }
        }
        // The paid entry's balance is checked and charged inside the grant
        // transaction, against the locked database row. There is deliberately no
        // check on the cached _character.Gold here: a stale cache would refuse a
        // wish the account can pay for, and the script's "not enough Gold" text is
        // reported when the store rejects the charge.

        var grant = await TryGrantWishingPoolSkillBookAsync(
            characterClass,
            paid ? WishingPoolPaidWishGoldCost : 0,
            cancellationToken);
        switch (grant.Outcome)
        {
            case WishingPoolGrantOutcome.InsufficientGold:
                // JN300 "you don't have enough Gold to throw into the wishing pool".
                await SendWishingPoolPageAsync(
                    npcId, page, [300], "WishingPoolGoldGate", cancellationToken);
                return;
            case WishingPoolGrantOutcome.InsufficientCapacity:
                // JN400's full-bag text.
                await SendWishingPoolPageAsync(
                    npcId, page, [400], "WishingPoolBagFull", cancellationToken);
                return;
            case WishingPoolGrantOutcome.Failed:
                Console.WriteLine(
                    $"[wishing-pool] wish failed class={characterClass} " +
                    $"paid={paid}");
                return;
            default:
                break;
        }


        if (!paid)
        {
            await _wishingPoolUsage!.RecordAsync(
                _character.Id,
                _realmCalendar,
                now,
                grant.ItemId,
                cancellationToken);
        }

        var advanced = WishingPoolCatalog.IsAdvancedTier(grant.SkillLevel);
        await SendWishingPoolPageAsync(
            npcId,
            page,
            [advanced
                ? WishingPoolAdvancedResultSubId
                : WishingPoolOrdinaryResultSubId],
            "WishingPoolResult",
            cancellationToken);
        // Only the advanced tier (levels 3-4) is announced; ordinary books
        // stay private. The tier is the draw's own split, not a second
        // threshold that could drift away from it.
        if (advanced)
        {
            await BroadcastWishingPoolGrantAsync(
                grant.ItemId,
                cancellationToken);
        }
    }

    /// <summary>
    /// Announces an advanced skill book to the realm through the client's own
    /// skill-blessing broadcast (opcode 10038, type 0).
    /// </summary>
    /// <remarks>
    /// The line is the client's: it renders its own
    /// "雅典玩家-&lt;名字&gt;-受到神的祝福,获得: &lt;技能&gt;" for Athens and
    /// "斯巴达玩家-&lt;名字&gt;-受到神的青睐,获得: &lt;技能&gt;" for Sparta from the
    /// camp and the book's item id, which is the same frame the reference server
    /// sends when a skill book is consumed (captured 2026-09-24).
    /// </remarks>
    private async Task BroadcastWishingPoolGrantAsync(
        int skillBookItemId,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        // The announcement is best effort: the book is already granted, so an
        // unusable camp skips the line instead of failing the wish.
        if (_character.Camp is not (GameDefaults.SpartaCamp or GameDefaults.AthensCamp))
        {
            Console.WriteLine(
                "[wishing-pool] skill broadcast skipped: " +
                $"unrecognized camp={_character.Camp}");
            return;
        }

        // Realm-wide, not map-wide: every session this process serves receives it,
        // whichever map the recipient is on. The personal channel is deliberately
        // not used - that one is a scrolling log line, not a centred banner.
        await _registry.BroadcastToAllSessionsAsync(
            PacketBuilder.SkillBookBroadcast(
                _character.Name,
                _character.Camp,
                skillBookItemId),
            cancellationToken,
            label: "WishingPoolSkillBroadcast");
    }

    private async Task SendWishingPoolPageAsync(
        uint npcId,
        int page,
        int[] reply,
        string reason,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                page,
                reply),
            cancellationToken,
            reason);
        Console.WriteLine(
            $"[npc] wishing pool npc={npcId} page={page} " +
            $"{reason}={string.Join(',', reply)}");
    }

    private async ValueTask SendNpcDescriptionOpenAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcDescriptionDialogOpenAck(
                npc.InteractionId,
                npc.NpcKey),
            cancellationToken,
            "NpcDescriptionDialogOpenAck");
        Console.WriteLine(
            $"[npc] description open npc={npc.InteractionId} " +
            $"key={npc.NpcKey}");
    }
}
