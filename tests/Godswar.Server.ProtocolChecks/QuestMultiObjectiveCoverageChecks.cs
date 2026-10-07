using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Every multi-objective quest the content table knows must fill one slot per
/// objective in all three frames that carry them.
/// </summary>
/// <remarks>
/// 10082 and 10076 hold parallel u16 arrays - the target at one offset and the
/// required count sixteen bytes later - and the 10090 login snapshot holds the
/// same arrays inside each 96-byte descriptor. Filling only the first slot is what
/// leaves the client's quest window showing a single objective, so this walks the
/// whole table rather than a hand-picked quest.
/// </remarks>
internal static class QuestMultiObjectiveCoverageChecks
{
    public const string CheckName = "Every multi-objective quest fills all objective slots";

    /// <summary>10082: target slots start here, one u16 per objective.</summary>
    private const int AnswerMonsterOffset = 32;
    private const int AnswerRequiredOffset = 48;

    /// <summary>10076: the same arrays four bytes earlier.</summary>
    private const int NextDetailMonsterOffset = 28;
    private const int NextDetailRequiredOffset = 44;

    /// <summary>10090: the arrays inside a descriptor that starts at frame 8.</summary>
    private const int SnapshotDescriptor = 8;
    private const int SnapshotMonsterOffset = 40;
    private const int SnapshotRequiredOffset = 56;

    public static Task RunAsync()
    {
        var multi = StarterQuestObjectives.ByQuestId
            .Where(entry => entry.Value.Length >= 2)
            .OrderBy(entry => entry.Key)
            .ToArray();

        Check.True(multi.Length > 0, "the content table has multi-objective quests");

        var answerFailures = new List<string>();
        var detailFailures = new List<string>();
        var snapshotFailures = new List<string>();

        foreach (var (questId, objectives) in multi)
        {
            var answer = PacketBuilder.QuestAnswer(0u, 0u, questId);
            var detail = PacketBuilder.QuestNextDetail(0u, questId);
            var snapshot = PacketBuilder.QuestSnapshot(
            [
                new PacketBuilder.QuestSnapshotEntry(
                    questId,
                    0u,
                    0u,
                    Objectives: objectives
                        .Select(objective => new PacketBuilder.QuestSnapshotObjective(
                            objective.MonsterId,
                            objective.Required,
                            0))
                        .ToArray())
            ]);

            for (var slot = 0; slot < objectives.Length; slot++)
            {
                if (Slot(answer, AnswerMonsterOffset, slot) !=
                    (ushort)objectives[slot].MonsterId)
                {
                    answerFailures.Add($"{questId} slot{slot} target");
                }

                if (Slot(answer, AnswerRequiredOffset, slot) !=
                    (ushort)objectives[slot].Required)
                {
                    answerFailures.Add($"{questId} slot{slot} count");
                }

                if (Slot(detail, NextDetailMonsterOffset, slot) !=
                    (ushort)objectives[slot].MonsterId)
                {
                    detailFailures.Add($"{questId} slot{slot} target");
                }

                if (Slot(detail, NextDetailRequiredOffset, slot) !=
                    (ushort)objectives[slot].Required)
                {
                    detailFailures.Add($"{questId} slot{slot} count");
                }

                if (Slot(snapshot, SnapshotDescriptor + SnapshotMonsterOffset, slot) !=
                    (ushort)objectives[slot].MonsterId)
                {
                    snapshotFailures.Add($"{questId} slot{slot} target");
                }

                if (Slot(snapshot, SnapshotDescriptor + SnapshotRequiredOffset, slot) !=
                    (ushort)objectives[slot].Required)
                {
                    snapshotFailures.Add($"{questId} slot{slot} count");
                }
            }
        }

        Check.True(
            answerFailures.Count == 0,
            $"every 10082 answer fills each objective slot " +
            $"({multi.Length} quests): {Describe(answerFailures)}");
        Check.True(
            detailFailures.Count == 0,
            $"every 10076 detail fills each objective slot " +
            $"({multi.Length} quests): {Describe(detailFailures)}");
        Check.True(
            snapshotFailures.Count == 0,
            $"every 10090 descriptor fills each objective slot " +
            $"({multi.Length} quests): {Describe(snapshotFailures)}");
        return Task.CompletedTask;
    }

    private static ushort Slot(byte[] packet, int baseOffset, int slot) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(baseOffset + (slot * 2), 2));

    private static string Describe(List<string> failures) =>
        failures.Count == 0
            ? "none"
            : string.Join(", ", failures.Take(12)) +
              (failures.Count > 12 ? $", +{failures.Count - 12} more" : string.Empty);
}
