namespace Godswar.Server.Application.ZeusGift;

/// <summary>
/// The Zeus gift event's rules: what the follower asks for, what a delivery pays,
/// how a number is turned into the <c>SubID</c> the client can print, and which
/// scrolls of <c>NpcFunZeus.lua</c> a page may draw on.
/// </summary>
/// <remarks>
/// <para>
/// Every number the client prints is read off <c>NpcFunZeus.lua</c> and the
/// client's own text table; the amounts are the server's choice, because no
/// capture contains a single reward. The two encodings the script itself
/// documents are:
/// </para>
/// <list type="bullet">
/// <item>the delivery progress line, <c>(tier * 100 + round) * 1000 + 9</c>: the
/// script reads it back with <c>math.mod(SubID,1000) == 9</c> and prints "你正在
/// 进行第 N 次,可以领到第 M 级的奖励";</item>
/// <item>the reward level, <c>tier * 10000 + 13</c>: the script reads it back with
/// <c>math.mod(SubID,10000) == 13</c> and fills the blank in "天神笑纳了你的献礼,
/// 并给你第 __ 级的经验和专长奖励!".</item>
/// </list>
/// <para>
/// A requested item travels as its own item id when one is wanted and as
/// <c>itemId * 10 + count</c> when two or three are: the script's own texts for
/// <c>42002</c> ("2个1级红宝石"), <c>42102</c>, <c>42202</c> and <c>42303</c>
/// ("3个1级水晶") are the item id times ten plus the count.
/// </para>
/// </remarks>
internal static class ZeusGiftPolicy
{
    /// <summary>
    /// The level the follower turns away, from <c>NF_L0_Z100</c> ("必须达到55级
    /// 以上才可以进行献礼"). Captured 2026-09-24 and 2026-09-27 answered
    /// <c>[100]</c> to a character below it.
    /// </summary>
    public const int MinimumLevel = 55;

    /// <summary>Deliveries a character may make per realm day.</summary>
    public const int WeekdayGiftLimit = 10;

    /// <summary>
    /// Deliveries per realm day on Saturday and Sunday. The client's own lines
    /// say it: <c>NF_L0_Z1401</c> reads "每天最多可以献礼10次,周末则是12次".
    /// </summary>
    public const int WeekendGiftLimit = 12;

    /// <summary>
    /// The weekend multiplier the same line promises ("周末时更是会有双倍的回报",
    /// <c>NF_L0_Z1511</c>).
    /// </summary>
    public const int WeekendRewardMultiplier = 2;

    /// <summary>
    /// The substitute: <c>NF_L0_Z1512</c> has the follower ask for "10个一级水晶"
    /// instead of whatever he wanted, and the event page says ten of them replace
    /// any requirement.
    /// </summary>
    public const int CrystalSubstituteItemId = 4230;

    /// <summary>How many crystals stand in for the requirement.</summary>
    public const int CrystalSubstituteCount = 10;

    /// <summary>
    /// The praying stone, the follower's own reward and the saint's currency
    /// (<c>Kaddish</c>, item 3900).
    /// </summary>
    public const int PrayingStoneItemId = 3900;

    /// <summary>Wine, the extra prize a late delivery can bring.</summary>
    public const int WineItemId = 4265;

    /// <summary>
    /// The first delivery of the day that can bring wine, which is the tenth.
    /// </summary>
    /// <remarks>
    /// The event page puts the chance on the tenth delivery only ("在每天第10次献礼
    /// 时将有一定几率获得额外奖励"美酒""), and on the weekend adds the two extra
    /// deliveries to it ("增加的这两次也都将有一定几率得到额外奖励"美酒""), so a
    /// round at or past ten is the whole condition.
    /// </remarks>
    public const int WineEligibleFromRound = 10;

    /// <summary>Whether a delivery round may bring wine.</summary>
    public static bool CanPayWine(int round) => round >= WineEligibleFromRound;

    /// <summary>
    /// The odds a wine-eligible delivery brings wine. The client only announces the
    /// possibility (<c>NF_L0_Z1519</c>), so the number is a server-side choice.
    /// </summary>
    public const int WineChancePercent = 20;

