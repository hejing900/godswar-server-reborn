using System.Buffers.Binary;

namespace Godswar.Server.Packets;

/// <summary>
/// 临时诊断：把"每条任务帧到底带了哪些奖励槽"打到服务端日志里。
/// </summary>
/// <remarks>
/// 用来查"519 的任务详情里看不到奖励、518/520 能看到"这件事：日志会说明
/// 该任务走的是抓包区还是 Starter 记录、被写进帧时要求多少字节、实际拷了多少、
/// 以及拷进去的槽位物品 ID。
/// <para>
/// 只是打印，不改任何行为；查完就删掉这个文件和两处调用。
/// </para>
/// </remarks>
internal static class QuestRewardDiagnostics
{
    private const int RecordBytes = 72;

    private const int ItemIdOffset = 8;

    private const int MaximumLoggedSlots = 8;

    /// <summary>记录取数来源，以及那个区里的槽位物品 ID。</summary>
    public static void Source(
        uint questId,
        uint objectiveKind,
        string source,
        ReadOnlySpan<byte> area) =>
        Console.WriteLine(
            $"[quest-reward] source quest={questId} kind={objectiveKind} " +
            $"from={source} bytes={area.Length} slots={Slots(area)}");

    /// <summary>记录实际写进帧的内容。</summary>
    public static void Write(
        uint questId,
        uint objectiveKind,
        int offset,
        int requested,
        int areaBytes,
        int copied,
        ReadOnlySpan<byte> packet)
    {
        var written = copied > 0 && offset + copied <= packet.Length
            ? packet.Slice(offset, copied)
            : ReadOnlySpan<byte>.Empty;
        Console.WriteLine(
            $"[quest-reward] write quest={questId} kind={objectiveKind} " +
            $"offset={offset} requested={requested} area={areaBytes} copied={copied} " +
            $"slots={Slots(written)}");
    }

    private static string Slots(ReadOnlySpan<byte> area)
    {
        if (area.Length < ItemIdOffset + sizeof(uint))
        {
            return "(none)";
        }

        var parts = new List<string>();
        for (var slot = 0; slot < MaximumLoggedSlots; slot++)
        {
            var start = (slot * RecordBytes) + ItemIdOffset;
            if (start + sizeof(uint) > area.Length)
            {
                break;
            }

            var id = BinaryPrimitives.ReadUInt32LittleEndian(area.Slice(start, sizeof(uint)));
            parts.Add(id is 0 or uint.MaxValue ? "-" : id.ToString());
        }

        return parts.Count == 0 ? "(none)" : string.Join(",", parts);
    }
}
