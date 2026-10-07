using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Pins the GM monster override layer: the snapshot's validation, the three
/// spawn shapes (disable / edit / append) and the attribute precedence.
/// </summary>
/// <remarks>
/// Pure in-memory: no database, so it runs everywhere. It installs a snapshot
/// into the process-wide catalog, so it restores the empty one in a
/// <c>finally</c> - a leaked override would silently change every later combat
/// check in the same run.
/// </remarks>
internal static partial class MonsterOverrideChecks
{
    public const string CheckName = "GM monster spawn and attribute overrides";

    private const short Map = 7;

    public static Task RunAsync()
    {
        try
        {
            CheckSnapshotValidation();
            CheckCatalogRoundTrip();
            CheckSpawnShapes();
            CheckAttributePrecedence();
        }
        finally
        {
            MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
        }

        return Task.CompletedTask;
    }

    private static void CheckSnapshotValidation()
    {
        var good = SpawnRow(
            name: "probe-a",
            objectId: 47_001,
            templateKey: "A_elitecopy_dryad_009",
            x: 10f,
            z: 20f);
        var snapshot = MonsterOverrideSnapshot.Create([good], [], []);
        Check.Equal(1, snapshot.Spawns.Count, "a well-formed GM spawn is accepted");

        Check.Throws<InvalidDataException>(
            () => MonsterOverrideSnapshot.Create(
                [SpawnRow("probe-short", 47_002, "A_elitecopy_dryad_009", 1f, 2f, packetLength: 64)],
                [],
                []),
            "a GM spawn packet shorter than the captured minimum is rejected");

        Check.Throws<InvalidDataException>(
            () => MonsterOverrideSnapshot.Create(
                [
                    SpawnRow("probe-dup-a", 47_003, "A_elitecopy_dryad_009", 1f, 2f),
                    SpawnRow("probe-dup-b", 47_003, "A_elitecopy_dryad_009", 3f, 4f)
                ],
                [],
                []),
            "two GM spawns may not share a map and object id");

        Check.Throws<InvalidDataException>(
            () => MonsterOverrideSnapshot.Create(
                [
                    SpawnRow("probe-dup-name", 47_004, "A_elitecopy_dryad_009", 1f, 2f),
                    SpawnRow("probe-dup-name", 47_005, "A_elitecopy_dryad_009", 3f, 4f)
                ],
                [],
                []),
            "two GM spawns may not share a name");

        Check.Throws<InvalidDataException>(
            () => MonsterOverrideSnapshot.Create(
                [SpawnRow("probe-nan", 47_006, "A_elitecopy_dryad_009", float.NaN, 2f)],
                [],
                []),
            "a GM spawn with a non-finite coordinate is rejected");
    }

    private static void CheckCatalogRoundTrip()
    {
        Check.True(
            MonsterOverrideCatalog.Current.IsEmpty,
            "the catalog starts empty so database-less tooling keeps working");

        var snapshot = MonsterOverrideSnapshot.Create(
            [SpawnRow("probe-catalog", 47_010, "A_elitecopy_dryad_009", 5f, 6f)],
            [],
            []);
        MonsterOverrideCatalog.Install(snapshot);
        Check.True(
            ReferenceEquals(snapshot, MonsterOverrideCatalog.Current),
            "Install is what the resolver reads back");
        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
        Check.True(MonsterOverrideCatalog.Current.IsEmpty, "the empty install restores");
    }