    /// <summary>
    /// Experience one praying stone banks when it is handed in during the week.
    /// </summary>
    /// <remarks>
    /// Handing a stone in pays nothing by itself: it banks this much experience and
    /// <see cref="TalentPointsPerPrayingStone"/> talent point, and the claim line
    /// (<c>1205</c>, <c>NF_L0_Z1113</c>) hands the whole bank over and empties it.
    /// </remarks>
    public const int ExperiencePerPrayingStone = 8_000;

    /// <summary>Talent points one praying stone banks.</summary>
    public const int TalentPointsPerPrayingStone = 1;

    /// <summary>
    /// Talent experience one banked talent point is worth when the claim pays out.
    /// </summary>
    /// <remarks>
    /// <see cref="State.TalentExperienceCatalog.ExperiencePerPoint"/> is 100, so a
    /// point banked by a stone becomes a hundred of the experience the character
    /// progression path converts back into exactly one point.
    /// </remarks>
    public const int TalentExperiencePerPoint =
        State.TalentExperienceCatalog.ExperiencePerPoint;

    /// <summary>
    /// Exchanges every character gets each week before any stone is handed in
    /// ("All players have 5 base chances to exchange their Dust for gifts").
    /// </summary>
    public const int BaseExchangesPerWeek = 5;

    /// <summary>
    /// Stones that buy one further exchange ("every 5 stones deposited add one
    /// extra exchange attempt for the weekend").
    /// </summary>
    public const int StonesPerExtraExchange = 5;

    /// <summary>The dust exchanges a week's deposits buy.</summary>
    public static int ExchangesForStones(int stonesDeposited) =>
        BaseExchangesPerWeek + (Math.Max(0, stonesDeposited) / StonesPerExtraExchange);

    /// <summary>
    /// Dusts one exchange costs, from <c>NF_L0_Z1213</c>: "只要有20个同类的粉尘
    /// 就行了".
    /// </summary>
    public const int DustPerExchange = 20;

    /// <summary>
    /// Dust any ten of, spent on one luck draw, from <c>NF_Z_T3011</c>: "将任意10
    /// 个粉尘放入下框中，就可以碰碰运气啦".
    /// </summary>
    public const int DustPerLuckDraw = 10;

    /// <summary>
    /// How many dusts a lucky number pays back, from <c>NF_Z_T3015</c>: "会立即
    /// 返还99个随机粉尘".
    /// </summary>
    public const int LuckyNumberDustRefund = 99;

    /// <summary>The roll that wins the chosen item outright (<c>NF_Z_T403</c>).</summary>
    public const int LuckyItemRoll = 176;

    /// <summary>The highest score a luck draw can roll, from <c>NF_Z_T3011</c>.</summary>
    public const int MaximumLuckScore = 1000;

    /// <summary>
    /// The rolled numbers that pay dusts back, from <c>NF_Z_T3015</c>. The client
    /// lists "111,222,333,444,555,666,777,888,999"; the event page adds 5555,
    /// which is not a 1–1000 score and so cannot be rolled.
    /// </summary>
    private static readonly int[] LuckyNumbers =
        [111, 222, 333, 444, 555, 666, 777, 888, 999];

    /// <summary>
    /// One thing the follower may ask for. <see cref="ItemId"/> is a real item
    /// template id, which is also the <c>SubID</c> the client prints for a single
    /// one; <see cref="Difficulty"/> orders the ladder the event page describes
    /// ("献礼次数越多，要求缴纳的道具难度将越高").
    /// </summary>
    public readonly record struct ZeusGiftRequirement(
        int ItemId,
        int Count,
        int Difficulty,
        string Label);

