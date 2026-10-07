namespace Godswar.Server.Application.World;

/// <summary>One operator-authored dialogue tree, frozen for runtime use.</summary>
internal sealed record GmNpcDialogue(
    string DialogueKey,
    string DisplayName,
    int FunctionFlag,
    int EntryPage,
    bool Enabled,
    IReadOnlyDictionary<int, string> Pages,
    IReadOnlyList<GmNpcDialogueButton> Buttons);

/// <summary>
/// One clickable slot. <see cref="ResultNumber"/> is what the client sends back -
/// the wire <c>SubID</c>, unique inside the tree - and
/// <see cref="NextPageIndex"/> is <c>null</c> when the conversation ends.
/// </summary>
internal sealed record GmNpcDialogueButton(
    int PageIndex,
    short Slot,
    string Label,
    int ResultNumber,
    int? NextPageIndex);

/// <summary>One operator-authored NPC placement.</summary>
internal sealed record GmNpcSpawnOverride(
    long Id,
    string Name,
    short MapId,
    string NpcKey,
    string TemplateKey,
    string? DialogueKey,
    uint ObjectId,
    uint AppearanceType,
    float X,
    float Z,
    float Facing,
    bool Enabled);

/// <summary>
/// A validated, frozen read of the four GM NPC tables.
/// </summary>
/// <remarks>
/// Built at startup by <c>PostgresGmNpcSnapshotReader</c> and installed into
/// <see cref="GmNpcOverrideCatalog"/>. Validation lives in
/// <see cref="Create"/> so a malformed row fails the load instead of reaching a
/// packet - in particular a button whose result number collides with another
/// button in the same tree, which would make a click ambiguous.
/// </remarks>
internal sealed class GmNpcOverrideSnapshot
{
    public static readonly GmNpcOverrideSnapshot Empty = new([], []);

    public const int MaximumSpawns = 2_000;

    public const int MaximumSlots = 12;

    private GmNpcOverrideSnapshot(
        IReadOnlyList<GmNpcSpawnOverride> spawns,
        IReadOnlyList<GmNpcDialogue> dialogues)
    {
        Spawns = spawns;
        Dialogues = dialogues;
        DialoguesByKey = dialogues.ToDictionary(
            static dialogue => dialogue.DialogueKey,
            StringComparer.Ordinal);
    }

    public static GmNpcOverrideSnapshot Create(
        IReadOnlyList<GmNpcSpawnOverride> spawns,
        IReadOnlyList<GmNpcDialogue> dialogues)
    {
        ArgumentNullException.ThrowIfNull(spawns);
        ArgumentNullException.ThrowIfNull(dialogues);
        if (spawns.Count > MaximumSpawns)
        {
            throw new InvalidDataException(
                $"The GM NPC table holds {spawns.Count} placements, above the " +
                $"supported maximum of {MaximumSpawns}.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var placements = new HashSet<(short, uint)>();
        foreach (var spawn in spawns)
        {
            if (spawn.MapId is < 0 or > 255 ||
                spawn.ObjectId == 0 ||
                string.IsNullOrWhiteSpace(spawn.NpcKey) ||
                string.IsNullOrWhiteSpace(spawn.TemplateKey) ||
                !float.IsFinite(spawn.X) ||
                !float.IsFinite(spawn.Z) ||
                !float.IsFinite(spawn.Facing) ||
                !keys.Add(spawn.NpcKey) ||
                !placements.Add((spawn.MapId, spawn.ObjectId)))
            {
                throw new InvalidDataException(
                    $"The GM NPC placement '{spawn.Name}' is malformed or " +
                    "duplicates another row's key or map+object id.");
            }
        }

        var dialogueKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dialogue in dialogues)
        {
            if (string.IsNullOrWhiteSpace(dialogue.DialogueKey) ||
                !dialogueKeys.Add(dialogue.DialogueKey) ||
                dialogue.Pages.Count == 0 ||
                !dialogue.Pages.ContainsKey(dialogue.EntryPage))
            {
                throw new InvalidDataException(
                    $"The GM dialogue '{dialogue.DialogueKey}' is malformed: it " +
                    "needs a key, at least one page and an existing entry page.");
            }

            var results = new HashSet<int>();
            var slots = new HashSet<(int, short)>();
            foreach (var button in dialogue.Buttons)
            {
                if (button.Slot is < 1 or > MaximumSlots ||
                    !dialogue.Pages.ContainsKey(button.PageIndex) ||
                    (button.NextPageIndex is { } next &&
                     !dialogue.Pages.ContainsKey(next)) ||
                    !results.Add(button.ResultNumber) ||
                    !slots.Add((button.PageIndex, button.Slot)))
                {
                    throw new InvalidDataException(
                        $"The GM dialogue '{dialogue.DialogueKey}' has a button " +
                        "that is out of range, points at a missing page, or " +
                        "shares a slot or a result number with another button.");
                }
            }

            if (dialogue.Buttons.Any(button =>
                    !string.IsNullOrEmpty(button.Label) && button.Label.Length > 120))
            {
                throw new InvalidDataException(
                    $"The GM dialogue '{dialogue.DialogueKey}' has a button label " +
                    "longer than the client accepts.");
            }
        }

        return new GmNpcOverrideSnapshot(spawns.ToArray(), dialogues.ToArray());
    }

    public IReadOnlyList<GmNpcSpawnOverride> Spawns { get; }

    public IReadOnlyList<GmNpcDialogue> Dialogues { get; }

    private Dictionary<string, GmNpcDialogue> DialoguesByKey { get; }

    public bool IsEmpty => Spawns.Count == 0 && Dialogues.Count == 0;

    public bool TryGetDialogue(string? dialogueKey, out GmNpcDialogue dialogue)
    {
        if (string.IsNullOrEmpty(dialogueKey))
        {
            dialogue = null!;
            return false;
        }

        return DialoguesByKey.TryGetValue(dialogueKey, out dialogue!);
    }

    /// <summary>The enabled placements for one map, in a stable order.</summary>
    public IReadOnlyList<GmNpcSpawnOverride> SpawnsFor(short mapId) =>
        Spawns.Where(spawn => spawn.Enabled && spawn.MapId == mapId)
            .OrderBy(static spawn => spawn.ObjectId)
            .ToList();
}

/// <summary>
/// Process-wide handle on the GM NPC content, mirroring
/// <c>MonsterOverrideCatalog</c>.
/// </summary>
internal static class GmNpcOverrideCatalog
{
    private static GmNpcOverrideSnapshot _current = GmNpcOverrideSnapshot.Empty;

    public static GmNpcOverrideSnapshot Current => Volatile.Read(ref _current);

    public static void Install(GmNpcOverrideSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
