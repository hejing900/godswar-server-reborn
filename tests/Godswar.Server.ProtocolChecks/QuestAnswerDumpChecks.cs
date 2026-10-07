using Godswar.Server.Packets;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Prints the server's own 10082 accept answer as hex, so it can be compared
/// against a captured reference frame byte for byte.
/// </summary>
/// <remarks>Diagnostic only: it asserts nothing about the frame.</remarks>
internal static class QuestAnswerDumpChecks
{
    public const string CheckName = "Quest answer full frame dump";

    public static Task RunAsync()
    {
        Dump("quest 1533", PacketBuilder.QuestAnswer(5286u, 5286u, 1533u));
        Dump("quest 526", PacketBuilder.QuestAnswer(5097u, 5054u, 526u));
        Dump("quest 520", PacketBuilder.QuestAnswer(5054u, 5054u, 520u));
        Dump("next-detail 1533", PacketBuilder.QuestNextDetail(5286u, 1533u));
        return Task.CompletedTask;
    }

    private static void Dump(string label, byte[] packet)
    {
        Console.WriteLine(
            $"[answer-dump] {label} len={packet.Length} " +
            Convert.ToHexString(packet));
    }
}