    /// <summary>
    /// Everything the follower can ask for. Every entry's <c>SubID</c> text exists
    /// in <c>NpcFunZeus.lua</c>'s first page, and the item ids match the client's
    /// own <c>ItemBaseAttribute.xml</c>.
    /// </summary>
    private static readonly ZeusGiftRequirement[] Requirements =
    [
        new(4230, 1, 1, "Level 1 Crystal"),
        new(3819, 1, 2, "Sicilian Spell"),
        new(4010, 1, 3, "Life Spring Water"),
        new(4040, 1, 4, "Magic Spring Water"),
        new(4150, 1, 5, "Small Money Bag"),
        new(4200, 1, 6, "Level 1 Ruby"),
        new(4210, 1, 7, "Level 1 Sapphire"),
        new(4220, 1, 8, "Level 1 Emerald"),
        new(4231, 1, 9, "Level 2 Crystal"),
        new(4230, 3, 10, "Level 1 Crystal x3"),
        new(4201, 1, 11, "Level 2 Ruby"),
        new(4211, 1, 12, "Level 2 Sapphire"),
        new(4221, 1, 13, "Level 2 Emerald"),
        new(4200, 2, 14, "Level 1 Ruby x2"),
        new(4210, 2, 15, "Level 1 Sapphire x2"),
        new(4220, 2, 16, "Level 1 Emerald x2")
    ];

    /// <summary>
    /// The highest difficulty a round may ask for. The ladder is spread over the
    /// twelve deliveries the weekend allows so that the first rounds are cheap and
    /// the last ones are not.
    /// </summary>
    public static int MaximumDifficultyForRound(int round) =>
        Math.Clamp(1 + (round * 2), 1, Requirements.Max(static r => r.Difficulty));

    /// <summary>
    /// Every requirement a round may draw. The client's own text says the pick is
    /// random ("该道具或装备为随机抽取"), which the capture agrees with: the first
    /// 2026-10-04 round asked for <c>4200</c> and the next three for <c>4230</c>.
    /// </summary>
    public static IReadOnlyList<ZeusGiftRequirement> RequirementsForRound(int round)
    {
        var maximum = MaximumDifficultyForRound(round);
        return [.. Requirements.Where(r => r.Difficulty <= maximum)];
    }

    /// <summary>
    /// The number the client prints for a requirement: the item id alone for one,
    /// <c>itemId * 10 + count</c> for two or three.
    /// </summary>
    public static int RequirementSubId(in ZeusGiftRequirement requirement) =>
        requirement.Count <= 1
            ? requirement.ItemId
            : checked((requirement.ItemId * 10) + requirement.Count);

    /// <summary>Whether a number is one the follower can ask for.</summary>
    public static bool IsRequirementSubId(int subId) =>
        Requirements.Any(r => RequirementSubId(r) == subId);

    /// <summary>
    /// The delivery progress line: <c>(round * 100 + tier) * 1000 + 9</c>.
    /// </summary>
    /// <remarks>
    /// The script prints the two fields in the opposite order from the way they are
    /// stored: <c>NpcFunZeus.lua:162</c> writes
    /// <c>NF_L0_Z1320 .. ((body - body % 100) / 100) .. NF_L0_Z1321 .. (body % 100) .. NF_L0_Z1322</c>,
    /// and <c>NF_L0_Z1320</c> ends in "你正在进行第" while <c>NF_L0_Z1321</c> opens
    /// "次,可以领到第". So the <b>high</b> pair is the delivery's ordinal
    /// ("第 N 次") and the <b>low</b> pair is the reward level ("可以领到第 M 级"),
    /// even though the level is the field the row stores as the tier.
    /// <para>
    /// In the field this showed up as the two numbers being swapped: a character
    /// three deliveries into the day who had just reset the level saw
    /// "第2次,可以领到第4级" instead of "第4次,可以领到第1级".
    /// </para>
    /// </remarks>
    public static int ProgressSubId(int round, int tier) =>
        checked((((round * 100) + tier) * 1000) + 9);

    /// <summary>
    /// The reward level: <c>tier * 10000 + 13</c>. The capture shows <c>10013</c>
    /// and <c>20013</c>.
    /// </summary>
    public static int RewardSubId(int tier) => checked((tier * 10000) + 13);

    /// <summary>
    /// The luck entry's rules line: <c>(chances + 1) * 10000 + 6</c>. The script
    /// prints the chances as <c>(SubID - 6) / 10000 - 1</c>.
    /// </summary>
    public static int LuckChancesSubId(int chances) =>
        checked(((chances + 1) * 10000) + 6);

    /// <summary>
    /// The chosen item's current highest score: <c>score * 10000 + 1</c>, which the
    /// script prints as <c>NF_Z_T3011</c> + score + <c>NF_Z_T3012</c>.
    /// </summary>
    public static int LuckHighestScoreSubId(int score) =>
        checked((score * 10000) + 1);

