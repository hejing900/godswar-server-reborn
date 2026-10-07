using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Pins the Cursed Land release (诅咒之地二, runtime map 29) ported from the
/// October 4 2026 reference capture.
/// </summary>
/// <remarks>
/// Two things are checked together because they must ship together: the V10 NPC
/// content release that places the seven captured actors, and the transport
/// table that gives them their captured behaviour. The publication refuses to
/// start when a declared count or revision disagrees with what it computed, so
/// these checks catch that here instead of as a crash loop in a running server.
/// They also pin the capture's own numbers per row and per destination, so a
/// later edit cannot silently move an actor or a landing the reference chose.
/// </remarks>
internal static class CursedLandContentChecks
{
    public const string CheckName = "Cursed Land map 29 content and transports";

    public static Task RunAsync()
    {
        CheckContentRelease();
        CheckDialogueRelease();
        CheckTransportTable();
        return Task.CompletedTask;
    }

    private static void CheckContentRelease()
    {
        var previous = NpcContentBaselineV9.LoadDefinitions();
        var definitions = NpcContentBaselineV10.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);

        Check.Equal(
            NpcContentBaselineV9.ExpectedEntryCount +
                NpcContentBaselineV10.AddedEntryCount,
            definitions.Length,
            "Cursed Land V10 adds exactly the seven captured actors to V9");
        Check.Equal(
            NpcContentBaselineV10.ExpectedEntryCount,
            definitions.Length,
            "Cursed Land V10 declared entry count");
        Check.Equal(
            414,
            definitions.Length,
            "Cursed Land V10 published entry count");
        Check.Equal(
            NpcContentBaselineV10.ExpectedRevision,
            revision.Sha256,
            "Cursed Land V10 golden revision");
        Check.Equal(
            definitions.Length,
            revision.EntryCount,
            "Cursed Land V10 revision entry count");

        // The reviewed V9 release stays immutable: this release is additive only.
        var previousKeys = previous
            .Select(static npc => (npc.MapId, npc.NpcKey))
            .ToHashSet();
        foreach (var key in previousKeys)
        {
            Check.True(
                definitions.Any(npc => (npc.MapId, npc.NpcKey) == key),
                $"Cursed Land V10 retains V9 actor {key.NpcKey}");
        }

        Check.True(
            !previous.Any(static npc =>
                npc.MapId == NpcContentBaselineV10.CursedLandMapId),
            "V9 published no map 29 actor");

        Check.Equal(
            NpcContentBaselineV10.AddedEntryCount,
            NpcContentBaselineV10.CapturedNpcs.Length,
            "Cursed Land V10 roster size");

