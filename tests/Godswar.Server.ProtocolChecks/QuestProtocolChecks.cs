using System.Buffers.Binary;
using System.Globalization;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using QuestObjectives = Godswar.Server.Domain.World.Content.StarterQuestObjectives;

namespace Godswar.Server.ProtocolChecks;

internal static partial class QuestProtocolChecks
{
    public const string CheckName = "Quest protocol framing";

    public static Task RunAsync()
    {
        // Every frame is the reference server's own packet, so only its shape is
        // asserted: length, opcode and the fields the flow depends on.
        var snapshot = PacketBuilder.LoginSnapshotFrame();
        Check.Equal(2048, snapshot.Length, "login snapshot length");
        Check.Equal(
            Opcodes.PlayerAcceptedQuests,
            BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(2, 2)),
            "login snapshot opcode");
        Check.Equal(
            1u,
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(4, 4)),
            "login snapshot reports one quest");
        Check.Equal(
            518u,
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(8, 4)),
            "login snapshot sequence 518");
        Check.Equal(
            5091u,
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(12, 4)),
            "login snapshot giver 5091");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(16, 4)),
            "login snapshot responder 5103");
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(20, 4)),
            "login snapshot objective count");

        var scene = PacketBuilder.SceneOfferFrame();
        Check.Equal(17, scene.Length, "scene offer length");
        Check.Equal(
            Opcodes.QuestSceneQuery,
            BinaryPrimitives.ReadUInt16LittleEndian(scene.AsSpan(2, 2)),
            "scene offer opcode");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(scene.AsSpan(8, 4)),
            "scene offer responder 5103");

        var guideOpen = PacketBuilder.GuideDialogOpenFrame();
        Check.Equal(48, guideOpen.Length, "guide dialog open length");
        Check.Equal(
            5091u,
            BinaryPrimitives.ReadUInt32LittleEndian(guideOpen.AsSpan(4, 4)),
            "guide dialog open addresses 5091");
        Check.Equal(
            3,
            BinaryPrimitives.ReadInt32LittleEndian(guideOpen.AsSpan(8, 4)),
            "guide dialog open flags 3");
        Check.Equal(
            "Sparta_094",
            System.Text.Encoding.ASCII.GetString(guideOpen, 16, 10),
            "guide dialog open script key");

        var responderOpen = PacketBuilder.ResponderDialogOpenFrame();
        Check.Equal(48, responderOpen.Length, "responder dialog open length");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(responderOpen.AsSpan(4, 4)),
            "responder dialog open addresses 5103");
        Check.Equal(
            "Sparta_106",
            System.Text.Encoding.ASCII.GetString(responderOpen, 16, 10),
            "responder dialog open script key");

        var accept = PacketBuilder.AcceptAnswerFrame();
        Check.Equal(648, accept.Length, "accept answer length");
        Check.Equal(
            Opcodes.QuestAction,
            BinaryPrimitives.ReadUInt16LittleEndian(accept.AsSpan(2, 2)),
            "accept answer opcode");
        Check.Equal(
            5091u,
            BinaryPrimitives.ReadUInt32LittleEndian(accept.AsSpan(4, 4)),
            "accept answer giver 5091");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(accept.AsSpan(8, 4)),
            "accept answer responder 5103");

        var confirm = PacketBuilder.AcceptConfirmFrame();
        Check.Equal(12, confirm.Length, "accept confirm length");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(confirm.AsSpan(4, 4)),
            "accept confirm responder 5103");
        Check.Equal(
            518u,
            BinaryPrimitives.ReadUInt32LittleEndian(confirm.AsSpan(8, 4)),
            "accept confirm quest 518");

        // The per-quest builders: the same captured frames with the quest's own
        // giver / responder / id written in, so a new chain row needs no new code.
        var builtOffer = PacketBuilder.QuestSceneOfferAck(519, 5054);
        Check.Equal(17, builtOffer.Length, "built scene offer length");
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtOffer.AsSpan(4, 4)),
            "built scene offer quest 519");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtOffer.AsSpan(8, 4)),
            "built scene offer responder 5054");
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtOffer.AsSpan(12, 4)),
            "built scene offer quest repeated");
        Check.Equal(
            (byte)1,
            builtOffer[16],
            "built scene offer flag 1");

        var builtAnswer = PacketBuilder.QuestAnswer(5103, 5054, 519);
        Check.Equal(648, builtAnswer.Length, "built accept answer length");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtAnswer.AsSpan(4, 4)),
            "built accept answer giver 5103");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtAnswer.AsSpan(8, 4)),
            "built accept answer responder 5054");
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtAnswer.AsSpan(12, 4)),
            "built accept answer quest 519");

        var builtConfirm = PacketBuilder.QuestConfirm(5054, 519);
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtConfirm.AsSpan(4, 4)),
            "built accept confirm responder 5054");
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtConfirm.AsSpan(8, 4)),
            "built accept confirm quest 519");

        CheckCapturedHandInFrames();

        // One carried quest has to reproduce the captured frame exactly: the
        // descriptor at payload 4 and the record at payload 100 are what the
        // capture holds, so this pins the whole layout, descriptors, records and
        // the empty slots in between.
        var oneQuest = PacketBuilder.QuestSnapshot(
            [new PacketBuilder.QuestSnapshotEntry(518u, 5091u, 5103u)]);
        Check.Equal(2048, oneQuest.Length, "single-quest snapshot length");
        Check.True(
            oneQuest.AsSpan().SequenceEqual(PacketBuilder.LoginSnapshotFrame()),
            "a one-quest snapshot reproduces the captured login frame");

        // Several carried quests: the count rises and each quest gets a block of
        // its own - descriptor, then that quest's reward slots - so the second
        // descriptor sits at +688, not at +104. The reference server's own
        // three-quest frame puts its descriptors at +8, +688 and +1368, and
        // 8 + 3 * 680 = 2048 is exactly the captured frame's length.
        const int SecondBlock = 8 + 680;
        var twoQuests = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(518u, 5091u, 5103u),
            new PacketBuilder.QuestSnapshotEntry(519u, 5103u, 5054u)
        ]);
        Check.Equal(2048, twoQuests.Length, "two-quest snapshot length");
        Check.Equal(
            2u,
            BinaryPrimitives.ReadUInt32LittleEndian(twoQuests.AsSpan(4, 4)),
            "two-quest snapshot reports two quests");
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(twoQuests.AsSpan(SecondBlock, 4)),
            "the second descriptor carries quest 519");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                twoQuests.AsSpan(SecondBlock + 4, 4)),
            "the second descriptor carries its giver");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                twoQuests.AsSpan(SecondBlock + 8, 4)),
            "the second descriptor carries its responder");
        Check.Equal(
            3876u,
            BinaryPrimitives.ReadUInt32LittleEndian(twoQuests.AsSpan(112, 4)),
            "the first quest keeps the gift bag in its own reward slot");
        Check.Equal(
            1000u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                twoQuests.AsSpan(SecondBlock + 104, 4)),
            "the second quest's first class weapon sits in the second block");

        var emptySnapshot = PacketBuilder.QuestSnapshot([]);
        Check.Equal(2048, emptySnapshot.Length, "empty snapshot length");
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(emptySnapshot.AsSpan(4, 4)),
            "empty snapshot reports no quest");

        var handInAck = PacketBuilder.HandInAckFrame();
        Check.Equal(120, handInAck.Length, "hand-in ack length");
        Check.Equal(
            0x2766,
            BinaryPrimitives.ReadUInt16LittleEndian(handInAck.AsSpan(2, 2)),
            "hand-in ack opcode");
        Check.Equal(
            5091u,
            BinaryPrimitives.ReadUInt32LittleEndian(handInAck.AsSpan(4, 4)),
            "hand-in ack giver 5091");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(handInAck.AsSpan(8, 4)),
            "hand-in ack responder 5103");

        var builtHandInAck = PacketBuilder.QuestHandInAck(
            5103, 5054, 519, 0xFFFF_FFFFu, 1234, 7);
        Check.Equal(120, builtHandInAck.Length, "built hand-in ack length");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtHandInAck.AsSpan(4, 4)),
            "built hand-in ack giver 5103");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtHandInAck.AsSpan(8, 4)),
            "built hand-in ack responder 5054");
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtHandInAck.AsSpan(12, 4)),
            "built hand-in ack quest 519");
        // The reward slot, the experience and the talent points belong to the
        // hand-in, not to the frame the acknowledgement was shaped from. Quest
        // 519 has no captured acknowledgement, so this ack used to keep the
        // template's slot 0 - and slot 0 of a quest whose slots are all free is
        // the null reward that faulted the client at 004AC835 on the Athens 1522
        // hand-in. The client's own request names the slot, so the caller's value
        // has to survive into the frame.
        Check.Equal(
            0xFFFF_FFFFu,
            BinaryPrimitives.ReadUInt32LittleEndian(builtHandInAck.AsSpan(16, 4)),
            "built hand-in ack carries the caller's reward slot");
        Check.Equal(
            1234u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtHandInAck.AsSpan(28, 4)),
            "built hand-in ack carries the caller's experience");
        Check.Equal(
            7u,
            BinaryPrimitives.ReadUInt32LittleEndian(builtHandInAck.AsSpan(36, 4)),
            "built hand-in ack carries the caller's talent points");

        var handInDetail = PacketBuilder.HandInDetailFrame();
        Check.Equal(356, handInDetail.Length, "hand-in detail length");
        Check.Equal(
            0x275C,
            BinaryPrimitives.ReadUInt16LittleEndian(handInDetail.AsSpan(2, 2)),
            "hand-in detail opcode");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(handInDetail.AsSpan(4, 4)),
            "hand-in detail addresses 5103");

        var markerList = PacketBuilder.QuestMarkerList(
            5103,
            [(519u, 1u), (522u, 0u), (524u, 0u)]);
        Check.Equal(36, markerList.Length, "marker list length");
        Check.Equal(
            Opcodes.QuestMarkerList,
            BinaryPrimitives.ReadUInt16LittleEndian(markerList.AsSpan(2, 2)),
            "marker list opcode");
        Check.Equal(
            5103u,
            BinaryPrimitives.ReadUInt32LittleEndian(markerList.AsSpan(4, 4)),
            "marker list npc");
        Check.Equal(
            3u,
            BinaryPrimitives.ReadUInt32LittleEndian(markerList.AsSpan(8, 4)),
            "marker list entry count");
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(markerList.AsSpan(12, 4)),
            "marker list first quest");
        Check.Equal(
            1u,
            BinaryPrimitives.ReadUInt32LittleEndian(markerList.AsSpan(16, 4)),
            "marker list first quest available");
        Check.Equal(
            524u,
            BinaryPrimitives.ReadUInt32LittleEndian(markerList.AsSpan(28, 4)),
            "marker list last quest");

        var handInList = PacketBuilder.QuestHandInMenu(5103, [518u, 521u, 523u]);
        Check.Equal(24, handInList.Length, "hand-in list length");
        Check.Equal(
            Opcodes.QuestHandInList,
            BinaryPrimitives.ReadUInt16LittleEndian(handInList.AsSpan(2, 2)),
            "hand-in list opcode");
        Check.Equal(
            3u,
            BinaryPrimitives.ReadUInt32LittleEndian(handInList.AsSpan(8, 4)),
            "hand-in list entry count");
        Check.Equal(
            518u,
            BinaryPrimitives.ReadUInt32LittleEndian(handInList.AsSpan(12, 4)),
            "hand-in list first quest");

        Check.Equal(16, PacketBuilder.HandInTailFrame().Length,
            "hand-in tail length");

        Check.True(
            Godswar.Server.Domain.World.Content.QuestContentBaseline
                .IsNewbieGuideResponder(5103) &&
            !Godswar.Server.Domain.World.Content.QuestContentBaseline
                .IsNewbieGuideResponder(5091),
            "the responder is distinguished from the guide");

        CheckQuestDialogOpen();
        CheckQuestMarkerTables();
        CheckQuestLookupAnswer();
        CheckQuestGatingRules();
        CheckQuestDailyQuotaRules();
        CheckQuestRewardRecords();
        CheckQuestHandInRewardSlotForEveryQuest();
        CheckQuestStateIsLoadedEverywhere();
        CheckQuestObjectives();
        CheckQuestObjectiveFrames();
        CheckEveryObjectiveResolves();
        CheckCapturedNpcPlacements();
        CheckCapturedAthensSpawnPlans();
        CheckMallProtocol();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The frames that make the quest window show an unfinished kill quest.
    /// </summary>
    /// <remarks>
    /// Taken from the reference server while 520 was accepted and worked on: its
    /// answer to the accept carries the target monster id and how many are wanted,
    /// and one 10087 follows every counted kill. Without them the window has no
    /// objective to show and falls back to looking finished.
    /// </remarks>
    private static void CheckQuestObjectiveFrames()
    {
        Check.Equal(
            1027u,
            QuestObjectives.For(520)[0].MonsterId,
            "quest 520's target is the client's monster 1027");

        var answer = PacketBuilder.QuestAnswer(5054, 5054, 520);
        Check.Equal(648, answer.Length, "kill-quest answer length");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(4, 4)),
            "kill-quest answer giver");
        Check.Equal(
            520u,
            BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(12, 4)),
            "kill-quest answer quest");
        Check.Equal(
            1027u,
            BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(32, 4)),
            "kill-quest answer carries the target monster");
        Check.Equal(
            10,
            BinaryPrimitives.ReadInt32LittleEndian(answer.AsSpan(48, 4)),
            "kill-quest answer carries the required count");

        // A talk quest keeps the plain frame: it has nothing to count.
        var talk = PacketBuilder.QuestAnswer(5091, 5103, 518);
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(talk.AsSpan(32, 4)),
            "the talk quest's answer carries no target monster");

        var detail = PacketBuilder.QuestObjectiveDetail(5054, 520, 1027, 10);
        Check.Equal(360, detail.Length, "quest detail length");
        Check.Equal(
            Opcodes.QuestSelection,
            BinaryPrimitives.ReadUInt16LittleEndian(detail.AsSpan(2, 2)),
            "quest detail opcode");
        Check.Equal(
            520u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(8, 4)),
            "quest detail quest");
        Check.Equal(
            1027u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(28, 4)),
            "quest detail target monster");
        Check.Equal(
            10,
            BinaryPrimitives.ReadInt32LittleEndian(detail.AsSpan(44, 4)),
            "quest detail required count");

        var progress = PacketBuilder.QuestObjectiveProgress(520, 5054, 1027);        Check.Equal(20, progress.Length, "progress frame length");
        Check.Equal(
            520u,
            BinaryPrimitives.ReadUInt32LittleEndian(progress.AsSpan(4, 4)),
            "progress frame quest");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(progress.AsSpan(8, 4)),
            "progress frame npc");
        Check.Equal(
            1,
            BinaryPrimitives.ReadInt32LittleEndian(progress.AsSpan(12, 4)),
            "progress frame steps by one kill");
        Check.Equal(
            1027u,
            BinaryPrimitives.ReadUInt32LittleEndian(progress.AsSpan(16, 4)),
            "progress frame target monster");

        // The reference server's own follow-up detail carries the next quest's
        // objective: the detail it sent for Athens 1520 (captured 2026-09-14
        // 01:10:08) is kind 8, monster 1010, ten required, and the one for Athens
        // 1521 is the same shape with monster 1011 and twelve required. Sparta 521
        // is Athens 1521's twin one thousand ids apart. The "79 frames with kind 4"
        // this check used to pin were this server's own frames from capture port
        // 7000: the objective was missing because this server left it out of them,
        // not because the reference does.
        var followUp = PacketBuilder.QuestNextDetail(5054, 521);
        Check.Equal(356, followUp.Length, "follow-up detail length");
        Check.Equal(
            521u,
            BinaryPrimitives.ReadUInt32LittleEndian(followUp.AsSpan(8, 4)),
            "follow-up detail quest");
        Check.Equal(
            8,
            BinaryPrimitives.ReadInt32LittleEndian(followUp.AsSpan(16, 4)),
            "follow-up detail names the kill objective");
        Check.Equal(
            1021u,
            BinaryPrimitives.ReadUInt32LittleEndian(followUp.AsSpan(28, 4)),
            "follow-up detail carries Wooden Puppets 1021");
        Check.Equal(
            12,
            BinaryPrimitives.ReadInt32LittleEndian(followUp.AsSpan(44, 4)),
            "follow-up detail requires twelve kills");

        var talkFollowUp = PacketBuilder.QuestNextDetail(5103, 522);
        Check.Equal(
            4,
            BinaryPrimitives.ReadInt32LittleEndian(talkFollowUp.AsSpan(16, 4)),
            "a follow-up with nothing to kill keeps the talk-quest marker");

        // The whole frame is the reference's own, then: Athens 1521's captured
        // detail with only the giver, the quest and the target monster swapped for
        // Sparta 521's - the quest's reward area is the one field that moves with
        // the quest, and 521's is free exactly as 1521's is.
        var referenceFollowUp = Convert.FromHexString(
            "64015c274b140000f105000000000000080000000000000000000000f30300000000000000000000000000000c00000000000000000000000000000000000000" +
            "00000000ffffffffffffffffffffffffffffffffffffffffffffffff010100010000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000ffffffffffffffffffffffffffffffffffffffffffffffff01010001000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000ffffffffffffffffffffffffffffffffffffffffffffffff0101000100000000000000000000000000000000" +
            "00000000000000000000000000000000000000000000000000000000ffffffffffffffffffffffffffffffffffffffffffffffff010100010000000000000000" +
            "000000000000000000000000000000000000000000000000000000000000000000000000");
        BinaryPrimitives.WriteUInt32LittleEndian(
            referenceFollowUp.AsSpan(4, 4), 5054u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            referenceFollowUp.AsSpan(8, 4), 521u);
        BinaryPrimitives.WriteUInt32LittleEndian(
            referenceFollowUp.AsSpan(28, 4), 1021u);
        Check.True(
            PacketBuilder.QuestNextDetail(5054, 521)
                .AsSpan()
                .SequenceEqual(referenceFollowUp),
            "the follow-up detail for a kill quest reproduces the reference frame");

        // The login snapshot's descriptor carries the objective and how far along
        // it is, at the offsets the reference server uses: its own snapshot for a
        // character carrying quest 520 at three of ten had the monster at +40, the
        // count at +56, the kill-quest marker at +68 and 3 << 16 at +80.
        var carried = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(
                520u, 5054u, 5054u, 1027u, 10, 3)
        ]);
        Check.Equal(2048, carried.Length, "carried snapshot length");
        Check.Equal(
            520u,
            BinaryPrimitives.ReadUInt32LittleEndian(carried.AsSpan(8, 4)),
            "carried snapshot quest");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(carried.AsSpan(12, 4)),
            "carried snapshot giver");
        Check.Equal(
            1027u,
            BinaryPrimitives.ReadUInt32LittleEndian(carried.AsSpan(48, 4)),
            "carried snapshot target monster");
        Check.Equal(
            10,
            BinaryPrimitives.ReadInt32LittleEndian(carried.AsSpan(64, 4)),
            "carried snapshot required count");
        Check.Equal(
            8,
            BinaryPrimitives.ReadInt32LittleEndian(carried.AsSpan(76, 4)),
            "carried snapshot marks the quest as having a kill objective");
        Check.Equal(
            3 << 16,
            BinaryPrimitives.ReadInt32LittleEndian(carried.AsSpan(88, 4)),
            "carried snapshot carries the kills already done");
    }

    /// <summary>
    /// The kill objectives generated from the client's own quest text.
    /// </summary>
    /// <remarks>
    /// Both the target and the count come from the client's own text: quest 520
    /// says "Kill 10 Dumb Wood Men", 528 asks for two things at once and 545 for
    /// three. Quest 518 is a talk quest and has none, which is why it is handable
    /// as soon as it is taken.
    /// </remarks>
    private static void CheckQuestObjectives()
    {
        Check.True(
            QuestObjectives.For(518).Count == 0,
            "the talk quest 518 has no kill objective");
        Check.True(
            QuestObjectives.For(520).Count == 1 &&
            QuestObjectives.For(520)[0].Required == 10 &&
            QuestObjectives.For(520)[0].Target == "Dumb Wood Men",
            "quest 520 asks for ten Dumb Wood Men");
        Check.True(
            QuestObjectives.For(528).Count == 2 &&
            QuestObjectives.For(528)[0].Required == 1 &&
            QuestObjectives.For(528)[1].Required == 8,
            "quest 528 asks for one Addiya and eight Fake Treasures");
        Check.True(
            QuestObjectives.For(545).Count == 3 &&
            QuestObjectives.For(545).All(static o => o.Required == 30),
            "quest 545 asks for thirty of each of three targets");
        Check.True(QuestObjectives.ByQuestId.Count >= 313,
            "the existing 313 kill rules remain covered when content is extended");

        // The client ships a second, never-offered copy of many quests whose level
        // band is 200/200 - quest 0 "[Lv200]Welcome to Sparta!" against the live
        // 518 "[Lv1]New to Sparta". The reference's own npc quest tables list none
        // of the 230, and neither does the chain.
        Check.True(
            Godswar.Server.Domain.World.Content.StarterQuestChain.Find(0) is null,
            "the never-offered 200/200 copy of the tutorial is not in the chain");
        Check.True(
            Godswar.Server.Domain.World.Content.StarterQuestChain.Find(518) is not null,
            "the live tutorial row is still in the chain");

        // The four quests the two newly published quest actors unlocked. 209 and
        // 1209 are talk quests in the client's own table (CreatureMapID empty), so
        // only their Sparta/Athens kill counterparts carry an objective.
        Check.True(
            QuestObjectives.For(209).Count == 0 &&
            QuestObjectives.For(1209).Count == 0,
            "the two message-delivery quests stay talk quests");
        Check.True(
            QuestObjectives.For(555).Count == 1 &&
            QuestObjectives.For(555)[0].Required == 40 &&
            QuestObjectives.For(555)[0].MonsterId == 1030u &&
            QuestObjectives.For(555)[0].MapId == 13u,
            "quest 555 asks for forty Huge Spiders on the Peloponnese map");
        Check.True(
            QuestObjectives.For(1555).Count == 1 &&
            QuestObjectives.For(1555)[0].Required == 40 &&
            QuestObjectives.For(1555)[0].MonsterId == 1033u &&
            QuestObjectives.For(1555)[0].MapId == 11u,
            "quest 1555 asks for forty Huge Lions on the Marathon map");

        var objective = QuestObjectives.For(520)[0];
        Check.True(
            QuestObjectives.Matches(objective, 0, "Dumb Wood Man", 183.3f, 11.1f),
            "the quest text's plural matches the monster's name");
        Check.True(
            !QuestObjectives.Matches(objective, 0, "Little Snake", 186.0f, 8.0f),
            "a different monster does not count even at the right place");
        Check.True(
            !QuestObjectives.Matches(objective, 4, "Dumb Wood Man", 186.0f, 8.0f),
            "a kill on another map does not count");
        Check.True(
            QuestObjectives.Matches(objective, 0, null, 200.0f, 8.0f),
            "an unknown monster name falls back to the position");

        // A target the client's own table names differently has to carry that
        // name, otherwise the kill is never credited: 528 asks for eight "Fake
        // Treasures" while the world carries them as "Juno's Box" (1023), which
        // is why the boxes counted on the server but nowhere in the window.
        var boxes = QuestObjectives.For(528)[1];
        Check.Equal(1023u, boxes.MonsterId, "quest 528's boxes are monster 1023");
        Check.Equal(
            "Juno's Box",
            QuestObjectives.NameOf(boxes),
            "the box objective matches the client's own name for it");
        Check.True(
            QuestObjectives.Matches(boxes, 0, "Juno's Box", 71.0f, 191.0f),
            "killing a Juno's Box credits quest 528's second objective");
        Check.True(
            !QuestObjectives.Matches(boxes, 0, "Addiya the Destroyer", 39.0f, 180.0f),
            "the first objective's monster does not credit the second");

        // A quest counts one target at a time, so the window has to be shown the
        // objective still open: 528 reads 1/1 for Addiya and would look finished
        // if the first objective were always the one published.
        Check.Equal(
            0,
            QuestObjectives.ActiveSlot(QuestObjectives.For(528), 0),
            "an untouched quest counts its first objective");
        Check.Equal(
            1,
            QuestObjectives.ActiveSlot(QuestObjectives.For(528), 1),
            "a quest whose first objective is done counts the next one");
        Check.Equal(
            1,
            QuestObjectives.ActiveSlot(
                QuestObjectives.For(528),
                1 | (8 << QuestObjectives.CounterBits)),
            "a finished quest counts its last objective");

        // Plurals in the quest text must still find the client's monster: its
        // table holds "Woodland Wolf" for "Woodland Wolves" and "Persian Spy"
        // for "Persian Spies". A target without a monster id never counts in the
        // window and leaves the quest with no path to follow.
        Check.Equal(
            1431u,
            QuestObjectives.For(531)[0].MonsterId,
            "Woodland Wolves is the client's monster 1431");
        Check.Equal(
            1432u,
            QuestObjectives.For(532)[0].MonsterId,
            "Persian Spies is the client's monster 1432");
        Check.Equal(
            1060u,
            QuestObjectives.For(235)[0].MonsterId,
            "Plains Wolves is the client's monster 1060");

        // The same plural gap sat in the server's own name matching: 531 asks for
        // "Woodland Wolves" and the world carries "Woodland Wolf", so folding the
        // trailing "s" left "wolve" against "wolf" and no kill was ever credited.
        Check.True(
            QuestObjectives.Matches(
                QuestObjectives.For(531)[0], 4, "Woodland Wolf", 197.0f, -200.0f),
            "killing a Woodland Wolf credits quest 531");
        Check.True(
            QuestObjectives.Matches(
                QuestObjectives.For(532)[0], 4, "Persian Spy", 65.0f, -45.0f),
            "killing a Persian Spy credits quest 532");
        Check.True(
            !QuestObjectives.Matches(
                QuestObjectives.For(531)[0], 4, "Vicious Fawn", 197.0f, -200.0f),
            "a different Sparta-outskirts monster does not credit quest 531");

        // Every objective of a live quest names a monster the client knows: the
        // reviewed exceptions are pinned in CheckEveryObjectiveResolves, which
        // walks the whole chain in one pass.

        // Progress packs one counter per objective, so the ten kills count up and
        // only the tenth one satisfies the quest.
        var progress = 0L;
        for (var kill = 0; kill < 9; kill++)
        {
            progress = QuestObjectives.WithCounter(
                progress,
                0,
                QuestObjectives.Counter(progress, 0) + 1);
        }

        Check.True(
            !QuestObjectives.IsSatisfied(QuestObjectives.For(520), progress),
            "nine of ten kills do not satisfy the quest");
        progress = QuestObjectives.WithCounter(progress, 0, 10);
        Check.True(
            QuestObjectives.IsSatisfied(QuestObjectives.For(520), progress),
            "the tenth kill satisfies the quest");
        Check.Equal(
            10,
            QuestObjectives.Counter(progress, 0),
            "the counter holds the kill count");

        // A quest with three targets needs all three, and one target's overflow
        // does not stand in for another.
        var threeTargets = QuestObjectives.For(545);
        var onlyFirst = QuestObjectives.WithCounter(0, 0, 30);
        Check.True(
            !QuestObjectives.IsSatisfied(threeTargets, onlyFirst),
            "one of three targets is not enough");
        var allThree = QuestObjectives.WithCounter(
            QuestObjectives.WithCounter(onlyFirst, 1, 30),
            2,
            30);
        Check.True(
            QuestObjectives.IsSatisfied(threeTargets, allThree),
            "all three targets satisfy the quest");
    }

    /// <summary>
    /// Every query that loads a character must carry the quest state.
    /// </summary>
    /// <remarks>
    /// The accepted-quest list is published from the loaded character, so a reader
    /// that forgets the quest rows drops every quest on relog and the client falls
    /// back to offering the first quest again. That happened once already: the
    /// quest columns were added to the store's column list but not to the login
    /// snapshot's own query, which is the one login actually uses. This pins both
    /// readers to <c>character_quests</c> so the next quest field cannot be added
    /// to only one of them.
    /// </remarks>
    private static void CheckQuestStateIsLoadedEverywhere()
    {
        var root = FindRepositoryRoot();
        string[] readers =
        [
            Path.Combine(
                "src", "Godswar.Server", "State", "PostgresGameStore.cs"),
            Path.Combine(
                "src", "Godswar.Server", "Infrastructure", "Characters",
                "PostgresCharacterSnapshotReader.Core.cs")
        ];
        foreach (var reader in readers)
        {
            var source = File.ReadAllText(Path.Combine(root, reader));
            Check.True(
                source.Contains("character_quests", StringComparison.Ordinal),
                $"{reader} loads the carried quests");
            Check.True(
                source.Contains(
                    "quest.state = 0",
                    StringComparison.Ordinal) &&
                source.Contains("quest.state = 1", StringComparison.Ordinal),
                $"{reader} loads both in-progress and completed quests");
        }
    }

    /// <summary>
    /// Every kill objective in the chain, checked against the client's own tables
    /// in one pass.
    /// </summary>
    /// <remarks>
    /// Walking a hundred quests by hand is not a verification strategy, and a
    /// broken objective is quiet: the quest still shows, still accepts and still
    /// hands in - it simply never counts a kill and never offers a path. This
    /// walks the whole generated table instead and names every objective that
    /// cannot be satisfied, so a run reports which quests are broken rather than
    /// which one happened to be tried.
    /// <para>
    /// Three things have to hold: the client's quest-monster table has to know the
    /// id, that entry has to carry the name the quest text uses, and the
    /// objective's own map has to ship a monster the server's matching rule
    /// accepts. On the Sparta outskirts a fourth holds as well: the generated
    /// spawn plan has to place that monster.
    /// </para>
    /// </remarks>
    private static void CheckEveryObjectiveResolves()
    {
        // Missing monster placements remain defined for the user's later capture.
        foreach (var (questId, objectives) in QuestObjectives.ByQuestId)
        {
            Check.True(objectives.Length is > 0 and <= 4, $"quest {questId} has bounded objective slots");
            foreach (var goal in objectives)
                Check.True(goal.Required is > 0 and <= QuestObjectives.CounterMaximum &&
                    !string.IsNullOrWhiteSpace(QuestObjectives.NameOf(goal)) &&
                    float.IsFinite(goal.X) && float.IsFinite(goal.Z),
                    $"quest {questId} has a count and named target before its spawn exists");
        }
    }

    /// <summary>
    /// The captured NPC placements are applied to the maps that were written
    /// from the client's ini, and to nobody else.
    /// </summary>
    /// <remarks>
    /// Sparta's city and newbie maps are published from the capture already, so
    /// their frames are the reference's own and must never move. Athens city's
    /// rows came from the client's ini, which uses a different appearance word
    /// (0x11 against the capture's 0x111) and ids that are mostly one off - the
    /// client does not treat those as interactive and clicking one sends nothing,
    /// which is the whole reason the policy exists.
    /// </remarks>
    private static void CheckCapturedNpcPlacements()
    {
        var athens = CapturedNpcPlacementPolicy.Apply(
            new NpcSpawnDefinition(
                1,
                "Athens",
                "Athens_025",
                "Athens_025_Male6",
                5164u,
                97f,
                -159f,
                5164u,
                17u,
                1.7f,
                [],
                []));
        Check.Equal(
            5165u,
            athens.ObjectId,
            "the Athens warehouse takes the captured object id");
        Check.Equal(
            5165u,
            athens.InteractionId,
            "the Athens warehouse takes the captured interaction id");
        Check.Equal(
            0x111u,
            athens.AppearanceType,
            "the Athens warehouse takes the captured appearance word");

        // Sparta is already the capture's own data: applying the policy must be a
        // no-op there, or a working map would be rewritten.
        var sparta = CapturedNpcPlacementPolicy.Apply(
            new NpcSpawnDefinition(
                0,
                "Sparta",
                "Sparta_023",
                "Sparta_023_Male6",
                47750u,
                97f,
                -159f,
                47750u,
                17u,
                1.7f,
                [],
                []));
        Check.Equal(
            47750u,
            sparta.ObjectId,
            "the Sparta warehouse keeps its published object id");
        Check.Equal(
            17u,
            sparta.AppearanceType,
            "the Sparta warehouse keeps its published appearance word");

        Check.Equal(
            125,
            CapturedNpcPlacements.CountFor(1),
            "the capture holds 125 Athens city NPCs");
        Check.True(
            CapturedNpcPlacements.CountFor(0) > 0,
            "the capture holds Sparta's city NPCs too");

        CheckCapturedNpcPlacementUniqueness();
    }

    /// <summary>
    /// A captured map is the reference server's own content, so an NPC the
    /// capture did not record there is dropped rather than renamed.
    /// </summary>
    /// <remarks>
    /// Athens_047 is a published row the capture never saw; Athens_052 is a
    /// captured neighbour the reference placed on 5190. An earlier revision kept
    /// 047 by moving it clear of 052, which is what a captured map used to need
    /// before the reference became authoritative for it. A captured map no longer
    /// renames anything: a row the reference never placed there is not its
    /// content. Maps the capture does not cover are still returned untouched,
    /// because "the reference has no such NPC" and "we never went there" are
    /// different statements.
    /// </remarks>
    private static void CheckCapturedNpcPlacementUniqueness()
    {
        NpcSpawnDefinition Published(short mapId, string scene, string key, uint objectId) =>
            new(
                mapId,
                scene,
                key,
                $"{key}_Male18",
                objectId,
                155f,
                -137f,
                objectId,
                17u,
                3f,
                [],
                []);

        var placed = CapturedNpcPlacementPolicy.ApplyToMap(
        [
            Published(1, "Athens", "Athens_047", 5186u),
            Published(1, "Athens", "Athens_052", 5191u)
        ]);

        var captured = placed.Single();
        Check.Equal(
            "Athens_052",
            captured.NpcKey,
            "the uncaptured Athens npc is dropped from a captured map");
        Check.Equal(
            5190u,
            captured.ObjectId,
            "the captured Athens npc keeps the reference's own id");
        Check.Equal(
            captured.ObjectId,
            captured.InteractionId,
            "the captured Athens npc keeps one identity on both axes");

        // A map the capture does not cover keeps every published row, ids included.
        var uncovered = CapturedNpcPlacementPolicy.ApplyToMap(
        [
            Published(3, "Parnitha_1", "Parnitha_1_001", 5190u),
            Published(3, "Parnitha_1", "Parnitha_1_002", 5191u)
        ]);
        Check.Equal(
            2,
            uncovered.Count,
            "an uncovered map keeps all of its published npcs");
        Check.True(
            uncovered.All(static npc => npc.ObjectId == npc.InteractionId),
            "an uncovered map keeps its published identities");

        // A map published from the capture is skipped by the placement step, so a
        // Sparta map is returned with the ids it came in with.
        var sparta = CapturedNpcPlacementPolicy.ApplyToMap(
            [Published(0, "Sparta", "Sparta_023", 47750u)]);
        Check.Equal(
            47750u,
            sparta.Single().ObjectId,
            "a capture-published map keeps its own ids");
    }

    /// <summary>
    /// The hand-in answers replay the reference's own frame for that quest.
    /// </summary>
    /// <remarks>
    /// The reference answers a hand-in with the completed quest's ack and the next
    /// quest's detail, and those frames are per quest: quest 1521's detail carries
    /// <c>kind = 8</c> with its kill objective (monster 1011, 12 required) and four
    /// empty reward groups, while the Sparta chain's details carry <c>kind = 4</c>,
    /// no objective and their own reward slots. Sending the wrong mix of the two is
    /// what faulted the client on the hand-in.
    /// </remarks>
    private static void CheckCapturedHandInFrames()
    {
        var detail = PacketBuilder.QuestNextDetail(5195, 1521);
        Check.Equal(356, detail.Length, "quest 1521 detail keeps its captured length");
        Check.Equal(
            5195u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(4, 4)),
            "quest 1521 detail names its giver");
        Check.Equal(
            1521u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(8, 4)),
            "quest 1521 detail names itself");
        Check.Equal(
            8u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(16, 4)),
            "quest 1521 detail is the kill-quest kind");
        Check.Equal(
            1011u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(28, 4)),
            "quest 1521 detail carries the reference's kill target");
        Check.Equal(
            12u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(44, 4)),
            "quest 1521 detail carries the reference's required count");
        foreach (var group in new[] { 68, 140, 212, 284 })
        {
            Check.True(
                Enumerable.Range(0, 5).All(index =>
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        detail.AsSpan(group + (index * 4), 4)) == 0xFFFF_FFFFu),
                $"quest 1521 detail leaves reward group {group} empty as captured");
        }

        // A kill quest's detail carries its objective, exactly as the reference's
        // own detail for the Athens twin of Sparta 520 does - captured 2026-09-14
        // 01:10:08, kind 8, monster 1010, 10 required. The first sample this
        // check read was this server's own frame from capture port 7000, which
        // left the objective out and made the missing objective look correct.
        var spartaDetail = PacketBuilder.QuestNextDetail(5054, 520);
        Check.Equal(
            8u,
            BinaryPrimitives.ReadUInt32LittleEndian(spartaDetail.AsSpan(16, 4)),
            "quest 520 detail names the kill objective");
        Check.Equal(
            1027u,
            BinaryPrimitives.ReadUInt32LittleEndian(spartaDetail.AsSpan(28, 4)),
            "quest 520 detail carries Dumb Wood Men 1027");
        Check.Equal(
            10u,
            BinaryPrimitives.ReadUInt32LittleEndian(spartaDetail.AsSpan(44, 4)),
            "quest 520 detail requires ten kills");
        Check.Equal(
            2100u,
            BinaryPrimitives.ReadUInt32LittleEndian(spartaDetail.AsSpan(68, 4)),
            "quest 520 detail carries the quest's own reward slot");

        var ack = PacketBuilder.QuestHandInAck(5195, 5195, 1520, 0u, 183, 9);
        Check.Equal(120, ack.Length, "quest 1520 ack keeps its captured length");
        Check.Equal(
            183u,
            BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(28, 4)),
            "quest 1520 ack carries the reference's reward figure");
        Check.Equal(
            9u,
            BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(36, 4)),
            "quest 1520 ack carries the reference's second reward figure");

        // Athens 1522 is the quest whose hand-in faulted the client at 004AC835
        // with no captured acknowledgement to replay: its answer's four reward
        // slots are all free and the client pays out whichever slot +16 names, so
        // the template's 0 pointed the client at a free slot. The client's own
        // hand-in request names -1 for this quest, and that is what the ack must
        // carry, together with the player's own figures.
        var unpaidAck = PacketBuilder.QuestHandInAck(
            5244, 5237, 1522, 0xFFFF_FFFFu, 149, 3);
        Check.Equal(120, unpaidAck.Length, "quest 1522 ack length");
        Check.Equal(
            1522u,
            BinaryPrimitives.ReadUInt32LittleEndian(unpaidAck.AsSpan(12, 4)),
            "quest 1522 ack names quest 1522");
        Check.Equal(
            0xFFFF_FFFFu,
            BinaryPrimitives.ReadUInt32LittleEndian(unpaidAck.AsSpan(16, 4)),
            "quest 1522 ack pays no reward slot");
        Check.Equal(
            149u,
            BinaryPrimitives.ReadUInt32LittleEndian(unpaidAck.AsSpan(28, 4)),
            "quest 1522 ack carries the player's experience");
        Check.Equal(
            3u,
            BinaryPrimitives.ReadUInt32LittleEndian(unpaidAck.AsSpan(36, 4)),
            "quest 1522 ack carries the player's talent points");

        // The accept answer is per quest too. The reference captured 2026-09-14
        // 09:07:49 answered quest 1519 with the four class-weapon slots
        // (1000/1400/1700/1800), while quest 518 offers the single newbie gift bag
        // (3876). Serving the 518 template for 1519 handed the client a one-slot
        // answer for a four-slot quest and faulted it at 004AC835 on the hand-in.
        var classWeaponChoice = PacketBuilder.QuestAnswer(5244, 5195, 1519);
        Check.Equal(
            648,
            classWeaponChoice.Length,
            "quest 1519 answer keeps its captured length");
        Check.Equal(
            1519u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                classWeaponChoice.AsSpan(12, 4)),
            "quest 1519 answer names itself");
        Check.True(
            new[] { 72, 144, 216, 288 }.Select(offset =>
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        classWeaponChoice.AsSpan(offset, 4)))
                .SequenceEqual([1000u, 1400u, 1700u, 1800u]),
            "quest 1519 answer carries the captured class-weapon choice");

        var giftBag = PacketBuilder.QuestAnswer(5091, 5054, 518);
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(giftBag.AsSpan(72, 4)) ==
                3876u &&
            Enumerable.Range(1, 3).All(index =>
                BinaryPrimitives.ReadUInt32LittleEndian(
                    giftBag.AsSpan(72 + (index * 72), 4)) == 0xFFFF_FFFFu),
            "quest 518 answer keeps its single captured gift bag");

        // A multi-target quest fills one slot per target. The older reading here
        // was that a multi-target quest leaves the objective area empty because it
        // "holds one target"; the reference server's own answer for the two-target
        // Athens quest 1533 (captured 2026-09-24 20:57:27) carries kind 8 with
        // monster 1414/1415 and count 20/20 in the first two slots, and leaving the
        // area empty is what made the installed client draw a single line for a
        // quest that asks for two targets. Quest 528 asks for one Addiya the
        // Destroyer and eight Fake Treasures, so both slots are filled.
        var twoTargetQuest = PacketBuilder.QuestAnswer(5054, 5054, 528);
        Check.Equal(
            8u,
            BinaryPrimitives.ReadUInt32LittleEndian(twoTargetQuest.AsSpan(20, 4)),
            "quest 528 answer marks a multi-target kill quest");
        Check.Equal(
            "D405FF03",
            Convert.ToHexString(twoTargetQuest.AsSpan(32, 4)),
            "quest 528 answer fills one monster slot per target");
        Check.Equal(
            "01000800",
            Convert.ToHexString(twoTargetQuest.AsSpan(48, 4)),
            "quest 528 answer fills one count slot per target");

        // No quest may wear another quest's rewards: 1522's captured answer carries
        // four free slots, and a quest the capture never answered (545) keeps them
        // free too instead of inheriting the template's gift bag.
        foreach (var (quest, giver, responder) in new[]
                 {
                     (1522u, 5244u, 5195u),
                     (545u, 5054u, 5054u)
                 })
        {
            var answer = PacketBuilder.QuestAnswer(giver, responder, quest);
            Check.True(
                new[] { 72, 144, 216, 288 }.All(offset =>
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        answer.AsSpan(offset, 4)) == 0xFFFF_FFFFu),
                $"quest {quest} answer keeps its reward slots free");
        }
    }

    /// <summary>
    /// The captured Athens populations stay exactly what the reference streamed.
    /// </summary>
    /// <remarks>
    /// Both maps come from the same session on 2026-09-14. Map 1 is Athens city,
    /// captured before the newbie-map transition at 08:53:57; map 2 is everything
    /// the server streamed after it. The map id is read from the frame itself
    /// (<c>ObjectType = (mapId &lt;&lt; 16) | appearance</c>), so the split is the
    /// reference's own and not an inference from timestamps.
    /// </remarks>
    private static void CheckCapturedAthensSpawnPlans()
    {
        // Athens now travels the way Sparta always did: its monsters are rows in
        // the published monster revision, not a generated plan appended at
        // runtime. These checks pin the same invariants the plan used to hold.
        var published = MonsterContentBaselineV1.LoadDefinitions();
        var byMap = published
            .GroupBy(static row => row.MapId)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        Check.Equal(
            351,
            byMap[1].Length,
            "the captured Athens city population keeps its reviewed size");
        Check.Equal(
            904,
            byMap[2].Length,
            "the captured Athens newbie population keeps its reviewed size");

        for (var mapId = 1; mapId <= 2; mapId++)
        {
            var rows = byMap[(short)mapId];
            Check.Equal(
                rows.Length,
                rows.Select(static row => row.ObjectId).Distinct().Count(),
                $"map {mapId} captured population repeats no object id");
            foreach (var row in rows)
            {
                Check.True(
                    row.ObjectId > 0 &&
                    row.Tier > 0 &&
                    AppearanceWordOf(row) > 0 &&
                    CurrentHealthOf(row) > 0 &&
                    float.IsFinite(row.X) &&
                    float.IsFinite(row.Z) &&
                    float.IsFinite(FacingOf(row)) &&
                    !string.IsNullOrWhiteSpace(row.TemplateKey),
                    $"map {mapId} captured spawn {row.ObjectId} is complete");
            }
        }

        Check.True(
            byMap[1].All(static row => row.ObjectId >= 10_457 && row.ObjectId <= 10_913),
            "the city population keeps the reference's own object id block");
        Check.True(
            byMap[2].All(static row => row.ObjectId >= 10_914 && row.ObjectId <= 12_060),
            "the newbie population keeps the reference's own object id block");

        // One pinned row per map: species, object id and the packet's own values.
        var deer = byMap[1].Single(static row => row.ObjectId == 10_505);
        Check.True(
            deer.TemplateKey == "A_normal_deer_001" &&
            AppearanceWordOf(deer) == 0x0212 &&
            deer.Tier == 3 &&
            CurrentHealthOf(deer) == 283,
            "city spawn 10505 is the reference's Little Deer");

        var boss = byMap[2].Single(static row => row.ObjectId == 11_955);
        Check.True(
            boss.TemplateKey == "C_boss_greecewarrior_001" &&
            AppearanceWordOf(boss) == 0x0112 &&
            boss.Tier == 200 &&
            CurrentHealthOf(boss) == 8_177_792,
            "newbie spawn 11955 is the reference's general");

        // The maps the 2026-09-24/25 capture session added, each with the
        // population and object-id block the reference's own frames carried.
        // Maps 3 and 11 already shipped a partial population and grew; maps 9,
        // 15, 18 and 19 are new to the server entirely.
        (short MapId, int Count, uint MinId, uint MaxId)[] captured =
        [
            (3, 263, 12_061u, 12_758u),
            (9, 308, 17_488u, 18_055u),
            (11, 452, 18_742u, 19_433u),
            (15, 273, 21_672u, 22_516u),
            (18, 185, 23_962u, 24_543u),
            (19, 185, 24_571u, 25_422u)
        ];
        foreach (var (mapId, count, minId, maxId) in captured)
        {
            Check.True(byMap.ContainsKey(mapId), $"map {mapId} has content");
            var rows = byMap[mapId];
            Check.Equal(
                count,
                rows.Length,
                $"map {mapId} captured population keeps its reviewed size");
            Check.Equal(
                0,
                rows.Length - rows.Select(static row => row.ObjectId)
                    .Distinct().Count(),
                $"map {mapId} captured population repeats no object id");
            Check.True(
                rows.All(row =>
                    row.ObjectId >= minId &&
                    row.ObjectId <= maxId),
                $"map {mapId} spawns stay inside the reference's id block");
            Check.True(
                rows.All(row =>
                    row.Tier > 0 &&
                    AppearanceWordOf(row) > 0 &&
                    CurrentHealthOf(row) > 0 &&
                    float.IsFinite(row.X) &&
                    float.IsFinite(row.Z) &&
                    !string.IsNullOrWhiteSpace(row.TemplateKey)),
                $"map {mapId} captured spawns are complete");
        }
    }

    /// <summary>
    /// The appearance word a captured spawn's own 10020 frame carries: the low
    /// 16 bits of the object-type word at packet offset +4 (0x0111/0x0211 for
    /// city NPCs, 0x0012/0x0112/0x0212 for monsters).
    /// </summary>
    private static ushort AppearanceWordOf(CapturedMonsterSpawn row) =>
        BinaryPrimitives.ReadUInt16LittleEndian(row.Packet.AsSpan(4, 2));

    /// <summary>The hit points the captured frame recorded at packet offset +20.</summary>
    private static uint CurrentHealthOf(CapturedMonsterSpawn row) =>
        BinaryPrimitives.ReadUInt32LittleEndian(row.Packet.AsSpan(20, 4));

    /// <summary>The facing the captured frame recorded at packet offset +40.</summary>
    private static float FacingOf(CapturedMonsterSpawn row) =>
        BinaryPrimitives.ReadSingleLittleEndian(row.Packet.AsSpan(40, 4));

    /// <summary>
    /// The mall's frames, checked against the reference capture's own bytes.
    /// </summary>
    /// <remarks>
    /// The mall npc is the Belle2 template actor: the reference opens it with
    /// flags 0x200 and answers its window with four frames keyed by window id 765
    /// (10021 definition with script "BOGART-", 10201 categories, 10248 page,
    /// 10199 with "Luminay"). Replaying them verbatim is what makes the window
    /// appear, so the bytes are pinned here.
    /// </remarks>
    private static void CheckMallProtocol()
    {
        var open = PacketBuilder.NpcMallDialogOpenAck(5212);
        Check.True(
            open.AsSpan().SequenceEqual(Convert.FromHexString(
                "300053275c14000000020000504efb02417468656e735f3037340000000000" +
                "0000000000000000000000000000000000")),
            "the mall npc opens with the captured 10067 frame");

        var window = PacketBuilder.MallWindow();
        Check.Equal(4, window.Count, "the mall window is four frames");
        Check.True(
            window.Select(frame =>
                (int)BinaryPrimitives.ReadUInt16LittleEndian(
                    frame.AsSpan(2, 2))).SequenceEqual([10021, 10201, 10248, 10199]),
            "the mall window frames carry the captured opcodes");
        Check.True(
            window.Select(static frame => frame.Length)
                .SequenceEqual([260, 64, 108, 80]),
            "the mall window frames keep their captured lengths");
        Check.Equal(
            765,
            BinaryPrimitives.ReadInt32LittleEndian(window[0].AsSpan(4, 4)),
            "the mall window is keyed by window id 765");
        Check.Equal(
            "BOGART-",
            System.Text.Encoding.ASCII.GetString(window[0], 12, 7),
            "the mall window carries its captured script name");

        Check.True(
            CapitalNpcServiceProtocol.TryResolve("Athens_074", 5212, out var a) &&
            a == CapitalNpcServiceKind.Mall &&
            CapitalNpcServiceProtocol.TryResolve("Sparta_074", 5071, out var s) &&
            s == CapitalNpcServiceKind.Mall,
            "both camps' mall npc resolve to the mall service");

        CheckMallCatalog();
    }

    /// <summary>
    /// The function-key mall: <c>C2S 10178</c> answers with the camp's captured
    /// catalog, fifteen frames of 88-byte listings.
    /// </summary>
    /// <remarks>
    /// Captured twice, Sparta on 2026-09-13 01:24 and Athens on 2026-09-14 07:10.
    /// Both streams are 19,568 bytes with the same frame lengths, the same
    /// categories 1792..1798 and the same prices; they differ in exactly twelve
    /// records, where each camp advertises its own item, so the two blobs are
    /// pinned separately here.
    /// </remarks>
    private static void CheckMallCatalog()
    {
        int[] capturedFrameLengths =
            [1416, 1416, 96, 1416, 1416, 1416, 1328, 1416, 1416, 1416, 1416, 1416, 1152, 1416, 1416];
        foreach (var camp in new[] { GameDefaults.SpartaCamp, GameDefaults.AthensCamp })
        {
            var catalog = PacketBuilder.MallCatalog(camp);
            Check.Equal(19_568, catalog.Length, $"camp {camp} mall catalog length");
            var lengths = new List<int>();
            var offset = 0;
            while (offset < catalog.Length)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    catalog.AsSpan(offset, 2));
                Check.Equal(
                    (ushort)10178,
                    BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(offset + 2, 2)),
                    $"camp {camp} mall catalog frame opcode");
                lengths.Add(length);
                offset += length;
            }

            Check.True(
                lengths.SequenceEqual(capturedFrameLengths),
                $"camp {camp} mall catalog keeps its captured frame lengths");
        }

        Check.True(
            !PacketBuilder.MallCatalog(GameDefaults.SpartaCamp).AsSpan()
                .SequenceEqual(PacketBuilder.MallCatalog(GameDefaults.AthensCamp)),
            "the two camps keep their own captured mall catalog");

        Check.True(
            PacketBuilder.TryResolveMallCatalogOffer(
                GameDefaults.AthensCamp, 0, 0, 9010, out var athensOffer) &&
            athensOffer.UnitPrice == 23 &&
            athensOffer.Currency == CapitalNpcServiceProtocol.MallCurrency,
            "Athens mall listing 0/0 prices item 9010 at the captured 23");

        Check.True(
            PacketBuilder.TryResolveMallCatalogOffer(
                GameDefaults.SpartaCamp, 2, 7, 4629, out var spartaOffer) &&
            spartaOffer.UnitPrice == 105,
            "Sparta mall listing 2/7 prices its own item 4629 at the captured 105");

        Check.True(
            !PacketBuilder.TryResolveMallCatalogOffer(
                GameDefaults.SpartaCamp, 2, 7, 4614, out _),
            "a camp cannot buy the other camp's mall item by index");

        Check.True(
            PacketBuilder.TryResolveMallCatalogOfferByItem(
                GameDefaults.SpartaCamp, 4629, out var byItem) &&
            byItem.UnitPrice == 105,
            "the mall fallback still prices from the captured record");

        // The two live purchase requests, captured from the client on 2026-09-14
        // 23:29:48: category 0 index 9 asked for item 9040 at 115 gold and category
        // 3 index 2 asked for item 10154 at 15550. Both catalogs answer them the
        // same way, so the request's own numbers never set the price.
        foreach (var camp in new[] { GameDefaults.SpartaCamp, GameDefaults.AthensCamp })
        {
            Check.True(
                PacketBuilder.TryResolveMallCatalogOffer(
                    camp, 0, 9, 9040, out var first) &&
                first.UnitPrice == 115 &&
                first.Currency == CapitalNpcServiceProtocol.MallCurrency,
                $"camp {camp} prices the captured purchase 0/9 at 115 gold");

            Check.True(
                PacketBuilder.TryResolveMallCatalogOffer(
                    camp, 3, 2, 10154, out var second) &&
                second.UnitPrice == 15550,
                $"camp {camp} prices the captured purchase 3/2 at 15550 gold");

            Check.True(
                !PacketBuilder.TryResolveMallCatalogOffer(
                    camp, 0, 9, 10154, out _),
                $"camp {camp} refuses a purchase whose item id is not at that index");
        }

        // Every captured listing resolves through its own category and index, so
        // the client can buy any of them.
        foreach (var camp in new[] { GameDefaults.SpartaCamp, GameDefaults.AthensCamp })
        {
            var listings = 0;
            for (var category = 0; category < 7; category++)
            {
                for (var index = 0; index < 64; index++)
                {
                    if (PacketBuilder.TryResolveMallCatalogOffer(
                            camp, category, index, MallListingItemId(camp, category, index), out _))
                    {
                        listings++;
                    }
                }
            }

            Check.Equal(221, listings, $"camp {camp} mall listings resolve");
        }
    }

    /// <summary>The item id the captured catalog holds at one listing.</summary>
    private static uint MallListingItemId(byte camp, int category, int listingIndex)
    {
        var catalog = PacketBuilder.MallCatalog(camp);
        var offset = 0;
        var categoryIndex = 0;
        while (offset < catalog.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(catalog.AsSpan(offset, 2));
            if (catalog[offset + 4] != category)
            {
                offset += length;
                continue;
            }

            if (catalog[offset + 7] != 0)
            {
                categoryIndex = 0;
            }

            var itemCount = catalog[offset + 6];
            for (var itemIndex = 0; itemIndex < itemCount; itemIndex++, categoryIndex++)
            {
                if (categoryIndex == listingIndex)
                {
                    return BinaryPrimitives.ReadUInt32LittleEndian(
                        catalog.AsSpan(offset + 8 + (itemIndex * 88), 4));
                }
            }

            offset += length;
        }

        return 0;
    }

    private static string FindRepositoryRoot()    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "Godswar.Server",
                    "Godswar.Server.csproj")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Godswar repository root.");
    }

    /// <summary>
    /// The accept answer must carry the quest's own reward slots, not the
    /// template's.
    /// </summary>
    /// <remarks>
    /// Both frames were captured from the reference server, so the slots are
    /// checked against what it actually sent: packet 247 answered quest 518 with a
    /// single filled slot holding 3876, the newbie gift bag, and packet 1373
    /// answered quest 519 with four, the four class weapons. The area is eight
    /// 72-byte slots at frame 64, the reward item sitting at slot + 8.
    /// </remarks>
    private static void CheckQuestRewardRecords()
    {
        var records518 = Godswar.Server.Domain.World.Content
            .StarterQuestRewardRecords.Find(518) ??
            throw new InvalidOperationException("Quest 518 has no reward records.");
        var records519 = Godswar.Server.Domain.World.Content
            .StarterQuestRewardRecords.Find(519) ??
            throw new InvalidOperationException("Quest 519 has no reward records.");
        Check.Equal(576, records518.Length, "quest 518 reward area length");
        Check.Equal(
            1,
            CountFilledSlots(records518),
            "quest 518 fills one reward slot");
        Check.Equal(
            4,
            CountFilledSlots(records519),
            "quest 519 fills four reward slots");
        Check.Equal(
            3876u,
            BinaryPrimitives.ReadUInt32LittleEndian(records518.AsSpan(8, 4)),
            "quest 518's slot holds the newbie gift bag at slot + 8");

        var answer518 = PacketBuilder.QuestAnswer(5091, 5103, 518);
        Check.True(
            answer518.AsSpan(64, 576).SequenceEqual(records518),
            "the 10082 answer for 518 carries its captured reward slots");
        Check.Equal(
            3876u,
            BinaryPrimitives.ReadUInt32LittleEndian(answer518.AsSpan(72, 4)),
            "quest 518 offers the newbie gift bag 3876");

        // The same builder for 519 must not repeat 518's gift bag: this is what
        // makes one reward table serve every quest instead of one code path each.
        var answer519 = PacketBuilder.QuestAnswer(5103, 5054, 519);
        Check.True(
            answer519.AsSpan(64, 576).SequenceEqual(records519),
            "the 10082 answer for 519 carries its captured reward slots");
        Check.Equal(
            1000u,
            BinaryPrimitives.ReadUInt32LittleEndian(answer519.AsSpan(72, 4)),
            "quest 519 offers the first class weapon 1000");
        Check.Equal(
            1800u,
            BinaryPrimitives.ReadUInt32LittleEndian(answer519.AsSpan(288, 4)),
            "quest 519 offers the fourth class weapon 1800");

        // A quest with no records of its own is answered with free slots, never
        // with the template's: the template belongs to another quest, and paying
        // out its items is what faulted the client at 004AC835 (one slot for the
        // four-slot choice of 1519). Quest 522 is a talk quest with no reward
        // table, and the first sample this check read was this server's own
        // capture-port-7000 frame, which had borrowed the template area.
        var answer522 = PacketBuilder.QuestAnswer(5103, 5096, 522);
        Check.True(
            Enumerable.Range(0, 4).All(slot =>
                BinaryPrimitives.ReadUInt32LittleEndian(
                    answer522.AsSpan(72 + (slot * 72), 4)) == 0xFFFF_FFFFu),
            "a quest with no captured records is answered with free slots");

        // 10076 carries the same records, four bytes earlier and only four slots
        // deep, so it takes the head of the area.
        var detail519 = PacketBuilder.QuestNextDetail(5103, 519);
        Check.True(
            detail519.AsSpan(60, 288).SequenceEqual(records519.AsSpan(0, 288)),
            "the 10076 follow-up detail carries the quest's reward slots");
    }

    /// <summary>
    /// Every quest of both chains hands in without the acknowledgement inventing a
    /// reward slot.
    /// </summary>
    /// <remarks>
    /// The client pays whichever slot the acknowledgement names, out of the reward
    /// area this server last sent it - the 10082 answer - and the slot it asks for
    /// is read off that same area. Athens 1522 faulted the client at 004AC835
    /// because the acknowledgement named slot 0 for a quest whose area is free and
    /// the client answered it with a null reward; the frame it was built from was
    /// another quest's, so the field said 0 whatever the hand-in was.
    /// <para>
    /// This walks all 920 rows of both camps and holds the invariant over every one
    /// of them: the acknowledgement repeats the slot the client asked for - the
    /// filled slots of that quest's own answer, and the "nothing to pay" value -
    /// and never substitutes a slot of its own. A quest whose answer offers no item
    /// must be able to answer "nothing", which is the exact case that used to
    /// crash.
    /// </para>
    /// </remarks>
    private static void CheckQuestHandInRewardSlotForEveryQuest()
    {
        const uint giver = 1000;
        const uint responder = 2000;
        var withObjective = 0;
        var withRewardSlot = 0;
        foreach (var step in StarterQuestChain.Steps)
        {
            var answer = PacketBuilder.QuestAnswer(giver, responder, step.QuestId);
            for (var slot = 0; slot < 4; slot++)
            {
                // A slot the client can offer is one whose item word is neither -1
                // (the reference's empty slot) nor 0 (the login snapshot's).
                if (BinaryPrimitives.ReadInt32LittleEndian(
                        answer.AsSpan(72 + (slot * 72), 4)) is -1 or 0)
                {
                    continue;
                }

                withRewardSlot++;
                var paid = PacketBuilder.QuestHandInAck(
                    giver, responder, step.QuestId, (uint)slot, 0, 0);
                Check.Equal(
                    (uint)slot,
                    BinaryPrimitives.ReadUInt32LittleEndian(paid.AsSpan(16, 4)),
                    $"quest {step.QuestId} ack repeats reward slot {slot}");
            }

            var unpaid = PacketBuilder.QuestHandInAck(
                giver, responder, step.QuestId, uint.MaxValue, 0, 0);
            Check.Equal(
                step.QuestId,
                BinaryPrimitives.ReadUInt32LittleEndian(unpaid.AsSpan(12, 4)),
                $"quest {step.QuestId} ack names the quest");
            Check.Equal(
                giver,
                BinaryPrimitives.ReadUInt32LittleEndian(unpaid.AsSpan(4, 4)),
                $"quest {step.QuestId} ack carries the giver");
            Check.Equal(
                responder,
                BinaryPrimitives.ReadUInt32LittleEndian(unpaid.AsSpan(8, 4)),
                $"quest {step.QuestId} ack carries the responder");
            Check.Equal(
                uint.MaxValue,
                BinaryPrimitives.ReadUInt32LittleEndian(unpaid.AsSpan(16, 4)),
                $"quest {step.QuestId} ack pays nothing when asked for nothing");

            var objectives = GameClientHandler.DisplayQuestObjectives(step.QuestId);
            withObjective += objectives.Count > 0 ? 1 : 0;
            var detail = PacketBuilder.QuestNextDetail(giver, step.QuestId);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(
                detail.AsSpan(16, 4));
            Check.True(
                kind is 4 or 8,
                $"quest {step.QuestId} detail carries an objective kind");
            if (objectives.Count == 1)
            {
                Check.Equal(
                    8u,
                    kind,
                    $"quest {step.QuestId} detail marks its kill objective");
                Check.Equal(
                    objectives[0].MonsterId,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        detail.AsSpan(28, 4)),
                    $"quest {step.QuestId} detail carries its target monster");
                Check.Equal(
                    objectives[0].Required,
                    BinaryPrimitives.ReadInt32LittleEndian(
                        detail.AsSpan(44, 4)),
                    $"quest {step.QuestId} detail carries its required count");
            }
            else if (objectives.Count == 0)
            {
                Check.Equal(
                    4u,
                    kind,
                    $"quest {step.QuestId} detail marks a quest with nothing to kill");
                Check.Equal(
                    0u,
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        detail.AsSpan(28, 4)),
                    $"quest {step.QuestId} detail carries no target monster");
            }
        }

        // The catalog is data, so the sweep asserts a shape rather than a count:
        // every row was visited (the loop above asserts per quest), a meaningful
        // share of them carry a kill objective, and at least one pays a reward slot.
        // A fixed count would break every time the generated catalog is
        // regenerated, which is exactly what it is meant to survive.
        Check.True(
            withObjective > StarterQuestChain.Steps.Count / 4,
            $"the sweep covers the kill quests of both chains ({withObjective} of " +
            $"{StarterQuestChain.Steps.Count})");
        Check.True(
            withRewardSlot > 0,
            $"the sweep covers quests that pay a reward ({withRewardSlot})");
        Console.WriteLine(
            $"[quest-sweep] rows={StarterQuestChain.Steps.Count} " +
            $"kill-objective={withObjective} reward-slots={withRewardSlot}");
    }

    private static int CountFilledSlots(byte[] area)
    {
        var filled = 0;
        for (var slot = 0; slot < area.Length / 72; slot++)
        {
            // The reward item is the third word of the slot. An unused slot leaves
            // it zero in the login snapshot and -1 in the accept answer, so both
            // count as empty.
            var item = BinaryPrimitives.ReadUInt32LittleEndian(
                area.AsSpan((slot * 72) + 8, 4));
            if (item is not (0u or uint.MaxValue))
            {
                filled++;
            }
        }

        return filled;
    }

    /// <summary>
    /// The generic quest dialog must reproduce the captured guide dialogs exactly.
    /// </summary>
    /// <remarks>
    /// Any npc that has a quest to give or take now opens through
    /// <see cref="PacketBuilder.NpcQuestDialogOpenAck"/> instead of the two
    /// captured frames. If the two agree byte for byte, the registered guide and
    /// responder keep the behaviour they were captured with and every other chain
    /// npc - the one that receives the second quest, for instance - gets the same
    /// quest page instead of a description window with no quest entry.
    /// </remarks>
    private static void CheckQuestDialogOpen()
    {
        var guide = PacketBuilder.NpcQuestDialogOpenAck(5091, "Sparta_094");
        Check.True(
            guide.AsSpan().SequenceEqual(PacketBuilder.GuideDialogOpenFrame()),
            "generic quest dialog reproduces the captured guide dialog");
        var responder = PacketBuilder.NpcQuestDialogOpenAck(5103, "Sparta_106");
        Check.True(
            responder.AsSpan().SequenceEqual(
                PacketBuilder.ResponderDialogOpenFrame()),
            "generic quest dialog reproduces the captured responder dialog");

        // The npc that receives the second quest: captured with flags 0, which is
        // the whole reason its window had no quest entry.
        var receiver = PacketBuilder.NpcQuestDialogOpenAck(5054, "Sparta_057");
        Check.Equal(48, receiver.Length, "quest receiver dialog length");
        Check.Equal(
            Opcodes.NpcDialogOpen,
            BinaryPrimitives.ReadUInt16LittleEndian(receiver.AsSpan(2, 2)),
            "quest receiver dialog opcode");
        Check.Equal(
            5054u,
            BinaryPrimitives.ReadUInt32LittleEndian(receiver.AsSpan(4, 4)),
            "quest receiver dialog npc");
        Check.Equal(
            3,
            BinaryPrimitives.ReadInt32LittleEndian(receiver.AsSpan(8, 4)),
            "quest receiver dialog advertises quest flags 3");
        Check.Equal(
            "Sparta_057",
            System.Text.Encoding.ASCII.GetString(receiver, 16, 10),
            "quest receiver dialog script key");

        // The two camps mirror each other and both have an npc named Acacia, so
        // the dialog has to name the clicked npc's own script key: advertising
        // Sparta's key for Athens_094 (5233) opened the Sparta page, and
        // Athens_106 (5245) was not recognised as a responder at all.
        var athensGuide = PacketBuilder.NpcQuestDialogOpenAck(5233, "Athens_094");
        Check.Equal(
            5233u,
            BinaryPrimitives.ReadUInt32LittleEndian(athensGuide.AsSpan(4, 4)),
            "the Athens guide dialog addresses 5233");
        Check.Equal(
            "Athens_094",
            System.Text.Encoding.ASCII.GetString(athensGuide, 16, 10),
            "the Athens guide dialog advertises its own script key");
        var athensResponder = PacketBuilder.NpcQuestDialogOpenAck(
            5245,
            "Athens_106");
        Check.Equal(
            5245u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                athensResponder.AsSpan(4, 4)),
            "the Athens responder dialog addresses 5245");
        Check.Equal(
            "Athens_106",
            System.Text.Encoding.ASCII.GetString(athensResponder, 16, 10),
            "the Athens responder dialog advertises its own script key");
        Check.True(
            Godswar.Server.Domain.World.Content.QuestContentBaseline
                .IsNewbieGuide(5233) &&
            Godswar.Server.Domain.World.Content.QuestContentBaseline
                .IsNewbieGuide(5245),
            "both Athens newbie npcs are recognised as the guide pair");
        Check.True(
            !Godswar.Server.Domain.World.Content.QuestContentBaseline
                .IsNewbieGuideResponder(5233) &&
            Godswar.Server.Domain.World.Content.QuestContentBaseline
                .IsNewbieGuideResponder(5245) &&
            Godswar.Server.Domain.World.Content.QuestContentBaseline
                .IsNewbieGuideResponder(5103),
            "each camp's responder is recognised, the guide is not");
    }

    /// <summary>
    /// The per-character quest tables must both match the capture and clear the
    /// flag of a quest the character has finished.
    /// </summary>
    private static void CheckQuestMarkerTables()
    {
        // Captured state right after the first quest was handed in: npc 5103
        // offers 519 and receives 518/521/523, plus quests outside the chain.
        var capturedMarker = Convert.FromHexString(
            "3C005D27EF1300000600000007020000010000000A020000000000000C020000" +
            "00000000680000000000000069000000000000006A00000000000000");
        var capturedHandIn = Convert.FromHexString(
            "24006027EF1300000600000006020000090200000B0200006800000069000000" +
            "6A000000");

        var existingMarker = ReadMarkerEntries(capturedMarker);
        var merged = GameClientHandler.MergeMarkerEntries(
            existingMarker,
            acceptableQuests: AcceptableStub(519u),
            chainQuestIds: [519u, 522u, 524u]);
        var rebuilt = PacketBuilder.QuestMarkerList(5103, merged);
        Check.True(
            rebuilt.AsSpan().SequenceEqual(capturedMarker),
            "a rebuilt chain npc marker table matches the capture exactly");

        var rebuiltHandIn = PacketBuilder.QuestHandInMenu(
            5103,
            GameClientHandler.MergeHandInEntries(
                ReadHandInEntries(capturedHandIn),
                [518u, 521u, 523u]));
        Check.True(
            rebuiltHandIn.AsSpan().SequenceEqual(capturedHandIn),
            "a rebuilt chain npc hand-in table matches the capture exactly");

        // The finished quest: the capture carries the guide's table with 518
        // switched on, which is what re-offered it after a relog. Once 518 is
        // done the flag has to go, and once 519 is the next quest the guide has
        // nothing to offer at all.
        var guideTable = Convert.FromHexString(
            "14005D27E3130000010000000602000001000000");
        var cleared = GameClientHandler.MergeMarkerEntries(
            ReadMarkerEntries(guideTable),
            acceptableQuests: AcceptableStub(519u),
            chainQuestIds: [518u]);
        Check.Equal(1, cleared.Count, "the guide still lists one quest");
        Check.Equal(518u, cleared[0].QuestId, "the guide still lists quest 518");
        Check.Equal(
            0u,
            cleared[0].Available,
            "a finished quest is no longer flagged available");
        var clearedFrame = PacketBuilder.QuestMarkerList(5091, cleared);
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(clearedFrame.AsSpan(16, 4)),
            "the rebuilt guide frame carries the cleared flag");
        Check.True(
            GameClientHandler.MergeMarkerEntries(
                ReadMarkerEntries(guideTable),
                acceptableQuests: [],
                chainQuestIds: [518u])[0].Available == 0,
            "with nothing left to accept no quest is flagged available");

        // Several of one npc's quests can be open at once now: a level-gated npc
        // lights every band the character has reached, which is what the
        // single-frontier shape could not express.
        var several = GameClientHandler.MergeMarkerEntries(
            [(146u, 0u), (147u, 0u), (148u, 0u)],
            acceptableQuests: AcceptableStub(146u, 147u),
            chainQuestIds: []);
        Check.Equal(3, several.Count, "the content table's rows are all kept");
        Check.Equal(1u, several[0].Available, "the first open row is flagged");
        Check.Equal(1u, several[1].Available, "the second open row is flagged too");
        Check.Equal(
            0u,
            several[2].Available,
            "a row outside the open list keeps its cleared flag");

        // A chain quest the content table does not carry is added, at the correct
        // flag, so a quest added to the chain needs no content change.
        var extended = GameClientHandler.MergeMarkerEntries(
            [(104u, 1u)],
            acceptableQuests: AcceptableStub(519u),
            chainQuestIds: [519u]);
        Check.Equal(2, extended.Count, "a missing chain quest is appended");
        Check.Equal(104u, extended[0].QuestId, "existing entries keep their order");
        Check.Equal(0u, extended[0].Available, "existing entries lose the flag");
        Check.Equal(519u, extended[1].QuestId, "the chain quest is appended");
        Check.Equal(1u, extended[1].Available, "the appended quest is available");
    }

    /// <summary>
    /// A stand-in accept list carrying just the quest ids a merge cares about.
    /// </summary>
    /// <remarks>
    /// The merge only reads <c>QuestId</c>, so the rest of the row is filler. The
    /// real rows come from the generated catalog and are exercised by
    /// <c>CheckQuestLookupAnswer</c> instead.
    /// </remarks>
    private static List<Godswar.Server.Domain.World.Content.StarterQuestChain.Step>
        AcceptableStub(params uint[] questIds)
    {
        var steps =
            new List<Godswar.Server.Domain.World.Content.StarterQuestChain.Step>(
                questIds.Length);
        foreach (var questId in questIds)
        {
            steps.Add(new Godswar.Server.Domain.World.Content.StarterQuestChain.Step(
                questId,
                Godswar.Server.Domain.World.Content.StarterQuestChain.SpartaCamp,
                "Sparta_106",
                "Sparta_057",
                0,
                0,
                0,
                0,
                1,
                120,
                true,
                Godswar.Server.Domain.World.Content.StarterQuestChain.SortStory,
                0,
                "1",
                false));
        }

        return steps;
    }

    /// <summary>
    /// The answer to the quest window's lookup panel (S2C 10092).
    /// </summary>
    /// <remarks>
    /// Byte-checked against the reference's own reply, captured 2026-10-06
    /// 16:45:40 in answer to the panel's C2S 10091: it listed these 19 quests for a
    /// level-57 Athens character, and listed nothing once that character held the
    /// only quest it could still take.
    /// </remarks>
    private static void CheckQuestLookupAnswer()
    {
        var capturedIds = new uint[]
        {
            1110, 1118, 1535, 1536, 1159, 1160, 1200, 1142, 1171, 1293,
            1146, 1150, 1172, 1232, 1233, 1235, 1240, 1241, 1275,
        };
        var captured = Convert.FromHexString(
            "30006c271300000056045e04ff05000687048804b004760493040d05" +
            "7a047e049404d004d104d304d804d904fb040000");

        var built = PacketBuilder.QuestLookupAnswer(capturedIds);
        Check.Equal(48, built.Length, "quest lookup length");
        Check.Equal(
            Opcodes.QuestActionPairAck,
            BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(2, 2)),
            "quest lookup is opcode 10092");
        Check.True(
            captured.AsSpan().SequenceEqual(built),
            "the quest lookup reproduces the reference's own reply byte for byte");
        Check.Equal(
            19u,
            BinaryPrimitives.ReadUInt32LittleEndian(built.AsSpan(4, 4)),
            "quest lookup counts the list");
        Check.Equal(
            1110u,
            BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(8, 2)),
            "quest lookup starts at the first quest id");
        Check.Equal(
            1275u,
            BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(44, 2)),
            "quest lookup ends at the nineteenth quest id");
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(46, 2)),
            "the twentieth slot stays zero when nineteen are listed");

        // The ordinary native layout places twenty buttons, even though its XML
        // includes thirty. Never send rows its map-heading layout cannot place.
        uint[] thirty = [.. Enumerable.Range(1000, 30).Select(value => (uint)value)];
        var grown = PacketBuilder.QuestLookupAnswer(thirty);
        Check.Equal(
            48,
            grown.Length,
            "thirty eligible quests keep the native twenty-slot frame");
        Check.Equal(
            20u,
            BinaryPrimitives.ReadUInt32LittleEndian(grown.AsSpan(4, 4)),
            "search publishes at most twenty quests");
        Check.Equal(
            1019u,
            BinaryPrimitives.ReadUInt16LittleEndian(grown.AsSpan(46, 2)),
            "the twentieth id lands in the final native slot");

        uint[] overlong = [.. Enumerable.Range(1000, 40).Select(value => (uint)value)];
        var trimmed = PacketBuilder.QuestLookupAnswer(overlong);
        Check.Equal(
            48,
            trimmed.Length,
            "long search lists remain within the captured frame");
        Check.Equal(
            20u,
            BinaryPrimitives.ReadUInt32LittleEndian(trimmed.AsSpan(4, 4)),
            "the frame is capped at the ordinary client's twenty rows");

        // An empty list is exactly the frame the reference sent behind an accept,
        // which is what that path still sends.
        Check.True(
            PacketBuilder.AcceptPairAckFrame()
                .AsSpan()
                .SequenceEqual(PacketBuilder.QuestLookupAnswer([])),
            "an empty lookup is the captured accept-path reply");

        // The list is the character's own: camp, progress and carried quests all
        // move it, which is what makes it different from a replayed constant.
        var freshSparta = new GameCharacter { Camp = GameDefaults.SpartaCamp };
        var sparta = GameClientHandler.AcceptableQuestIds(freshSparta);
        Check.Equal(1, sparta.Count(id => StarterQuestChain.Find(id) is { IsMainLine: true }), "a fresh Sparta character has one main-line quest");
        Check.Equal(518u, sparta[0], "the fresh Sparta character starts at 518");

        var afterFirst = new GameCharacter
        {
            Camp = GameDefaults.SpartaCamp,
            // 519's own floor is 2, so the frontier row is only offered once the
            // character meets its band as well as the chain.
            Level = 2,
            QuestCompletedIds = [518u],
        };
        var next = GameClientHandler.AcceptableQuestIds(afterFirst);
        Check.Equal(1, next.Count(id => StarterQuestChain.Find(id) is { IsMainLine: true }), "the main-line frontier is still one quest");
        Check.Equal(519u, next[0], "handing 518 in moves the frontier to 519");

        var carrying = new GameCharacter
        {
            Camp = GameDefaults.SpartaCamp,
            Level = 20,
            Quests = [new CharacterQuest { QuestId = 518 }],
        };
        // The claim is about the carried row itself, not about the list being
        // empty: a level-20 character also clears several level-gated bands, and
        // every one of those stays open beside the story it is carrying.
        Check.True(
            !GameClientHandler.AcceptableQuestIds(carrying).Contains(518u),
            "a carried quest is not offered again");

        var freshAthens = new GameCharacter { Camp = GameDefaults.AthensCamp };
        var athens = GameClientHandler.AcceptableQuestIds(freshAthens);
        Check.Equal(1, athens.Count(id => StarterQuestChain.Find(id) is { IsMainLine: true }),
            "a fresh Athens character has one main-line quest");
        Check.Equal(1518u, athens[0], "Athens walks its own chain, not Sparta's");
    }

    private static List<(uint QuestId, uint Available)> ReadMarkerEntries(
        byte[] table)
    {
        var entries = new List<(uint, uint)>();
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

    private static List<uint> ReadHandInEntries(byte[] table)
    {
        var entries = new List<uint>();
        var count = (table.Length - 12) / 4;
        for (var index = 0; index < count; index++)
        {
            entries.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                table.AsSpan(12 + (index * 4), 4)));
        }

        return entries;
    }


}

/// <summary>Small shared values for the quest gating checks.</summary>
internal static class QuestProtocolCheckFixtures
{
    /// <summary>
    /// The pre-130 level cap, which <c>MaxLevel</c> uses as "no upper bound".
    /// </summary>
    internal const int LegacyLevelCap = 120;
}
