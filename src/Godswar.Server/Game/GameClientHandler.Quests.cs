using System.Buffers.Binary;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>The quest the server last offered, used when the client answers
    /// an accept without repeating the id (C2S 10081).</summary>
    private uint _offeredQuestId;

    // Quest ids are the client's own (Quest.xml / Text/Quest), so the client
    // renders real text. A character may carry several quests at once, each with
    // its own objective progress, and acceptance is always driven by the id the
    // client sends: the chain supplies the giver and responder for whatever id
    // comes in, so a new quest only needs a new chain row.

    /// <summary>
    /// How many quests one character may carry at once.
    /// </summary>
    /// <remarks>
    /// Carried, not finished: a quest handed in is a completed row and takes no
    /// slot. The login snapshot publishes every carried quest - a 96-byte
    /// descriptor plus eight 72-byte reward records and an eight-byte tail, so
    /// the frame grows beyond its captured 2048 bytes when twenty quests need
    /// the room (<c>8 + 20 * 680 = 13608</c>).
    /// </remarks>
    internal const int MaximumCarriedQuests = 20;

    /// <summary>
    /// Publishes the accepted-quest snapshot: the client's list of the quests it
    /// is working on, each with its giver, responder and the objective it is
    /// counting right now.
    /// </summary>
    /// <remarks>
    /// The snapshot is the client's own state of the quest log, so it goes out on
    /// world entry and again whenever the objective a carried quest is counting
    /// changes. A quest with several targets - 528 asks for one Addiya the
    /// Destroyer and eight Fake Treasures - is published one target at a time, so
    /// finishing the first has to republish the quest or the window keeps waiting
    /// for a monster that is already dead and never shows the rest.
    /// <para>
    /// A character with no quests gets an empty snapshot; quests are only ever
    /// accepted by hand at the npc, so nothing appears here on its own.
    /// </para>
    /// </remarks>
    private async Task SendQuestSnapshotAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var entries = new List<PacketBuilder.QuestSnapshotEntry>();
        foreach (var quest in _character.Quests)
        {
            if (StarterQuestChain.Find(quest.QuestId) is not { } step)
            {
                continue;
            }

            var objectives = DisplayQuestObjectives(quest.QuestId);
            // Every target the quest names is published, not just the one still
            // open: the client draws one line per filled slot, so sending only the
            // active objective is what made a multi-target quest show a single
            // line. The reference server's own answer for the two-target quest
            // 1533 carries both targets, and the same parallel slots are used
            // here. Progress travels per slot, so finishing the first target
            // updates its line instead of hiding the rest.
            var targets = new PacketBuilder.QuestSnapshotObjective[
                objectives.Count];
            for (var slot = 0; slot < objectives.Count; slot++)
            {
                targets[slot] = new PacketBuilder.QuestSnapshotObjective(
                    objectives[slot].MonsterId,
                    objectives[slot].Required,
                    StarterQuestObjectives.Counter(quest.Progress, slot));
            }

            entries.Add(new PacketBuilder.QuestSnapshotEntry(
                step.QuestId,
                await ResolveQuestNpcIdForSnapshotAsync(
                    step.GiverKey,
                    cancellationToken),
                await ResolveQuestNpcIdForSnapshotAsync(
                    step.ResponderKey,
                    cancellationToken),
                Objectives: targets,
                RequirementsSatisfied: AreQuestRequirementsSatisfied(_character, quest)));
            if (entries.Count == MaximumCarriedQuests)
            {
                break;
            }
        }

        await _session.SendAsync(
            PacketBuilder.QuestSnapshot(entries),
            cancellationToken,
            "LoginQuestSnapshot",
            framed: false);
        // The mark lists follow the snapshot: they are what the client's quest
        // search reads, and they have to match the quests just published.
        await SendQuestNpcMarksAsync(cancellationToken);
        Console.WriteLine(
            $"[quest] snapshot character={_character.Name} reason={reason} " +
            $"carried={entries.Count} of={_character.Quests.Count}");
    }

    /// <summary>
    /// Re-sends the npc quest tables so the quest mark matches the character again.
    /// </summary>
    /// <remarks>
    /// The tables are per npc and carry the "available here" flag, so they have to
    /// follow every change to what the character may take - which is why the hand-in
    /// path already sends them. A deleted quest now does the same, otherwise the
    /// npc that offers it keeps showing nothing.
    /// </remarks>
    private async Task SendQuestRefreshAsync(
        uint changedQuestId,
        CancellationToken cancellationToken)
    {
        // The global mark lists go out whatever the per-npc tables resolve to: a
        // drop can change which npc has something for the character, and the npc a
        // dropped quest came from is not necessarily the one that now offers it.
        await SendQuestNpcMarksAsync(cancellationToken);

        var acceptable = AcceptableQuests();
        var npcId = acceptable.Count > 0
            ? ResolveQuestNpcId(acceptable[0].GiverKey)
            : StarterQuestChain.Find(changedQuestId) is { } changed
                ? ResolveQuestNpcId(changed.GiverKey)
                : 0u;
        if (npcId == 0)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.QuestMarkerList(npcId, QuestMarkerEntries(npcId, acceptable)),
            cancellationToken,
            "QuestMarkerList",
            framed: false);
        await _session.SendAsync(
            PacketBuilder.QuestHandInMenu(npcId, QuestHandInEntries(npcId)),
            cancellationToken,
            "QuestHandInList",
            framed: false);
        QuestFrameTrace.Append(
            $"[quest] refreshed npc={npcId} acceptable=" +
            $"{string.Join(',', acceptable.Select(step => step.QuestId))} " +
            $"character={_character?.Name}",
            []);
    }

    /// <summary>
    /// Publishes the global quest-mark lists: which npcs have a quest the
    /// character may accept, and which take back one it can hand in.
    /// </summary>
    /// <remarks>
    /// These are what the client's quest search reads. The per-npc 10077 tables
    /// carry the same availability flag, but they only travel with an npc the
    /// client has already streamed in, so a player standing in the capital cannot
    /// see that an npc on the other side of the map has something to offer. The
    /// reference server publishes both lists at login and again whenever the quest
    /// state changes - its own 10078 arrives in the same burst as the hand-in
    /// acknowledgement.
    /// <para>
    /// A list is only sent when it has something in it. Every captured sample
    /// carries at least one id - the reference never sent an empty one - and the
    /// per-npc tables are what clear a mark that is no longer wanted.
    /// </para>
    /// </remarks>
    private async Task SendQuestNpcMarksAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        // The npcs that give a quest the character may take now: the open
        // main-line step's giver, plus every npc whose level-gated quests are open.
        // The main line can also expose its five optional branches; the level
        // gate can clear several rows at once, making a repeatable quest's npc show its
        // mark the moment the character is the right level.
        var available = new List<uint>();
        var resolvedGivers = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var acceptable in AcceptableQuests())
        {
            if (acceptable.GiverMapId >= 0 && acceptable.GiverMapId != _character.CurrentMap)
            {
                continue;
            }

            if (!resolvedGivers.TryGetValue(acceptable.GiverKey, out var giver))
            {
                // The snapshot-time resolver, not the in-memory catalog: this runs
                // at login before the npc catalog is installed, where the plain
                // lookup falls back to zero and the list would come out empty.
                giver = await ResolveQuestNpcIdForSnapshotAsync(
                    acceptable.GiverKey,
                    cancellationToken);
                resolvedGivers[acceptable.GiverKey] = giver;
            }

            if (giver != 0 && !available.Contains(giver))
            {
                available.Add(giver);
            }
        }

        // The npcs that take back a carried quest whose objectives are all met.
        var handIn = new List<uint>();
        foreach (var quest in _character.Quests)
        {
            if (StarterQuestChain.Find(quest.QuestId) is not { } step)
            {
                continue;
            }

            if (step.ResponderMapId >= 0 && step.ResponderMapId != _character.CurrentMap)
            {
                continue;
            }

            var objectives = StarterQuestObjectives.For(quest.QuestId);
            if (!AreQuestRequirementsSatisfied(_character, quest))
            {
                continue;
            }

            var responder = await ResolveQuestNpcIdForSnapshotAsync(
                step.ResponderKey,
                cancellationToken);
            if (responder != 0 && !handIn.Contains(responder))
            {
                handIn.Add(responder);
            }
        }

        if (available.Count > 0)
        {
            await _session.SendAsync(
                PacketBuilder.QuestNpcMarks(
                    Opcodes.QuestAvailableNpcList,
                    available),
                cancellationToken,
                "QuestAvailableNpcList",
                framed: false);
        }

        if (handIn.Count > 0)
        {
            await _session.SendAsync(
                PacketBuilder.QuestNpcMarks(
                    Opcodes.QuestHandInNpcList,
                    handIn),
                cancellationToken,
                "QuestHandInNpcList",
                framed: false);
        }
    }

    /// <summary>
    /// Republishes the quest marks after the character's accept list changed for a
    /// reason other than a quest event - today, a level-up.
    /// </summary>
    /// <remarks>
    /// A level-gated quest opens on its band alone, so gaining a level is what
    /// unlocks it. Without this the new quest would not appear until the next
    /// hand-in or relog: the mark lists are only re-sent from those two paths, and
    /// the per-npc tables are only streamed with an npc.
    /// <para>
    /// The map's quest npcs are re-sent so the mark moves on the npcs already on
    /// screen, not just in the quest search. Only npcs whose rows are relevant to
    /// this character are included. Exact-level daily givers are also included
    /// after they close, so their old offer flags are cleared immediately.
    /// </para>
    /// </remarks>
    private async Task SendQuestLevelUpRefreshAsync(CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var acceptable = AcceptableQuests();
        await SendQuestNpcMarksAsync(cancellationToken);

        var givers = new HashSet<uint>();
        foreach (var step in acceptable)
        {
            var giver = ResolveQuestNpcId(step.GiverKey);
            if (giver != 0)
            {
                givers.Add(giver);
            }
        }

        // Closed exact-level offers also need refreshed NPC tables.
        foreach (var step in StarterQuestChain.Steps)
        {
            if (step.RequiresExactLevel && step.GiverMapId == _character.CurrentMap)
            {
                var giver = ResolveQuestNpcId(step.GiverKey);
                if (giver != 0)
                {
                    givers.Add(giver);
                }
            }
        }

        foreach (var quest in _character.Quests)
        {
            if (StarterQuestChain.Find(quest.QuestId) is not { } carried)
            {
                continue;
            }

            var responder = ResolveQuestNpcId(carried.ResponderKey);
            if (responder != 0)
            {
                givers.Add(responder);
            }
        }

        foreach (var npcId in givers)
        {
            await _session.SendAsync(
                PacketBuilder.QuestMarkerList(
                    npcId,
                    QuestMarkerEntries(npcId, acceptable)),
                cancellationToken,
                "QuestLevelUpMarkerList",
                framed: false);
            await _session.SendAsync(
                PacketBuilder.QuestHandInMenu(
                    npcId,
                    QuestHandInEntries(npcId)),
                cancellationToken,
                "QuestLevelUpHandInList",
                framed: false);
        }

        Console.WriteLine(
            $"[quest] level-up refresh character={_character.Name} " +
            $"level={_character.Level} acceptable={acceptable.Count} " +
            $"npcs={givers.Count}");
    }

    /// <summary>
    /// Tells the client which carried quests are ready to hand in.
    /// </summary>
    /// <remarks>
    /// It is published from the client's EnterUiReady, not with the login snapshot,
    /// because that is where the client can take it: sending it ahead of the world
    /// entry made the client dereference npc zero and crash, and the reference
    /// capture only ever shows it in the middle of play, immediately behind the
    /// kill that finished the quest.
    /// <para>
    /// How far along a quest is now travels in the snapshot descriptor itself, so
    /// nothing replays the per-kill progress frames on login - doing that would add
    /// the same kills a second time. What the snapshot does not say is whether the
    /// quest may be handed in, and that is what this frame carries.
    /// </para>
    /// <para>
    /// It deliberately does not re-send the accept answer either: that made the
    /// client call acceptQuest for a quest it already held, which errored and then
    /// crashed.
    /// </para>
    /// </remarks>
    private async Task SendLoginQuestProgressAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        foreach (var quest in _character.Quests)
        {
            var objectives = StarterQuestObjectives.For(quest.QuestId);
            if (StarterQuestChain.Find(quest.QuestId) is not { } step ||
                !AreQuestRequirementsSatisfied(_character, quest))
            {
                continue;
            }

            // The in-memory npc catalog is installed after this runs, so the
            // synchronous resolver reads zero here; the snapshot resolver reads the
            // published map content instead. A frame naming responder zero is what
            // crashed the client the moment a quest's objectives were met while the
            // character was offline, so an unresolvable responder is skipped
            // outright rather than published.
            var responderNpcId = await ResolveQuestNpcIdForSnapshotAsync(
                step.ResponderKey,
                cancellationToken);
            if (responderNpcId == 0)
            {
                Console.Error.WriteLine(
                    $"[quest] skipped objectives-met without a responder " +
                    $"character={_character.Name} quest={step.QuestId} " +
                    $"responder={step.ResponderKey} map={_character.CurrentMap}");
                continue;
            }

            await _session.SendAsync(
                PacketBuilder.QuestConfirm(
                    responderNpcId,
                    step.QuestId),
                cancellationToken,
                "LoginQuestObjectivesMet",
                framed: false);
            Console.Error.WriteLine(
                $"[quest] login objectives met character={_character.Name} " +
                $"quest={step.QuestId} progress={quest.Progress} " +
                $"responder={responderNpcId} " +
                $"objectives={DescribeObjectives(objectives)}");
        }
    }

    /// <summary>Handles C2S 10083, the per-quest state query.</summary>
    /// <remarks>
    /// The client names a quest at <c>+8</c> (the scene key at <c>+4</c> is not
    /// echoed) and the answer carries that quest with its responder npc.
    /// <para>
    /// This is the quest window's delete button. Every click of it produced one of
    /// these and nothing else - three clicks, three packets - and the reference
    /// capture shows the same thing: it asks about the quest it is holding
    /// seconds before going back to the giver. So the answer confirms the removal
    /// and the row has to go with it: while only the client dropped the quest, the
    /// next login published it again and the player saw a quest they had deleted
    /// come back as if it had been accepted for them.
    /// </para>
    /// A quest the character is not carrying is simply answered, which is what the
    /// duplicate clicks produce once the first one has removed it.
    /// </remarks>
    private async Task HandleQuestSceneQueryAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || packet.Payload.Length < 12)
        {
            return;
        }

        var sceneKey = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload.Slice(4, sizeof(uint)));
        var requestedQuestId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(uint)));

        var removed = FindCarriedQuest(requestedQuestId);
        if (removed is not null)
        {
            _character.Quests.Remove(removed);
            try { await SaveQuestStateAsync(cancellationToken); }
            catch
            {
                _character.Quests.Add(removed);
                _character.Quests.Sort(static (left, right) => left.QuestId.CompareTo(right.QuestId));
                throw;
            }
            QuestFrameTrace.Append(
                $"[quest] dropped character={_character.Name} " +
                $"quest={removed.QuestId} carried={_character.Quests.Count}",
                []);
            Console.Error.WriteLine(
                $"[quest] dropped character={_character.Name} " +
                $"quest={removed.QuestId} carried={_character.Quests.Count}");

        }

        // Del must acknowledge the selected quest, including duplicate clicks.
        // Substituting the first available quest leaves the selected client row
        // alive even though its server state has already been removed.
        var step = StarterQuestChain.Find(requestedQuestId);
        if (step is not { } answered)
        {
            return;
        }

        if (_offeredQuestId == requestedQuestId)
        {
            _offeredQuestId = 0;
        }
        var responderNpcId = await ResolveQuestNpcIdForSnapshotAsync(
            answered.ResponderKey, cancellationToken);
        await _session.SendAsync(
            PacketBuilder.QuestSceneOfferAck(answered.QuestId, responderNpcId),
            cancellationToken,
            "QuestSceneOffer",
            framed: false);
        if (removed is not null)
        {
            await SendQuestSnapshotAsync("quest-deleted", cancellationToken);
            await SendQuestRefreshAsync(removed.QuestId, cancellationToken);
        }
        // stderr: stdout diagnostics are folded into counters by the legacy log
        // suppressor, so this is the channel that survives.
        Console.Error.WriteLine(
            $"[quest] scene query scene=0x{sceneKey:x8} " +
            $"asked={requestedQuestId} answered={answered.QuestId} " +
            $"npc={responderNpcId} character={_character.Name}");
    }

    /// <summary>Handles C2S 10081, the quest picked in a giver's window.</summary>
    /// <remarks>
    /// Captured shape (12 bytes): <c>+4</c> is zero and <c>+8</c> is the quest the
    /// player clicked - the packet's payload is only 8 bytes, so the quest sits at
    /// payload <c>+4</c>. The id is taken from the packet when it names a chain
    /// quest and from the last offer otherwise.
    /// <para>
    /// The accept answer is what fills the window in: with the objective written
    /// into it the client shows "0 of 10" while the player is still deciding.
    /// Sending the reference's 360-byte detail frame instead - which is what one
    /// reference session did before its client confirmed - made the window lose
    /// that line entirely, so this stays a single step.
    /// </para>
    /// </remarks>
    private async Task HandleQuestSelectionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || packet.Payload.Length < 8)
        {
            return;
        }

        var requestedQuestId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload.Slice(4, sizeof(uint)));
        var selectedQuestId = requestedQuestId;
        var usedFallback = false;
        if (StarterQuestChain.Find(selectedQuestId) is null)
        {
            selectedQuestId = _offeredQuestId;
            usedFallback = true;
        }

        // Diagnostic only: this path decides which quest a "select" accepts, and the
        // player-visible symptom of getting it wrong is a quest that is taken but
        // never appears in the list. The raw first words are logged so the packet
        // layout can be read off a real reproduction instead of inferred.
        Console.Error.WriteLine(
            $"[quest-select] raw={Convert.ToHexString(packet.Payload)} " +
            $"body+0={BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(0, 4))} " +
            $"body+4={requestedQuestId} fallback={usedFallback} " +
            $"offered={_offeredQuestId} selected={selectedQuestId} " +
            $"open=[{string.Join(',', AcceptableQuests().Select(step => step.QuestId))}] " +
            $"carried=[{string.Join(',', _character.Quests.Select(quest => quest.QuestId))}] " +
            $"character={_character.Name}");

        // 本客户端的"接受"就是这个 10081（它不发 C2S 10082），所以这里必须落任务状态 ——
        // 否则客户端以为接了、服务端库里没有，带击杀目标的任务永远完不成。
        await AcceptQuestAsync(selectedQuestId, cancellationToken);
    }

    /// <summary>Handles C2S 10082, the captured accept action.</summary>
    /// <remarks>
    /// Captured shape: <c>+12</c> is the quest being accepted (518, then 519 in
    /// the second cycle). The two leading words are client pointers, not ids.
    /// </remarks>
    private async Task HandleQuestActionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || packet.Payload.Length < 12)
        {
            return;
        }

        var questId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload.Slice(8, sizeof(uint)));
        // Diagnostic only, same reason as the select path above.
        Console.Error.WriteLine(
            $"[quest-accept-req] len={packet.Payload.Length} " +
            $"body+4={BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.Slice(4, 4))} " +
            $"body+8={questId} offered={_offeredQuestId} " +
            $"open=[{string.Join(',', AcceptableQuests().Select(step => step.QuestId))}] " +
            $"carried=[{string.Join(',', _character.Quests.Select(quest => quest.QuestId))}] " +
            $"character={_character.Name}");
        await AcceptQuestAsync(questId, cancellationToken);
    }

    /// <summary>
    /// Answers a quest acceptance for <paramref name="questId"/>.
    /// </summary>
    /// <remarks>
    /// The chain row supplies the giver and responder, so the whole exchange is
    /// the same three captured frames for every quest. A character may hold
    /// several quests, so the only refusals are a quest it has already finished or
    /// is already carrying; a quest is never accepted twice.
    /// </remarks>
    private async Task AcceptQuestAsync(
        uint questId,
        CancellationToken cancellationToken,
        bool commit = true)
    {
        if (_character is null || StarterQuestChain.Find(questId) is not { } step)
        {
            return;
        }

        if (FindCarriedQuest(questId) is not null)
        {
            Console.Error.WriteLine(
                $"[quest-accept] already carried character={_character.Name} " +
                $"quest={questId}");
            return;
        }

        // The carried-quest limit. The chain's own gating is unchanged; this only
        // refuses once the character is already holding as many as it may, and the
        // check comes before the accept frames so a refused quest leaves no state
        // and no window behind.
        if (_character.Quests.Count >= MaximumCarriedQuests)
        {
            Console.Error.WriteLine(
                $"[quest-accept] full character={_character.Name} " +
                $"quest={questId} carried={_character.Quests.Count} " +
                $"maximum={MaximumCarriedQuests}");
            return;
        }

        var scrollSlot = step.IsQuestScroll ? FindQuestScrollSlot(step.QuestId) : -1;
        if (!CanAcceptQuest(_character, step, QuestDailyState.Today(), fromScroll: scrollSlot >= 0))
        {
            Console.Error.WriteLine(
                $"[quest-accept] locked character={_character.Name} " +
                $"quest={questId}");
            return;
        }

        var giverNpcId = await ResolveQuestNpcIdForSnapshotAsync(step.GiverKey, cancellationToken);
        var responderNpcId = await ResolveQuestNpcIdForSnapshotAsync(step.ResponderKey, cancellationToken);
        if ((!step.IsQuestScroll && giverNpcId == 0) ||
            (step.ResponderKey.Length > 0 && responderNpcId == 0))
        {
            return;
        }

        // 点开任务（C2S 10081）要发的帧必须和"接受任务"那条**完全相同**，只差一步：
        // 不落任务状态。之前详情只发 10076（详情帧，客户端只读一条记录 → 只显示一个
        // 物品），或只发 10082（缺配对应答帧 → 一个物品且字段错位）；接任务那条是
        // 10082 + 配对应答，客户端就能画出完整记录区（519 的四个职业武器）。
        if (!commit)
        {
            await _session.SendAsync(
                PacketBuilder.QuestAnswer(giverNpcId, responderNpcId, step.QuestId),
                cancellationToken,
                "QuestDetailAnswer",
                framed: false);
            await _session.SendAsync(
                PacketBuilder.AcceptPairAckFrame(),
                cancellationToken,
                "QuestDetailPairAck",
                framed: false);
            Console.Error.WriteLine(
                $"[quest] detail only character={_character.Name} quest={step.QuestId} " +
                $"giver={giverNpcId} responder={responderNpcId}");
            return;
        }

        var accepted = new CharacterQuest { QuestId = step.QuestId };
        RecordQuestExploration(_character, accepted);
        if (step.IsQuestScroll)
        {
            if (_account is null || scrollSlot < 0) return;
            var scroll = KitBagSlots.GetItem(_character.KitBag, scrollSlot);
            if (!await _store.ConsumeQuestBagItemAsync(_account.Id, _character.Id, scrollSlot,
                scroll.Id, scroll.Stack, [], [], step.QuestId, cancellationToken)) return;
            _character.KitBag = ConsumeQuestBagProjection(_character.KitBag, scrollSlot);
        }
        _character.Quests.Add(accepted);
        _character.Quests.Sort(static (left, right) => left.QuestId.CompareTo(right.QuestId));
        try { await SaveQuestStateAsync(cancellationToken); }
        catch
        {
            // A scroll acceptance was already committed atomically with its item.
            if (!step.IsQuestScroll) _character.Quests.Remove(accepted);
            throw;
        }
        _offeredQuestId = 0;
        await _session.SendAsync(
            PacketBuilder.QuestAnswer(giverNpcId, responderNpcId, step.QuestId),
            cancellationToken,
            "QuestAcceptAnswer",
            framed: false);

        await _session.SendAsync(
            PacketBuilder.AcceptPairAckFrame(),
            cancellationToken,
            "QuestAcceptPairAck",
            framed: false);

        // 10084 means "the objectives are met, go and hand it in". A quest with
        // nothing to kill is met the moment it is taken - that is why the capture
        // has it for the talk quest 518 - but sending it for a kill quest told the
        // client the work was already done, so the window came up full (10 of 10
        // for a quest asking for ten) and every kill pushed it past that.
        var objectives = StarterQuestObjectives.For(step.QuestId);
        if (AreQuestRequirementsSatisfied(_character, accepted))
        {
            await _session.SendAsync(
                PacketBuilder.QuestConfirm(responderNpcId, step.QuestId),
                cancellationToken,
                "QuestAcceptConfirm",
                framed: false);
        }

        if (step.IsQuestScroll) await SendKitBagRefreshAsync(cancellationToken);
        Console.WriteLine(
            $"[quest] accepted character={_character.Name} " +
            $"quest={step.QuestId} giver={giverNpcId} responder={responderNpcId} " +
            $"carried={_character.Quests.Count}");
    }

    /// <summary>Handles C2S 10091, the quest window's two-in-one request.</summary>
    /// <remarks>
    /// One opcode carries two client actions and only <c>+4</c> tells them apart.
    /// <c>0x00810EFB</c> is the pair that follows an accept: all 33 captured
    /// samples sit directly behind a C2S 10082. A small word - 16 in the captures,
    /// 4 once - is the lookup panel's own request, and none of those 35 samples
    /// follows a 10082. Both are answered with the same frame, because in both
    /// cases the answer is the character's current acceptable-quest list: right
    /// after an accept that list is empty, which is exactly the all-zero frame the
    /// reference sent on the accept path.
    /// </remarks>
    private async Task HandleQuestActionPairAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || packet.Payload.Length < 4)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.QuestLookupAnswer(AcceptableQuestIds(_character)),
            cancellationToken,
            "QuestLookup",
            framed: false);
    }

    /// <summary>Handles C2S 10084, the hand-in request.</summary>
    /// <remarks>
    /// Captured shape: <c>+4</c> is the quest being handed in. The reward comes
    /// from that quest's chain row, so no quest carries its own reward code, and
    /// the hand-in is refused while the quest still has unsatisfied kill
    /// objectives. Handing in clears that one quest - and only that one, since a
    /// character may be carrying others - and offers the next one without
    /// accepting it.
    /// </remarks>
    private async Task HandleQuestHandInAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null || packet.Payload.Length < 8)
        {
            return;
        }

        var requestedQuestId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload.Slice(4, sizeof(uint)));
        // The client names the reward slot it wants paid in the same request: 0
        // for a quest that pays one item, the chosen slot for the class-weapon
        // choice, -1 for a quest that pays nothing. The acknowledgement repeats
        // it and the client pays out that slot, so sending a slot the quest does
        // not have is the 004AC835 fault (Athens 1522, whose slots are all free,
        // was being answered with the template's 0).
        var rewardIndex = packet.Payload.Length >= 12
            ? BinaryPrimitives.ReadUInt32LittleEndian(
                packet.Payload.Slice(8, sizeof(uint)))
            : 0u;
        var step = StarterQuestChain.Find(requestedQuestId);
        if (step is not { } rewarded)
        {
            return;
        }

        var carried = FindCarriedQuest(rewarded.QuestId);
        if (carried is null || rewarded.Camp != ChainCampFor(_character.Camp) ||
            !HasDailyAllowance(_character, rewarded, QuestDailyState.Today()) ||
            (rewarded.MaxCompletionsPerDay == 0 &&
             _character.QuestCompletedIds.Contains(rewarded.QuestId)))
        {
            return;
        }

        var objectives = StarterQuestObjectives.For(rewarded.QuestId);
        if (!AreQuestRequirementsSatisfied(_character, carried))
        {
            // The client offers the quest as handable, but the objectives are the
            // server's own: until every target has been killed the required number
            // of times there is nothing to hand in.
            Console.Error.WriteLine(
                $"[quest-handin] objectives incomplete character=" +
                $"{_character.Name} quest={rewarded.QuestId} " +
                $"progress={carried?.Progress ?? 0} " +
                $"required={DescribeObjectives(objectives)}");
            return;
        }

        Console.WriteLine(
            $"[quest] hand-in character={_character.Name} quest={rewarded.QuestId}");

        // The GM tool owns a quest's payout when it has a row for it; otherwise
        // the quest's own captured values stand. The override is read once here,
        // so a hand-in cannot pay half from each source.
        var payout = QuestRewardContentCatalog.Current.Resolve(
            rewarded.QuestId,
            new QuestRewardPayout(
                rewarded.Experience,
                rewarded.TalentPoints,
                rewarded.Silver,
                rewarded.Gold));

        // The durable quest experience appraisal scales this hand-in's
        // experience. Monster rewards keep their own multiplier path: the
        // appraisal is quest-only, which is what the client's own SM_L0_06 text
        // promises ("完成任务时可额外获得10%的经验").
        var rewardExperience = await ScaleQuestRewardExperienceAsync(
            payout.Experience,
            cancellationToken);
        var progression = await _store.ApplyMonsterKillRewardAsync(
            _account?.Id ?? 0,
            _character.Id,
            rewardExperience,
            payout.TalentPoints,
            cancellationToken);
        var levelUps = progression?.LevelUps ?? [];
        if (progression is not null)
        {
            _character.Level = progression.CurrentLevel;
            _character.Experience = progression.CurrentExperience;
            _character.TalentExperience = progression.CurrentTalentExperience;
            _character.TalentPoints = progression.CurrentTalentPoints;
        }

        // The money half of the reward is written to the character row before
        // anything is sent, so a relog cannot lose it. The status frame this
        // hand-in ends with carries the wallet, so the client shows it at once.
        try
        {
            var wallet = await _store.GrantQuestCurrencyAsync(
                _account?.Id ?? 0,
                _character.Id,
                payout.Silver,
                payout.Gold,
                cancellationToken);
            if (wallet is not null)
            {
                _character.Silver = wallet.Silver;
                _character.Gold = wallet.Gold;
                Console.Error.WriteLine(
                    $"[quest] reward paid character={_character.Name} " +
                    $"quest={rewarded.QuestId} silver=+{payout.Silver} " +
                    $"gold=+{payout.Gold} wallet={wallet.Silver}/" +
                    $"{wallet.Gold}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[quest] reward payment failed character={_character.Name} " +
                $"quest={rewarded.QuestId} {ex.GetType().Name}: {ex.Message}");
        }

        if (carried is not null)
        {
            _character.Quests.Remove(carried);
        }

        var completed = new List<uint>(_character.QuestCompletedIds);
        if (!completed.Contains(rewarded.QuestId))
        {
            completed.Add(rewarded.QuestId);
        }

        _character.QuestCompletedIds = [.. completed];
        _offeredQuestId = 0;
        // The per-day count is recorded before anything is written, so the row the
        // hand-in just made unavailable is already counted when the accept gate is
        // asked again - the "next quest" frame below publishes from that same gate.
        var countedToday = RecordDailyCompletion(rewarded);
        await SaveQuestStateAsync(cancellationToken);
        if (countedToday)
        {
            await SaveQuestDailyCompletionsAsync(cancellationToken);
        }

        if (levelUps.Count > 0)
        {
            await RefreshLevelUpStatsAsync(_character, cancellationToken);
        }

        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);

        var giverNpcId = await ResolveQuestNpcIdForSnapshotAsync(rewarded.GiverKey, cancellationToken);
        var responderNpcId = await ResolveQuestNpcIdForSnapshotAsync(rewarded.ResponderKey, cancellationToken);

        // Captured hand-in order (reference capture 2026-09-14 09:08:15, ids
        // 142589..142595): the status refresh first, then one opcode-10030
        // level-up notice per level the reward carried, then the hand-in ack,
        // the follow-up detail, the npc marker list and the hand-in menu.
        // Applying the reward experience without that 10030 notice leaves the
        // client rendering a level its panels never received, which is the
        // 004AC835 fault right after a hand-in - and is why only the quests
        // whose reward crosses a level threshold crashed.
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "QuestHandInStatus");

        foreach (var levelUp in levelUps)
        {
            var clientExperienceMaximum =
                PlayerExperienceCatalog.GetClientExperienceMaximum(
                    levelUp.Level,
                    _character.FighterLevelSealed);
            await _session.SendAsync(
                PacketBuilder.PlayerLevelUp(
                    LocalPlayerObjectId,
                    levelUp.Level,
                    clientExperienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                "QuestHandInLevelUp");
            await _registry.BroadcastToMapAsync(
                _character.CurrentMap,
                PacketBuilder.PlayerLevelUp(
                    CurrentPlayerObjectId,
                    levelUp.Level,
                    clientExperienceMaximum,
                    levelUp.CurrentExperience,
                    _character.MaxHp,
                    _character.CurrentHp,
                    _character.MaxMp,
                    _character.CurrentMp),
                cancellationToken,
                _session,
                "QuestHandInLevelUpWorld");
        }

        // The client pays the reward slot it picked itself and announces the item
        // on opcode 10056 right after this acknowledgement, so the promise the
        // answer carries has to be remembered before it is sent: that is what the
        // announcement is checked against, and what turns the client's own copy
        // into a durable item the next relog still shows.
        OfferQuestRewardItems(rewarded.QuestId);

        await _session.SendAsync(
            PacketBuilder.QuestHandInAck(
                giverNpcId,
                responderNpcId,
                rewarded.QuestId,
                rewardIndex,
                (int)Math.Clamp(_character.Experience, 0, int.MaxValue),
                _character.TalentPoints),
            cancellationToken,
            "QuestHandInAck",
            framed: false);

        // The client expects the follow-up quest's record after the ack, so the
        // detail is always sent: a captured answer is replayed verbatim, and a
        // quest the capture never answered is built from this server's own chain
        // and objective data with its own reward slots (or free ones). Omitting
        // the frame - which is what happened while the next quest had no captured
        // answer, e.g. Athens 1523 after handing in 1522 - faulted the client at
        // 004AC835. Opcodes 10077 and 10080 stay built from the chain.
        //
        // Only the completed serial main line may publish its follow-up. Daily
        // and utility hand-ins must not reopen another route's unfinished quest.
        var followUp = NextMainLineQuest(rewarded.QuestId);
        if (followUp is { } next)
        {
            var nextGiverNpcId = await ResolveQuestNpcIdForSnapshotAsync(next.GiverKey, cancellationToken);
            var nextResponderNpcId = await ResolveQuestNpcIdForSnapshotAsync(next.ResponderKey, cancellationToken);
            await _session.SendAsync(
                PacketBuilder.QuestNextDetail(nextGiverNpcId, next.QuestId),
                cancellationToken,
                "QuestNextDetail",
                framed: false);

            // The offer frame is what lets the client's accept button work on the
            // quest that just popped up. Without it the window shows the follow-up
            // but a click does nothing until the player closes the window and talks
            // to the npc again - reopening re-runs the scene query, which is the
            // only path that used to send this frame. The scene query answers with
            // the next quest's responder, so the same id is used here.
            await _session.SendAsync(
                PacketBuilder.QuestSceneOfferAck(next.QuestId, nextResponderNpcId),
                cancellationToken,
                "QuestSceneOffer",
                framed: false);
            _offeredQuestId = next.QuestId;

            await _session.SendAsync(
                PacketBuilder.QuestMarkerList(
                    nextGiverNpcId,
                    QuestMarkerEntries(nextGiverNpcId, AcceptableQuests())),
                cancellationToken,
                "QuestMarkerList",
                framed: false);
            await _session.SendAsync(
                PacketBuilder.QuestHandInMenu(
                    nextGiverNpcId,
                    QuestHandInEntries(nextGiverNpcId)),
                cancellationToken,
                "QuestHandInList",
                framed: false);
        }

        await _session.SendAsync(
            PacketBuilder.QuestHandInTailFrame(),
            cancellationToken,
            "QuestHandInTail",
            framed: false);
        // The reference publishes its quest-mark lists in this same burst - its
        // own 10078 sits between the level-up frame and the hand-in
        // acknowledgement - because the hand-in is what changes which npcs have
        // something for the character.
        if (levelUps.Count > 0)
        {
            await SendQuestLevelUpRefreshAsync(cancellationToken);
        }
        else
        {
            await SendQuestNpcMarksAsync(cancellationToken);
        }
        Console.WriteLine(
            $"[quest] handed in character={_character.Name} " +
            $"quest={rewarded.QuestId} rewardExp={rewardExperience} " +
            $"rewardTp={payout.TalentPoints} " +
            $"levelUps={levelUps.Count} " +
            $"next={followUp?.QuestId ?? 0} " +
            $"carried={_character.Quests.Count}");
    }

    /// <summary>
    /// The eligible successor within the completed quest's serial route.
    /// </summary>
    /// <remarks>
    /// Daily main-line tasks are independent offers, so they have no automatic
    /// successor. Level-31 optional branches may offer the unlocked level-32
    /// serial continuation, but never select a different main-line segment.
    /// </remarks>
    private StarterQuestChain.Step? NextMainLineQuest(uint completedQuestId)
    {
        if (StarterQuestChain.Find(completedQuestId) is not { IsMainLine: true, IsDaily: false } completed)
        {
            return null;
        }

        var acceptable = AcceptableQuests();
        StarterQuestChain.Step? optional = null;
        foreach (var step in acceptable)
        {
            if (step.IsMainLine && !step.IsDaily && step.Segment == completed.Segment &&
                (step.PrerequisiteQuestId == completedQuestId ||
                 (completed.IsOptionalMainLine && !step.IsOptionalMainLine &&
                  step.PrerequisiteQuestId == completed.PrerequisiteQuestId)))
            {
                if (!step.IsOptionalMainLine)
                {
                    return step;
                }

                optional ??= step;
            }
        }

        return optional;
    }

    /// <summary>The same eligibility rules serve NPC menus, lookup and acceptance.</summary>
    internal IReadOnlyList<StarterQuestChain.Step> AcceptableQuests() =>
        _character is null ? [] : EligibleQuests(_character, QuestDailyState.Today());

    private static List<StarterQuestChain.Step> EligibleQuests(
        GameCharacter character,
        DateOnly today) =>
        StarterQuestChain.Steps
            .Where(step => CanAcceptQuest(character, step, today))
            .OrderByDescending(step => step.IsMainLine)
            .ThenBy(step => step.MinLevel)
            .ThenBy(step => step.QuestId)
            .ToList();

    /// <summary>Daily Thebes needs exact level; serial/utility need minimum; others Q-5..Q+9.</summary>
    internal static bool IsLevelEligibleFor(
        GameCharacter character,
        StarterQuestChain.Step step) => IsWithinAcceptBand(character.Level, step);

    internal static bool IsWithinAcceptBand(int level, StarterQuestChain.Step step) =>
        step.RequiresExactLevel
            ? level == step.MinLevel
            : step.IsMainLine || step.IsOneTimeUtility
            ? level >= step.MinLevel
            : level >= step.MinLevel - QuestLevelLead && IsWithinNineLevels(level, step);

    internal static bool IsWithinNineLevels(int characterLevel, StarterQuestChain.Step step) =>
        characterLevel - step.MinLevel <= NineLevelBand;

    internal const int NineLevelBand = 9;
    internal const int QuestLevelLead = 5;
    internal const int LegacyLevelCap = 120;

    /// <summary>Daily completion records expire by quota day, while one-time records do not.</summary>
    internal static bool CanAcceptQuest(
        GameCharacter character,
        StarterQuestChain.Step step,
        DateOnly today,
        bool fromScroll = false) =>
        (HasResolvableNpc(step) || (fromScroll && step.IsQuestScroll)) &&
        IsLevelEligibleFor(character, step) &&
        IsChainUnlocked(character, step) &&
        HasDailyAllowance(character, step, today);

    internal static bool HasDailyAllowance(
        GameCharacter character,
        StarterQuestChain.Step step,
        DateOnly today) =>
        QuestDailyState.AllowsCompletion(
            character.QuestCompletionsOn(step.QuestId, today),
            step.MaxCompletionsPerDay);

    // Scroll quests still require an item acceptance path; no NPC zero is published.
    private static bool HasResolvableNpc(StarterQuestChain.Step step) =>
        !step.IsQuestScroll && step.GiverKey.Length > 0;

    private string ChainCamp() =>
        ChainCampFor(_character?.Camp ?? GameDefaults.SpartaCamp);

    internal static string ChainCampFor(byte camp) =>
        camp == GameDefaults.SpartaCamp
            ? StarterQuestChain.SpartaCamp
            : StarterQuestChain.AthensCamp;

    /// <summary>Only the explicit predecessor gates a story quest.</summary>
    /// <remarks>552..556 and 559 all depend on 551; the five branches never gate 559.</remarks>
    internal static bool IsChainUnlocked(
        GameCharacter character,
        StarterQuestChain.Step step)
    {
        if (step.Camp != ChainCampFor(character.Camp) || IsCarried(character, step.QuestId))
        {
            return false;
        }

        if (step.MaxCompletionsPerDay == 0 && character.QuestCompletedIds.Contains(step.QuestId))
        {
            return false;
        }

        return !step.IsMainLine || step.PrerequisiteQuestId == 0 ||
            character.QuestCompletedIds.Contains(step.PrerequisiteQuestId);
    }

    internal static List<uint> AcceptableQuestIds(GameCharacter character) =>
        EligibleQuests(character, QuestDailyState.Today()).Select(step => step.QuestId).ToList();

    /// <summary>True when the character is carrying that quest.</summary>
    private static bool IsCarried(GameCharacter character, uint questId)
    {
        foreach (var quest in character.Quests)
        {
            if (quest.QuestId == questId)
            {
                return true;
            }
        }

        return false;
    }

    private CharacterQuest? FindCarriedQuest(uint questId)
    {
        foreach (var quest in _character?.Quests ?? [])
        {
            if (quest.QuestId == questId)
            {
                return quest;
            }
        }

        return null;
    }

    /// <summary>Describes a quest's objectives for the diagnostics.</summary>
    private static string DescribeObjectives(
        IReadOnlyList<QuestObjective> objectives)
    {
        var parts = new List<string>(objectives.Count);
        foreach (var objective in objectives)
        {
            var name = StarterQuestObjectives.NameOf(objective);
            parts.Add(name == objective.Target
                ? $"{objective.Required}x {objective.Target}"
                : $"{objective.Required}x {objective.Target} [= {name}]");
        }

        return string.Join(", ", parts);
    }

    private Task SaveQuestStateAsync(CancellationToken cancellationToken) =>
        _character is null || _account is null
            ? Task.CompletedTask
            : _store.SaveCharacterQuestStateAsync(
                _account.Id,
                _character.Id,
                _character.Quests,
                _character.QuestCompletedIds,
                cancellationToken);

    /// <summary>
    /// Records one more completion of a repeatable row inside the quest day.
    /// </summary>
    /// <remarks>
    /// Only the rows that carry a per-day cap are counted: a daily or guild row
    /// allows one a day and a repeat row three. Daily main-line rows also count;
    /// serial main lines and utility rows use their lifetime completion history.
    /// Scroll rows classified as daily also carry the daily cap, although
    /// their item-based acceptance path is not implemented here.
    /// </remarks>
    /// <returns>True when the row carries a cap and was therefore counted.</returns>
    private bool RecordDailyCompletion(StarterQuestChain.Step step)
    {
        if (_character is null || step.MaxCompletionsPerDay <= 0)
        {
            return false;
        }

        var completed = _character.RecordQuestCompletion(
            step.QuestId,
            QuestDailyState.Today());
        Console.WriteLine(
            $"[quest] daily count character={_character.Name} " +
            $"quest={step.QuestId} sort={step.UIQuestSort} " +
            $"completedToday={completed} cap={step.MaxCompletionsPerDay}");
        return true;
    }

    private Task SaveQuestDailyCompletionsAsync(
        CancellationToken cancellationToken) =>
        _character is null || _account is null
            ? Task.CompletedTask
            : SaveQuestDailyCompletionsCoreAsync(cancellationToken);

    /// <summary>
    /// Writes the per-day counts to <c>character_quest_daily</c>.
    /// </summary>
    /// <remarks>
    /// The count is durable the moment the quest is paid out, so a restart cannot
    /// reset it. The character's completed-quest record and the quota are the same
    /// durable fact, written by the same hand-in, so both go out together.
    /// </remarks>
    private async Task SaveQuestDailyCompletionsCoreAsync(
        CancellationToken cancellationToken)
    {
        await _store.SaveCharacterQuestStateAsync(
            _account!.Id,
            _character!.Id,
            _character.Quests,
            _character.QuestCompletedIds,
            cancellationToken);
        await _store.SaveQuestDailyCompletionsAsync(
            _account.Id,
            _character.Id,
            _character.QuestDailyCompletions,
            cancellationToken);
    }

    /// <summary>
    /// Loads the character's per-day quest counts for this session.
    /// </summary>
    /// <remarks>
    /// The counts live in their own table, so they are read once when the
    /// character is installed: the accept gate asks the in-memory copy, and the
    /// hand-in writes it back. A failure here is logged and leaves every counter
    /// at zero rather than dropping the login - a wrong quota is recoverable, a
    /// refused login is not.
    /// </remarks>
    private async Task LoadQuestDailyCompletionsAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        try
        {
            var counts = await _store.LoadQuestDailyCompletionsAsync(
                _character.Id,
                cancellationToken);
            var loaded = new Dictionary<uint, GameCharacter.QuestDailyCount>();
            foreach (var (questId, count) in counts)
            {
                loaded[questId] = count;
            }

            _character.QuestDailyCompletions = loaded;
            Console.WriteLine(
                $"[quest] daily counts loaded character={_character.Name} " +
                $"rows={counts.Count} today={QuestDailyState.Today():yyyy-MM-dd}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[quest] daily counts load failed character={_character.Name} " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private uint ResolveQuestNpcId(string? npcKey) =>
        npcKey is not null &&
        _mapNpcsByInteractionId.Values.FirstOrDefault(
            npc => string.Equals(npc.NpcKey, npcKey, StringComparison.Ordinal))
            is { } match
            ? match.InteractionId
            : 0u;

    /// <summary>
    /// Resolves a chain npc key for the login snapshot.
    /// </summary>
    /// <remarks>
    /// The snapshot goes out before the npc catalog is installed - that ordering is
    /// what the client expects, and moving it broke the map-transition frame
    /// sequence - so the in-memory catalog is still empty at this point and the
    /// lookup fell back to zero. The published map content is read instead, which
    /// is where the catalog comes from anyway. A descriptor naming npc zero is what
    /// left the client's quest entry unusable and crashed it on the first hand-in.
    /// </remarks>
    private async Task<uint> ResolveQuestNpcIdForSnapshotAsync(
        string? npcKey,
        CancellationToken cancellationToken)
    {
        if (npcKey is null || _character is null)
        {
            return 0u;
        }

        if (ResolveQuestNpcId(npcKey) is var resolved && resolved != 0)
        {
            return resolved;
        }

        try
        {
            short sourceMap = _character.CurrentMap;
            foreach (var step in StarterQuestChain.Steps)
            {
                if (step.GiverKey == npcKey && step.GiverMapId >= 0)
                {
                    sourceMap = step.GiverMapId;
                    break;
                }

                if (step.ResponderKey == npcKey && step.ResponderMapId >= 0)
                {
                    sourceMap = step.ResponderMapId;
                    break;
                }
            }

            var mapContent = await _worldContent.ReadMapAsync(sourceMap, cancellationToken);
            // The ids have to be the ones the client was actually sent, so the
            // lookup runs the same placement pipeline world entry runs: the
            // captured Athens ids move a few published npcs, and naming the
            // published id would point the client at an object that is not there.
            var effectiveNpcs = CapturedNpcPlacementPolicy.ApplyToMap(
                [.. mapContent.Npcs
                    .Select(CapitalNpcServiceProtocol.ApplyCapturedSpawnCompatibility)]);
            foreach (var npc in effectiveNpcs)
            {
                if (string.Equals(npc.NpcKey, npcKey, StringComparison.Ordinal))
                {
                    return npc.InteractionId;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine(
                $"[quest] npc lookup failed character={_character.Name} " +
                $"key={npcKey} {ex.GetType().Name}: {ex.Message}");
        }

        return 0u;
    }

    /// <summary>
    /// True when the npc gives or receives a quest on the chain at all.
    /// </summary>
    private bool IsQuestChainNpc(uint interactionId)
    {
        foreach (var step in StarterQuestChain.Steps)
        {
            if (ResolveQuestNpcId(step.GiverKey) == interactionId ||
                ResolveQuestNpcId(step.ResponderKey) == interactionId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the npc has a quest to offer or to take from this character.
    /// </summary>
    /// <remarks>
    /// This is what decides whether a click opens the quest page. An npc that is
    /// on the chain but has nothing for this character yet keeps the ordinary
    /// description window, and one that gives the quest the character may accept
    /// next - or receives any quest the character is carrying - opens the quest
    /// page. The captured dialogs advertise exactly this through their flags: 3
    /// for the guide and the responder, 0 for an npc with no quest function.
    /// </remarks>
    private bool HasQuestFunctionFor(uint interactionId)
    {
        if (_character is null)
        {
            return false;
        }

        foreach (var quest in _character.Quests)
        {
            if (StarterQuestChain.Find(quest.QuestId) is { } carried &&
                ResolveQuestNpcId(carried.ResponderKey) == interactionId)
            {
                return true;
            }
        }

        foreach (var acceptable in AcceptableQuests())
        {
            if (ResolveQuestNpcId(acceptable.GiverKey) == interactionId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Rewrites the quest tables that travel with an npc spawn so they reflect
    /// this character's progress.
    /// </summary>
    /// <remarks>
    /// The tables are world content, identical for every player, so the captured
    /// ones still advertise a quest the character has already finished. The
    /// availability flag is per character, so the chain npcs' tables are rebuilt
    /// here before they are sent. Everything the content table already listed is
    /// kept - these tables also carry quests outside the newbie chain, which the
    /// client still needs - and only the flags change.
    /// </remarks>
    private IReadOnlyList<NpcSpawnDefinition> ProjectQuestMarkerTables(
        IReadOnlyList<NpcSpawnDefinition> spawns)
    {
        if (_character is null || spawns.Count == 0)
        {
            return spawns;
        }

        var acceptable = AcceptableQuests();
        List<NpcSpawnDefinition>? projected = null;
        for (var index = 0; index < spawns.Count; index++)
        {
            var spawn = spawns[index];
            if (!IsQuestChainNpc(spawn.InteractionId))
            {
                continue;
            }

            var given = new List<uint>();
            var received = new List<uint>();
            foreach (var step in StarterQuestChain.Steps)
            {
                if (ResolveQuestNpcId(step.GiverKey) == spawn.InteractionId)
                {
                    given.Add(step.QuestId);
                }

                if (ResolveQuestNpcId(step.ResponderKey) == spawn.InteractionId)
                {
                    received.Add(step.QuestId);
                }
            }

            projected ??= [.. spawns];
            projected[index] = spawn with
            {
                Detail10077 = PacketBuilder.QuestMarkerList(
                    spawn.InteractionId,
                    MergeMarkerEntries(
                        ReadMarkerEntries(spawn),
                        acceptable,
                        given)),
                Detail10080 = PacketBuilder.QuestHandInMenu(
                    spawn.InteractionId,
                    MergeHandInEntries(ReadHandInEntries(spawn), received))
            };
        }

        return projected ?? spawns;
    }

    /// <summary>
    /// Keeps a content table's quests and re-flags them for one character.
    /// </summary>
    /// <remarks>
    /// A quest is flagged available only when the character may take it right now,
    /// so a finished quest loses its flag and stops being offered even though the
    /// captured content table has it switched on. Unlike the single-frontier shape
    /// this replaced, several of an npc's rows can be flagged at once: a main-line
    /// npc offers its open story step, and a repeatable quest's npc lights up for
    /// every level band the character has reached.
    /// </remarks>
    internal static List<(uint QuestId, uint Available)> MergeMarkerEntries(
        IReadOnlyList<(uint QuestId, uint Available)> existing,
        IReadOnlyList<StarterQuestChain.Step> acceptableQuests,
        IReadOnlyList<uint> chainQuestIds)
    {
        var available = new HashSet<uint>();
        foreach (var step in acceptableQuests)
        {
            available.Add(step.QuestId);
        }

        var merged = new List<(uint, uint)>(existing.Count + chainQuestIds.Count);
        var seen = new HashSet<uint>();
        foreach (var entry in existing)
        {
            if (seen.Add(entry.QuestId))
            {
                merged.Add((
                    entry.QuestId,
                    available.Contains(entry.QuestId) ? 1u : 0u));
            }
        }

        foreach (var questId in chainQuestIds)
        {
            if (seen.Add(questId))
            {
                merged.Add((
                    questId,
                    available.Contains(questId) ? 1u : 0u));
            }
        }

        return merged;
    }

    /// <summary>Keeps a content table's hand-ins and adds missing chain rows.</summary>
    internal static List<uint> MergeHandInEntries(
        IReadOnlyList<uint> existing,
        IReadOnlyList<uint> chainQuestIds)
    {
        var merged = new List<uint>(existing.Count + chainQuestIds.Count);
        var seen = new HashSet<uint>();
        foreach (var questId in existing.Concat(chainQuestIds))
        {
            if (seen.Add(questId))
            {
                merged.Add(questId);
            }
        }

        return merged;
    }

    /// <summary>The 10077 entries for one npc: every quest it gives.</summary>
    /// <remarks>
    /// <paramref name="acceptable"/> is the character's whole open list, computed
    /// once by the caller: walking the catalog per npc would re-resolve every npc
    /// key for every spawn in the map.
    /// </remarks>
    private List<(uint QuestId, uint Available)> QuestMarkerEntries(
        uint npcId,
        IReadOnlyList<StarterQuestChain.Step> acceptable)
    {
        var available = new HashSet<uint>();
        foreach (var step in acceptable)
        {
            available.Add(step.QuestId);
        }

        var entries = new List<(uint, uint)>();
        foreach (var step in StarterQuestChain.Steps)
        {
            if (ResolveQuestNpcId(step.GiverKey) != npcId)
            {
                continue;
            }

            entries.Add((
                step.QuestId,
                available.Contains(step.QuestId) ? 1u : 0u));
        }

        return entries;
    }

    /// <summary>The 10080 entries for one npc: every quest it receives.</summary>
    private List<uint> QuestHandInEntries(uint npcId)
    {
        var entries = new List<uint>();
        foreach (var step in StarterQuestChain.Steps)
        {
            if (ResolveQuestNpcId(step.ResponderKey) == npcId)
            {
                entries.Add(step.QuestId);
            }
        }

        return entries;
    }

    /// <summary>Reads the 10077 entries a content table carries, if any.</summary>
    private static List<(uint QuestId, uint Available)> ReadMarkerEntries(
        NpcSpawnDefinition spawn)
    {
        var entries = new List<(uint, uint)>();
        var table = spawn.Detail10077;
        if (!IsQuestTable(
                table,
                Opcodes.QuestMarkerList,
                spawn.InteractionId))
        {
            return entries;
        }

        var count = (table.Length - 12) / 8;
        for (var index = 0; index < count; index++)
        {
            entries.Add((
                BinaryPrimitives.ReadUInt32LittleEndian(
                    table.AsSpan(12 + (index * 8), 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(
                    table.AsSpan(16 + (index * 8), 4))));
        }

        return entries;
    }

    /// <summary>Reads the 10080 entries a content table carries, if any.</summary>
    private static List<uint> ReadHandInEntries(NpcSpawnDefinition spawn)
    {
        var entries = new List<uint>();
        var table = spawn.Detail10080;
        if (!IsQuestTable(
                table,
                Opcodes.QuestHandInList,
                spawn.InteractionId))
        {
            return entries;
        }

        var count = (table.Length - 12) / 4;
        for (var index = 0; index < count; index++)
        {
            entries.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                table.AsSpan(12 + (index * 4), 4)));
        }

        return entries;
    }

    private static bool IsQuestTable(
        byte[] table,
        ushort opcode,
        uint interactionId) =>
        table.Length >= 12 &&
        BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(0, 2)) ==
            table.Length &&
        BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(2, 2)) == opcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4, 4)) ==
            interactionId;
}
