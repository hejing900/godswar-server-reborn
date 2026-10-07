namespace Godswar.Server.Domain.World.Content;

/// <summary>
/// The Cursed Land transport network the October 4 2026 reference capture drove.
/// </summary>
/// <remarks>
/// <para>
/// The client owns the whole conversation in <c>NpcFunTranmit.lua</c>; the
/// server only has to move the character. Every number, key, coordinate and
/// object id below is read off capture session
/// <c>825354ec-c6fe-4bf0-a4b8-25397ca68e8f</c> (local 2026-10-04 09:00-10:18,
/// proxy log <c>captures/godswar-proxy-20261004-0900.log</c>), which is the
/// window 09:58-10:06 this release ports.
/// </para>
/// <para>
/// Naming: the client's own button for the destination is
/// <c>NF_L0_204</c> "▽传送到诅咒之地二#1-1-200", so the player-visible name of
/// this map is 诅咒之地二. The runtime map id is <b>29</b>, its scene key is
/// <c>Execratively3</c> (<c>Localization/en_us/Settings/Sys/MapIdToNameConfig.ini</c>
/// <c>MapID29 = Execratively3,206</c>), and the client's <c>Message.dat</c> calls
/// that scene 诅咒区域. All three names refer to the one map; the server always
/// uses the runtime id 29.
/// </para>
/// <para>
/// The scene-key mismatch is the client's own: map 29's scene key is
/// <c>Execratively3</c> while its actors live in the client's
/// <c>Execrativelys3</c> data set, which is why the npc keys below read
/// <c>Execrativelys3_*</c> against a map scene key of <c>Execratively3</c>.
/// </para>
/// </remarks>
internal static class CursedLandTransportProtocol
{
    /// <summary>The runtime map id of 诅咒之地二, from the captured landing frame.</summary>
    public const short MapId = 29;

    /// <summary>The map's own scene key, from the client's map-id configuration.</summary>
    public const string SceneKey = "Execratively3";

    /// <summary>
    /// The scene key the map's actors and their description rows carry.
    /// </summary>
    /// <remarks>
    /// The client keeps this map's NPC data under <c>Execrativelys3</c> - one
    /// letter more than the map's own scene key - and
    /// <c>npc_text_templates.scene_key</c> carries that spelling. The dialogue
    /// loader requires every published text row's scene key to equal its spawn
    /// row's, so the NPC content release must use this key rather than
    /// <see cref="SceneKey"/>. The shipped data does the same for maps 30 and 31,
    /// where <c>Labyrinth_006</c> stands on a map whose scene key is
    /// <c>Labyrint2</c>.
    /// </remarks>
    public const string NpcSceneKey = "Execrativelys3";

    /// <summary>
    /// The client function that selects <c>NpcFunTranmit.lua</c>
    /// (<c>NPC_FLAG_SYS_TRANMIT = 1</c>). The two capital Event Transporters
    /// advertise exactly this number in their captured open frame.
    /// </summary>
    public const int EventTransporterFunction = 1;

    /// <summary>
    /// The captured open frame's dialog index for the Event Transporter, which is
    /// the function number the client echoes back in every action.
    /// </summary>
    public const int EventTransporterDialogIndex = EventTransporterFunction;

    /// <summary>
    /// The Event Transporter's "▽传送到诅咒之地二" entry. The capture's opening
    /// menu was <c>[200, 700, 500, 600, 202]</c> and the player clicked 200; the
    /// client script draws <c>NF_L0_204</c> for it on page one, and the probe tag
    /// appended to that text records the same number (<c>#1-1-200</c>).
    /// </summary>
    public const int CursedLandTwoSubId = 200;

    /// <summary>
    /// <c>NF_L0_34</c> "等级不符合要求不能传送." - the answer the September 28 2026
    /// reference capture gave to the very same click when the character was not
    /// eligible (<c>S2C 10070 {5069, dialog 1, [2001]}</c>). The dialog index
    /// stays 1: the refusal is a result of the page-one button, not a new page.
    /// </summary>
    public const int LevelRequirementResultSubId = 2001;

    /// <summary>
    /// The eligible level for 诅咒之地二: the client's own lower bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate exists in both captured directions. On 2026-09-28 at 02:01:51 the
    /// character <c>1234v123</c> - level <b>3</b>, read from its own <c>10002</c>
    /// byte +39 and its <c>10043</c>/<c>10166</c> level word - was answered
    /// <c>S2C 10070 {5069, dialog 1, [2001]}</c>. On 2026-10-04 at 09:59:37 the
    /// character <c>2v41e12</c> - level <b>56</b>, read the same way - was moved
    /// instead.
    /// </para>
    /// <para>
    /// No sample pins an upper bound, and the shipped client texts disagree about
    /// the band: <c>NF_L0_35</c> says "诅咒之地一，二：必须是20级-75级的玩家才可以
    /// 传送。" while <c>NF_L0_31</c> says "诅咒之地：必须是55级以上的玩家才可以传送。",
    /// and the destination map's own actors are the Level 55/75/95/115/125
    /// transporters. Both <c>&gt;=20</c> and <c>20-75</c> fit the two captured
    /// levels, so this server enforces only the bound the texts agree on - the
    /// lower one - and refuses nobody above it. Adding the unproven 75 cap would
    /// lock out exactly the high-level characters the map's own roster serves.
    /// </para>
    /// </remarks>
    public const int MinimumLevel = 20;