    /// <summary>
    /// The player's own score on the item: <c>score * 10000 + 2</c>. This is the
    /// one branch of the script's third page that also raises the item slot
    /// (<c>FirstWin_ItemBtn1</c>), so it is the page a draw is placed on.
    /// </summary>
    public static int LuckOwnScoreSubId(int score) =>
        checked((score * 10000) + 2);

    /// <summary>
    /// The score one draw rolled: <c>score * 10000 + 3</c>, printed as
    /// <c>NF_Z_T4021</c> + score + <c>NF_Z_T3014</c> and then closed.
    /// </summary>
    public static int LuckRolledSubId(int score) =>
        checked((score * 10000) + 3);

    /// <summary>
    /// The settled week's final highest score: <c>score * 10000 + 5</c>.
    /// </summary>
    public static int LuckFinalHighestSubId(int score) =>
        checked((score * 10000) + 5);

    /// <summary>
    /// The player's own final score, printed with the claim deadline:
    /// <c>score * 10000 + 4</c>.
    /// </summary>
    public static int LuckFinalOwnSubId(int score) =>
        checked((score * 10000) + 4);

    /// <summary>
    /// The "投出" clock: <c>(dayFlag * 100000 + secondsOfDay) * 1000 + 901</c>.
    /// The script reads the first digit back to choose 周六 or 周日 and the rest as
    /// seconds, so <paramref name="dayFlag"/> is one for Saturday and two for
    /// Sunday.
    /// </summary>
    public static int LuckPlacedAtSubId(int dayFlag, int secondsOfDay, bool fourthText) =>
        checked((((dayFlag * 100000) + secondsOfDay) * 1000) + (fourthText ? 902 : 901));

    /// <summary>
    /// Whether a roll is one of the lucky numbers that pay the dusts back.
    /// </summary>
    public static bool IsLuckyNumber(int score) => LuckyNumbers.Contains(score);

    /// <summary>
    /// One five-level band of the delivery reward's level curve.
    /// </summary>
    /// <param name="FirstLevel">The band's first level.</param>
    /// <param name="LastLevel">The band's last level, which is the last before the
    /// next band starts; the final band runs to the end of the curve.</param>
    /// <param name="BaseExperience">The first tier's experience at this level.</param>
    /// <param name="BaseTalentPoints">The first tier's talent points at this level.</param>
    public readonly record struct ZeusGiftDeliveryBand(
        int FirstLevel,
        int LastLevel,
        int BaseExperience,
        int BaseTalentPoints);

    /// <summary>
    /// The level the curve starts at, which is also the event's own entry level.
    /// </summary>
    public const int MinimumRewardLevel = 55;

    /// <summary>
    /// The level the curve ends at. A character past it is paid the last band.
    /// </summary>
    public const int MaximumRewardLevel = 140;

    /// <summary>Levels between two bands.</summary>
    public const int DeliveryBandLevels = 5;

    /// <summary>
    /// The delivery reward's level curve, one band per five levels, each holding the
    /// first tier's own values.
    /// </summary>
    /// <remarks>
    /// The two anchors are level 55 at 30,000 experience and 2 talent points and
    /// level 140 at 120,000 experience and 8 talent points, interpolated as
    /// <c>30000 + k * 90000/17</c> and <c>2 + floor(k * 6/17)</c> for band
    /// <c>k</c> = 0..17 and then rounded up to the next hundred so every value ends in
    /// two zeros, which is what rounds the eleventh band's 82,941 up to 83,000. The
    /// talent points only move six times across the whole curve, so the bands share
    /// them: 2 for the first three, then one more every third band, with the eighth
    /// point landing on the last band.
    /// </remarks>
    public static readonly ZeusGiftDeliveryBand[] DeliveryBands =
    [
        new(55, 59, 30_000, 2),
        new(60, 64, 35_300, 2),
        new(65, 69, 40_600, 2),
        new(70, 74, 45_900, 3),
        new(75, 79, 51_200, 3),
        new(80, 84, 56_500, 3),
        new(85, 89, 61_800, 4),
        new(90, 94, 67_100, 4),
        new(95, 99, 72_400, 4),
        new(100, 104, 77_700, 5),
        new(105, 109, 83_000, 5),
        new(110, 114, 88_300, 5),
        new(115, 119, 93_600, 6),
        new(120, 124, 98_900, 6),
        new(125, 129, 104_200, 6),
        new(130, 134, 109_500, 7),
        new(135, 139, 114_800, 7),
        new(140, 144, 120_000, 8)
    ];

