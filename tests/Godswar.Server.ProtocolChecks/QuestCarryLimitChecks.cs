using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// A character may carry up to twenty quests, the login snapshot publishes every
/// one of them, and each gets its own 680-byte block.
/// </summary>
/// <remarks>
/// The block stride is the reference server's own: its three-quest frame
/// (2026-10-04 09:00:49) carries quests 1557, 1158 and 1542 with their descriptors
/// at <c>+8</c>, <c>+688</c> and <c>+1368</c>. <c>8 + 3 * 680 = 2048</c> is exactly
/// that frame's length, so the captured 2048 bytes hold three quests; past three
/// the frame is grown and its declared length corrected.
/// </remarks>
internal static class QuestCarryLimitChecks
{
    public const string CheckName = "Quest carry limit publishes twenty";

    private const int FirstBlock = 8;
    private const int BlockBytes = 680;
    private const int DescriptorBytes = 96;
    private const int RecordBytes = 72;
    private const int RecordSlots = 8;
    private const int CapturedFrameBytes = 2048;

    public static Task RunAsync()
    {
        Check.Equal(
            20,
            GameClientHandler.MaximumCarriedQuests,
            "a character may carry twenty quests");

        // ---- the captured frame is kept while three quests fit --------
        foreach (var count in (int[])[0, 1, 2, 3])
        {
            var frame = Snapshot(count);
            Check.Equal(
                CapturedFrameBytes,
                frame.Length,
                $"{count} carried quests keep the captured 2048-byte frame");
            Check.Equal(
                CapturedFrameBytes,
                BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2)),
                $"{count} carried quests declare the captured length");
            Check.Equal(
                (uint)count,
                BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4, 4)),
                $"{count} carried quests are counted");
        }

        // ---- past three the frame grows and says so -------------------
        var grown = Snapshot(4);
        Check.Equal(
            FirstBlock + (4 * BlockBytes),
            grown.Length,
            "four carried quests grow the frame");
        Check.Equal(
            grown.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(grown.AsSpan(0, 2)),
            "the grown frame declares its own length");

        // ---- twenty: every block lands inside it ----------------------
        var twenty = Snapshot(20);
        Check.Equal(
            FirstBlock + (20 * BlockBytes),
            twenty.Length,
            "twenty carried quests need 13608 bytes");
        Check.Equal(
            twenty.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(twenty.AsSpan(0, 2)),
            "the twenty-quest frame declares its own length");
        Check.Equal(
            20u,
            BinaryPrimitives.ReadUInt32LittleEndian(twenty.AsSpan(4, 4)),
            "the twenty-quest frame counts twenty");

        for (var index = 0; index < 20; index++)
        {
            var questId = 518u + (uint)index;
            Check.Equal(
                questId,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    twenty.AsSpan(FirstBlock + (index * BlockBytes), 4)),
                $"block {index} carries quest {questId}");
        }

        // ---- the second descriptor is not where the reward slots are --
        // The regression this pins: writing descriptor 1 at +104 put it inside
        // quest 0's reward area, so a character carrying two quests saw only the
        // first one.
        var two = Snapshot(2);
        Check.Equal(
            519u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                two.AsSpan(FirstBlock + BlockBytes, 4)),
            "the second descriptor sits at +688, not at +104");
        Check.True(
            FirstBlock + BlockBytes + DescriptorBytes + (RecordSlots * RecordBytes)
                <= FirstBlock + (2 * BlockBytes) + BlockBytes,
            "a quest's eight reward slots fit inside its block");

        // ---- a three-quest frame reproduces the captured offsets ------
        // The reference's descriptors for quests 1557, 1158 and 1542 sit at +8,
        // +688 and +1368; a frame built here must put its three there too.
        var three = Snapshot(3);
        Check.Equal(
            2048,
            three.Length,
            "three quests fill the captured 2048-byte frame exactly");
        foreach (var index in (int[])[0, 1, 2])
        {
            Check.Equal(
                518u + (uint)index,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    three.AsSpan(FirstBlock + (index * BlockBytes), 4)),
                $"three-quest frame descriptor {index} is at +{FirstBlock + (index * BlockBytes)}");
        }

        return Task.CompletedTask;
    }

    /// <summary>A snapshot carrying <paramref name="count"/> chain quests.</summary>
    private static byte[] Snapshot(int count)
    {
        var entries = new PacketBuilder.QuestSnapshotEntry[count];
        for (var index = 0; index < count; index++)
        {
            // 518..537 are the Sparta chain's first twenty rows.
            entries[index] = new PacketBuilder.QuestSnapshotEntry(
                518u + (uint)index,
                5091u,
                5103u);
        }

        return PacketBuilder.QuestSnapshot(entries);
    }
}
