using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Pins the GM NPC layer: the wire entry-id encoding, opening / stepping a
/// dialogue, the snapshot's validation, and placement of the actors.
/// </summary>
/// <remarks>
/// Pure in-memory, so it runs everywhere. It installs a snapshot into the
/// process-wide catalog and restores the empty one in a <c>finally</c> - a leaked
/// NPC would show up in every later login check in the same run.
/// </remarks>
internal static class GmNpcDialogueChecks
{
    public const string CheckName = "GM NPC placement and dialogue stepping";

    private const short Map = 29;

    public static Task RunAsync()
    {
        try
        {
            CheckWireEncoding();
            CheckOpenAndStep();
            CheckSnapshotValidation();
            CheckPlacement();
        }
        finally
        {
            GmNpcOverrideCatalog.Install(GmNpcOverrideSnapshot.Empty);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The exact numbers matter: the generated client patch matches on these, and
    /// the tool's self-test asserts the same values from the other side.
    /// </summary>
    private static void CheckWireEncoding()
    {
        Check.Equal(1001101, GmNpcDialoguePolicy.BodySubId(1001), "body id of page 1001");
        Check.Equal(1001001, GmNpcDialoguePolicy.ButtonSubId(1001, 1), "slot 1 of page 1001");
        Check.Equal(1001012, GmNpcDialoguePolicy.ButtonSubId(1001, 12), "slot 12 of page 1001");
        Check.Equal(1002101, GmNpcDialoguePolicy.BodySubId(1002), "body id of page 1002");
        Check.True(
            GmNpcDialoguePolicy.ButtonSubId(1001, 1) != GmNpcDialoguePolicy.ButtonSubId(1002, 1),
            "the same slot on two pages must not share a wire id");
        Check.True(
            GmNpcDialoguePolicy.ButtonSubId(1001, 1) != GmNpcDialoguePolicy.BodySubId(1001),
            "a button must not collide with its own page's body id");
    }

    private static void CheckOpenAndStep()
    {
        var dialogue = Dialogue();
        Check.True(
            GmNpcDialoguePolicy.TryOpen(dialogue, out var entry, out var opening),
            "a usable tree opens");
        Check.Equal(1001, entry, "the entry page is the configured one");
        Check.Equal(3, opening.Length, "the entry page sends the body plus two buttons");
        Check.Equal(GmNpcDialoguePolicy.BodySubId(1001), opening[0], "the body entry comes first");
        Check.Equal(
            GmNpcDialoguePolicy.ButtonSubId(1001, 1),
            opening[1],
            "buttons follow in slot order");

        Check.True(
            GmNpcDialoguePolicy.TryStep(
                dialogue,
                GmNpcDialoguePolicy.ButtonSubId(1001, 1),
                out var next,
                out var page),
            "clicking the first button steps");
        Check.Equal(1002, next, "and lands on the page the operator pointed at");
        Check.Equal(1, page.Length, "a page with no buttons sends only its body entry");

        Check.True(
            !GmNpcDialoguePolicy.TryStep(dialogue, 987654, out _, out _),
            "a number that is not one of this tree's buttons steps nowhere");

        Check.True(
            !GmNpcDialoguePolicy.TryOpen(
                dialogue with { Enabled = false },
                out _,
                out _),
            "a disabled tree does not open");
    }

    private static void CheckSnapshotValidation()
    {
        var dialogue = Dialogue();
        var spawn = Spawn(objectId: 47_300, templateKey: "Execrativelys3_006");
        var snapshot = GmNpcOverrideSnapshot.Create([spawn], [dialogue]);
        Check.Equal(1, snapshot.Spawns.Count, "a well-formed placement is accepted");
        Check.True(
            snapshot.TryGetDialogue("gm-probe", out _),
            "and its dialogue is reachable by key");

        Check.Throws<InvalidDataException>(
            () => GmNpcOverrideSnapshot.Create(
                [spawn],
                [dialogue with { EntryPage = 9_999 }]),
            "an entry page that does not exist is rejected");

        Check.Throws<InvalidDataException>(
            () => GmNpcOverrideSnapshot.Create(
                [spawn],
                [dialogue with
                {
                    Buttons =
                    [
                        new GmNpcDialogueButton(1001, 1, "a", 1_001_001, 1002),
                        new GmNpcDialogueButton(1001, 2, "b", 1_001_001, 1002)
                    ]
                }]),
            "two buttons sharing a result number are rejected");

        Check.Throws<InvalidDataException>(
            () => GmNpcOverrideSnapshot.Create(
                [spawn],
                [dialogue with
                {
                    Buttons = [new GmNpcDialogueButton(1001, 13, "a", 1_001_013, 1002)]
                }]),
            "a slot above the client's twelfth button is rejected");

        Check.Throws<InvalidDataException>(
            () => GmNpcOverrideSnapshot.Create(
                [spawn],
                [dialogue with
                {
                    Buttons = [new GmNpcDialogueButton(1001, 1, "a", 1_001_001, 7_777)]
                }]),
            "a button pointing at a missing page is rejected");

        Check.Throws<InvalidDataException>(
            () => GmNpcOverrideSnapshot.Create([spawn, spawn], [dialogue]),
            "two placements sharing an npc key are rejected");

        Check.Throws<InvalidDataException>(
            () => GmNpcOverrideSnapshot.Create(
                [spawn, Spawn(objectId: 47_300, templateKey: "X", npcKey: "gm-probe-2")],
                [dialogue]),
            "two placements sharing a map and object id are rejected");
    }

    private static void CheckPlacement()
    {
        var published = new List<NpcSpawnDefinition>
        {
            Published(objectId: 27_405, templateKey: "Execrativelys3_006", "Execratively3")
        };
        var known = Spawn(objectId: 47_300, templateKey: "Execrativelys3_006");
        var unknown = Spawn(
            objectId: 47_301,
            templateKey: "NoSuchTemplate",
            npcKey: "gm-probe-unknown");
        var collision = Spawn(
            objectId: 27_405,
            templateKey: "Execrativelys3_006",
            npcKey: "gm-probe-collision");
        GmNpcOverrideCatalog.Install(
            GmNpcOverrideSnapshot.Create([known, unknown, collision], [Dialogue()]));

        var built = GmNpcDialoguePolicy.BuildPlacements(
            GmNpcOverrideCatalog.Current,
            Map,
            published);
        Check.Equal(1, built.Count, "only the usable placement survives");
        var npc = built[0];
        Check.Equal(47_300u, npc.ObjectId, "the placement keeps its object id");
        Check.Equal(npc.ObjectId, npc.InteractionId, "interaction id tracks the object id");
        Check.Equal("gm-probe", npc.NpcKey, "the placement keeps its key");
        Check.Equal(Map, npc.MapId, "the placement keeps its map");
        Check.Equal(
            published[0].SceneKey,
            npc.SceneKey,
            "the placement borrows the map's published scene key");

        var otherMap = GmNpcDialoguePolicy.BuildPlacements(
            GmNpcOverrideCatalog.Current,
            mapId: 7,
            published);
        Check.Equal(0, otherMap.Count, "another map gets none of these placements");

        GmNpcOverrideCatalog.Install(GmNpcOverrideSnapshot.Empty);
        Check.Equal(
            0,
            GmNpcDialoguePolicy.BuildPlacements(
                GmNpcOverrideCatalog.Current,
                Map,
                published).Count,
            "with nothing installed no NPC is placed");
    }

    private static GmNpcDialogue Dialogue() => new(
        "gm-probe",
        "自检 NPC",
        85,
        EntryPage: 1001,
        Enabled: true,
        Pages: new Dictionary<int, string>
        {
            [1001] = "第一页",
            [1002] = "终点页"
        },
        Buttons:
        [
            new GmNpcDialogueButton(1001, 1, "去终点", 1_001_001, 1002),
            new GmNpcDialogueButton(1001, 2, "算了", 1_001_002, null)
        ]);

    private static GmNpcSpawnOverride Spawn(
        uint objectId,
        string templateKey,
        string npcKey = "gm-probe") => new(
        Id: objectId,
        Name: npcKey,
        MapId: Map,
        NpcKey: npcKey,
        TemplateKey: templateKey,
        DialogueKey: "gm-probe",
        ObjectId: objectId,
        AppearanceType: 1,
        X: 10f,
        Z: 20f,
        Facing: 1f,
        Enabled: true);

    private static NpcSpawnDefinition Published(
        uint objectId,
        string templateKey,
        string sceneKey) => new(
        Map,
        sceneKey,
        "published",
        templateKey,
        objectId,
        0f,
        0f,
        objectId,
        1,
        0f,
        [],
        []);
}
