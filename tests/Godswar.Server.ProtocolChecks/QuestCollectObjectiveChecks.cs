using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The item half of a quest's objectives - "Kill 15 Little Snakes and collect 5
/// Snake Tails" - is its own pair of fields, beside the parallel monster/count
/// arrays, and every frame that carries an objective area carries it.
/// </summary>
/// <remarks>
/// The client draws that half on a list of its own: QuestViewUI.xml declares
/// <c>ItemList</c> (text and count) next to <c>CreatureList</c> (name and count).
/// Leaving the pair zero is therefore what made a quest that asks for two things
/// draw one - and every frame here used to leave it zero.
/// <para>
/// The goldens are the reference server's own frames, and the item ids are the
/// ones the client's <c>Text/QuestItem.dat</c> gives those names:
/// </para>
/// <list type="bullet">
/// <item>10082 <c>+24</c>/<c>+40</c>: quest 526 = 400 (Snake Tail) and 5, 1526 =
/// 300 (Honeycomb) and 5, 1528 = 301 (Snake Fangs) and 8.</item>
/// <item>10076/10081 <c>+20</c>/<c>+36</c>: 1526 = 300 and 5, 1528 = 301 and 8,
/// 1146 = 123 and 10.</item>
/// <item>10090 descriptor <c>+32</c>/<c>+48</c>: 1557 = 303 (Deer Antler) and 10,
/// beside its kill objective 1034 wanting 100 at <c>+40</c>/<c>+56</c>.</item>
/// </list>
/// </remarks>
internal static class QuestCollectObjectiveChecks
{
    public const string CheckName = "Quest item objectives fill the item pair";

