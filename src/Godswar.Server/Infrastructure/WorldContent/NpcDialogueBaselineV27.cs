using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Binds the dialogue publication to the V11 spawn release, which places the two
/// quest actors <c>Marathon_All_006</c> and <c>Peloponnese_All_006</c>.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is added to the dialogue set itself. Both are ordinary map NPCs with a
/// shipped description and no capital dialogue route, so they only need the text
/// row the publication's own join already produces for every placed key. That
/// join is why this release exists at all: the text count is the spawn release's
/// entry count, so a spawn release that adds rows cannot be served by a dialogue
/// release pinned to the previous one.
/// </para>
/// <para>
/// Every V26 text, route, profile and binding is retained unchanged, and the V26
/// spawn dependency is replaced with V11's.
/// </para>
/// </remarks>
internal static class NpcDialogueBaselineV27
{
    /// <summary>
    /// The published text rows the V11 spawn release resolves to: V26's count plus
    /// the two quest actors, each of which has a shipped <c>npc_text_templates</c>
    /// row.
    /// </summary>
    public const int ExpectedTextCount =
        NpcDialogueBaselineV26.ExpectedTextCount +
        NpcContentBaselineV11.AddedEntryCount;

    public const int ExpectedProfileCount =
        NpcDialogueBaselineV26.ExpectedProfileCount;

    public static readonly int ExpectedRouteCount =
        NpcDialogueBaselineV26.ExpectedRouteCount;

    public static readonly int ExpectedMenuEntryCount =
        NpcDialogueBaselineV26.ExpectedMenuEntryCount;

    /// <summary>
    /// Texts plus routes, which is what the publication's revision records.
    /// </summary>
    public static readonly int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;

    /// <summary>
    /// The spawn release this dialogue release targets. The two quest actors only
    /// exist from V11 on, so the V26 dependency cannot satisfy it.
    /// </summary>
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV11.ExpectedRevision;

    public const string Source = "reviewed-published-npc-dialogue-v27";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical text and route set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "73093A49AC5A9F5E04FA4A1A60D62F6456494E39DEAA52DD8B812AF1941B8534";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV26.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV26.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV26.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts) =>
        NpcDialogueBaselineV26.ApplyTextOverrides(texts);
}
