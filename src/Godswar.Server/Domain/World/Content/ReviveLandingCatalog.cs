namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// Free-revive landing points, keyed by the map the character died on. The
/// reference server relocates a revived character to that map's own revive
/// point instead of one global camp coordinate, so the destination - including
/// the destination map - is a per-map record.
/// <para>
/// Only capture-proven rows belong here. A map without a row keeps the camp
/// capital fallback in <c>GameDefaults.InitializeStartingLocation</c>, and the
/// revive trace logs the fallback so a missing map is visible instead of
/// silently wrong.
/// </para>
/// </summary>
internal static class ReviveLandingCatalog
{
    /// <param name="MapId">Map the character is placed on after reviving.</param>
    /// <param name="X">Landing X.</param>
    /// <param name="Z">Landing Z.</param>
    internal readonly record struct ReviveLanding(byte MapId, float X, float Z);

    /// <summary>
    /// Captured landing points, one per map the character can die on. The
    /// destination map and its coordinate are the reference's own: a character
    /// revives on the map it died on, at that map's own revive point, and the
    /// two captures below are far from each other and from any camp capital.
    /// </summary>
    /// <remarks>
    /// Athens city (map 1), captured 2026-09-15. A character killed there
    /// received the 28-byte landing frame
    /// <c>1C002227 8F040000 0000A041 00000000 0000C8C2 01000100 01000000</c>:
    /// object 1167 at x = 20, y = 0, z = -100. See
    /// docs/death-revive-capture-20260915.md.
    /// <para>
    /// Megara (map 18), captured 2026-09-25 at 00:13:20. A character killed
    /// there answered the revive prompt with <c>C2S 10028 {a7, type 2}</c> and
    /// the server returned
    /// <c>1C002227 A7000000 00006042 00000000 0000B042 12001200 01000000</c>:
    /// object 167 at x = 56, y = 0, z = 88. The word at +20 is the same field
    /// in both frames and reads 0x0012 here against 0x0001 for Athens, which is
    /// how the landing map is identified.
    /// </para>
    /// <para>
    /// The Cursed Land (map 29), captured 2026-10-04 at 10:00:13, 10:01:07,
    /// 10:01:48, 10:02:10, 10:02:33 and 10:03:26. Six times the character the
    /// Event Transporter had just placed there died and answered the revive
    /// prompt with <c>C2S 10028 {596, 2}</c>, and six times the server returned
    /// the same 28-byte landing frame
    /// <c>1C002227 54020000 000044C3 00000000 00003042 1D001D00 01000000</c>:
    /// object 596 at x = -196, y = 0, z = 44, which is also the point the
    /// Event Transporter lands a character on. The word at +20 reads 0x001D, the
    /// map's own id, so this is a same-map revive.
    /// </para>
    /// </remarks>
    private static readonly ReviveLanding[] Landings =
    [
        new(MapId: 1, X: 20f, Z: -100f),
        new(MapId: 18, X: 56f, Z: 88f),
        new(MapId: 29, X: -196f, Z: 44f)
    ];

    /// <summary>Every capture-proven landing point, in capture order.</summary>
    public static IReadOnlyList<ReviveLanding> Captured => Landings;

    public static bool TryResolve(byte deathMap, out ReviveLanding landing)
    {
        foreach (var candidate in Landings)
        {
            if (candidate.MapId == deathMap)
            {
                landing = candidate;
                return true;
            }
        }

        landing = default;
        return false;
    }
}