    /// <summary>The band a level falls in, clamped to the curve.</summary>
    public static ZeusGiftDeliveryBand DeliveryBandForLevel(int level) =>
        DeliveryBands[Math.Clamp(
            (level - MinimumRewardLevel) / DeliveryBandLevels,
            0,
            DeliveryBands.Length - 1)];

    /// <summary>The first tier's experience at a level.</summary>
    public static int BaseDeliveryExperience(int level) =>
        DeliveryBandForLevel(level).BaseExperience;

    /// <summary>The first tier's talent points at a level.</summary>
    public static int BaseDeliveryTalentPoints(int level) =>
        DeliveryBandForLevel(level).BaseTalentPoints;

    /// <summary>
    /// What one delivery of a level pays, before the weekend doubles it. The tier
    /// ladder itself is unchanged: tier N pays N times the level's own base value, and
    /// the event page's own promise is only that the reward grows with the round
    /// ("献礼次数越多…获得的奖励也将越高").
    /// </summary>
    public static int ExperienceForTier(int level, int tier) =>
        Math.Clamp(tier, 1, WeekendGiftLimit) * BaseDeliveryExperience(level);

    /// <summary>Talent points one delivery of a level pays, before the weekend.</summary>
    public static int TalentPointsForTier(int level, int tier) =>
        Math.Clamp(tier, 1, WeekendGiftLimit) * BaseDeliveryTalentPoints(level);

    /// <summary>
    /// Praying stones one delivery pays. The saint prices the weekend exchange and
    /// the luck chances in stones, so the first rounds pay one and the later ones
    /// pay more.
    /// </summary>
    public static int PrayingStonesForTier(int tier) =>
        Math.Clamp(1 + (tier / 3), 1, 5);

    /// <summary>
    /// The multiplier a realm day carries: the weekend doubles everything.
    /// </summary>
    public static int RewardMultiplier(DateOnly realmDay) =>
        IsWeekend(realmDay) ? WeekendRewardMultiplier : 1;

    /// <summary>Whether the realm day is Saturday or Sunday.</summary>
    public static bool IsWeekend(DateOnly realmDay) =>
        realmDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Deliveries allowed on a realm day.</summary>
    public static int GiftLimit(DateOnly realmDay) =>
        IsWeekend(realmDay) ? WeekendGiftLimit : WeekdayGiftLimit;

