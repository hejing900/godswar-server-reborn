using Godswar.Server.Domain.World.Content;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

/// <summary>
/// The Cursed Land (诅咒之地二, runtime map 29) transports the October 4 2026
/// reference capture drove.
/// </summary>
/// <remarks>
/// <para>
/// Three shapes travel over the same two opcodes. The capital Event Transporter
/// opens its ordinary <c>NpcFunTranmit</c> window (function 1) and the server
/// answers the page-one number <c>200</c> by moving the character; the map's five
/// Event Transporters advertise function <c>96</c> on their own and move the
/// character the moment the client asks for the page's entries; and the map's
/// Main City Transporter advertises function <c>97</c> and sends the character
/// home. Every destination, id and number is capture-proven and lives in
/// <see cref="CursedLandTransportProtocol"/>.
/// </para>
/// <para>
/// Split out of <c>GameClientHandler.NpcDialog.cs</c>, whose handler had reached
/// the repository's file-size limit. The routing tables in
/// <c>GameClientHandler.NpcDialog.cs</c> and <c>GameClientHandler.NpcDialogOpen.cs</c>
/// are the only places these actors are bound, so nothing else changes when the
/// roster grows.
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Whether the click is the Event Transporter's 诅咒之地二 entry.
    /// </summary>
    /// <remarks>
    /// The check is deliberately narrow: the same number <c>200</c> is also a
    /// page-two button of the client's script (page-two <c>200</c> draws
    /// "传送到诅咒之地一"), so only dialog index 1 - the page the capture's
    /// click carried - resolves here. Every other selection of that window is
    /// still answered by <c>EventTransporterDialogue</c>.
    /// </remarks>
    private static bool IsCursedLandTwoClick(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int subId) =>
        CursedLandTransportProtocol.IsCursedLandTwoSelection(
            npc.NpcKey,
            dialogIndex,
            subId);

    /// <summary>
    /// Opens one of the map's transport windows.
    /// </summary>
    /// <remarks>
    /// The captured open frame is an ordinary 48-byte <c>10067</c> with flags
    /// <c>0x200</c> and the transport's own function number packed at
    /// <c>+12</c>: <c>30005327 C8150000 00020000 60000000 "Execrativelys3_009"</c>
    /// for function 96 and <c>...61000000 "Execrativelys3_014"</c> for 97. The
    /// client then raises the matching <c>NpcFun*.lua</c> window itself and sends
    /// its initial action back, which is the click this class answers.
    /// </remarks>
    private async Task SendCursedLandTransporterOpenAsync(
        NpcSpawnDefinition npc,
        int functionNumber,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                [functionNumber],
                npc.NpcKey),
            cancellationToken,
            "CursedLandTransporterOpen");
        Console.WriteLine(
            $"[cursed-land] transporter open npc={npc.InteractionId} " +
            $"key={npc.NpcKey} function={functionNumber}");
    }

    /// <summary>
    /// Moves an eligible character to 诅咒之地二.
    /// </summary>
    /// <remarks>
    /// The captured admission is a two-frame burst on the same millisecond:
    /// <c>10025 {596, 0, 0}</c> then <c>10018 {596, -196, 0, 44, 0x001D0001, 1}</c>.
    /// <c>TransporterProtocol</c>'s ordinary world map arrivals are not reused:
    /// the reference's landing here is the map's own arrival point, not a portal
    /// anchor, and the map has no walking link to admit it.
    /// <para>
    /// A character outside the band is refused with the reference's own answer,
    /// <c>10070 {npc, 1, [2001]}</c> (<c>NF_L0_34</c>), which is exactly what the
    /// September 28 2026 capture recorded for the same click at level 3.
    /// </para>
    /// </remarks>
    private async Task HandleCursedLandTwoTeleportAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (_character.CurrentMap == CursedLandTransportProtocol.MapId)
        {
            Console.WriteLine(
                $"[cursed-land] already on the destination map " +
                $"character={_character.Name} npc={npc.InteractionId}");
            return;
        }

        if (!CursedLandTransportProtocol.IsLevelEligible(_character.Level))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npc.InteractionId,
                    CursedLandTransportProtocol.EventTransporterDialogIndex,
                    CursedLandTransportProtocol.LevelRequirementResultSubId),
                cancellationToken,
                "CursedLandLevelRequirement");
            Console.WriteLine(
                "[cursed-land] level gate rejected " +
                $"character={_character.Name} level={_character.Level} " +
                $"minimum={CursedLandTransportProtocol.MinimumLevel}");
            return;
        }

        var outcome = await TryBeginMapTransitionCoreAsync(
            checked((byte)CursedLandTransportProtocol.MapId),
            CursedLandTransportProtocol.Arrival.X,
            CursedLandTransportProtocol.Arrival.Z,
            $"cursed-land-two:{npc.NpcKey}",
            cancellationToken,
            publishFightStateReset: true);
        if (outcome == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }

        if (outcome != SceneTransitionOutcome.CommittedAwaitingReadiness)
        {
            Console.Error.WriteLine(
                "[cursed-land] map authority rejected the admission " +
                $"character={_character.Name} npc={npc.InteractionId}");
            return;
        }

        Console.WriteLine(
            "[cursed-land] admitted " +
            $"character={_character.Name} level={_character.Level} " +
            $"npc={npc.InteractionId} map={CursedLandTransportProtocol.MapId} " +
            $"arrival={CursedLandTransportProtocol.Arrival.X}," +
            $"{CursedLandTransportProtocol.Arrival.Z}");
    }

    /// <summary>
    /// Answers an in-map transport actor of map 29.
    /// </summary>
    /// <remarks>
    /// The client sends its initial action with <c>+16 == -1</c> as soon as the
    /// window opens, which is the only click the capture ever recorded for these
    /// actors, so a later selection is logged and left unanswered rather than
    /// guessed at.
    /// </remarks>
    private async Task HandleCursedLandInMapActionAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (_character.CurrentMap != CursedLandTransportProtocol.MapId)
        {
            Console.WriteLine(
                $"[cursed-land] transporter ignored off-map " +
                $"character={_character.Name} npc={npc.InteractionId} " +
                $"map={_character.CurrentMap}");
            return;
        }

        if (subId != -1)
        {
            Console.WriteLine(
                $"[cursed-land] unregistered transport selection " +
                $"npc={npc.InteractionId} key={npc.NpcKey} " +
                $"dialog={dialogIndex} subId={subId}");
            return;
        }

        if (dialogIndex == CursedLandTransportProtocol.RandomTeleportFunction &&
            CursedLandTransportProtocol.TryGetRandomTarget(
                npc.NpcKey,
                out var point))
        {
            var outcome = await TryBeginSameMapSceneTransitionCoreAsync(
                point.X,
                point.Z,
                $"cursed-land-random:{npc.NpcKey}",
                continuationGuard: null,
                cancellationToken,
                publishRevivalVitals: false,
                publishFightStateReset: true);
            if (outcome != SceneTransitionOutcome.CommittedAwaitingReadiness)
            {
                Console.Error.WriteLine(
                    "[cursed-land] random transport rejected " +
                    $"character={_character.Name} npc={npc.InteractionId} " +
                    $"outcome={outcome}");
                return;
            }

            Console.WriteLine(
                "[cursed-land] random transport " +
                $"character={_character.Name} npc={npc.InteractionId} " +
                $"key={npc.NpcKey} arrival={point.X},{point.Z}");
            return;
        }

        if (dialogIndex == CursedLandTransportProtocol.HomeTeleportFunction)
        {
            await HandleCursedLandHomeAsync(npc, cancellationToken);
            return;
        }

        Console.WriteLine(
            $"[cursed-land] unregistered transport function " +
            $"npc={npc.InteractionId} key={npc.NpcKey} dialog={dialogIndex}");
    }

    /// <summary>
    /// Sends the character back to their own capital.
    /// </summary>
    /// <remarks>
    /// The captured landing is Athens <c>(20, -100)</c>, the same point the
    /// reference uses for a free revive in that city and for the farm and arena
    /// returns in three other sessions. No Sparta-camp character was ever
    /// captured returning from a special map, so the Sparta branch reuses the
    /// reviewed Sparta capital location the server already places a fresh Sparta
    /// character at (<see cref="GameDefaults"/>) instead of inventing one; that
    /// branch is the one part of this class the capture does not prove.
    /// </remarks>
    private async Task HandleCursedLandHomeAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        var sparta = _character.Camp == GameDefaults.SpartaCamp;
        var targetMapId = sparta
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        var targetX = sparta
            ? GameDefaults.StartingPositionX
            : CursedLandTransportProtocol.AthensHome.X;
        var targetZ = sparta
            ? GameDefaults.StartingPositionZ
            : CursedLandTransportProtocol.AthensHome.Z;

        var outcome = await TryBeginMapTransitionCoreAsync(
            targetMapId,
            targetX,
            targetZ,
            $"cursed-land-home:{npc.NpcKey}",
            cancellationToken,
            publishFightStateReset: true);
        if (outcome == SceneTransitionOutcome.CommittedRequiresReconnect)
        {
            return;
        }

        if (outcome != SceneTransitionOutcome.CommittedAwaitingReadiness)
        {
            Console.Error.WriteLine(
                "[cursed-land] home transport rejected " +
                $"character={_character.Name} npc={npc.InteractionId}");
            return;
        }

        Console.WriteLine(
            "[cursed-land] home transport " +
            $"character={_character.Name} camp={_character.Camp} " +
            $"npc={npc.InteractionId} map={targetMapId} " +
            $"arrival={targetX},{targetZ}");
    }
}
