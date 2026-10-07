using System.IO;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Places the two quest actors the client's own data describes but no release
/// ever published: <c>Marathon_All_006</c> and <c>Peloponnese_All_006</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both are the giver or responder of four quests that the chain generator could
/// not include, because it drops every quest whose npc is not published:
/// <c>1209</c> and <c>1555</c> (Athens) and their Sparta mirrors <c>209</c> and
/// <c>555</c>. Their placement is the client's own:
/// <c>Settings/Sys/Quest.xml</c> gives the map and the coordinates, and
/// <c>NPC.INI</c> carries the appearance under a template whose name field
/// agrees with <c>Text/NpcName.dat</c> - <c>Marathon_006_AthenianWarrior1</c> is
/// "Crophi" and <c>Peloponnese_006_SpartanWarrior1</c> is "Meges", which is
/// exactly what NpcName.dat calls <c>Marathon_All_006</c> and
/// <c>Peloponnese_All_006</c>.
/// </para>
/// <para>
/// Marathon is the captured side: the reference's own 10020 frame placed that
/// actor as object 5480 with appearance word 0x211 at (-1, -38) facing 2.0, and
/// <see cref="CapturedNpcPlacements"/> now files that placement under
/// <c>Marathon_All_006</c>, so the run-time policy rewrites this row to the
/// reference's numbers. Peloponnese was never captured - no export exists for
/// map 13 - so its row is the client's own data and nothing more. Its id is
/// server-assigned, which the placement policy already does for every
/// uncaptured npc: the id is a handle the client echoes back, so a fresh free
/// one is harmless where a duplicate ends the session.
/// </para>
/// <para>
/// The reviewed V10 release and every earlier one stay immutable, so these two
/// actors only ever appear through this release.
/// </para>
/// </remarks>
internal static class NpcContentBaselineV11
{
    private const short MarathonMapId = 11;
    private const short PeloponneseMapId = 13;

    public const int AddedEntryCount = 2;

    public const int ExpectedEntryCount =
        NpcContentBaselineV10.ExpectedEntryCount + AddedEntryCount;

    public const string Source = "reviewed-published-npc-baseline-v11";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical definition set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "11F3F145343059ACE88D0CFF953AAD7F4FD10988BB253278975EDDE6DB96AF66";

    /// <summary>
    /// One actor: the key the quest data uses, the client's appearance template
    /// and the placement its own tables give.
    /// </summary>
    internal readonly record struct QuestActor(
        short MapId,
        string SceneKey,
        string NpcKey,
        string TemplateKey,
        uint ObjectId,
        uint AppearanceType,
        float X,
        float Z,
        float Facing);

    /// <summary>The two actors, Sparta's mirror first.</summary>
    public static readonly QuestActor[] QuestActors =
    [
        new(PeloponneseMapId, "Peloponnese_All", "Peloponnese_All_006",
            "Peloponnese_006_SpartanWarrior1", 5_485u, 0x11u, -41.00f, -4.00f, 1.00f),
        new(MarathonMapId, "Marathon_All", "Marathon_All_006",
            "Marathon_006_AthenianWarrior1", 5_480u, 0x211u, -1.00f, -38.00f, 2.00f)
    ];

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var previous = NpcContentBaselineV10.LoadDefinitions();
        foreach (var actor in QuestActors)
        {
            if (previous.Any(definition =>
                    definition.MapId == actor.MapId &&
                    string.Equals(
                        definition.NpcKey,
                        actor.NpcKey,
                        StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"The V11 release requires a V10 set without " +
                    $"{actor.NpcKey} on map {actor.MapId}.");
            }
        }

        var definitions = previous
            .Concat(QuestActors.Select(Create))
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();
        if (definitions.Length != ExpectedEntryCount ||
            definitions.Select(static definition =>
                    (definition.MapId, definition.ObjectId))
                .Distinct().Count() != definitions.Length ||
            definitions.Select(static definition =>
                    (definition.MapId, definition.InteractionId))
                .Distinct().Count() != definitions.Length)
        {
            throw new InvalidDataException(
                "The V11 release requires " +
                $"{ExpectedEntryCount} unique map-scoped actors with " +
                $"{AddedEntryCount} quest actors added.");
        }

        return definitions;
    }

    private static NpcSpawnDefinition Create(QuestActor actor) =>
        new(
            actor.MapId,
            actor.SceneKey,
            actor.NpcKey,
            actor.TemplateKey,
            actor.ObjectId,
            actor.X,
            actor.Z,
            // The client echoes the object id back as the interaction id, which
            // is what every captured dialogue click is keyed by.
            actor.ObjectId,
            actor.AppearanceType,
            actor.Facing,
            Detail10077: [],
            Detail10080: []);
}