    /// <summary>
    /// Whether the Monday-to-Friday rule on handing stones in is suspended.
    /// </summary>
    /// <remarks>
    /// A test switch, and off unless <c>GODSWAR_ZEUS_STONE_ANY_DAY=true</c>, so the
    /// shipped behaviour stays the one the client itself states. It exists because
    /// the deposit can otherwise only be exercised on five days out of seven.
    /// </remarks>
    public static bool StonesAcceptedEveryDay { get; } = string.Equals(
        Environment.GetEnvironmentVariable("GODSWAR_ZEUS_STONE_ANY_DAY"),
        "true",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether praying stones may be handed in: Monday 00:00 to Friday 23:55, which
    /// the client states as <c>NF_L0_Z1202</c> and refuses otherwise with
    /// <c>NF_L0_Z1306</c> ("只有在周一到周五才能缴纳祈祷石").
    /// </summary>
    public static bool AcceptsPrayingStones(DateOnly realmDay) =>
        StonesAcceptedEveryDay ||
        realmDay.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>
    /// Whether the weekend exchange is open: Saturday 12:00 to Sunday 23:55, which
    /// <c>NF_L0_Z1110</c> and the event page both state.
    /// </summary>
    public static bool IsExchangeWindow(DateOnly realmDay, TimeOnly realmTime)
    {
        if (realmDay.DayOfWeek == DayOfWeek.Saturday)
        {
            return realmTime >= new TimeOnly(12, 0);
        }
        if (realmDay.DayOfWeek == DayOfWeek.Sunday)
        {
            return realmTime <= new TimeOnly(23, 55);
        }
        return false;
    }

    /// <summary>
    /// Whether the luck contest is open: Saturday 12:00 to Sunday 12:00, which
    /// <c>NF_Z_T2</c> states and <c>NF_Z_T300</c> refuses otherwise.
    /// </summary>
    public static bool IsLuckWindow(DateOnly realmDay, TimeOnly realmTime)
    {
        if (realmDay.DayOfWeek == DayOfWeek.Saturday)
        {
            return realmTime >= new TimeOnly(12, 0);
        }
        if (realmDay.DayOfWeek == DayOfWeek.Sunday)
        {
            return realmTime <= new TimeOnly(12, 0);
        }
        return false;
    }

    /// <summary>
    /// Whether the week's results can be claimed: Sunday 12:00 to 23:55, the window
    /// <c>NF_Z_T3017</c> gives ("本周日晚23:55是最后的领奖期限").
    /// </summary>
    public static bool IsClaimWindow(DateOnly realmDay, TimeOnly realmTime) =>
        realmDay.DayOfWeek == DayOfWeek.Sunday &&
        realmTime >= new TimeOnly(12, 0) &&
        realmTime <= new TimeOnly(23, 55);

    /// <summary>
    /// The odds one ordinary dust exchange succeeds. The client only promises that
    /// failure happens and that the odds worsen with the prize
    /// (<c>NF_L0_Z1518</c>), so both rates are server-side choices.
    /// </summary>
    public const int OrdinaryExchangeSuccessPercent = 70;

    /// <summary>The odds one limited dust exchange succeeds.</summary>
    public const int LimitedExchangeSuccessPercent = 40;

    /// <summary>One prize a shelf or a luck entry can hand over.</summary>
    public readonly record struct ZeusGiftPrize(int ItemId, int Count, string Label);

    /// <summary>
    /// The ordinary shelf, transcribed from the script's third page
    /// (<c>SubID</c> 1207–1211) and the event page's list of ordinary rewards:
    /// "一级水晶、任务卷轴、二级水晶、西西里岛符文、七天时限的时装". Every entry is
    /// untradeable per the page, which is why the server grants them bound.
    /// </summary>
    private static readonly ZeusGiftPrize[] OrdinaryPrizes =
    [
        new(4230, 1, "GF_ZEUS_ORDINARY_CRYSTAL_1"),
        new(4838, 1, "GF_ZEUS_ORDINARY_QUEST_SCROLL"),
        new(4231, 1, "GF_ZEUS_ORDINARY_CRYSTAL_2"),
        new(3819, 1, "GF_ZEUS_ORDINARY_RUNE"),
        new(8000, 1, "GF_ZEUS_ORDINARY_FASHION_7D")
    ];

    /// <summary>
    /// The limited shelf, transcribed from the script's third page
    /// (<c>SubID</c> 4000–4006) and the event page's list of limited rewards:
    /// "中级晶光碎片、三级水晶、包裹开启石、火焰颗粒、次级晶光碎片、西西里岛符文、紫宝石".
    /// </summary>
    private static readonly ZeusGiftPrize[] LimitedPrizes =
    [
        new(9962, 1, "GF_ZEUS_LIMITED_QUARTZ_3"),
        new(9961, 1, "GF_ZEUS_LIMITED_QUARTZ_2"),
        new(3819, 10, "GF_ZEUS_LIMITED_RUNE_10"),
        new(4103, 1, "GF_ZEUS_LIMITED_PARCEL_STONE"),
        new(9990, 1, "GF_ZEUS_LIMITED_FLAME_SPARK"),
        new(4252, 1, "GF_ZEUS_LIMITED_AMETHYST_3"),
        new(4232, 5, "GF_ZEUS_LIMITED_CRYSTAL_3")
    ];

    /// <summary>
    /// The eight goods the luck page offers, transcribed from the script's second
    /// page (<c>NF_Z_B200</c>–<c>NF_Z_B207</c>). Seven of them are the limited
    /// shelf's prizes; the eighth is the rebirth spring water
    /// (<c>Pet10107</c>, item 10107).
    /// </summary>
    private static readonly ZeusGiftPrize[] LuckGoods =
    [
        new(9962, 1, "NF_Z_B200"),
        new(9961, 1, "NF_Z_B201"),
        new(3819, 10, "NF_Z_B202"),
        new(4103, 1, "NF_Z_B203"),
        new(9990, 1, "NF_Z_B204"),
        new(4252, 1, "NF_Z_B205"),
        new(4232, 5, "NF_Z_B206"),
        new(10107, 1, "NF_Z_B207")
    ];

    public static IReadOnlyList<ZeusGiftPrize> Ordinary => OrdinaryPrizes;

    public static IReadOnlyList<ZeusGiftPrize> Limited => LimitedPrizes;

    public static IReadOnlyList<ZeusGiftPrize> Luck => LuckGoods;

    /// <summary>
    /// How many of one limited prize a realm and camp hands out in a week, which is
    /// what makes the shelf first come, first served.
    /// </summary>
    /// <remarks>
    /// The stock counts per realm <b>and</b> per camp: Athens' limited shelf and
    /// Sparta's are kept separately, so one side running a prize out leaves the other
    /// side's shelf untouched.
    /// </remarks>
    public const int LimitedStockPerWeek = 5;

    /// <summary>
    /// The level-three crystal, the one limited prize the event stocks deeper than the
    /// rest.
    /// </summary>
    public const int LevelThreeCrystalItemId = 4232;

    /// <summary>How many level-three crystals a week stocks.</summary>
    public const int LevelThreeCrystalStockPerWeek = 25;

    /// <summary>The week's stock of one limited prize, per realm and camp.</summary>
    public static int LimitedStockForItem(int itemId) =>
        itemId == LevelThreeCrystalItemId
            ? LevelThreeCrystalStockPerWeek
            : LimitedStockPerWeek;

    /// <summary>The ordinary shelf's button numbers, in the script's own order.</summary>
    public static readonly int[] OrdinaryShelfButtons = [1207, 1208, 1209, 1210, 1211];

    /// <summary>The limited shelf's button numbers, in the script's own order.</summary>
    public static readonly int[] LimitedShelfButtons = [4000, 4001, 4002, 4003, 4004, 4005, 4006];

    /// <summary>The luck page's goods buttons, in the script's own order.</summary>
    public static readonly int[] LuckGoodButtons = [200, 201, 202, 203, 204, 205, 206, 207];

    /// <summary>
    /// The twenty-one dusts the client's own two lists name
    /// (<c>NF_L0_Z1213</c>'s nineteen and the two extra templates in the client's
    /// item table), which is what "所有粉尘都可以用来兑换好东西" means.
    /// </summary>
    private static readonly int[] DustItemIds =
    [
        9900, 9901, 9902, 9903, 9904, 9905, 9906, 9907, 9908,
        9910, 9911, 9912, 9913, 9914, 9915, 9916, 9917, 9918, 9919
    ];

    /// <summary>Whether an item id is one of the event's dusts.</summary>
    public static bool IsDust(int itemId) => DustItemIds.Contains(itemId);

    public static IReadOnlyList<int> Dusts => DustItemIds;

    /// <summary>
    /// The prize a shelf button stands for, or null when the number is not a shelf
    /// button. The two shelves index their own lists.
    /// </summary>
    public static ZeusGiftPrize? ResolveOrdinaryPrize(int buttonSubId)
    {
        var index = Array.IndexOf(OrdinaryShelfButtons, buttonSubId);
        return index < 0 ? null : OrdinaryPrizes[index];
    }

    /// <summary>The limited shelf's prize for a button number.</summary>
    public static ZeusGiftPrize? ResolveLimitedPrize(int buttonSubId)
    {
        var index = Array.IndexOf(LimitedShelfButtons, buttonSubId);
        return index < 0 ? null : LimitedPrizes[index];
    }

    /// <summary>The luck page's good for a button number.</summary>
    public static ZeusGiftPrize? ResolveLuckGood(int buttonSubId)
    {
        var index = Array.IndexOf(LuckGoodButtons, buttonSubId);
        return index < 0 ? null : LuckGoods[index];
    }
}
