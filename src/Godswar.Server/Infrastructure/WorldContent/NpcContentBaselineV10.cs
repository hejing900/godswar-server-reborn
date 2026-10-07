using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

/// <summary>
/// Places the Cursed Land (诅咒之地二, runtime map 29) actors the October 4 2026
/// capture recorded.
/// </summary>
/// <remarks>
/// <para>
/// The reference server sent opcode-10020 world objects for seven actors on map
/// 29 during capture session
/// <c>825354ec-c6fe-4bf0-a4b8-25397ca68e8f</c> (local 2026-10-04 09:59-10:05,
/// proxy log <c>captures/godswar-proxy-20261004-0900.log</c>) while
/// <c>npc_spawn_definitions</c> held no row for the map at all, so the client
/// never saw a single actor there. Every field below is the capture's own: the
/// object id, the appearance word, the position and the facing come from the
/// 10020 frame the reference sent, and the npc key is the ASCII name that frame
/// carried.
/// </para>
/// <para>
/// The capture holds seven of the client's eighteen <c>Execrativelys3_*</c>
/// actors, and only those seven are placed. The other eleven (the 001-005
/// transporters, Pan's Envoy 011, the Main City Transporter 013 and the two
/// remaining quest actors) were never sent as world objects in any capture, so
/// their placement is unknown and is left unpublished rather than invented. The
/// full transport behaviour of the seven that are placed lives in
/// <see cref="CursedLandTransportProtocol"/>.
/// </para>
/// <para>
/// The reviewed V9 release and every earlier one stay immutable, so these actors
/// only ever appear through this release.
/// </para>
/// </remarks>
internal static class NpcContentBaselineV10
{
    public const short CursedLandMapId = CursedLandTransportProtocol.MapId;

    /// <remarks>
    /// The actors' own scene key, not the map's: the client keeps this map's NPC
    /// data under <c>Execrativelys3</c>, and that is the scene key every captured
    /// 10020 template key and every shipped <c>npc_text_templates</c> row for
    /// these seven actors carries. The dialogue loader requires the two to agree.
    /// </remarks>
    public const string CursedLandSceneKey =
        CursedLandTransportProtocol.NpcSceneKey;

    public const int AddedEntryCount = 7;

    public const int ExpectedEntryCount =
        NpcContentBaselineV9.ExpectedEntryCount + AddedEntryCount;

    public const string Source = "reviewed-published-npc-baseline-v10";

    /// <summary>
    /// The release revision. It is the SHA-256 the canonical definition set
    /// hashes to, and is verified on load and again at publication.
    /// </summary>
    public const string ExpectedRevision =
        "6B6C8E20592568F6D3503F472297A779A7228E87A90EA6B6A8AD33DB36A76B8D";

    /// <summary>
    /// One captured Cursed Land actor: the key, the appearance template and the
    /// placement the reference's own 10020 frame carried.
    /// </summary>
    internal readonly record struct CapturedCursedLandNpc(
        string NpcKey,
        string TemplateKey,
        uint ObjectId,
        uint AppearanceType,
        float X,
        float Z,
        float Facing);

    /// <summary>
    /// The seven actors, in the order the capture first sent them (object ids
    /// 5573-5579).
    /// </summary>
    public static readonly CapturedCursedLandNpc[] CapturedNpcs =
    [
        new("Execrativelys3_006", "Execrativelys3_006_Exec12", 5_573u, 0x211u, -226.00f, 48.00f, 1.70f),
        new("Execrativelys3_007", "Execrativelys3_007_Exec5", 5_574u, 0x211u, -227.00f, 53.00f, 1.70f),
        new("Execrativelys3_008", "Execrativelys3_008_Exec6", 5_575u, 0x211u, -225.00f, 58.00f, 2.30f),
        new("Execrativelys3_009", "Execrativelys3_009_Exec9", 5_576u, 0x211u, -222.00f, 65.00f, 2.50f),
        new("Execrativelys3_010", "Execrativelys3_010_Exec10", 5_577u, 0x211u, -215.00f, 65.00f, 3.00f),
        new("Execrativelys3_012", "Execrativelys3_012_Losebook1", 5_578u, 0x211u, -202.00f, 64.00f, 3.00f),
        new("Execrativelys3_014", "Execrativelys3_014_Aga1", 5_579u, 0x111u, -192.00f, 52.00f, 3.00f)
    ];

    public static NpcSpawnDefinition[] LoadDefinitions()
    {
        var previous = NpcContentBaselineV9.LoadDefinitions();
        if (previous.Any(static definition =>
                definition.MapId == CursedLandMapId))
        {
            throw new InvalidDataException(
                "The V10 Cursed Land release requires a V9 set with no map " +
                "29 rows.");
        }

        var definitions = previous
            .Concat(CapturedNpcs.Select(Create))
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
                "The V10 Cursed Land release requires " +
                $"{ExpectedEntryCount} unique map-scoped actors with " +
                $"{AddedEntryCount} Cursed Land additions.");
        }

        return definitions;
    }

    private static NpcSpawnDefinition Create(CapturedCursedLandNpc npc) =>
        new(
            CursedLandMapId,
            CursedLandSceneKey,
            npc.NpcKey,
            npc.TemplateKey,
            npc.ObjectId,
            npc.X,
            npc.Z,
            // The client echoes the object id back as the interaction id, which
            // is what every captured dialogue click is keyed by.
            npc.ObjectId,
            npc.AppearanceType,
            npc.Facing,
            Detail10077: [],
            Detail10080: []);
}
