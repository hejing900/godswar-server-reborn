using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

/// <summary>
/// Turns an operator-authored dialogue tree into the numbers the client
/// understands, and the GM placements into world NPCs.
/// </summary>
/// <remarks>
/// <para>
/// The client's dialogue protocol is <b>stateless</b>: the server sends a list of
/// entry ids, the client sends the clicked entry's id straight back, and the
/// server looks that number up in a flat table (the same shape as
/// <c>ScriptedNpcDialogue.Steps</c>). There is no "current page" on the wire, so
/// a click must identify its page by itself - which is why every wire id is
/// <c>page * 1000 + entry</c>.
/// </para>
/// <para>
/// The tool's <c>NpcDialogueStore.SubIdStride</c> computes the same values; both
/// sides have a check pinning the exact numbers, so a drift shows up as a
/// failure instead of as a silent mismatch in game.
/// </para>
/// </remarks>
internal static class GmNpcDialoguePolicy
{
    /// <inheritdoc cref="GmNpcOverrideSnapshot.MaximumSlots"/>
    public const int SubIdStride = 1_000;

    /// <summary>The body-text entry of a page.</summary>
    public static int BodySubId(int pageIndex) => (pageIndex * SubIdStride) + 101;

    /// <summary>A button's entry, which is also the number the client sends back.</summary>
    public static int ButtonSubId(int pageIndex, int slot) =>
        (pageIndex * SubIdStride) + slot;

    /// <summary>The entry ids that make the client draw one page.</summary>
    public static int[] PageSubIds(GmNpcDialogue dialogue, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(dialogue);
        var ids = new List<int> { BodySubId(pageIndex) };
        ids.AddRange(dialogue.Buttons
            .Where(button => button.PageIndex == pageIndex)
            .OrderBy(static button => button.Slot)
            .Select(button => ButtonSubId(pageIndex, button.Slot)));
        return ids.ToArray();
    }

    /// <summary>The page a GM NPC opens with. False when it has no usable tree.</summary>
    public static bool TryOpen(
        GmNpcDialogue? dialogue,
        out int pageIndex,
        out int[] subIds)
    {
        if (dialogue is null || !dialogue.Enabled || dialogue.Pages.Count == 0)
        {
            pageIndex = 0;
            subIds = [];
            return false;
        }

        pageIndex = dialogue.EntryPage;
        subIds = PageSubIds(dialogue, dialogue.EntryPage);
        return true;
    }

    /// <summary>
    /// The page a clicked number leads to, or <c>false</c> when the number is not
    /// one of this tree's buttons (an end-of-conversation click, or a stray one).
    /// </summary>
    public static bool TryStep(
        GmNpcDialogue? dialogue,
        int number,
        out int nextPageIndex,
        out int[] subIds)
    {
        nextPageIndex = 0;
        subIds = [];
        if (dialogue is null || !dialogue.Enabled)
        {
            return false;
        }

        var button = dialogue.Buttons.FirstOrDefault(
            candidate => candidate.ResultNumber == number);
        if (button?.NextPageIndex is not { } next || !dialogue.Pages.ContainsKey(next))
        {
            return false;
        }

        nextPageIndex = next;
        subIds = PageSubIds(dialogue, next);
        return true;
    }

    /// <summary>
    /// The world NPCs the operator added to one map.
    /// </summary>
    /// <remarks>
    /// A placement is only kept when the published content already uses its
    /// appearance template on that map: the client loads the actor's model from
    /// the template key written into the appearance packet, so an unknown
    /// template would produce an invisible or unclickable NPC. Object ids and
    /// interaction ids are taken from the placement, matching how the authored
    /// farm NPCs do it (<c>InteractionId == ObjectId</c>).
    /// </remarks>
    public static List<NpcSpawnDefinition> BuildPlacements(
        GmNpcOverrideSnapshot snapshot,
        short mapId,
        IReadOnlyList<NpcSpawnDefinition> published)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(published);
        var result = new List<NpcSpawnDefinition>();
        var placements = snapshot.SpawnsFor(mapId);
        if (placements.Count == 0)
        {
            return result;
        }

        var knownTemplates = published
            .Select(static npc => npc.TemplateKey)
            .ToHashSet(StringComparer.Ordinal);
        var usedObjectIds = published
            .Select(static npc => npc.ObjectId)
            .ToHashSet();
        var sceneKey = published.Count > 0
            ? published[0].SceneKey
            : string.Empty;
        foreach (var placement in placements)
        {
            if (!knownTemplates.Contains(placement.TemplateKey))
            {
                Console.WriteLine(
                    $"[npc] GM placement skipped: template not published on this " +
                    $"map map={placement.MapId} template={placement.TemplateKey} " +
                    $"name={placement.Name}");
                continue;
            }

            if (!usedObjectIds.Add(placement.ObjectId))
            {
                Console.WriteLine(
                    $"[npc] GM placement skipped: object id already used " +
                    $"map={placement.MapId} object={placement.ObjectId} " +
                    $"name={placement.Name}");
                continue;
            }

            result.Add(new NpcSpawnDefinition(
                placement.MapId,
                sceneKey,
                placement.NpcKey,
                placement.TemplateKey,
                placement.ObjectId,
                placement.X,
                placement.Z,
                InteractionId: placement.ObjectId,
                placement.AppearanceType,
                placement.Facing,
                Detail10077: [],
                Detail10080: []));
            Console.WriteLine(
                $"[npc] GM placement applied map={placement.MapId} " +
                $"object={placement.ObjectId} key={placement.NpcKey} " +
                $"dialogue={placement.DialogueKey ?? "(none)"} " +
                $"x={placement.X:F1} z={placement.Z:F1}");
        }

        return result;
    }
}
