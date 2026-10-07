using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The login accepted-quest snapshot, compared against the reference server's own
/// 10090 frame byte for byte.
/// </summary>
/// <remarks>
/// The golden is the frame the reference published on 2026-10-06 13:43:06, in the
/// EnterMain burst of a re-login, for a character carrying quest 520 at none of its
/// ten kills. That frame is reachable only inside a coalesced TCP segment - the
/// proxy logged its head as <c>declared=65535 opcode=65535</c>, so it never appears
/// as an <c>opcode=10090</c> record and the quest snapshot looked absent from every
/// capture.
/// <para>
/// Only the count, the 96-byte descriptor and the 72-byte record are the builder's
/// own: the rest of the frame's 2048 bytes are the template's, so those regions are
/// what this check pins. The two words that used to be wrong are in the descriptor -
/// the objective state at its <c>+72</c>, which stayed at the template's 3 and told
/// the client a quest was finished, and the count done at its <c>+80</c>.
/// </para>
/// </remarks>
internal static class QuestLoginSnapshotGoldenChecks
{
    public const string CheckName = "Login quest snapshot matches the captured 10090";

    public static Task RunAsync()
    {
        // What the runtime publishes for a character carrying quest 520 with none
        // of its ten kills: the objective slot the snapshot writer fills.
        var packet = PacketBuilder.QuestSnapshot(
        [
            new PacketBuilder.QuestSnapshotEntry(
                520u,
                5054u,
                5054u,
                Objectives:
                [
                    new PacketBuilder.QuestSnapshotObjective(1027u, 10, 0)
                ])
        ]);

        Check.Equal(2048, packet.Length, "snapshot keeps the captured frame size");
        Check.Equal(
            "01000000",
            Convert.ToHexString(packet.AsSpan(4, 4)),
            "snapshot advertises one carried quest");
        Check.Equal(
            ReferenceDescriptor,
            Convert.ToHexString(packet.AsSpan(8, 96)),
            "snapshot descriptor matches the captured 10090");
        Check.Equal(
            ReferenceRecord,
            Convert.ToHexString(packet.AsSpan(104, 72)),
            "snapshot record matches the captured 10090");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The 96-byte descriptor of the reference frame: quest 520, giver and
    /// responder 5054, target 1027, ten wanted, the kill-quest marker, 4 - the
    /// objective still open - and no kills done.
    /// </summary>
    private const string ReferenceDescriptor =
        "08020000BE130000BE1300000000000000000000000000000000000000000000" +
        "0000000000000000030400000000000000000000000000000A00000000000000" +
        "0000000008000000040000000000000000000000000000000000000000000000";

    /// <summary>
    /// The 72-byte record of the reference frame: the newbie gift bag 2100, five
    /// <c>-1</c> and the fill flag.
    /// </summary>
    private const string ReferenceRecord =
        "000000000000000034080000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF03010101" +
        "000000000000000000000000000000000000000000000000000000000000000000000000";
}