        foreach (var captured in NpcContentBaselineV10.CapturedNpcs)
        {
            var matches = definitions
                .Where(npc => npc.NpcKey == captured.NpcKey)
                .ToArray();
            Check.Equal(
                1,
                matches.Length,
                $"Cursed Land V10 publishes {captured.NpcKey} exactly once");
            if (matches.Length != 1)
            {
                continue;
            }

            var npc = matches[0];
            Check.Equal(
                CursedLandTransportProtocol.MapId,
                npc.MapId,
                $"{captured.NpcKey} stands on the Cursed Land map");
            Check.Equal(
                CursedLandTransportProtocol.NpcSceneKey,
                npc.SceneKey,
                $"{captured.NpcKey} uses the actor scene key its text row carries");
            Check.Equal(
                captured.TemplateKey,
                npc.TemplateKey,
                $"{captured.NpcKey} keeps the captured appearance template");
            Check.Equal(
                captured.ObjectId,
                npc.ObjectId,
                $"{captured.NpcKey} keeps the captured object id");
            Check.Equal(
                npc.ObjectId,
                npc.InteractionId,
                $"{captured.NpcKey} interacts under its own object id");
            Check.Equal(
                captured.AppearanceType,
                npc.AppearanceType,
                $"{captured.NpcKey} keeps the captured appearance word");
            Check.Equal(
                captured.X,
                npc.X,
                $"{captured.NpcKey} keeps the captured x");
            Check.Equal(
                captured.Z,
                npc.Z,
                $"{captured.NpcKey} keeps the captured z");
            Check.Equal(
                captured.Facing,
                npc.Facing,
                $"{captured.NpcKey} keeps the captured facing");
        }
    }

    /// <remarks>
    /// Rebuilds the publication's own pipeline - placed keys against the shipped
    /// text seed, then the inherited text overlays - exactly like the V25 release
    /// check does. Two placed keys ship no <c>npc_text_templates</c> row of their
    /// own (<c>Arena_006</c> and <c>DuelArena_001</c>, from the arena releases);
    /// <c>NpcDialogueBaselineV20</c> synthesises those two, so the final text
    /// count equals the spawn release's entry count and the publication's count
    /// gate agrees.
    /// </remarks>
    private static void CheckDialogueRelease()
    {
        var definitions = NpcContentBaselineV10.LoadDefinitions();

        var textByKey = NpcTemplateSeeds.Texts
            .GroupBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        var rawTexts = definitions
            .Select(static definition => definition.NpcKey)
            .Distinct(StringComparer.Ordinal)
            .Where(textByKey.ContainsKey)
            .Select(key => textByKey[key])
            .Select(static text => new NpcTextDefinition(
                text.NpcKey,
                text.SceneKey,
                text.DisplayName,
                text.Description))
            .OrderBy(static text => text.NpcKey, StringComparer.Ordinal)
            .ToArray();

        var texts = NpcDialogueBaselineV26.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV26.CreateRoutes();
        var profiles = NpcDialogueBaselineV26.Profiles;
        var payload = WorldContentRevisionHasher.HashNpcDialogues(texts, routes);

        Check.Equal(
            NpcDialogueBaselineV26.ExpectedTextCount,
            texts.Length,
            "Cursed Land V26 published text count");
        // The publication's own trigger: the text count must equal the spawn
        // release's entry count, which is why a spawn release that adds rows
        // cannot be served by a dialogue release pinned to the previous one.
        Check.Equal(
            definitions.Length,
            texts.Length,
            "Cursed Land V26 text rows equal the V10 spawn rows");
        Check.Equal(
            NpcDialogueBaselineV26.ExpectedRouteCount,
            routes.Length,
            "Cursed Land V26 published route count");
        Check.Equal(
            NpcDialogueBaselineV26.ExpectedProfileCount,
            profiles.Length,
            "Cursed Land V26 published profile count");
        Check.Equal(
            NpcDialogueBaselineV26.ExpectedMenuEntryCount,
            profiles.Sum(static profile => profile.InitialMenuSubIds.Length),
            "Cursed Land V26 published menu-entry count");
        Check.Equal(
            NpcDialogueBaselineV26.ExpectedHashedEntryCount,
            payload.EntryCount,
            "Cursed Land V26 hashed entry count");
        Check.Equal(
            NpcDialogueBaselineV26.ExpectedRevision,
            payload.Sha256,
            "Cursed Land V26 canonical revision");
        Check.Equal(
            NpcContentBaselineV10.ExpectedRevision,
            NpcDialogueBaselineV26.ExpectedSpawnRevision,
            "Cursed Land V26 targets the V10 spawn release");

        foreach (var captured in NpcContentBaselineV10.CapturedNpcs)
        {
            Check.True(
                texts.Any(text => text.NpcKey == captured.NpcKey),
                $"{captured.NpcKey} publishes a description row");
        }

        // The seven are description-only: their behaviour is server-owned, so no
        // dialogue route may name them.
        var capturedKeys = NpcContentBaselineV10.CapturedNpcs
            .Select(static npc => npc.NpcKey)
            .ToHashSet(StringComparer.Ordinal);
        Check.True(
            !routes.Any(route => capturedKeys.Contains(route.NpcKey)),
            "the seven captured actors carry no dialogue route");
    }

    private static void CheckTransportTable()
    {
        Check.Equal(
            (short)29,
            CursedLandTransportProtocol.MapId,
            "Cursed Land runtime map id");
        Check.Equal(
            "Execratively3",
            CursedLandTransportProtocol.SceneKey,
            "Cursed Land map scene key");
        Check.Equal(
            "Execrativelys3",
            CursedLandTransportProtocol.NpcSceneKey,
            "Cursed Land actor scene key");
        Check.True(
            !string.Equals(
                CursedLandTransportProtocol.SceneKey,
                CursedLandTransportProtocol.NpcSceneKey,
                StringComparison.Ordinal),
            "the map and its actor data set keep their distinct scene keys");

        // The two capital Event Transporters, and nothing else, own the entry.
        Check.True(
            CursedLandTransportProtocol.IsEventTransporter("Athens_072"),
            "Athens' Event Transporter owns the destination");
        Check.True(
            CursedLandTransportProtocol.IsEventTransporter("Sparta_072"),
            "Sparta's Event Transporter owns the destination");
        Check.True(
            !CursedLandTransportProtocol.IsEventTransporter("Athens_041"),
            "the ordinary world Transporter does not");

        Check.True(
            CursedLandTransportProtocol.IsCursedLandTwoSelection(
                "Athens_072",
                CursedLandTransportProtocol.EventTransporterDialogIndex,
                CursedLandTransportProtocol.CursedLandTwoSubId),
            "the captured page-one click resolves");
        Check.True(
            !CursedLandTransportProtocol.IsCursedLandTwoSelection(
                "Athens_072",
                dialogIndex: 2,
                CursedLandTransportProtocol.CursedLandTwoSubId),
            "a page-two click does not borrow the page-one entry");
        Check.True(
            !CursedLandTransportProtocol.IsCursedLandTwoSelection(
                "Athens_072",
                CursedLandTransportProtocol.EventTransporterDialogIndex,
                subId: 201),
            "the sibling entry is not the destination");

        // The level gate: the capture refused level 3 and admitted level 56, and
        // nothing in it refuses anybody above that. Only the lower bound is
        // enforced, so a character past the client text's 75 still travels.
        Check.True(
            !CursedLandTransportProtocol.IsLevelEligible(3),
            "the refused capture level stays outside the gate");
        Check.True(
            CursedLandTransportProtocol.IsLevelEligible(56),
            "the admitted capture level stays inside the gate");
        Check.True(
            !CursedLandTransportProtocol.IsLevelEligible(
                CursedLandTransportProtocol.MinimumLevel - 1),
            "the gate's lower bound is exclusive below it");
        Check.True(
            CursedLandTransportProtocol.IsLevelEligible(
                CursedLandTransportProtocol.MinimumLevel),
            "the gate's lower bound is inclusive");
        Check.True(
            CursedLandTransportProtocol.IsLevelEligible(140),
            "a character above the client text's 75 is still admitted");
        Check.True(
            CursedLandTransportProtocol.IsLevelEligible(int.MaxValue),
            "the gate carries no upper bound");

        // The five captured actors, each with the point the reference sent the
        // player to when it was clicked.
        (string NpcKey, float X, float Z)[] randomTargets =
        [
            ("Execrativelys3_006", -167f, 125f),
            ("Execrativelys3_007", -130f, 50f),
            ("Execrativelys3_008", -50f, 169f),
            ("Execrativelys3_009", 60f, 140f),
            ("Execrativelys3_010", 180f, 150f)
        ];
        foreach (var (npcKey, x, z) in randomTargets)
        {
            Check.True(
                CursedLandTransportProtocol.TryGetInMapFunction(
                    npcKey,
                    out var function),
                $"{npcKey} owns a transport function");
            Check.Equal(
                CursedLandTransportProtocol.RandomTeleportFunction,
                function,
                $"{npcKey} advertises the random-transport function");
            Check.True(
                CursedLandTransportProtocol.TryGetRandomTarget(
                    npcKey,
                    out var point),
                $"{npcKey} owns a destination");
            Check.Equal(x, point.X, $"{npcKey} keeps the captured x");
            Check.Equal(z, point.Z, $"{npcKey} keeps the captured z");
        }

        // The home actor. Only _014 was captured; the client's sibling _013 was
        // never placed or clicked, so it must not answer.
        Check.True(
            CursedLandTransportProtocol.TryGetInMapFunction(
                "Execrativelys3_014",
                out var homeFunction),
            "the Main City Transporter owns a transport function");
        Check.Equal(
            CursedLandTransportProtocol.HomeTeleportFunction,
            homeFunction,
            "the Main City Transporter advertises the home function");
        Check.True(
            CursedLandTransportProtocol.IsHomeTransporter("Execrativelys3_014"),
            "the Main City Transporter is the home actor");
        Check.True(
            !CursedLandTransportProtocol.IsInMapTransporter(
                "Execrativelys3_013"),
            "the uncaptured sibling is not offered");
        Check.True(
            !CursedLandTransportProtocol.IsInMapTransporter(
                "Execrativelys3_012"),
            "Pan's Envoy is a description-only actor");

        // The landing and the Athens return the capture recorded.
        Check.Equal(
            -196f,
            CursedLandTransportProtocol.Arrival.X,
            "captured arrival x");
        Check.Equal(
            44f,
            CursedLandTransportProtocol.Arrival.Z,
            "captured arrival z");
        Check.Equal(
            20f,
            CursedLandTransportProtocol.AthensHome.X,
            "captured Athens return x");
        Check.Equal(
            -100f,
            CursedLandTransportProtocol.AthensHome.Z,
            "captured Athens return z");

        // The landing is also the map's free-revive point, which the same capture
        // showed six times.
        Check.True(
            ReviveLandingCatalog.TryResolve(
                (byte)CursedLandTransportProtocol.MapId,
                out var landing),
            "the Cursed Land has a captured revive landing");
        Check.Equal(
            (byte)CursedLandTransportProtocol.MapId,
            landing.MapId,
            "the Cursed Land revive stays on its own map");
        Check.Equal(
            CursedLandTransportProtocol.Arrival.X,
            landing.X,
            "the Cursed Land revive landing is the arrival point");
        Check.Equal(
            CursedLandTransportProtocol.Arrival.Z,
            landing.Z,
            "the Cursed Land revive landing z is the arrival point");
    }
}
