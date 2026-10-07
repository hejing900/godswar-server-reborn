using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// The V11 NPC content release: the two quest actors whose absence kept four
/// quests out of the chain.
/// </summary>
/// <remarks>
/// <c>1209</c>/<c>1555</c> (Athens) and their Sparta mirrors <c>209</c>/<c>555</c>
/// name <c>Marathon_All_006</c> and <c>Peloponnese_All_006</c> as giver or
/// responder. Neither npc was ever published, so the chain generator dropped all
/// four with "its npc is not published".
/// </remarks>
internal static class QuestActorContentChecks
{
    public const string CheckName = "Quest actor NPC content release";

    public static Task RunAsync()
    {
        var previous = NpcContentBaselineV10.LoadDefinitions();
        var definitions = NpcContentBaselineV11.LoadDefinitions();
        var revision = WorldContentRevisionHasher.HashNpcs(definitions);

        Check.Equal(
            NpcContentBaselineV10.ExpectedEntryCount +
                NpcContentBaselineV11.AddedEntryCount,
            definitions.Length,
            "V11 adds exactly the two quest actors to V10");
        Check.Equal(
            NpcContentBaselineV11.ExpectedEntryCount,
            definitions.Length,
            "V11 declared entry count");
        Check.Equal(
            NpcContentBaselineV11.ExpectedRevision,
            revision.Sha256,
            "V11 golden revision");
        Check.Equal(
            definitions.Length,
            revision.EntryCount,
            "V11 revision entry count");
        Check.Equal(
            NpcContentBaselineV11.AddedEntryCount,
            NpcContentBaselineV11.QuestActors.Length,
            "V11 declared actor count matches the table");

        // The reviewed V10 release stays immutable: this release is additive only.
        var previousKeys = previous
            .Select(static npc => (npc.MapId, npc.NpcKey))
            .ToHashSet();
        foreach (var key in previousKeys)
        {
            Check.True(
                definitions.Any(npc => (npc.MapId, npc.NpcKey) == key),
                $"V11 retains V10 actor {key.NpcKey}");
        }

        foreach (var actor in NpcContentBaselineV11.QuestActors)
        {
            Check.True(
                !previous.Any(npc =>
                    npc.MapId == actor.MapId &&
                    npc.NpcKey == actor.NpcKey),
                $"V10 published no {actor.NpcKey}");
            Check.True(
                definitions.Any(npc =>
                    npc.MapId == actor.MapId &&
                    npc.NpcKey == actor.NpcKey &&
                    npc.TemplateKey == actor.TemplateKey &&
                    npc.InteractionId == actor.ObjectId),
                $"V11 places {actor.NpcKey} on the client's own template");
        }

        // The Marathon actor is the captured one: the reference's own 10020 frame
        // carried object 5480, appearance 0x211 at (-1, -38) facing 2.0, and the
        // capture table must agree with the release or the run-time policy would
        // rewrite the row to different numbers than the hash covers.
        Check.True(
            CapturedNpcPlacements.TryFind("Marathon_All_006", out var captured) &&
            captured.ObjectId == 5_480u &&
            captured.TemplateKey == "Marathon_006_AthenianWarrior1" &&
            captured.AppearanceType == 0x211u &&
            captured.X == -1.00f &&
            captured.Z == -38.00f &&
            captured.Facing == 2.00f,
            "the Marathon_All_006 capture and the release agree");

        // Peloponnese was never captured - no export exists for map 13 - so the
        // release must not claim a capture for it.
        Check.True(
            !CapturedNpcPlacements.TryFind("Peloponnese_All_006", out _),
            "the Peloponnese_All_006 placement is the client's own data, not a capture");

        CheckDialogueRelease();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The dialogue release that ships with V11.
    /// </summary>
    /// <remarks>
    /// A spawn release that adds rows cannot be served by a dialogue release
    /// pinned to the previous one: the publication's own gate is that the text
    /// count equals the spawn release's entry count, so V26 refused to start
    /// against V11 until V27 replaced its dependency. That failure only ever
    /// showed up as a crash loop in the running server, so it is pinned here.
    /// </remarks>
    private static void CheckDialogueRelease()
    {
        var definitions = NpcContentBaselineV11.LoadDefinitions();

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

        var texts = NpcDialogueBaselineV27.ApplyTextOverrides(rawTexts);
        var routes = NpcDialogueBaselineV27.CreateRoutes();
        var profiles = NpcDialogueBaselineV27.Profiles;
        var payload = WorldContentRevisionHasher.HashNpcDialogues(texts, routes);

        Check.Equal(
            NpcDialogueBaselineV27.ExpectedTextCount,
            texts.Length,
            "V27 published text count");
        Check.Equal(
            definitions.Length,
            texts.Length,
            "V27 text rows equal the V11 spawn rows");
        Check.Equal(
            NpcDialogueBaselineV27.ExpectedRouteCount,
            routes.Length,
            "V27 published route count");
        Check.Equal(
            NpcDialogueBaselineV27.ExpectedProfileCount,
            profiles.Length,
            "V27 published profile count");
        Check.Equal(
            NpcDialogueBaselineV27.ExpectedMenuEntryCount,
            profiles.Sum(static profile => profile.InitialMenuSubIds.Length),
            "V27 published menu-entry count");
        Check.Equal(
            NpcDialogueBaselineV27.ExpectedHashedEntryCount,
            payload.EntryCount,
            "V27 hashed entry count");
        Check.Equal(
            NpcDialogueBaselineV27.ExpectedRevision,
            payload.Sha256,
            "V27 canonical revision");
        Check.Equal(
            NpcContentBaselineV11.ExpectedRevision,
            NpcDialogueBaselineV27.ExpectedSpawnRevision,
            "V27 targets the V11 spawn release");

        foreach (var actor in NpcContentBaselineV11.QuestActors)
        {
            Check.True(
                texts.Any(text => text.NpcKey == actor.NpcKey),
                $"V27 publishes a text row for {actor.NpcKey}");
        }
    }
}
