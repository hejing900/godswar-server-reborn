using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

/// <summary>
/// The runtime half of operator-authored GM NPCs: opening one and answering its
/// clicks.
/// </summary>
/// <remarks>
/// <para>
/// A GM NPC is injected into the map roster at login
/// (<see cref="GmNpcDialoguePolicy.BuildPlacements"/>), so the published dialogue
/// lookup finds nothing for its key - exactly like the Wonderland instance
/// actors, which is why this branch runs before that lookup.
/// </para>
/// <para>
/// The client's dialogue protocol is stateless: <c>10067</c> opens the actor and
/// advertises the function page, the client then asks with <c>10069</c>, and the
/// server answers <c>10070</c> with the entry ids of one page. The clicked id
/// comes back in the next <c>10069</c>, and because every id is
/// <c>page * 1000 + entry</c> the click alone says which page it came from.
/// </para>
/// <para>
/// A button whose <c>next_page_index</c> is null draws no reply at all - the
/// operator points a "cancel" button at a page that has no buttons, and that
/// page's body entry is what closes the window on the client side.
/// </para>
/// </remarks>
internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Opens a GM NPC's dialogue. Returns false for any other actor, so the
    /// published routes keep owning everything they already own.
    /// </summary>
    private async Task<bool> TryHandleGmNpcDialogOpenAsync(
        NpcSpawnDefinition npc,
        CancellationToken cancellationToken)
    {
        var snapshot = GmNpcOverrideCatalog.Current;
        var placement = snapshot.SpawnsFor(npc.MapId)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.NpcKey, npc.NpcKey, StringComparison.Ordinal));
        if (placement is null ||
            !snapshot.TryGetDialogue(placement.DialogueKey, out var dialogue) ||
            !dialogue.Enabled)
        {
            return false;
        }

        // 10067 advertises the function page; the client's Set_NpcFun_Text
        // dispatches on exactly this number, and 85 is the one the generated
        // client patch claims.
        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                dialogue.FunctionFlag,
                ScriptKey(dialogue)),
            cancellationToken,
            "GmNpcDialogOpenAck");
        Console.WriteLine(
            $"[npc] GM dialog open npc={npc.InteractionId} key={npc.NpcKey} " +
            $"dialogue={dialogue.DialogueKey} function={dialogue.FunctionFlag} " +
            $"entry={dialogue.EntryPage}");
        return true;
    }

    /// <summary>
    /// Answers a GM NPC's <c>10069</c>: either the opening request
    /// (<c>subId &lt;= 0</c>) or a click on one of the buttons.
    /// </summary>
    private async Task<bool> TryHandleGmNpcFunctionActionAsync(
        NpcSpawnDefinition npc,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        var snapshot = GmNpcOverrideCatalog.Current;
        var placement = snapshot.SpawnsFor(npc.MapId)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.NpcKey, npc.NpcKey, StringComparison.Ordinal));
        if (placement is null ||
            !snapshot.TryGetDialogue(placement.DialogueKey, out var dialogue) ||
            !dialogue.Enabled)
        {
            return false;
        }

        if (dialogIndex != dialogue.FunctionFlag)
        {
            // The actor exists but the client asked for another function page
            // (a quest page, a shop page): leave it to the published handlers.
            return false;
        }

        if (subId <= 0)
        {
            if (!GmNpcDialoguePolicy.TryOpen(dialogue, out var entry, out var opening))
            {
                return false;
            }

            await SendGmPageAsync(npc, dialogue, entry, opening, cancellationToken);
            return true;
        }

        if (!GmNpcDialoguePolicy.TryStep(dialogue, subId, out var next, out var page))
        {
            // A click that leads nowhere: the operator left next_page_index null.
            Console.WriteLine(
                $"[npc] GM dialog click with no next page npc={npc.InteractionId} " +
                $"dialogue={dialogue.DialogueKey} clicked={subId}");
            return true;
        }

        await SendGmPageAsync(npc, dialogue, next, page, cancellationToken);
        return true;
    }

    private async Task SendGmPageAsync(
        NpcSpawnDefinition npc,
        GmNpcDialogue dialogue,
        int pageIndex,
        int[] subIds,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npc.InteractionId,
                dialogue.FunctionFlag,
                subIds),
            cancellationToken,
            "GmNpcFunctionActionResponse");
        Console.WriteLine(
            $"[npc] GM dialog page npc={npc.InteractionId} " +
            $"dialogue={dialogue.DialogueKey} page={pageIndex} " +
            $"subIds={string.Join(",", subIds)}");
    }

    /// <summary>
    /// The script key the client is told to load. The generated patch registers
    /// itself in NpcFunLoad.xml, so the key only has to be stable and honest.
    /// </summary>
    private static string ScriptKey(GmNpcDialogue dialogue) =>
        $"NpcFunGM:{dialogue.DialogueKey}";
}
