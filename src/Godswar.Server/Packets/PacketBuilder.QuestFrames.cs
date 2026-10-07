using System.Buffers.Binary;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    /// <summary>Where a reward record carries its item id.</summary>
    private const int RewardSlotItemIdOffset = 8;

    /// <summary>
    /// Where a 72-byte reward slot carries its five attribute ids.
    /// </summary>
    /// <remarks>
    /// Ids out of <c>item_attribute_templates</c>, four bytes apart; the empty
    /// slot marker is <c>FFFFFFFF</c>. The id carries its own tier, so there is
    /// no separate level word.
    /// </remarks>
    private const int RewardSlotAttributeOffset = 12;

    /// <summary>Five attribute id words fit before the quality bytes.</summary>
    private const int RewardSlotAttributeSlots = 5;

    /// <summary>
    /// Where a 72-byte reward slot carries its quality and grade words.
    /// </summary>
    /// <remarks>
    /// Read out of the captured answer areas: quest 520's reward (the one the
    /// client draws with an "精致的" prefix) carries <c>03 01</c> here while
    /// 518/519/1518 carry <c>01 01</c>, which matches
    /// <see cref="ItemGrantAttributes"/>'s quality-1/grade-1 default. The client
    /// builds the quality prefix itself - the client name table only holds the
    /// bare name ("轻皮护胸") - so this word is what the reward window shows.
    /// </remarks>
    private const int RewardSlotQualityOffset = 32;

    /// <summary>
    /// The shape of a free reward slot, taken from the captured answer that
    /// carried no reward at all.
    /// </summary>
    private static ReadOnlySpan<byte> EmptyQuestRewardSlot =>
        ReadRewardSlot(EmptyQuestRewardArea, 0);

    /// <summary>
    /// Where the 10082 accept answer carries its eight 72-byte reward slots.
    /// </summary>
    /// <remarks>
    /// Payload offset 60, so frame offset 64 once the 4-byte length+opcode header
    /// is counted. Confirmed against the capture: the newbie gift bag 240f0000
    /// sits at frame 72 of packet 247, and that is slot 0 plus its eight-byte
    /// record prefix.
    /// </remarks>
    private const int QuestAnswerRecordOffset = 64;

    /// <summary>
    /// Where the 10076 follow-up detail carries the same reward slots.
    /// </summary>
    /// <remarks>
    /// The same records, four bytes earlier than in 10082 and only four slots
    /// deep: packet 1341 has the first slot's reward item at payload 64, which is
    /// slot 0 plus eight, so its records start at payload 56.
    /// </remarks>
    private const int QuestNextDetailRecordOffset = 60;

    /// <summary>How many of a quest's slots the 10076 detail carries.</summary>
    private const int QuestNextDetailRecordBytes = 4 * 72;

    /// <summary>
    /// Where the 10082 kill-quest frame carries the target monster id.
    /// </summary>
    /// <remarks>
    /// Payload +28, so frame +32. Taken from the reference server's own answer for
    /// quest 520, which asked for the ten Dumb Wood Men whose id is 1027.
    /// </remarks>
    private const int ObjectiveAnswerMonsterOffset = 32;

    /// <summary>Where the 10082 kill-quest frame carries the required count.</summary>
    private const int ObjectiveAnswerRequiredOffset = 48;

    /// <summary>
    /// Where the 10082 kill-quest frame carries its kill-quest marker.
    /// </summary>
    /// <remarks>
    /// The reference server's answer for quest 1533 - two targets, 1414 and 1415 -
    /// reads 8 here, the same marker the 10076 detail and the login snapshot
    /// carry.
    /// </remarks>
    private const int ObjectiveAnswerKindOffset = 20;

    /// <summary>
    /// Where the objective area carries a quest's <em>item</em> objective: the
    /// item's own id and how many of it are wanted.
    /// </summary>
    /// <remarks>
    /// A quest's objectives are not all kills. The client's own text says "Kill 15
    /// Little Snakes and collect 5 Snake Tails", and the item half of that is a
    /// separate pair of fields from the parallel monster/count arrays - which is
    /// why the client draws it on a list of its own (<c>ItemList</c> in
    /// QuestViewUI.xml, next to <c>CreatureList</c>).
    /// <para>
    /// The reference server fills this pair for every quest that names an item and
    /// leaves it zero otherwise, all three offsets verified against its own frames:
    /// </para>
    /// <list type="bullet">
    /// <item>10082 <c>+24</c> id / <c>+40</c> count - quest 526 carries 400 (Snake
    /// Tail, the id <c>Text/QuestItem.dat</c> gives that name) and 5; 1526 carries
    /// 300 (Honeycomb) and 5; 1528 carries 301 (Snake Fangs) and 8.</item>
    /// <item>10076/10081 <c>+20</c> / <c>+36</c> - the same pair four bytes earlier:
    /// 1526 carries 300 and 5, 1528 carries 301 and 8, 1146 carries 123 and
    /// 10.</item>
    /// <item>the login snapshot's descriptor <c>+32</c> / <c>+48</c> - eight bytes
    /// later than 10082's pair, because the descriptor's kill arrays also sit eight
    /// bytes late (monster +40, count +56). Quest 1557 carries 303 (Deer Antler)
    /// and 10, with its kill objective 1034 at +40 wanting 100 at +56.</item>
    /// </list>
    /// <para>
    /// Leaving the pair zero is what makes such a quest show only its kill
    /// objective, which is what this server used to do for every quest.
    /// </para>
    /// </remarks>
    private const int ObjectiveAnswerItemIdOffset = 24;
    private const int ObjectiveAnswerItemCountOffset = 40;

    /// <summary>The 10076/10081 offsets of the item pair.</summary>
    private const int QuestNextDetailItemIdOffset = 20;
    private const int QuestNextDetailItemCountOffset = 36;

    /// <summary>The login snapshot descriptor's offsets of the item pair.</summary>
    private const int QuestSnapshotItemIdOffset = 32;
    private const int QuestSnapshotItemCountOffset = 48;

    /// <summary>
    /// How many targets the parallel objective arrays can hold.
    /// </summary>
    /// <remarks>
    /// Four. The monster array starts at 10082 <c>+32</c> and the count array at
    /// <c>+48</c>, sixteen bytes apart, but the quest's item pair sits inside that
    /// gap - the item's count is the word at <c>+40</c>, which is exactly where a
    /// fifth kill slot would be. The reference server's own answer for quest 526
    /// proves the array stops short of it: it reads <c>1028</c> in slot 0, nothing
    /// in slots 1 to 3, and <c>5</c> - how many Snake Tails the quest wants - at
    /// <c>+40</c>. The same four-slot array is at the snapshot descriptor's
    /// <c>+40</c>, whose item count is the word at <c>+48</c>.
    /// <para>
    /// Clearing past the fourth slot would wipe the item count, which is how this
    /// went wrong once already. No quest in the content names more than three
    /// targets, so four has room to spare.
    /// </para>
    /// </remarks>
    private const int QuestObjectiveMaximumSlots = 4;

    /// <summary>
    /// The 10076 equivalents of those two fields, four bytes earlier.
    /// </summary>
    /// <remarks>
    /// The same parallel objective arrays as 10082, four bytes earlier, and the
    /// builder fills them for every quest that names a target - one slot per
    /// target.
    /// <para>
    /// History worth keeping: the 2026-09-13 capture had every one of its 79 10076
    /// frames carrying <c>kind = 4</c>, <c>monster = 0</c> and <c>required = 0</c>,
    /// and filling a single objective into one of them (quest 532) made the client
    /// fault with a null dereference - because the frame then disagreed with the
    /// reference in exactly four bytes, with a kill-quest marker that did not match
    /// the half-filled area. The 2026-09-24 capture settled it: the reference's own
    /// 10076 for the two-target quest 1533 carries <c>kind = 8</c> with both of its
    /// targets in the first two slots. Kind and area are therefore written
    /// together, never one without the other.
    /// </para>
    /// </remarks>
    private const int QuestNextDetailMonsterOffset = 28;
    private const int QuestNextDetailRequiredOffset = 44;

    /// <summary>
    /// The field that reads 4 for a quest with nothing to kill and 8 for one with
    /// a kill objective, at its 10076 offset.
    /// </summary>
    private const int QuestNextDetailKindOffset = 16;

    /// <summary>Bytes of one quest descriptor in the login snapshot.</summary>
    private const int QuestSnapshotDescriptorBytes = 96;

    /// <summary>
    /// Bytes one quest occupies in the login snapshot: its descriptor, its eight
    /// 72-byte reward slots and eight bytes of padding.
    /// </summary>
    /// <remarks>
    /// The snapshot is not "every descriptor, then every record" - each quest is a
    /// block of its own, and the next quest's descriptor follows its predecessor's
    /// reward slots. Read off the reference server's own three-quest frame
    /// (2026-10-04 09:00:49), which carries quests 1557, 1158 and 1542 with their
    /// descriptors at <c>+8</c>, <c>+688</c> and <c>+1368</c>, and 1557's first
    /// reward slots - item 5802 at <c>+112</c>, 14280 at <c>+184</c> - inside its
    /// own block.
    /// <para>
    /// The arithmetic is what proves it: <c>8 + 3 * 680 = 2048</c> is exactly the
    /// captured frame's length, so a 2048-byte snapshot holds three quests, not the
    /// twelve the old "descriptors then records" reading suggested. Writing the
    /// second descriptor at <c>+104</c> instead - which is what this builder used
    /// to do - put it where the client reads the first quest's reward slots, so a
    /// character carrying two quests only ever saw the first of them.
    /// </para>
    /// </remarks>
    private const int QuestSnapshotBlockBytes = 680;

    /// <summary>How many 72-byte reward slots one quest's block carries.</summary>
    private const int QuestSnapshotRecordSlots = 8;

    /// <summary>
    /// Where a descriptor carries the target monster, how many are wanted, the
    /// kill-quest marker, whether the objective is met and how many are done,
    /// measured from the quest id.
    /// </summary>
    private const int QuestSnapshotMonsterOffset = 40;
    private const int QuestSnapshotRequiredOffset = 56;
    private const int QuestSnapshotKindOffset = 68;
    private const int QuestSnapshotStateOffset = 72;
    private const int QuestSnapshotProgressOffset = 80;

    /// <summary>The marker a quest with something to kill carries.</summary>
    private const int QuestWithObjectivesKind = 8;

    /// <summary>
    /// What a descriptor carries at <see cref="QuestSnapshotStateOffset"/> while
    /// its objective is still open, and once it has been met.
    /// </summary>
    /// <remarks>
    /// Taken from the reference server's own snapshots. Its two 10090 frames for
    /// quest 1540 in one session differ in exactly two words: the count-done word
    /// at <c>+80</c> went from <c>12 &lt;&lt; 16</c> to <c>30 &lt;&lt; 16</c> when
    /// the thirtieth kill landed, and this word went from 4 to 3. Every unfinished
    /// kill quest in the captures carries 4 (520 at 0 of 10, 1523 at 0 of 12, 1531
    /// at 0 of 20) and the met one carries 3; the quests with nothing to kill carry
    /// 3 as well (518, 1522).
    /// <para>
    /// Leaving this word at the template's 3 is what made a quest read as finished
    /// on the client the moment the character re-entered the world: the client drew
    /// the hand-in the server's own objective check was right to refuse. The
    /// accept-time frames never carried the stale value, which is why a quest
    /// looked correct until the next login.
    /// </para>
    /// </remarks>
    private const int QuestObjectiveOutstandingState = 4;
    private const int QuestObjectiveSatisfiedState = 3;

    /// <summary>
    /// Where the first quest's block starts, which is also where its descriptor
    /// starts: the count is one 4-byte word, and the captured frame's quest id sits
    /// at payload 4, which is frame 8.
    /// </summary>
    private const int QuestSnapshotFirstDescriptor = 8;

    /// <summary>Bytes of one record slot, in both the login snapshot and 10082.</summary>
    private const int QuestRecordBytes = 72;

    /// <summary>The fill flag an unused record slot carries in the capture.</summary>
    private const uint QuestRecordEmptyFlag = 0x01000101;

    // Verified field layouts, read off the reference capture. Offsets count from
    // the start of the frame, so "+4" is the first payload word after the 4-byte
    // length+opcode header:
    //
    //   10090 S2C  +4 count | 96-byte descriptors | 72-byte records
    //              a descriptor, from its own start: +0 quest | +4 giver |
    //              +8 responder | +40 target monster | +56 required |
    //              +68 kill-quest marker | +72 objective met (4 open, 3 met) |
    //              +80 count done << 16
    //   10083 S2C  +4 quest | +8 responder | +12 quest | +16 = 1
    //   10082 S2C  +4 giver | +8 responder | +12 quest | +16 slot count
    //              then 72-byte slots from +60: +8 reward item, +12..+28 -1 x5,
    //              +32 fill flag
    //   10084 S2C  +4 responder | +8 quest
    //   10086 S2C  +4 giver | +8 responder | +12 quest
    //   10076 S2C  +4 giver | +8 quest
    //   10077 S2C  +4 npc | +8 count | (quest, available) x count
    //   10080 S2C  +4 npc | +8 count | quest x count
    //
    // Every field that varies from quest to quest is a parameter, and the two
    // lists are assembled from the chain, so adding a quest never adds packet
    // code - it only adds a chain row.

    /// <summary>One target a carried quest is still counting.</summary>
    /// <remarks>
    /// A quest can name several targets, and the snapshot carries every one of
    /// them: the objective areas are parallel arrays, so slot <c>i</c> of the
    /// monster array belongs with slot <c>i</c> of the count and progress arrays.
    /// </remarks>
    internal readonly record struct QuestSnapshotObjective(
        uint MonsterId,
        int Required,
        int Current);

    /// <summary>One quest as the login snapshot describes it.</summary>
    /// <remarks>
    /// The descriptor carries the target monster, how many are wanted, how many
    /// are done and whether the objective has been met, so a quest picked up again
    /// after a relog still shows what it is waiting for instead of the hand-in it
    /// cannot take. The offsets are the reference server's own: its snapshot for a
    /// character carrying quest 520 at three of ten had the monster at +40, the
    /// count at +56, the kill-quest marker at +68, the state at +72 and
    /// <c>3 &lt;&lt; 16</c> - the progress - at +80.
    /// <para>
    /// <paramref name="Objectives"/> is the multi-target form: when it is given,
    /// every entry fills one slot of the parallel arrays and the single
    /// <paramref name="MonsterId"/>/<paramref name="Required"/>/<paramref name="Current"/>
    /// triple is ignored. The single form is kept because captured frames and
    /// existing callers pass exactly one target, and it must stay byte for byte
    /// what it was.
    /// </para>
    /// </remarks>
    internal readonly record struct QuestSnapshotEntry(
        uint QuestId,
        uint GiverNpcId,
        uint ResponderNpcId,
        uint MonsterId = 0,
        int Required = 0,
        int Current = 0,
        IReadOnlyList<QuestSnapshotObjective>? Objectives = null,
        bool? RequirementsSatisfied = null);

    /// <summary>
    /// Builds the opcode-10090 accepted-quest snapshot from the captured frame.
    /// </summary>
    /// <remarks>
    /// Captured layout, all offsets from the frame start:
    /// <list type="bullet">
    /// <item><c>+4</c> the quest count.</item>
    /// <item>one 96-byte descriptor per quest, starting at <c>+8</c>: quest id,
    /// giver npc, responder npc.</item>
    /// <item>one 72-byte record per quest after the descriptors: <c>+8</c> reward
    /// item, <c>+12..+28</c> five <c>-1</c>, <c>+32</c> the fill flag.</item>
    /// </list>
    /// The capture holds one quest, and for one quest this reproduces it byte for
    /// byte - descriptor at payload 4 and record at payload 100, which is what
    /// <c>4 + 96</c> works out to. A character carrying several quests is the same
    /// structure with the count raised, one block per quest.
    /// <para>
    /// The captured frame is 2048 bytes, which is exactly three quests
    /// (<c>8 + 3 * 680 = 2048</c>). A character may carry more, so the frame is
    /// grown to fit them and its declared length corrected; up to three it is the
    /// captured 2048-byte frame untouched, which is what keeps the one-quest golden
    /// byte for byte what the reference sent. Past three there is no reference
    /// sample, and the extra bytes are the captured frame's own tail - each quest's
    /// block is laid out the same either way.
    /// </para>
    /// <para>
    /// An empty snapshot (count zero) leaves the frame's own slots alone: it
    /// advertises no quest, which is how a character holding none is represented.
    /// </para>
    /// </remarks>
    public static byte[] QuestSnapshot(IReadOnlyList<QuestSnapshotEntry> quests)
    {
        var template = LoginSnapshotFrame();
        // The declared length is the frame's own u16 at +0, and the client trusts
        // it, so a grown frame has to say so.
        var needed = QuestSnapshotFirstDescriptor +
            (quests.Count * QuestSnapshotBlockBytes);
        var packet = needed > template.Length
            ? new byte[needed]
            : (byte[])template.Clone();
        if (needed > template.Length)
        {
            template.CopyTo(packet, 0);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)), checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), (uint)quests.Count);
        if (quests.Count == 0)
        {
            return packet;
        }

        var descriptor = QuestSnapshotFirstDescriptor;
        foreach (var quest in quests)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(descriptor, 4), quest.QuestId);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(descriptor + 4, 4), quest.GiverNpcId);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(descriptor + 8, 4), quest.ResponderNpcId);
            var descriptorObjectives = quest.Objectives;
            if (descriptorObjectives is { Count: > 0 })
            {
                WriteSnapshotObjectives(
                    packet.AsSpan(descriptor),
                    quest.QuestId,
                    descriptorObjectives);
            }
            else if (quest.Required > 0)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    packet.AsSpan(
                        descriptor + QuestSnapshotMonsterOffset,
                        4),
                    quest.MonsterId);
                BinaryPrimitives.WriteInt32LittleEndian(
                    packet.AsSpan(
                        descriptor + QuestSnapshotRequiredOffset,
                        4),
                    quest.Required);
                BinaryPrimitives.WriteInt32LittleEndian(
                    packet.AsSpan(descriptor + QuestSnapshotKindOffset, 4),
                    QuestWithObjectivesKind);
                BinaryPrimitives.WriteInt32LittleEndian(
                    packet.AsSpan(descriptor + QuestSnapshotProgressOffset, 4),
                    Math.Clamp(quest.Current, 0, ushort.MaxValue) << 16);
                BinaryPrimitives.WriteInt32LittleEndian(
                    packet.AsSpan(descriptor + QuestSnapshotStateOffset, 4),
                    quest.Current >= quest.Required
                        ? QuestObjectiveSatisfiedState
                        : QuestObjectiveOutstandingState);
            }
            WriteCollectObjective(packet.AsSpan(descriptor), QuestSnapshotItemIdOffset,
                QuestSnapshotItemCountOffset, quest.QuestId);

            // The quest's own reward slots sit inside its block, right behind its
            // descriptor - not in one shared run after every descriptor. See
            // QuestSnapshotBlockBytes.
            if (quest.RequirementsSatisfied is { } requirementsSatisfied)
            {
                BinaryPrimitives.WriteInt32LittleEndian(
                    packet.AsSpan(descriptor + QuestSnapshotStateOffset, 4),
                    requirementsSatisfied ? QuestObjectiveSatisfiedState : QuestObjectiveOutstandingState);
            }
            WriteQuestRecord(
                packet,
                descriptor + QuestSnapshotDescriptorBytes,
                quest.QuestId);
            descriptor += QuestSnapshotBlockBytes;
        }

        return packet;
    }

    /// <summary>
    /// Writes one quest's eight 72-byte reward slots over whatever the frame held.
    /// </summary>
    /// <remarks>
    /// The slots are the quest's own captured 10082 reward area, which is why quest
    /// 518's block carries the same newbie gift bag at the same offset inside its
    /// first slot: the two frames use one record layout. The reference's three-quest
    /// snapshot fills several slots of a block - quest 1557's carries items 5802,
    /// 14280, 14340 and 14260 - so the whole area is copied, not just its first
    /// slot. Every slot the quest has no record for keeps the empty pattern rather
    /// than an invented one.
    /// </remarks>
    private static void WriteQuestRecord(byte[] packet, int offset, uint questId)
    {
        var records = StarterQuestRewardRecords.Find(questId);

        // Slot 0 is the quest's own record and is always written, so a quest with
        // no captured area cannot inherit the template's - which belongs to another
        // quest entirely.
        WriteEmptyQuestSlot(packet, offset);
        if (records is { Length: > 0 } area &&
            !IsEmptyRewardSlot(area.AsSpan(0, QuestRecordBytes)))
        {
            area.AsSpan(0, QuestRecordBytes).CopyTo(
                packet.AsSpan(offset, QuestRecordBytes));
        }

        // The later slots are the quest's other reward choices. A slot the quest
        // has no record for is left exactly as the frame holds it: the captured
        // frame's own empty slots are not all the same shape - its first three
        // carry five -1 words and a 01010001 flag, the rest seven and 00000101 -
        // so writing one uniform pattern over them changes bytes the capture fixes.
        for (var slot = 1; slot < QuestSnapshotRecordSlots; slot++)
        {
            if (records is not { Length: > 0 } choices)
            {
                break;
            }

            var source = slot * QuestRecordBytes;
            if (source + QuestRecordBytes > choices.Length ||
                IsEmptyRewardSlot(choices.AsSpan(source, QuestRecordBytes)))
            {
                continue;
            }

            choices.AsSpan(source, QuestRecordBytes).CopyTo(
                packet.AsSpan(offset + source, QuestRecordBytes));
        }
    }

    /// <summary>Writes the frame's own empty-slot pattern at one record offset.</summary>
    private static void WriteEmptyQuestSlot(byte[] packet, int offset)
    {
        packet.AsSpan(offset, QuestRecordBytes).Clear();
        for (var index = 0; index < 5; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(offset + 12 + (index * 4), 4),
                uint.MaxValue);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(offset + 32, 4), QuestRecordEmptyFlag);
    }

    /// <summary>
    /// Builds the opcode-10083 scene answer from the captured frame.
    /// </summary>
    /// <remarks>
    /// Captured shape: <c>+4</c> the offered quest, <c>+8</c> the responder npc,
    /// <c>+12</c> the offered quest again and <c>+16</c> the constant 1. The
    /// client's scene key (its request carries 0x1AF720 at <c>+4</c>) does not
    /// come back in the answer, so there is nothing to echo.
    /// </remarks>
    public static byte[] QuestSceneOfferAck(uint questId, uint responderNpcId)
    {
        var packet = (byte[])SceneOfferFrame().Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), questId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)), responderNpcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, sizeof(uint)), questId);
        return packet;
    }

    /// <summary>
    /// Builds the opcode-10082 accept answer from the captured frame.
    /// </summary>
    /// <remarks>
    /// <c>+4</c> giver, <c>+8</c> responder and <c>+12</c> quest are the only
    /// header fields the two captured cycles differ in, and every other difference
    /// lies inside the four reward slots. The slots are therefore the per-quest
    /// part and come from <see cref="StarterQuestRewardRecords"/>; a quest with no
    /// captured records keeps the frame's own slots, because there is no reference
    /// sample to copy for it.
    /// <para>
    /// A quest with no kill objective the capture answered keeps its own frame
    /// instead: <see cref="CapturedQuestAnswers"/> holds the reference's 648 bytes
    /// for it, and only the giver and responder are patched. Quest 1519 answers
    /// with the four class-weapon slots (1000/1400/1700/1800) while the template
    /// carries quest 518's single gift bag; serving the template there handed the
    /// client a one-slot answer for a four-slot quest, and its quest window faulted
    /// at <c>004AC835</c> when the hand-in refreshed that record.
    /// </para>
    /// <para>
    /// The objective area holds exactly one target, so only a quest with exactly
    /// one objective uses it: the capture filled it for the single-target quests
    /// (520 <c>kind=8</c> required 10, 1520 required 10, 1521 required 12) and left
    /// it empty for every multi-target one (528, 533, 537 and 539 - two targets
    /// each - all <c>kind=4</c> with a zero objective). Writing the first target
    /// into a multi-target quest replaced the client's own objective list with one
    /// line, which is why such a quest showed a single counter and hid the rest.
    /// </para>
    /// </remarks>
    public static byte[] QuestAnswer(
        uint giverNpcId,
        uint responderNpcId,
        uint questId)
    {
        var objectives = Godswar.Server.Game.GameClientHandler.DisplayQuestObjectives(questId);
        if (objectives.Count == 0 &&
            CapturedQuestAnswers.TryGetValue(questId, out var capturedHex))
        {
            var captured = Convert.FromHexString(capturedHex);
            BinaryPrimitives.WriteUInt32LittleEndian(
                captured.AsSpan(4, sizeof(uint)), giverNpcId);
            BinaryPrimitives.WriteUInt32LittleEndian(
                captured.AsSpan(8, sizeof(uint)), responderNpcId);
            // A quest answered from a captured frame never reaches the builder
            // below, so a reward menu the GM tool replaced has to be written here
            // as well. Without an override the captured bytes stay untouched.
            if (QuestRewardContentCatalog.Current.TryGetSlots(questId, out _))
            {
                ApplyQuestRewardRecords(
                    captured,
                    QuestAnswerRecordOffset,
                    questId,
                    StarterQuestRewardRecords.AreaBytes,
                    4u);
            }

            return captured;
        }

        var hasObjectives = objectives.Count > 0;
        var packet = hasObjectives
            ? (byte[])Objective10082Bytes.Clone()
            : (byte[])AcceptAnswerFrame().Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), giverNpcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)), responderNpcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, sizeof(uint)), questId);
        // The item half of the quest's objectives is a pair of its own, and it is
        // written whether or not the quest has anything to kill: the reference
        // server's answers for 526/1526/1528 carry it beside their kill objective,
        // and a quest whose text asks only for an item carries it with the kill
        // arrays left empty. It goes last so the kill arrays' clear cannot reach it.
        if (hasObjectives)
        {
            // Every target the quest names goes into its own slot: the reference
            // server's own answer for quest 1533, which asks for fifteen Plumed
            // and fifteen Frenzied Harpies, carries monster 1414/1415 and count
            // 20/20 in the first two slots of the two arrays. Writing only the
            // first target is what left a multi-target quest showing one line.
            WriteObjectiveSlots(
                packet,
                ObjectiveAnswerMonsterOffset,
                ObjectiveAnswerRequiredOffset,
                objectives);
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(ObjectiveAnswerKindOffset, sizeof(int)),
                QuestWithObjectivesKind);
        }

        WriteCollectObjective(
            packet,
            ObjectiveAnswerItemIdOffset,
            ObjectiveAnswerItemCountOffset,
            questId);

        ApplyQuestRewardRecords(
            packet,
            QuestAnswerRecordOffset,
            questId,
            StarterQuestRewardRecords.AreaBytes,
            hasObjectives ? 8u : 4u);
        return packet;
    }

    /// <summary>
    /// Builds the opcode-10084 accept confirmation from the captured frame.
    /// </summary>
    /// <remarks>Captured shape: <c>+4</c> responder npc, <c>+8</c> quest.</remarks>
    public static byte[] QuestConfirm(uint responderNpcId, uint questId)
    {
        var packet = (byte[])AcceptConfirmFrame().Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), responderNpcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)), questId);
        return packet;
    }

    /// <summary>
    /// Builds the opcode-10086 hand-in acknowledgement.
    /// </summary>
    /// <remarks>
    /// Captured shape: <c>+4</c> giver, <c>+8</c> responder, <c>+12</c> the quest
    /// the hand-in completed, <c>+16</c> the reward slot the client asked to be
    /// paid, <c>+28</c> the player's experience and <c>+36</c> the player's talent
    /// points after the reward.
    /// <para>
    /// The three value fields are the frame's own, not the template's. The client
    /// reads <c>+16</c> as the index of the reward slot it must pay out of the
    /// quest record's own slot area, and the reference echoes the index the client
    /// put in its hand-in request: 0 for a quest that pays one item, the chosen
    /// slot for the class-weapon choice (1519 carries 3), and <c>-1</c> for a quest
    /// that pays nothing (1521, 1522). A quest without a captured acknowledgement
    /// used to keep the template's 0, so the client was told to pay slot 0 of a
    /// quest whose slots are all free - that is the 004AC835 null dereference, and
    /// it is what broke every quest the capture never answered (Athens 1522).
    /// </para>
    /// <para>
    /// Experience and talent points are the player's post-hand-in values, exactly
    /// as the reference's own acknowledgements carry them (1518 exp 40 tp 2, 1519
    /// exp 88 tp 5, 1520 exp 183 tp 9, 1521 exp 333 tp 11 - each equal to wire 96
    /// and wire 228 of the status frame sent with it).
    /// </para>
    /// </remarks>
    public static byte[] QuestHandInAck(
        uint giverNpcId,
        uint responderNpcId,
        uint questId,
        uint rewardIndex,
        int experience,
        int talentPoints)
    {
        var packet = CapturedHandInAcks.TryGetValue(questId, out var captured)
            ? Convert.FromHexString(captured)
            : (byte[])HandInAckBytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), giverNpcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)), responderNpcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, sizeof(uint)), questId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(QuestHandInRewardIndexOffset, sizeof(uint)),
            rewardIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(QuestHandInExperienceOffset, sizeof(int)),
            experience);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(QuestHandInTalentPointsOffset, sizeof(int)),
            talentPoints);
        return packet;
    }

    /// <summary>
    /// Where the 10086 acknowledgement carries the reward slot the client asked
    /// for, the player's experience and the player's talent points.
    /// </summary>
    /// <remarks>
    /// Verified against every captured acknowledgement, e.g. quest 1519's on
    /// 2026-09-14 01:08:15 (index 3, exp 88, tp 5) and quest 1521's on
    /// 2026-09-14 01:12:57 (index -1, exp 333, tp 11).
    /// </remarks>
    private const int QuestHandInRewardIndexOffset = 16;
    private const int QuestHandInExperienceOffset = 28;
    private const int QuestHandInTalentPointsOffset = 36;

    /// <summary>
    /// Builds the opcode-10076 follow-up detail from the captured frame.
    /// </summary>
    /// <remarks>
    /// Captured shape: <c>+4</c> the next giver, <c>+8</c> the next quest. This is
    /// what makes the client pop the follow-up quest after a hand-in.
    /// <para>
    /// The reference sends one detail per next quest and they are not uniform:
    /// quest 1521's carries <c>kind = 8</c> with its kill objective (monster 1011,
    /// 12 required) and empty reward slots, while the Sparta chain's carry
    /// <c>kind = 4</c>, no objective and their own reward slots. The captured frame
    /// for the quest is therefore replayed as it is, and only the giver is patched;
    /// filling the objective into a frame that came from a different quest, or
    /// copying another quest's reward slots over it, is what made the client fault
    /// on the hand-in. A quest the capture never answered keeps the template.
    /// </para>
    /// </remarks>
    /// <summary>
    /// True when the reference capture answered this quest's follow-up detail.
    /// </summary>
    /// <remarks>
    /// The fallback below is another quest's frame: its objective area and its
    /// four reward slots belong to that quest. Replaying them for a quest the
    /// capture never answered is what faults the installed client at
    /// <c>004AC835</c> right after a hand-in, so a caller that has no captured
    /// answer for the next quest must not ask for the frame at all.
    /// </remarks>
    public static bool HasCapturedQuestNextDetail(uint questId) =>
        CapturedNextQuestDetails.ContainsKey(questId);

    public static byte[] QuestNextDetail(uint giverNpcId, uint questId)
    {
        if (CapturedNextQuestDetails.TryGetValue(questId, out var captured))
        {
            var capturedPacket = Convert.FromHexString(captured);
            BinaryPrimitives.WriteUInt32LittleEndian(
                capturedPacket.AsSpan(4, sizeof(uint)), giverNpcId);
            // 抓包帧直接返回的话，这条详情里的奖励槽永远是参考服那一份 —— 工具改过的
            // 奖励进不去，客户端详情里就看不到（519 有抓包详情、520 没有，所以 520 走
            // 下面内建那条路反而正常）。和 QuestAnswer 的抓包分支一样：有覆盖才写，
            // 没覆盖就保持抓包原样，免得动到本来正常显示的任务。
            if (QuestRewardContentCatalog.Current.TryGetSlots(questId, out _))
            {
                ApplyQuestRewardRecords(
                    capturedPacket,
                    QuestNextDetailRecordOffset,
                    questId,
                    QuestNextDetailRecordBytes,
                    4u);
            }

            return capturedPacket;
        }

        var detailObjectives = Godswar.Server.Game.GameClientHandler.DisplayQuestObjectives(questId);
        var detailHasObjectives = detailObjectives.Count > 0;
        var packet = (byte[])HandInDetailBytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), giverNpcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)), questId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(QuestNextDetailKindOffset, sizeof(uint)),
            detailHasObjectives ? 8u : 4u);
        // The detail carries the same item pair as the accept answer, four bytes
        // earlier; 1526's own detail is the sample (300 and 5). Written after the
        // kill arrays so their clear cannot reach it.
        if (detailHasObjectives)
        {
            // The same parallel arrays as 10082, four bytes earlier. The reference
            // server's own 10076 for quest 1533 carries both of its targets, so a
            // multi-target follow-up detail is written the same way as the accept
            // answer rather than left empty.
            WriteObjectiveSlots(
                packet,
                QuestNextDetailMonsterOffset,
                QuestNextDetailRequiredOffset,
                detailObjectives);
        }

        WriteCollectObjective(
            packet,
            QuestNextDetailItemIdOffset,
            QuestNextDetailItemCountOffset,
            questId);

        ApplyQuestRewardRecords(
            packet,
            QuestNextDetailRecordOffset,
            questId,
            QuestNextDetailRecordBytes,
            detailHasObjectives ? 8u : 4u);
        return packet;
    }

    /// <summary>
    /// Copies a quest's captured reward slots over the frame's own.
    /// </summary>
    /// <remarks>
    /// The slots belong to the quest, not to the frame, and their number differs
    /// even between siblings: 519 offers the four class weapons, 520 one light
    /// armour, 1521 and 1522 nothing. So the area comes from that quest's own
    /// captured answer - the sample whose objective kind matches the shape being
    /// sent - then from the quest's captured records, and otherwise stays free.
    /// Leaving the template's slots in place was shipping another quest's rewards,
    /// which is what faulted the client at <c>004AC835</c> (one slot for the
    /// four-slot choice of 1519, one bag for the empty 1522).
    /// </remarks>
    private static void ApplyQuestRewardRecords(
        byte[] packet,
        int offset,
        uint questId,
        int bytes,
        uint objectiveKind)
    {
        var area = ResolveQuestRewardArea(questId, objectiveKind);
        var length = Math.Min(bytes, area.Length);
        area.AsSpan(0, length).CopyTo(packet.AsSpan(offset, length));
        QuestRewardDiagnostics.Write(
            questId,
            objectiveKind,
            offset,
            bytes,
            area.Length,
            length,
            packet);
    }

    /// <summary>
    /// The reward area a quest's frames carry, in the order the sources are
    /// consulted: the captured answer whose objective kind matches the shape
    /// being sent, then the quest's own captured records, and otherwise the free
    /// template.
    /// </summary>
    /// <remarks>
    /// One area serves both directions: the accept answer copies its leading 576
    /// bytes (eight 72-byte slots) and the follow-up detail its leading 288 (four
    /// slots). <see cref="QuestRewardItemCatalog"/> reads the item ids out of the
    /// same bytes, so what this server tells the client it will pay and what it
    /// records as payable can never drift apart.
    /// </remarks>
    internal static byte[] ResolveQuestRewardArea(uint questId, uint objectiveKind)
    {
        var area = ResolveBaseQuestRewardArea(questId, objectiveKind);
        return QuestRewardContentCatalog.Current.TryGetSlots(
            questId,
            out var overrides)
            ? ApplyRewardSlotOverrides(area, overrides)
            : area;
    }

    /// <summary>
    /// The reward area the quest shipped with, ignoring any GM override.
    /// </summary>
    /// <remarks>
    /// Used only when the GM has not replaced the reward menu.
    /// </remarks>
    internal static byte[] ResolveBaseQuestRewardArea(
        uint questId,
        uint objectiveKind)
    {        if (CapturedQuestRewardAreas.TryGetValue(
                (questId, objectiveKind),
                out var areaHex) ||
            CapturedQuestRewardAreas.TryGetValue(
                (questId, objectiveKind == 8u ? 4u : 8u),
                out areaHex))
        {
            var capturedArea = Convert.FromHexString(areaHex);
            QuestRewardDiagnostics.Source(questId, objectiveKind, "captured", capturedArea);
            return capturedArea;
        }

        var starterArea = StarterQuestRewardRecords.Find(questId);
        QuestRewardDiagnostics.Source(
            questId,
            objectiveKind,
            starterArea is null ? "empty" : "starter",
            starterArea ?? EmptyQuestRewardArea);
        return starterArea ?? EmptyQuestRewardArea;
    }

    /// <summary>
    /// Builds the reward menu the GM tool replaced: every slot the override names
    /// takes the shape of the slot the quest already shipped, with its item id
    /// swapped in, and every slot it leaves out is empty.
    /// </summary>
    /// <remarks>
    /// A 72-byte slot carries, in order: the item id at +8, <b>five attribute ids</b>
    /// at +12/+16/+20/+24/+28 (<c>FFFFFFFF</c> = empty), the quality and star bytes
    /// at +32/+33, and zeros after that.
    /// <para>
    /// The attribute words are ids from <c>item_attribute_templates</c>, the same
    /// table the GM tool's "添加属性" picker uses - the id itself is the tier
    /// (0 = physical attack I, 1 = II, 3 = IV), so no separate level is needed.
    /// Verified against the captured answer of quest 519, whose reward is
    /// <c>01 50 28 83 FF</c> = AttackB, PhysicalDamage, Hit, MaxHPB, empty, and
    /// 80/40/60 are the ids the operator names "增加物理伤害/命中/暴击加成".
    /// </para>
    /// <para>
    /// An earlier revision of this comment called +16/+20 the "icon words" and
    /// +24 the "icon file". That was wrong: the client resolves the icon from the
    /// item id, and 131 is <c>MaxHPB</c> (max health II), which is why every class
    /// weapon's captured slot carries it.
    /// </para>
    /// <para>
    /// Everything the override does not name keeps the captured bytes, so a quest
    /// the tool never touched answers byte-for-byte as it was captured.
    /// </para>
    /// </remarks>
    private static byte[] ApplyRewardSlotOverrides(
        byte[] area,
        IReadOnlyList<QuestRewardSlotOverride> overrides)
    {
        var result = new byte[QuestRewardItemCatalog.MaximumSlots * QuestRewardItemCatalog.RecordBytes];
        for (var slot = 0; slot < QuestRewardItemCatalog.MaximumSlots; slot++)
        {
            var captured = ReadRewardSlot(area, slot);
            var overridden = overrides.Any(item => item.SlotIndex == slot);
            var template = !overridden || IsEmptyRewardSlot(captured)
                ? EmptyQuestRewardSlot
                : captured;
            var target = result.AsSpan(
                slot * QuestRewardItemCatalog.RecordBytes,
                QuestRewardItemCatalog.RecordBytes);
            var length = Math.Min(target.Length, template.Length);
            template[..length].CopyTo(target);
            foreach (var item in overrides)
            {
                if (item.SlotIndex == slot)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        target.Slice(RewardSlotItemIdOffset, sizeof(uint)),
                        item.ItemId);

                    // 五个属性 ID 就此槽位自己的属性写死：工具配了几个就写几个，
                    // 剩下的写 FFFFFFFF（空）。**不能只写"配了的那些"而把其余留给
                    // 抓包字节** —— 否则拿一个原本带 4 条属性的槽位（例如 519）改成
                    // 只带 1 条，剩下 3 条会残留原任务的属性。
                    var attributes = item.Attributes;
                    Span<short?> ids =
                    [
                        attributes.Attribute1,
                        attributes.Attribute2,
                        attributes.Attribute3,
                        attributes.Attribute4,
                        attributes.Attribute5
                    ];
                    for (var index = 0; index < RewardSlotAttributeSlots; index++)
                    {
                        var offset = RewardSlotAttributeOffset + (index * sizeof(uint));
                        if (offset + sizeof(uint) > target.Length)
                        {
                            break;
                        }

                        var id = ids[index];
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            target.Slice(offset, sizeof(uint)),
                            id is { } value && value > 0 ? (uint)value : uint.MaxValue);
                    }

                    // 品质与星级(grade)就在这一档位的 +32/+33 两个**单字节**上：客户端把
                    // "精致的/重皮护胸"这种前缀按品质拼出来（名字表里只有"轻皮护胸"，
                    // 没有"精致的"），抓包里 520 是 03（精致）、其余任务是 01（加固）-
                    // 按 2 字节写会把 +33/+35 一起清掉（这一条是检查抓出来的）。
                    // 被工具覆盖过的槽位按覆盖值写，跟"发放时盖到物品实例上的属性"保持一致；
                    // 没被覆盖的槽位仍然原样保留抓包字节。
                    if (target.Length >= RewardSlotQualityOffset + 2)
                    {
                        target[RewardSlotQualityOffset] = (byte)attributes.Quality;
                        target[RewardSlotQualityOffset + 1] = (byte)attributes.Grade;
                    }
                }
            }
        }

        return result;
    }

    private static ReadOnlySpan<byte> ReadRewardSlot(byte[] area, int slot)
    {
        var offset = slot * QuestRewardItemCatalog.RecordBytes;
        return offset + QuestRewardItemCatalog.RecordBytes <= area.Length
            ? area.AsSpan(offset, QuestRewardItemCatalog.RecordBytes)
            : [];
    }

    private static bool IsEmptyRewardSlot(ReadOnlySpan<byte> slot) =>
        slot.Length < RewardSlotItemIdOffset + sizeof(uint) ||
        BinaryPrimitives.ReadUInt32LittleEndian(
            slot.Slice(RewardSlotItemIdOffset, sizeof(uint))) is
            0 or uint.MaxValue;

    /// <summary>
    /// Writes every target a quest names into the frame's parallel objective
    /// arrays: slot <c>i</c> holds one monster id and one required count.
    /// </summary>
    /// <remarks>
    /// Verified against the reference server's own answer for quest 1533, whose
    /// objective area carries monster 1414 at <c>+32</c> and 1415 at <c>+34</c>,
    /// with 20 at <c>+48</c> and 20 at <c>+50</c>. The area is cleared first so a
    /// template's own single target cannot bleed into a later slot.
    /// </remarks>
    private static void WriteObjectiveSlots(
        byte[] packet,
        int monsterOffset,
        int requiredOffset,
        IReadOnlyList<QuestObjective> objectives)
    {
        packet.AsSpan(monsterOffset, QuestObjectiveMaximumSlots * 2).Clear();
        packet.AsSpan(requiredOffset, QuestObjectiveMaximumSlots * 2).Clear();
        var count = Math.Min(objectives.Count, QuestObjectiveMaximumSlots);
        for (var slot = 0; slot < count; slot++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(monsterOffset + (slot * 2), 2),
                checked((ushort)Math.Clamp(
                    objectives[slot].MonsterId,
                    0u,
                    ushort.MaxValue)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(requiredOffset + (slot * 2), 2),
                checked((ushort)Math.Clamp(
                    objectives[slot].Required,
                    0,
                    ushort.MaxValue)));
        }
    }

    /// <summary>
    /// Writes a quest's item objective - the item id and how many are wanted -
    /// into a frame's item pair, or clears it for a quest that names no item.
    /// </summary>
    /// <remarks>
    /// A quest's objectives are not all kills: the client's own text also says
    /// "collect 5 Snake Tails", and that half lives in its own pair of fields with
    /// its own list on the client. The pair is the quest's own, so a quest without
    /// one clears the area rather than leaving whatever the template held. See
    /// <see cref="ObjectiveAnswerItemIdOffset"/> for the reference samples behind
    /// each of the three offsets.
    /// </remarks>
    private static void WriteCollectObjective(
        Span<byte> packet,
        int itemIdOffset,
        int itemCountOffset,
        uint questId)
    {
        var itemId = 0u;
        var required = 0;
        if (StarterQuestCollectObjectives.TryGet(questId, out var objective))
        {
            itemId = objective.ItemId;
            // Zero means the client goal specifies a virtual collection, but
            // no verified native display ID is available. Its progress is sent
            // through a personal notice; never fabricate a native item record.
            required = itemId == 0 ? 0 : objective.Required;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.Slice(itemIdOffset, sizeof(uint)),
            itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.Slice(itemCountOffset, sizeof(uint)),
            (uint)Math.Max(0, required));
    }

    /// <summary>
    /// Writes a carried quest's targets into one login-snapshot descriptor, plus
    /// the progress of each slot and whether the quest's objective has been met.
    /// </summary>
    /// <remarks>
    /// The monster and count arrays are the same parallel u16 arrays as 10082, at
    /// the descriptor's own offsets. Progress is the one field with no
    /// multi-target sample: the captured single-target descriptor puts the count
    /// done in the high half of the word at <c>+80</c> (<c>3 &lt;&lt; 16</c> for
    /// three of ten), so slot <c>i</c> keeps that shape at <c>+80 + 4i</c>. That
    /// per-slot stride is inferred, not captured.
    /// <para>
    /// The state word at <c>+72</c> is one word per quest, not per slot, so a quest
    /// counts as met only once every target has been: the captures hold one target
    /// per descriptor, and reporting a partly finished multi-target quest as met is
    /// the same client-side hand-in the server then refuses.
    /// </para>
    /// </remarks>
    private static void WriteSnapshotObjectives(
        Span<byte> descriptor,
        uint questId,
        IReadOnlyList<QuestSnapshotObjective> objectives)
    {
        descriptor.Slice(
            QuestSnapshotMonsterOffset,
            QuestObjectiveMaximumSlots * 2).Clear();
        descriptor.Slice(
            QuestSnapshotRequiredOffset,
            QuestObjectiveMaximumSlots * 2).Clear();
        var count = Math.Min(objectives.Count, QuestObjectiveMaximumSlots);
        for (var slot = 0; slot < count; slot++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                descriptor.Slice(QuestSnapshotMonsterOffset + (slot * 2), 2),
                checked((ushort)Math.Clamp(
                    objectives[slot].MonsterId,
                    0u,
                    ushort.MaxValue)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                descriptor.Slice(QuestSnapshotRequiredOffset + (slot * 2), 2),
                checked((ushort)Math.Clamp(
                    objectives[slot].Required,
                    0,
                    ushort.MaxValue)));
            // The descriptor is 96 bytes and the progress words are the last field
            // in it, so only the slots whose word still fits inside this descriptor
            // are written. The fourth slot's word is the last one that does, which
            // is the same four the monster array holds, so this is a guard rather
            // than a case that is reached today.
            if (QuestSnapshotProgressOffset + (slot * 4) + 4 >
                QuestSnapshotDescriptorBytes)
            {
                break;
            }

            BinaryPrimitives.WriteInt32LittleEndian(
                descriptor.Slice(QuestSnapshotProgressOffset + (slot * 4), 4),
                Math.Clamp(objectives[slot].Current, 0, ushort.MaxValue) << 16);
        }

        BinaryPrimitives.WriteInt32LittleEndian(
            descriptor.Slice(QuestSnapshotKindOffset, 4),
            QuestWithObjectivesKind);
        // The descriptor's item pair, eight bytes later than the answer's: quest
        // 1557's own snapshot carries 303 (Deer Antler) at +32 and the ten it wants
        // at +48, beside its kill objective. Written after the arrays' clear, which
        // stops at the fourth slot so it cannot reach +32 or +48.
        WriteCollectObjective(
            descriptor,
            QuestSnapshotItemIdOffset,
            QuestSnapshotItemCountOffset,
            questId);
        var satisfied = true;
        for (var slot = 0; slot < count; slot++)
        {
            if (objectives[slot].Current < objectives[slot].Required)
            {
                satisfied = false;
                break;
            }
        }

        BinaryPrimitives.WriteInt32LittleEndian(
            descriptor.Slice(QuestSnapshotStateOffset, 4),
            satisfied
                ? QuestObjectiveSatisfiedState
                : QuestObjectiveOutstandingState);
    }

    /// <summary>
    /// Builds one of the global quest-mark lists (10078 / 10079).
    /// </summary>
    /// <remarks>
    /// These are what the client's quest-search panel reads: a flat list of the npc
    /// interaction ids that currently have a quest for the character, so it can draw
    /// the mark over them and list the quests without the player walking the map.
    /// The frame is a fixed 648 bytes - the reference server sends it that size even
    /// when it carries one id - with <c>+4</c> the count and one u32 id each from
    /// <c>+8</c>, the rest zero.
    /// <para>
    /// Captured shape, 2026-10-06 13:42:25 (10078, one id):
    /// <c>88 02 5e 27 01 00 00 00 ac 13 00 00</c> then zeros - count 1, id 5036.
    /// The paired 2026-09-28 02:09:33 login burst carries the same id in both
    /// frames, which is what an npc that both gives and receives a quest looks like.
    /// </para>
    /// </remarks>
    public static byte[] QuestNpcMarks(ushort opcode, IReadOnlyList<uint> npcIds)
    {
        ArgumentNullException.ThrowIfNull(npcIds);
        var packet = new byte[QuestNpcMarkBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)), QuestNpcMarkBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)), opcode);
        var count = Math.Min(npcIds.Count, QuestNpcMarkCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), (uint)count);
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(QuestNpcMarkFirstId + (index * sizeof(uint)), sizeof(uint)),
                npcIds[index]);
        }

        return packet;
    }

    /// <summary>Bytes of a global quest-mark list frame, header included.</summary>
    private const int QuestNpcMarkBytes = 648;

    /// <summary>Where the first npc id sits, and how many fit.</summary>
    private const int QuestNpcMarkFirstId = 8;
    private const int QuestNpcMarkCapacity =
        (QuestNpcMarkBytes - QuestNpcMarkFirstId) / sizeof(uint);

    /// <summary>
    /// Builds the answer to the quest window's lookup panel, S2C 10092.
    /// </summary>
    /// <remarks>
    /// Captured 2026-10-06 16:45:40 as the reply to the panel's own request, a
    /// C2S 10091 carrying <c>16</c>: a fixed 48 bytes with <c>+4</c> the count and
    /// then twenty u16 quest ids from <c>+8</c>. The reference answered the same 19
    /// ids to four clicks in a row, and answered <c>0</c> in the same session once
    /// the character held the only quest it could still take, so the list is
    /// per-character state and not a constant to replay.
    /// <para>
    /// The caller passes the quests this server would actually accept, so the panel
    /// cannot advertise one <c>AcceptQuestAsync</c> would refuse. An empty list
    /// comes out byte-identical to the frame the reference sent behind an accept,
    /// which is what that path keeps sending.
    /// </para>
    /// <para>
    /// Origin.exe 005D11D6 and 005D14AE limit ordinary characters to twenty
    /// search buttons; thirty is a special client mode (byte +2B9 equals 10).
    /// Sending thirty to an ordinary character leaves map headings visible
    /// beyond the rows the layout places, producing overlapping text. Keep the
    /// captured twenty-slot frame for every character.
    /// </para>
    /// </remarks>
    public static byte[] QuestLookupAnswer(IReadOnlyList<uint> questIds)
    {
        ArgumentNullException.ThrowIfNull(questIds);
        var count = Math.Min(questIds.Count, QuestLookupCapacity);
        var bytes = Math.Max(
            QuestLookupBytes,
            QuestLookupFirstId + (count * sizeof(ushort)));
        var packet = new byte[bytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)), checked((ushort)bytes));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)), Opcodes.QuestActionPairAck);

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)), (uint)count);
        for (var index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(
                    QuestLookupFirstId + (index * sizeof(ushort)),
                    sizeof(ushort)),
                checked((ushort)questIds[index]));
        }

        return packet;
    }

    /// <summary>Bytes of the shortest quest-lookup answer, header included.</summary>
    /// <remarks>
    /// The length every capture shows, and the shape an empty or short list keeps.
    /// </remarks>
    private const int QuestLookupBytes = 48;

    /// <summary>Where the first quest id sits.</summary>
    private const int QuestLookupFirstId = 8;

    /// <summary>
    /// How many quest ids the frame can carry.
    /// </summary>
    /// <remarks>
    /// The ordinary client's layout handles twenty rows despite thirty XML
    /// controls. Additional eligible quests remain available at their NPCs.
    /// </remarks>
    internal const int QuestLookupCapacity = 20;

    /// <summary>
    /// Builds the opcode-10077 npc quest list, the table behind the quest mark
    /// over an npc.
    /// </summary>
    /// <remarks>
    /// Captured shape: <c>+4</c> npc, <c>+8</c> entry count, then one
    /// (quest, available) pair per entry, 8 bytes each. The captured frame for
    /// npc 5103 lists 519/522/524 - exactly the chain rows whose giver is that
    /// npc - so the list is derived from the chain instead of replayed, which is
    /// what keeps new quests data-only. <paramref name="available"/> is 1 for the
    /// quest the character can accept right now and 0 for the rest.
    /// </remarks>
    public static byte[] QuestMarkerList(
        uint npcId,
        IReadOnlyList<(uint QuestId, uint Available)> entries)
    {
        var packet = new byte[12 + (entries.Count * 8)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2), checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2), Opcodes.QuestMarkerList);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4), (uint)entries.Count);

        var offset = 12;
        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(offset, 4), entry.QuestId);
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(offset + 4, 4), entry.Available);
            offset += 8;
        }

        return packet;
    }

    /// <summary>
    /// Builds the opcode-10080 npc hand-in list.
    /// </summary>
    /// <remarks>
    /// Captured shape: <c>+4</c> npc, <c>+8</c> entry count, then one quest id per
    /// entry. The captured frame for npc 5103 lists 518/521/523 - exactly the
    /// chain rows whose responder is that npc - so this list is derived from the
    /// chain too.
    /// </remarks>
    public static byte[] QuestHandInMenu(uint npcId, IReadOnlyList<uint> questIds)
    {
        var packet = new byte[12 + (questIds.Count * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2), checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2), Opcodes.QuestHandInList);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4), (uint)questIds.Count);

        var offset = 12;
        foreach (var questId in questIds)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                packet.AsSpan(offset, 4), questId);
            offset += 4;
        }

        return packet;
    }
}