    public static Task RunAsync()
    {
        // ---- the three 10082 goldens -----------------------------------
        // 1526 and 1528 reproduce the reference frame byte for byte across the
        // whole objective area; only their names are recorded here, so the id each
        // entry carries is visible in the failure.
        var honeycombs = PacketBuilder.QuestAnswer(0u, 0u, 1526u);
        Check.Equal(
            "2C010000" + "00000000" + "F5030000" + "00000000" +
            "05000000" + "00000000" + "0F000000" + "00000000",
            Hex(honeycombs, 24, 32),
            "1526 answer carries Honeycomb 300 x5 beside kill 1013 x15");

        var fangs = PacketBuilder.QuestAnswer(0u, 0u, 1528u);
        Check.Equal(
            "2D010000" + "00000000" + "83050000" + "00000000" +
            "08000000" + "00000000" + "01000000" + "00000000",
            Hex(fangs, 24, 32),
            "1528 answer carries Snake Fangs 301 x8 beside kill 1411 x1");

        var tails = PacketBuilder.QuestAnswer(0u, 0u, 526u);
        Check.Equal(
            400u,
            BinaryPrimitives.ReadUInt32LittleEndian(tails.AsSpan(24, 4)),
            "526 answer carries Snake Tail 400");
        Check.Equal(
            5u,
            BinaryPrimitives.ReadUInt32LittleEndian(tails.AsSpan(40, 4)),
            "526 answer carries the five tails it wants");

        // ---- the 10076/10081 goldens, four bytes earlier ----------------
        var detail = PacketBuilder.QuestNextDetail(0u, 1526u);
        Check.Equal(
            300u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(20, 4)),
            "1526 detail carries Honeycomb 300 at +20");
        Check.Equal(
            5u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(36, 4)),
            "1526 detail carries the five it wants at +36");
        Check.Equal(
            1013u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(28, 4)),
            "1526 detail keeps its kill objective at +28");
        Check.Equal(
            15u,
            BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(44, 4)),
            "1526 detail keeps its kill count at +44");

        var fangDetail = PacketBuilder.QuestNextDetail(0u, 1528u);
        Check.Equal(
            301u,
            BinaryPrimitives.ReadUInt32LittleEndian(fangDetail.AsSpan(20, 4)),
            "1528 detail carries Snake Fangs 301");
        Check.Equal(
            8u,
            BinaryPrimitives.ReadUInt32LittleEndian(fangDetail.AsSpan(36, 4)),
            "1528 detail carries the eight it wants");

        // 1146 is the third goldens' id, and the one the clause rules cannot reach
        // ("pairs of their Buckteeth"), so it is pinned from its captured frame.
        var buckteeth = PacketBuilder.QuestNextDetail(0u, 1146u);
        Check.Equal(
            123u,
            BinaryPrimitives.ReadUInt32LittleEndian(buckteeth.AsSpan(20, 4)),
            "1146 detail carries item 123 at +20");
        Check.Equal(
            10u,
            BinaryPrimitives.ReadUInt32LittleEndian(buckteeth.AsSpan(36, 4)),
            "1146 detail carries the ten it wants at +36");

        // ---- the login snapshot descriptor, eight bytes later -----------
        var snapshot = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(
                1557u,
                5483u,
                5483u,
                Objectives:
                [
                    new PacketBuilder.QuestSnapshotObjective(1034u, 100, 0)
                ])
        ]);
        const int descriptor = 8;
        Check.Equal(
            303u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot.AsSpan(descriptor + 32, 4)),
            "1557 snapshot carries Deer Antler 303 at +32");
        Check.Equal(
            10u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot.AsSpan(descriptor + 48, 4)),
            "1557 snapshot carries the ten it wants at +48");
        Check.Equal(
            1034u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot.AsSpan(descriptor + 40, 4)),
            "1557 snapshot keeps its kill objective at +40");
        Check.Equal(
            100u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                snapshot.AsSpan(descriptor + 56, 4)),
            "1557 snapshot keeps its kill count at +56");

        // ---- the 10081 detail writes every target, not just the first --
        // Its only reference sample with two targets is quest 1541, whose own
        // detail carries 1421/1422 at +28/+30 and 25/25 at +44/+46. This builder
        // used to write one target as a u32, which zeroed the second slot.
        var turtles = PacketBuilder.QuestObjectiveDetail(5284u, 1541u, 0u, 0);
        Check.Equal(
            "8D058E0500000000",
            Hex(turtles, 28, 8),
            "1541 detail fills both target slots like the capture");
        Check.Equal(
            "1900190000000000",
            Hex(turtles, 44, 8),
            "1541 detail fills both count slots like the capture");
        Check.Equal(
            8,
            BinaryPrimitives.ReadInt32LittleEndian(turtles.AsSpan(16)),
            "1541 detail keeps the kill-quest marker");

        // ---- the four-slot kill array must not reach the item pair ------
        // The item count sits where a fifth kill slot would, so a quest with the
        // content's maximum of three targets still leaves both item words alone.
        var three = PacketBuilder.QuestAnswer(0u, 0u, 526u);
        Check.Equal(
            400u,
            BinaryPrimitives.ReadUInt32LittleEndian(three.AsSpan(24, 4)),
            "the kill array's clear stops short of the item id");
        Check.Equal(
            5u,
            BinaryPrimitives.ReadUInt32LittleEndian(three.AsSpan(40, 4)),
            "the kill array's clear stops short of the item count");

        // ---- a quest with no item objective keeps the pair zero ---------
        var noItem = PacketBuilder.QuestAnswer(0u, 0u, 520u);
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(noItem.AsSpan(24, 4)),
            "a quest with no item objective names no item");
        Check.Equal(
            0u,
            BinaryPrimitives.ReadUInt32LittleEndian(noItem.AsSpan(40, 4)),
            "a quest with no item objective wants none");

        // Every requirement needs a real positive count. A zero display ID
        // means an unresolved client-table binding, never a made-up bag item.
        var broken = StarterQuestCollectObjectives.ByQuestId
            .Where(entry => entry.Value.Required <= 0 ||
                entry.Value.Required > StarterQuestObjectives.CounterMaximum)
            .Select(entry => entry.Key)
            .ToArray();
        Check.True(
            broken.Length == 0,
            $"every collect objective has a supported positive count: " +
            $"{string.Join(", ", broken)}");

        foreach (var entry in StarterQuestCollectObjectives.ByQuestId.Where(row => row.Value.ItemId == 0))
        {
            var answer = PacketBuilder.QuestAnswer(0u, 0u, entry.Key);
            Check.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(24, 4)),
                "unresolved display binding cannot fabricate a native item ID");
            Check.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(answer.AsSpan(40, 4)),
                "unresolved display binding cannot publish a count against native item zero");
            var held = new Godswar.Server.State.CharacterQuest { QuestId = entry.Key };
            Check.True(!Godswar.Server.Game.GameClientHandler.AreQuestRequirementsSatisfied(
                new Godswar.Server.State.GameCharacter { Level = 200 }, held),
                "an unresolved display binding still requires actual collection progress");
        }

        // The three 10082 goldens must be in the table, not just in this check.
        (uint QuestId, uint ItemId, int Required)[] goldens =
        [
            (526u, 400u, 5),
            (1526u, 300u, 5),
            (1528u, 301u, 8)
        ];
        foreach (var (questId, itemId, required) in goldens)
        {
            Check.True(
                StarterQuestCollectObjectives.TryGet(questId, out var objective) &&
                objective.ItemId == itemId &&
                objective.Required == required,
                $"quest {questId} carries item {itemId} x{required} in the table");
        }

        return Task.CompletedTask;
    }

    private static string Hex(byte[] packet, int offset, int length) =>
        Convert.ToHexString(packet.AsSpan(offset, length));
}