    private static void CheckSpawnShapes()
    {
        var published = new List<CapturedMonsterSpawn>
        {
            Program.CreateCapturedMonster(
                objectId: 47_100,
                x: 100f,
                z: 200f,
                templateKey: "A_elitecopy_dryad_009",
                tier: 4,
                maximumHealth: 1_000,
                mapId: Map,
                sceneKey: "Olympia_All"),
            Program.CreateCapturedMonster(
                objectId: 47_101,
                x: 110f,
                z: 210f,
                templateKey: "A_elitecopy_dryad_009",
                tier: 4,
                maximumHealth: 1_000,
                mapId: Map,
                sceneKey: "Olympia_All")
        };

        var edits = new List<((short MapId, uint ObjectId) Key, MonsterSpawnEdit Edit)>
        {
            // A published point an operator removed outright.
            ((Map, 47_101u), new MonsterSpawnEdit(
                Enabled: false,
                X: null,
                Z: null,
                Facing: null,
                Attributes: MonsterAttributeOverride.None)),
            // A published point an operator moved, levelled up and re-HP'd.
            ((Map, 47_100u), new MonsterSpawnEdit(
                Enabled: true,
                X: 12f,
                Z: 34f,
                Facing: 2.5f,
                Attributes: new MonsterAttributeOverride(
                    Level: 9,
                    MaximumHealth: 500,
                    CurrentHealth: 900)))
        };

        var spawns = new List<MonsterSpawnOverrideRow>
        {
            SpawnRow("probe-new", 47_200, "A_elitecopy_dryad_009", 300f, 400f),
            // No published spawn of this template on this map -> must be skipped.
            SpawnRow("probe-unknown-template", 47_201, "Z_never_published_001", 1f, 2f),
            // Another map's row must not leak into this map.
            SpawnRow("probe-other-map", 47_202, "A_elitecopy_dryad_009", 1f, 2f, mapId: 9)
        };

        MonsterOverrideCatalog.Install(
            MonsterOverrideSnapshot.Create(spawns, edits, []));
        var merged = MonsterOverridePolicy.Apply(published, Map);

        Check.Equal(
            2,
            merged.Count,
            "one disabled published point drops, one new point appends");

        var edited = merged.Single(static spawn => spawn.ObjectId == 47_100);
        Check.Equal(12f, edited.AppearanceX, "the edited x reaches the packet");
        Check.Equal(34f, edited.AppearanceZ, "the edited z reaches the packet");
        Check.Equal(12f, edited.X, "the record's own x follows the packet");
        Check.Equal(34f, edited.Z, "the record's own z follows the packet");
        Check.Equal(9u, edited.Tier, "the edited level reaches the packet tier");
        Check.Equal(
            500u,
            BinaryPrimitives.ReadUInt32LittleEndian(edited.Packet.AsSpan(24, 4)),
            "the edited maximum health reaches the packet");
        Check.Equal(
            500u,
            BinaryPrimitives.ReadUInt32LittleEndian(edited.Packet.AsSpan(20, 4)),
            "current health is clamped down to the new maximum");
        Check.Equal(
            2.5f,
            BinaryPrimitives.ReadSingleLittleEndian(edited.Packet.AsSpan(40, 4)),
            "the edited facing reaches the packet");
        var validateFailed = false;
        try
        {
            edited.Validate(Map);
        }
        catch (InvalidDataException)
        {
            validateFailed = true;
        }

        Check.True(
            !validateFailed,
            "the edited spawn still passes the captured-spawn validator");
        Check.True(
            !merged.Any(static spawn => spawn.ObjectId == 47_101),
            "the disabled published point is gone");

        var appended = merged.Single(static spawn => spawn.ObjectId == 47_200);
        Check.Equal(300f, appended.AppearanceX, "the new point keeps its x");
        Check.Equal("A_elitecopy_dryad_009", appended.TemplateKey, "the new point keeps its template");
        Check.True(
            !merged.Any(static spawn => spawn.ObjectId is 47_201 or 47_202),
            "an unpublished template and another map's row are both skipped");

        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
        Check.Equal(
            published.Count,
            MonsterOverridePolicy.Apply(published, Map).Count,
            "with no overrides installed the published list passes through");
    }

    private static void CheckAttributePrecedence()
    {
        var edits = new List<((short MapId, uint ObjectId) Key, MonsterSpawnEdit Edit)>
        {
            ((Map, 47_300u), new MonsterSpawnEdit(
                Enabled: true,
                X: null,
                Z: null,
                Facing: null,
                Attributes: new MonsterAttributeOverride(
                    PhysicalAttack: 777,
                    Critical: 55)))
        };
        var attributes = new List<
            ((short MapId, string TemplateKey) Key, MonsterAttributeOverride Override)>
        {
            ((Map, "A_elitecopy_dryad_009"), new MonsterAttributeOverride(
                PhysicalAttack: 111,
                Hit: 222,
                Critical: 33))
        };

        MonsterOverrideCatalog.Install(
            MonsterOverrideSnapshot.Create([], edits, attributes));

        // The per-point layer only names what it changes, so attack and critical
        // come from the point while hit keeps the template's value.
        Check.True(
            MonsterOverridePolicy.TryResolveAttributes(
                Map,
                47_300u,
                "A_elitecopy_dryad_009",
                out var merged),
            "a configured point resolves its overrides");
        Check.True(merged.PhysicalAttack == 777, "the per-point attack wins");
        Check.True(merged.Critical == 55, "the per-point critical wins");
        Check.True(merged.Hit == 222, "the template's hit survives the overlay");

        Check.True(
            MonsterOverridePolicy.TryResolveAttributes(
                Map,
                47_999u,
                "A_elitecopy_dryad_009",
                out var templateOnly),
            "a point with no row of its own still gets the template layer");
        Check.True(templateOnly.PhysicalAttack == 111, "the template attack applies");
        Check.True(templateOnly.Critical == 33, "the template critical applies");
        Check.True(templateOnly.Hit == 222, "the template hit applies");

        Check.True(
            !MonsterOverridePolicy.TryResolveAttributes(
                Map,
                47_999u,
                "A_never_configured_001",
                out _),
            "an unconfigured template reports no overrides");

        MonsterOverrideCatalog.Install(MonsterOverrideSnapshot.Empty);
    }

    private static MonsterSpawnOverrideRow SpawnRow(
        string name,
        uint objectId,
        string templateKey,
        float x,
        float z,
        short mapId = Map,
        int packetLength = 108)
    {
        var definition = Program.CreateCapturedMonster(
            objectId,
            x,
            z,
            templateKey,
            mapId: mapId,
            sceneKey: "Olympia_All");
        var packet = packetLength >= definition.Packet.Length
            ? definition.Packet
            : Truncate(definition.Packet, packetLength);
        return new MonsterSpawnOverrideRow(
            Id: objectId,
            Name: name,
            WaypointId: null,
            MapId: mapId,
            SceneKey: "Olympia_All",
            TemplateKey: templateKey,
            DisplayName: templateKey,
            ObjectId: objectId,
            X: x,
            Z: z,
            Packet: packet,
            Enabled: true);
    }

    /// <summary>Shortens a fixture packet so a length-bound case can be built.</summary>
    private static byte[] Truncate(byte[] packet, int length)
    {
        var result = new byte[length];
        Array.Copy(packet, result, Math.Min(packet.Length, length));
        if (length >= 4)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0, 2), (ushort)length);
        }

        return result;
    }
}
