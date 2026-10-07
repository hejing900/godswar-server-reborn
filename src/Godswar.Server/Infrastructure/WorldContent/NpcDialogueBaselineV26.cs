using System.Collections.Immutable;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Binds the dialogue publication to the V10 spawn release, which places the
/// seven Cursed Land (诅咒之地二, map 29) actors the October 4 2026 capture
/// recorded.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is added to the dialogue set itself. The seven are ordinary map NPCs
/// with a shipped description and no capital dialogue route - the transport
/// behaviour is server-owned and lives in
/// <see cref="CursedLandTransportProtocol"/> - so they only need the text row the
/// publication's own join already produces for every placed key. That join is
/// why this release exists at all: the text count is the spawn release's entry
/// count, so a spawn release that adds rows cannot be served by a dialogue
/// release pinned to the previous one.
/// </para>
/// <para>
/// Every V25 text, route, profile and binding is retained unchanged, and the V25
/// spawn dependency is replaced with V10's.
/// </para>
/// </remarks>
internal static class NpcDialogueBaselineV26
{
    /// <summary>
    /// The published text rows the V10 spawn release resolves to: V25's count plus
    /// the seven captured Cursed Land actors, each of which has a shipped
    /// <c>npc_text_templates</c> row.
    /// </summary>
    public const int ExpectedTextCount =
        NpcDialogueBaselineV25.ExpectedTextCount +
        NpcContentBaselineV10.AddedEntryCount;

    public const int ExpectedProfileCount =
        NpcDialogueBaselineV25.ExpectedProfileCount;

    public static readonly int ExpectedRouteCount =
        NpcDialogueBaselineV25.ExpectedRouteCount;

    public static readonly int ExpectedMenuEntryCount =
        NpcDialogueBaselineV25.ExpectedMenuEntryCount;

    /// <summary>
    /// Texts plus routes, which is what the publication's revision records.
    /// </summary>
    public static readonly int ExpectedHashedEntryCount =
        ExpectedTextCount + ExpectedRouteCount;

    /// <summary>
    /// The spawn release this dialogue release targets. The seven Cursed Land
    /// actors only exist from V10 on, so the V25 dependency cannot satisfy it.
    /// </summary>
    public const string ExpectedSpawnRevision =
        NpcContentBaselineV10.ExpectedRevision;

    public const string Source = "reviewed-published-npc-dialogue-v26";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical text and route set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "AAE553B67EC7A6D87DBB53FFF01EC2FD3B00EC04711AB39DEB3632962457B404";

    public static ImmutableArray<NpcDialogueProfileBaseline> Profiles =>
        NpcDialogueBaselineV25.Profiles;

    public static ImmutableArray<NpcDialogueBindingBaseline> Bindings =>
        NpcDialogueBaselineV25.Bindings;

    public static NpcDialogueRouteDefinition[] CreateRoutes() =>
        NpcDialogueBaselineV25.CreateRoutes();

    public static NpcTextDefinition[] ApplyTextOverrides(
        IReadOnlyList<NpcTextDefinition> texts) =>
        NpcDialogueBaselineV25.ApplyTextOverrides(texts);
}
