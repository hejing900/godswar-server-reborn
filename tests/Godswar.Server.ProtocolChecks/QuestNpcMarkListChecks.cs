using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The global quest-mark lists (10078 / 10079) that the client's quest search
/// reads: a fixed 648-byte frame carrying npc interaction ids.
/// </summary>
/// <remarks>
/// The goldens are the reference server's own frames:
/// <list type="bullet">
/// <item>2026-10-06 13:42:25 - 10078 with one id, <c>5036</c> (Sparta_039), right
/// after a hand-in.</item>
/// <item>2026-09-24 20:55:25 - 10078 with three, <c>5155</c> (Athens_016) and
/// <c>5200</c> twice (Athens_061).</item>
/// <item>2026-09-28 02:09:33 - the pair at login, both carrying <c>5176</c>.</item>
/// </list>
/// Every id resolves to a placed npc, and none is a quest id: the client's own
/// Quest.xml holds 1176 quests numbered 0..1656, so the 5xxx range is the npc
/// interaction id space.
/// </remarks>
internal static class QuestNpcMarkListChecks
{
    public const string CheckName = "Global quest mark lists match the capture";

    private const int FrameBytes = 648;
    private const int FirstId = 8;

    public static Task RunAsync()
    {
        // ---- 2026-10-06 13:42:25, one id -------------------------------
        var single = PacketBuilder.QuestNpcMarks(
            Opcodes.QuestAvailableNpcList,
            [5036u]);
        Check.Equal(FrameBytes, single.Length, "the mark list frame is 648 bytes");
        Check.Equal(
            648,
            BinaryPrimitives.ReadUInt16LittleEndian(single.AsSpan(0, 2)),
            "the mark list declares 648");
        Check.Equal(
            Opcodes.QuestAvailableNpcList,
            BinaryPrimitives.ReadUInt16LittleEndian(single.AsSpan(2, 2)),
            "the mark list keeps opcode 10078");
        Check.Equal(
            "88025E2701000000AC130000",
            Convert.ToHexString(single.AsSpan(0, 12)),
            "the mark list reproduces the captured 10078 header and id");
        Check.True(
            single.AsSpan(12).IndexOfAnyExcept((byte)0) < 0,
            "the rest of the mark list frame stays zero");

        // ---- 2026-09-24 20:55:25, three ids ---------------------------
        var three = PacketBuilder.QuestNpcMarks(
            Opcodes.QuestAvailableNpcList,
            [5155u, 5200u, 5200u]);
        Check.Equal(
            "88025E2703000000231400005014000050140000",
            Convert.ToHexString(three.AsSpan(0, 20)),
            "the mark list reproduces the captured three-id 10078");
        Check.True(
            three.AsSpan(20).IndexOfAnyExcept((byte)0) < 0,
            "a three-id mark list leaves the tail zero");

        // ---- 2026-09-28 02:09:33, the pair ---------------------------
        var available = PacketBuilder.QuestNpcMarks(
            Opcodes.QuestAvailableNpcList,
            [5176u]);
        var handIn = PacketBuilder.QuestNpcMarks(
            Opcodes.QuestHandInNpcList,
            [5176u]);
        Check.Equal(
            "88025E270100000038140000",
            Convert.ToHexString(available.AsSpan(0, 12)),
            "the available list reproduces the captured 10078");
        Check.Equal(
            "88025F270100000038140000",
            Convert.ToHexString(handIn.AsSpan(0, 12)),
            "the hand-in list reproduces the captured 10079");

        // ---- the count matches what was written ----------------------
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                PacketBuilder.QuestNpcMarks(Opcodes.QuestAvailableNpcList, [])
                    .AsSpan(4, 4)),
            "a list with nothing in it counts zero");

        var many = PacketBuilder.QuestNpcMarks(
            Opcodes.QuestAvailableNpcList,
            Enumerable.Range(0, 400).Select(index => 5000u + (uint)index).ToArray());
        var capacity = (FrameBytes - FirstId) / 4;
        Check.Equal(
            (uint)capacity,
            BinaryPrimitives.ReadUInt32LittleEndian(many.AsSpan(4, 4)),
            "a list longer than the frame is truncated to its capacity");
        Check.Equal(
            5000u + (uint)capacity - 1u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                many.AsSpan(FirstId + ((capacity - 1) * 4), 4)),
            "the last id that fits is the last one written");
        Check.Equal(
            FrameBytes,
            many.Length,
            "a truncated list is still one frame");

        return Task.CompletedTask;
    }
}
