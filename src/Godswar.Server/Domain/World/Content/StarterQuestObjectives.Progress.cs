namespace Godswar.Server.Domain.World.Content;

internal static partial class StarterQuestObjectives
{
    /// <summary>
    /// The objective a quest window is counting right now: the first one still
    /// open, or the last one once every objective has been met.
    /// </summary>
    /// <remarks>
    /// The published snapshot carries one target per quest - the monster, how
    /// many the quest wants and how many are done - so a quest with several
    /// objectives has to name the one still being worked on. Reporting the first
    /// objective unconditionally made a partly finished quest read as finished
    /// on the client, which then offered a hand-in the server was right to
    /// refuse: quest 528 asks for one Addiya the Destroyer and eight Fake
    /// Treasures, and counting only the first showed 1/1 while the boxes were
    /// still untouched.
    /// </remarks>
    public static int ActiveSlot(
        IReadOnlyList<QuestObjective> objectives,
        long progress)
    {
        for (var slot = 0; slot < objectives.Count; slot++)
        {
            if (Counter(progress, slot) < objectives[slot].Required)
            {
                return slot;
            }
        }

        return objectives.Count - 1;
    }
}
