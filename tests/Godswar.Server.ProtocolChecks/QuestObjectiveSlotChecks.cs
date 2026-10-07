using System.Buffers.Binary;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The objective areas of the quest frames are parallel u16 arrays: slot i holds
/// one monster id and one required count, and every target a quest names fills
/// its own slot. Sending only the first slot - which is what a u32 write at the
/// monster offset does - is what left a multi-target quest showing a single line.
/// </summary>
/// <remarks>
/// The two-target golden is the reference server's own 10082 for the Athens quest
/// 1533 (captured 2026-09-24 20:57:27): monster 1414 at +32 and 1415 at +34, count
/// 20 at +48 and 20 at +50. See docs/quest-experience-appraisal-20260924.md's
/// sibling note in docs/quest-system-status-20260913.md.
/// </remarks>
internal static class QuestObjectiveSlotChecks
{
    public const string CheckName = "Multi-target quest objective slots";

    public static Task RunAsync()
    {
        // ---- Athens, two targets: the captured quest 1533 ---------------
        // The monster ids are the reference's own. The counts are this server's:
        // the reference frame carried 20/20 for 1533 while the client's quest text
        // and this server's content both say fifteen each, so the encoding is what
        // is compared here, not the reference's own numbers.
        var harpies = PacketBuilder.QuestAnswer(5286u, 5286u, 1533u);
        Check.Equal(
            "86058705",
            Hex(harpies, 32, 4),
            "1533 accept answer monster slots match the capture");
        Check.Equal(
            "0F000F00",
            Hex(harpies, 48, 4),
            "1533 accept answer fills one count slot per target");
        Check.Equal(
            8,
            BinaryPrimitives.ReadInt32LittleEndian(harpies.AsSpan(20)),
            "1533 accept answer keeps the kill-quest marker");

        // The follow-up detail carries the same two targets four bytes earlier.
        var detail = PacketBuilder.QuestNextDetail(5286u, 1533u);
        Check.Equal(
            "86058705",
            Hex(detail, 28, 4),
            "1533 follow-up detail monster slots match the capture");
        Check.Equal(
            "0F000F00",
            Hex(detail, 44, 4),
            "1533 follow-up detail fills one count slot per target");
        Check.Equal(
            8,
            BinaryPrimitives.ReadInt32LittleEndian(detail.AsSpan(16)),
            "1533 follow-up detail keeps the kill-quest marker");

        // ---- Athens, three targets: quest 1545 -------------------------
        var dragons = PacketBuilder.QuestAnswer(0u, 0u, 1545u);
        Check.Equal(
            "9405930595050000",
            Hex(dragons, 32, 8),
            "1545 accept answer fills three monster slots");
        Check.Equal(
            "1E001E001E000000",
            Hex(dragons, 48, 8),
            "1545 accept answer fills three count slots");

        // ---- Sparta, three targets: quest 545 --------------------------
        var weapons = PacketBuilder.QuestAnswer(0u, 0u, 545u);
        Check.Equal(
            "A605A505A7050000",
            Hex(weapons, 32, 8),
            "545 accept answer fills three monster slots");
        Check.Equal(
            "1E001E001E000000",
            Hex(weapons, 48, 8),
            "545 accept answer fills three count slots");

        // ---- one target keeps the single-slot shape --------------------
        var puppets = PacketBuilder.QuestAnswer(0u, 0u, 520u);
        Check.Equal(
            "03040000",
            Hex(puppets, 32, 4),
            "520 accept answer still names its one target");
        Check.Equal(
            "0A000000",
            Hex(puppets, 48, 4),
            "520 accept answer still carries ten kills");

        // ---- no target leaves the area empty and the marker clear ------
        var talk = PacketBuilder.QuestAnswer(0u, 0u, 1525u);
        Check.Equal(
            "0000000000000000",
            Hex(talk, 32, 8),
            "1525 accept answer names no target");
        Check.True(
            BinaryPrimitives.ReadInt32LittleEndian(talk.AsSpan(20)) != 8,
            "1525 accept answer is not a kill quest");

        // ---- the login snapshot carries every target of a carried quest -
        var snapshot = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(
                1533u,
                5286u,
                5286u,
                Objectives:
                [
                    new PacketBuilder.QuestSnapshotObjective(1414u, 20, 3),
                    new PacketBuilder.QuestSnapshotObjective(1415u, 20, 5)
                ])
        ]);
        const int descriptor = 8;
        Check.Equal(
            "86058705",
            Hex(snapshot, descriptor + 40, 4),
            "snapshot descriptor fills two monster slots");
        Check.Equal(
            "14001400",
            Hex(snapshot, descriptor + 56, 4),
            "snapshot descriptor fills two count slots");
        Check.Equal(
            8,
            BinaryPrimitives.ReadInt32LittleEndian(
                snapshot.AsSpan(descriptor + 68)),
            "snapshot descriptor keeps the kill-quest marker");
        Check.Equal(
            3 << 16,
            BinaryPrimitives.ReadInt32LittleEndian(
                snapshot.AsSpan(descriptor + 80)),
            "snapshot descriptor carries the first target's progress");
        Check.Equal(
            5 << 16,
            BinaryPrimitives.ReadInt32LittleEndian(
                snapshot.AsSpan(descriptor + 84)),
            "snapshot descriptor carries the second target's progress");

        // The single-target form stays byte for byte what the capture holds.
        var single = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(520u, 5054u, 5054u, 1027u, 10, 3)
        ]);
        Check.Equal(
            1027u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                single.AsSpan(descriptor + 40)),
            "single-target descriptor still writes its monster");
        Check.Equal(
            10,
            BinaryPrimitives.ReadInt32LittleEndian(
                single.AsSpan(descriptor + 56)),
            "single-target descriptor still writes its count");
        Check.Equal(
            3 << 16,
            BinaryPrimitives.ReadInt32LittleEndian(
                single.AsSpan(descriptor + 80)),
            "single-target descriptor still writes its progress");

        // ---- the state word says whether the objective has been met -----
        // The reference server's own 10090 for quest 1540 changed just this word
        // and the count done when the thirtieth kill landed: 4 while 12 of 30,
        // 3 once 30 of 30. Its snapshots for the unfinished kill quests carry 4
        // (520 at 0 of 10, 1523 at 0 of 12, 1531 at 0 of 20). Leaving the frame
        // template's 3 here is what made a quest read as finished on the client
        // while the server's own objective check still refused the hand-in.
        Check.Equal(
            4,
            BinaryPrimitives.ReadInt32LittleEndian(
                snapshot.AsSpan(descriptor + 72)),
            "unfinished multi-target snapshot keeps the objective open");
        Check.Equal(
            4,
            BinaryPrimitives.ReadInt32LittleEndian(
                single.AsSpan(descriptor + 72)),
            "unfinished single-target snapshot keeps the objective open");
        var met = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(520u, 5054u, 5054u, 1027u, 10, 10)
        ]);
        Check.Equal(
            3,
            BinaryPrimitives.ReadInt32LittleEndian(
                met.AsSpan(descriptor + 72)),
            "met single-target snapshot marks the objective satisfied");
        var halfMet = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(
                1533u,
                5286u,
                5286u,
                Objectives:
                [
                    new PacketBuilder.QuestSnapshotObjective(1414u, 20, 20),
                    new PacketBuilder.QuestSnapshotObjective(1415u, 20, 5)
                ])
        ]);
        Check.Equal(
            4,
            BinaryPrimitives.ReadInt32LittleEndian(
                halfMet.AsSpan(descriptor + 72)),
            "a partly finished multi-target snapshot is still open");
        return Task.CompletedTask;
    }

    private static string Hex(byte[] packet, int offset, int length) =>
        Convert.ToHexString(packet.AsSpan(offset, length));
}