    /// <summary>
    /// <c>NPC_FLAG_SYS_RANDOM = 96</c>, the client's 随机传送 window. Five of the
    /// map's Event Transporters advertise this number and move the character to
    /// their own point the moment the window is opened.
    /// </summary>
    public const int RandomTeleportFunction = 96;

    /// <summary>
    /// <c>NPC_FLAG_SYS_HOME = 97</c>, the client's 传送回城 window, used by the
    /// map's "Main City Transporter".
    /// </summary>
    public const int HomeTeleportFunction = 97;

    /// <summary>The open frame's flags word for an extended function list.</summary>
    public const int FunctionListOpenFlags = 0x200;

    /// <summary>
    /// An in-map transport point: 1.7 units of the reference's own frame, kept as
    /// floats because that is what the landing frame carries.
    /// </summary>
    internal readonly record struct CursedLandPoint(float X, float Z);

    /// <summary>
    /// The point the Event Transporter lands on, and the point a character that
    /// dies on map 29 free-revives at. Both are the same landing and the capture
    /// shows it eleven times: six free revives after
    /// <c>C2S 10028 {596, 2}</c> and the arrival that follows the
    /// <c>200</c> click.
    /// </summary>
    public static readonly CursedLandPoint Arrival = new(-196f, 44f);

    /// <summary>
    /// Where the map's "Main City Transporter" sends an Athens character. The
    /// captured landing <c>1C002227 54020000 0000A041 00000000 0000C8C2 1D000100 01000000</c>
    /// put object 596 on map 1 at x = 20, z = -100, which is the same Athens-city
    /// point the reference uses for a free revive and for the farm and arena
    /// returns in three other sessions.
    /// </summary>
    public static readonly CursedLandPoint AthensHome = new(20f, -100f);

    /// <summary>
    /// The five random-teleport actors of map 29 and the point each one sends the
    /// player to, keyed by the npc key the client echoes back.
    /// </summary>
    /// <remarks>
    /// The capture clicked all five. <c>Execrativelys3_009</c> was clicked twice
    /// (10:00:00 and 10:00:53) and the reference answered both times with the same
    /// landing (60, 140), while the other four were each clicked once and each got
    /// its own distinct point, so the destination is modelled as fixed per actor.
    /// The client's labels order them by level - 55, 75, 95, 115, 125 - and the
    /// five actors placed on the map are the 006-010 half of the shipped roster.
    /// </remarks>
    private static readonly Dictionary<string, CursedLandPoint> RandomTargets =
        new(StringComparer.Ordinal)
        {
            ["Execrativelys3_006"] = new(-167f, 125f),
            ["Execrativelys3_007"] = new(-130f, 50f),
            ["Execrativelys3_008"] = new(-50f, 169f),
            ["Execrativelys3_009"] = new(60f, 140f),
            ["Execrativelys3_010"] = new(180f, 150f)
        };

    /// <summary>
    /// The map's "Main City Transporter". Only <c>_014</c> was captured; the
    /// client also ships a sibling <c>Execrativelys3_013</c> with identical text
    /// and appearance, but the reference never placed or clicked it, so it is not
    /// offered here.
    /// </summary>
    private const string HomeNpcKey = "Execrativelys3_014";

    /// <summary>The two capital Event Transporters, both driven by <c>Athens_072</c>'s script.</summary>
    public static bool IsEventTransporter(string npcKey) =>
        npcKey is "Athens_072" or "Sparta_072";

    /// <summary>
    /// Whether the npc owns an in-map transport function, and which one. The
    /// captured open frames advertise the function on its own with flags 0x200.
    /// </summary>
    public static bool TryGetInMapFunction(string npcKey, out int functionNumber)
    {
        if (string.Equals(npcKey, HomeNpcKey, StringComparison.Ordinal))
        {
            functionNumber = HomeTeleportFunction;
            return true;
        }

        if (RandomTargets.ContainsKey(npcKey))
        {
            functionNumber = RandomTeleportFunction;
            return true;
        }

        functionNumber = 0;
        return false;
    }

    /// <summary>Whether the npc is one of the map's in-map transport actors.</summary>
    public static bool IsInMapTransporter(string npcKey) =>
        TryGetInMapFunction(npcKey, out _);

    /// <summary>Whether the npc sends the character home rather than across the map.</summary>
    public static bool IsHomeTransporter(string npcKey) =>
        string.Equals(npcKey, HomeNpcKey, StringComparison.Ordinal);

    /// <summary>The fixed point a random-teleport actor sends the character to.</summary>
    public static bool TryGetRandomTarget(string npcKey, out CursedLandPoint point) =>
        RandomTargets.TryGetValue(npcKey, out point);

    /// <summary>
    /// Resolves the Event Transporter's 诅咒之地二 click. Mirrors the captured
    /// frame shape: the capital Event Transporter, dialog index 1, the page-one
    /// number 200.
    /// </summary>
    public static bool IsCursedLandTwoSelection(
        string npcKey,
        int dialogIndex,
        int subId) =>
        IsEventTransporter(npcKey) &&
        dialogIndex == EventTransporterDialogIndex &&
        subId == CursedLandTwoSubId;

    /// <summary>
    /// Whether the character's level admits the destination. Only the lower bound
    /// is enforced; see <see cref="MinimumLevel"/> for why.
    /// </summary>
    public static bool IsLevelEligible(int level) => level >= MinimumLevel;
}
