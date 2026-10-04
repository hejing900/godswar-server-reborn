using Godswar.Server.Application.ZeusGift;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Guards the two things that decide whether a Zeus gift window works at all: that
/// every number a reply carries is one the client's own <c>NpcFunZeus.lua</c> draws
/// on the page that reply lands on, and that the numbers the server encodes survive
/// the script's own inverse formulas.
/// </summary>
/// <remarks>
/// The client passes its page counter to the script as <c>Index</c>, and that
/// counter is the ordinal of the reply inside one open window - not the position of
/// the function number in the packed list. The capture settles it: the follower
/// opens with <c>list=[26]</c> (a single entry) and answers its second and third
/// replies with numbers that only exist on pages two and three. A reply carrying a
/// number from another page draws nothing at all, so this check is the regression
/// guard for that whole class of mistake.
/// </remarks>
internal static class ZeusGiftProtocolChecks
{
    public const string CheckName =
        "Zeus gift replies stay on the client script page they are drawn from";

    /// <summary>
    /// The pages of every <c>SubID</c> the event can send, transcribed from
    /// <c>NpcFunZeus.lua</c>. The block boundaries in that file are
    /// <c>Index == 1</c> at line 14, <c>== 2</c> at 166, <c>== 3</c> at 294,
    /// <c>== 4</c> at 582 and <c>== 5</c> at 677.
    /// </summary>
    /// <remarks>
    /// A few numbers are drawn by two pages, which the script really does:
    /// <c>5105</c> sits in the first and second blocks, <c>207</c> in the second and
    /// third, and <c>1103</c> in the third and fourth. Those are listed with both.
    /// </remarks>
    private static readonly Dictionary<int, int[]> PagesOf = BuildPageMap();

    public static Task RunAsync()
    {
        CheckFunctionList();
        CheckClientStatedLimits();
        CheckEncodings();
        CheckRequirementLadder();
        CheckShelves();
        CheckReplyPages();
        CheckStoneEconomy();
        CheckLuckSettlement();
        CheckDeliveryLevelCurve();
        CheckAcquisitionProjection();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The grant notice has to keep the native acquisition's own shape: one
    /// <c>0x27C9</c> item for every at-most-99 chunk, the client's scratch slot
    /// evicted after each one, and the authoritative bag pages last so the client
    /// stops showing its optimistic bag.
    /// </summary>
    /// <summary>
    /// The praying-stone economy: what one stone banks, how many exchanges the week
    /// buys, and that a week is one row keyed on Monday so Monday clears it.
    /// </summary>
    private static void CheckStoneEconomy()
    {
        Check.Equal(
            8_000,
            ZeusGiftPolicy.ExperiencePerPrayingStone,
            "one praying stone banks eight thousand experience");
        Check.Equal(
            1,
            ZeusGiftPolicy.TalentPointsPerPrayingStone,
            "one praying stone banks one talent point");

        // Five exchanges for everybody, then one more for every five stones.
        var expected = new[] { 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7 };
        for (var stones = 0; stones < expected.Length; stones++)
        {
            Check.Equal(
                expected[stones],
                ZeusGiftPolicy.ExchangesForStones(stones),
                $"{stones} stones buy {expected[stones]} exchanges");
        }

        // The limited shelf stocks per prize: the level-three crystal five times as
        // deep as everything else. The ordinary shelf is unlimited.
        Check.Equal(
            5,
            ZeusGiftPolicy.LimitedStockForItem(9962),
            "a quartz shelf stocks five");
        Check.Equal(
            25,
            ZeusGiftPolicy.LimitedStockForItem(ZeusGiftPolicy.LevelThreeCrystalItemId),
            "the level-three crystal shelf stocks twenty-five");
        foreach (var prize in ZeusGiftPolicy.Limited)
        {
            Check.Equal(
                prize.ItemId == ZeusGiftPolicy.LevelThreeCrystalItemId ? 25 : 5,
                ZeusGiftPolicy.LimitedStockForItem(prize.ItemId),
                $"{prize.Label} stocks its own depth");
        }

        var week = new DateOnly(2026, 10, 5);
        Check.True(
            week.DayOfWeek == DayOfWeek.Monday,
            "the week key this state is built on is a Monday");
        var fresh = ZeusGiftWeeklyState.Fresh(week);
        Check.True(!fresh.HasPendingReward, "a fresh week has nothing banked");
        Check.Equal(5, fresh.ExchangeLimit, "a fresh week still has its five exchanges");

        // Two stones: one week's bank, six exchanges, two luck chances.
        var banked = fresh with
        {
            StonesDeposited = 2,
            PendingExperience = 2 * ZeusGiftPolicy.ExperiencePerPrayingStone,
            PendingTalentPoints = 2 * ZeusGiftPolicy.TalentPointsPerPrayingStone
        };
        Check.True(banked.HasPendingReward, "two stones bank a reward");
        Check.Equal(16_000L, banked.PendingExperience, "two stones bank 16,000 experience");
        Check.Equal(2, banked.PendingTalentPoints, "two stones bank two talent points");
        Check.Equal(5, banked.ExchangeLimit, "two stones still buy the base five exchanges");
        Check.Equal(2, banked.LuckChances, "two stones buy two luck chances");

        // Five stones: six exchanges, and the bank grows with them.
        var five = fresh with
        {
            StonesDeposited = 5,
            PendingExperience = 5 * ZeusGiftPolicy.ExperiencePerPrayingStone,
            PendingTalentPoints = 5 * ZeusGiftPolicy.TalentPointsPerPrayingStone
        };
        Check.Equal(6, five.ExchangeLimit, "five stones buy a sixth exchange");
        Check.Equal(40_000L, five.PendingExperience, "five stones bank 40,000 experience");
        Check.Equal(5, five.PendingTalentPoints, "five stones bank five talent points");

        // The claim empties the bank without touching what bought it.
        var claimed = five with
        {
            ClaimsTaken = 1,
            PendingExperience = 0,
            PendingTalentPoints = 0
        };
        Check.True(!claimed.HasPendingReward, "the claim empties the bank");
        Check.Equal(
            6,
            claimed.ExchangeLimit,
            "claiming does not spend the exchanges the stones bought");
        Check.Equal(
            DeriveClaimExperience(5),
            DeriveClaimExperience(5),
            "the banked figures stay reproducible");
    }

    /// <summary>What a week's stones bank, the way the store adds it up.</summary>
    private static long DeriveClaimExperience(int stones) =>
        (long)stones * ZeusGiftPolicy.ExperiencePerPrayingStone;

    /// <summary>
    /// The contest's settled page, which the reference answers for an untouched board.
    /// </summary>
    private static void CheckLuckSettlement()
    {
        var settled = GameClientHandler.BuildZeusGiftLuckSettlement(highestScore: 0, ownScore: 0);
        Check.True(
            settled.Length == 3 &&
            settled[0] == 5 &&
            settled[1] == 4 &&
            settled[2] == GameClientHandler.ZeusGiftLuckClaimButton,
            "a zero board settles as [4, 5, 302], which is the captured reply");

        var scored = GameClientHandler.BuildZeusGiftLuckSettlement(highestScore: 812, ownScore: 640);
        Check.True(
            scored[0] == (812 * 10000) + 5 &&
            scored[1] == (640 * 10000) + 4 &&
            scored[2] == 302 &&
            scored[0] / 10000 == 812 &&
            scored[1] / 10000 == 640,
            "the final highest and the own score stay in their own fields");

        // The button is not conditional: the verdict is settled on the click, which is
        // what 408 (tied but not first) and 409 (not the highest) answer.
        foreach (var (highest, own) in new[] { (0, 0), (5, 5), (900, 100), (1000, 1000) })
        {
            Check.True(
                GameClientHandler.BuildZeusGiftLuckSettlement(highest, own)[2] == 302,
                $"the claim button is drawn for highest={highest} own={own}");
        }

        Check.Equal(
            408,
            GameClientHandler.ZeusGiftLuckTiedPage[0],
            "a tied claim answers the client's own 408 line");
        Check.Equal(
            409,
            GameClientHandler.ZeusGiftLuckNotWinnerPage[0],
            "a lower score answers the client's own 409 line");
    }

    /// <summary>
    /// The delivery reward's level curve: one band per five levels, from level 55's
    /// 30,000 experience and 2 talent points to level 140's 120,000 and 8.
    /// </summary>
    private static void CheckDeliveryLevelCurve()
    {
        Check.Equal(
            18,
            ZeusGiftPolicy.DeliveryBands.Length,
            "the curve is eighteen five-level bands");
        Check.True(
            ZeusGiftPolicy.DeliveryBands[0].BaseExperience == 30_000 &&
            ZeusGiftPolicy.DeliveryBands[0].BaseTalentPoints == 2,
            "level 55 pays 30,000 experience and 2 talent points");
        var last = ZeusGiftPolicy.DeliveryBands[^1];
        Check.True(
            last.BaseExperience == 120_000 && last.BaseTalentPoints == 8,
            "level 140 pays 120,000 experience and 8 talent points");

        // Every band's experience ends in two zeros, because the interpolation is
        // rounded up to the next hundred - which is what takes 82,941 to 83,000.
        foreach (var band in ZeusGiftPolicy.DeliveryBands)
        {
            Check.True(
                band.BaseExperience % 100 == 0,
                $"band {band.FirstLevel} stocks {band.BaseExperience}, which ends in two zeros");
        }

        // The bands tile 55..140 with no gap and never go backwards.
        for (var index = 0; index < ZeusGiftPolicy.DeliveryBands.Length; index++)
        {
            var band = ZeusGiftPolicy.DeliveryBands[index];
            Check.True(
                band.LastLevel - band.FirstLevel + 1 == ZeusGiftPolicy.DeliveryBandLevels ||
                index == ZeusGiftPolicy.DeliveryBands.Length - 1,
                $"band {band.FirstLevel} spans five levels");
            if (index > 0)
            {
                var previous = ZeusGiftPolicy.DeliveryBands[index - 1];
                Check.True(
                    band.FirstLevel == previous.LastLevel + 1,
                    $"band {band.FirstLevel} follows {previous.LastLevel}");
                Check.True(
                    band.BaseExperience > previous.BaseExperience &&
                    band.BaseTalentPoints >= previous.BaseTalentPoints,
                    $"band {band.FirstLevel} pays more than {previous.FirstLevel}");
            }
        }

        // The boundaries a player actually crosses, including the clamp past 140.
        Check.Equal(30_000, ZeusGiftPolicy.BaseDeliveryExperience(55), "level 55");
        Check.Equal(30_000, ZeusGiftPolicy.BaseDeliveryExperience(59), "the last level of band one");
        Check.Equal(35_300, ZeusGiftPolicy.BaseDeliveryExperience(60), "band two starts at 60");
        Check.Equal(40_600, ZeusGiftPolicy.BaseDeliveryExperience(69), "band three ends at 69");
        Check.Equal(45_900, ZeusGiftPolicy.BaseDeliveryExperience(70), "band four starts at 70");
        Check.Equal(83_000, ZeusGiftPolicy.BaseDeliveryExperience(105), "82,941 rounds up to 83,000");
        Check.Equal(114_800, ZeusGiftPolicy.BaseDeliveryExperience(139), "the last full band");
        Check.Equal(120_000, ZeusGiftPolicy.BaseDeliveryExperience(140), "level 140");
        Check.Equal(
            120_000,
            ZeusGiftPolicy.BaseDeliveryExperience(200),
            "a level past the curve is paid the last band");
        Check.Equal(
            30_000,
            ZeusGiftPolicy.BaseDeliveryExperience(1),
            "a level below the curve is paid the first band");

        Check.Equal(2, ZeusGiftPolicy.BaseDeliveryTalentPoints(55), "two points at level 55");
        Check.Equal(3, ZeusGiftPolicy.BaseDeliveryTalentPoints(70), "three points from level 70");
        Check.Equal(8, ZeusGiftPolicy.BaseDeliveryTalentPoints(140), "eight points at level 140");

        // The tier ladder itself is unchanged: tier N is N times the level's base.
        Check.Equal(
            83_000,
            ZeusGiftPolicy.ExperienceForTier(level: 105, tier: 1),
            "the first tier pays the base");
        Check.Equal(
            249_000,
            ZeusGiftPolicy.ExperienceForTier(level: 105, tier: 3),
            "the third tier pays three times the base");
        Check.Equal(
            15,
            ZeusGiftPolicy.TalentPointsForTier(level: 105, tier: 3),
            "the third tier pays three times the base talent points");
    }

    private static void CheckAcquisitionProjection()
    {
        var character = new GameCharacter { Id = 1, Name = "ZeusCheck" };

        var quiet = GameClientHandler.BuildZeusGiftAcquisitionProjection(character, []);
        Check.True(
            quiet.Count == PacketBuilder.KitBagDetailPages(character).Length +
                PacketBuilder.KitBagSlotIndexes(character).Length,
            "a refresh with nothing granted announces nothing and only restores the bag");

        var announced = GameClientHandler.BuildZeusGiftAcquisitionProjection(
            character,
            [(ZeusGiftPolicy.PrayingStoneItemId, 1), (ZeusGiftPolicy.WineItemId, 1)]);
        var clears = announced.Count(packet =>
            packet.AsSpan().SequenceEqual(PacketBuilder.StorageItemKitBagDelete(0)));
        var expectedTail = PacketBuilder.KitBagDetailPages(character)
            .Concat(PacketBuilder.KitBagSlotIndexes(character))
            .ToArray();
        var tail = announced.Skip(announced.Count - expectedTail.Length).ToArray();
        Check.True(
            clears == 3,
            "one scratch eviction opens the batch and one follows each of the two grants");
        Check.True(
            tail.Length == expectedTail.Length &&
            tail.Zip(expectedTail).All(pair => pair.First.AsSpan().SequenceEqual(pair.Second)),
            "the authoritative bag pages close the batch");

        var chunked = GameClientHandler.BuildZeusGiftAcquisitionProjection(
            character,
            [(ZeusGiftPolicy.PrayingStoneItemId, 200)]);
        Check.True(
            chunked.Count >
                GameClientHandler.BuildZeusGiftAcquisitionProjection(
                    character, [(ZeusGiftPolicy.PrayingStoneItemId, 99)]).Count,
            "a quantity over ninety-nine is split into more than one acquisition");

        var mixed = GameClientHandler.BuildZeusGiftAcquisitionProjection(
            character,
            [(0, 5), (ZeusGiftPolicy.PrayingStoneItemId, 0)]);
        Check.True(
            mixed.Count == quiet.Count,
            "an entry with no item or no quantity announces nothing");
    }

    private static void CheckFunctionList()
    {
        Check.True(
            GameClientHandler.ZeusGiftFunctionList.SequenceEqual([26]),
            "both Zeus endpoints open with the single function number 26");
        Check.Equal(
            55,
            GameClientHandler.ZeusGiftMinimumLevel,
            "the level both endpoints turn away is the client's own 55");
        Check.True(
            GameClientHandler.ZeusGiftLevelReply.SequenceEqual([100]),
            "the level gate answers with the first page's NF_L0_Z100 line");
        Check.True(
            GameClientHandler.ZeusGiftFollowerDeliveryEntries.SequenceEqual([1001, 1002, 1003]),
            "the follower's own delivery entries are the capture's 1001, 1002 and 1003");
    }

    private static void CheckClientStatedLimits()
    {
        Check.Equal(10, ZeusGiftPolicy.WeekdayGiftLimit, "ten deliveries a weekday");
        Check.Equal(
            12,
            ZeusGiftPolicy.WeekendGiftLimit,
            "twelve deliveries on a weekend day, from NF_L0_Z1401");
        Check.Equal(
            10,
            ZeusGiftPolicy.CrystalSubstituteCount,
            "ten level-one crystals stand in for any requirement, from NF_L0_Z1512");
        Check.Equal(
            20,
            ZeusGiftPolicy.DustPerExchange,
            "a dust exchange costs twenty of one kind, from NF_L0_Z1213");
        Check.Equal(
            10,
            ZeusGiftPolicy.DustPerLuckDraw,
            "a luck draw costs ten dusts, from NF_Z_T3011");
        Check.Equal(
            99,
            ZeusGiftPolicy.LuckyNumberDustRefund,
            "a lucky number pays ninety-nine dusts back, from NF_Z_T3015");
        Check.Equal(1000, ZeusGiftPolicy.MaximumLuckScore, "a luck score runs to 1000");

        Check.True(
            ZeusGiftPolicy.GiftLimit(new DateOnly(2026, 10, 5)) == 10 &&
            ZeusGiftPolicy.GiftLimit(new DateOnly(2026, 10, 3)) == 12 &&
            ZeusGiftPolicy.GiftLimit(new DateOnly(2026, 10, 4)) == 12,
            "Monday allows ten deliveries and Saturday and Sunday allow twelve");
        Check.Equal(
            2,
            ZeusGiftPolicy.RewardMultiplier(new DateOnly(2026, 10, 4)),
            "a Sunday doubles the delivery rewards");

        // 2026-10-05 is a Monday, 2026-10-10 a Saturday, 2026-10-11 a Sunday.
        // GODSWAR_ZEUS_STONE_ANY_DAY suspends the weekday rule for testing, so the
        // shipped rule is what is asserted when the switch is off.
        if (ZeusGiftPolicy.StonesAcceptedEveryDay)
        {
            Check.True(
                ZeusGiftPolicy.AcceptsPrayingStones(new DateOnly(2026, 10, 10)) &&
                ZeusGiftPolicy.AcceptsPrayingStones(new DateOnly(2026, 10, 11)),
                "the test switch lets stones in on the weekend");
        }
        else
        {
            Check.True(
                ZeusGiftPolicy.AcceptsPrayingStones(new DateOnly(2026, 10, 5)) &&
                ZeusGiftPolicy.AcceptsPrayingStones(new DateOnly(2026, 10, 9)) &&
                !ZeusGiftPolicy.AcceptsPrayingStones(new DateOnly(2026, 10, 10)) &&
                !ZeusGiftPolicy.AcceptsPrayingStones(new DateOnly(2026, 10, 11)),
                "praying stones are taken Monday to Friday and refused on the weekend");
        }

        Check.True(
            !ZeusGiftPolicy.IsExchangeWindow(new DateOnly(2026, 10, 10), new TimeOnly(11, 59)) &&
            ZeusGiftPolicy.IsExchangeWindow(new DateOnly(2026, 10, 10), new TimeOnly(12, 0)) &&
            ZeusGiftPolicy.IsExchangeWindow(new DateOnly(2026, 10, 11), new TimeOnly(23, 55)) &&
            !ZeusGiftPolicy.IsExchangeWindow(new DateOnly(2026, 10, 11), new TimeOnly(23, 56)) &&
            !ZeusGiftPolicy.IsExchangeWindow(new DateOnly(2026, 10, 9), new TimeOnly(20, 0)),
            "the dust exchange runs Saturday noon to Sunday 23:55");

        Check.True(
            !ZeusGiftPolicy.IsLuckWindow(new DateOnly(2026, 10, 10), new TimeOnly(11, 59)) &&
            ZeusGiftPolicy.IsLuckWindow(new DateOnly(2026, 10, 10), new TimeOnly(12, 0)) &&
            ZeusGiftPolicy.IsLuckWindow(new DateOnly(2026, 10, 11), new TimeOnly(12, 0)) &&
            !ZeusGiftPolicy.IsLuckWindow(new DateOnly(2026, 10, 11), new TimeOnly(12, 1)),
            "the luck contest runs Saturday noon to Sunday noon");

        Check.True(
            !ZeusGiftPolicy.IsClaimWindow(new DateOnly(2026, 10, 11), new TimeOnly(11, 59)) &&
            ZeusGiftPolicy.IsClaimWindow(new DateOnly(2026, 10, 11), new TimeOnly(12, 0)) &&
            ZeusGiftPolicy.IsClaimWindow(new DateOnly(2026, 10, 11), new TimeOnly(23, 55)) &&
            !ZeusGiftPolicy.IsClaimWindow(new DateOnly(2026, 10, 11), new TimeOnly(23, 56)),
            "the Sunday prize pickup runs noon to 23:55, from NF_Z_T3017");
    }

    private static void CheckEncodings()
    {
        // The capture's own three progress lines: (tier * 100 + round) * 1000 + 9.
        Check.Equal(101009, ZeusGiftPolicy.ProgressSubId(1, 1), "captured progress line, round 1");
        Check.Equal(202009, ZeusGiftPolicy.ProgressSubId(2, 2), "captured progress line, round 2");
        Check.Equal(303009, ZeusGiftPolicy.ProgressSubId(3, 3), "captured progress line, round 3");

        // NpcFunZeus.lua:162 reads the tier and the round back out of that number.
        for (var tier = 1; tier <= ZeusGiftPolicy.WeekendGiftLimit; tier++)
        {
            var subId = ZeusGiftPolicy.ProgressSubId(tier, tier);
            var body = (subId - 9) / 1000;
            Check.True(
                (body - (body % 100)) / 100 == tier && body % 100 == tier,
                $"the script prints round {tier} and level {tier} back out of {subId}");
        }

        // The script prints the delivery's ordinal after "你正在进行第" and the
        // reward level after "次,可以领到第", which is the opposite order from the
        // way the row stores them. The field showed the swap as "第2次,第4级" for a
        // character who was four deliveries in and paying level two.
        for (var round = 1; round <= ZeusGiftPolicy.WeekendGiftLimit; round++)
        {
            for (var tier = 1; tier <= ZeusGiftPolicy.WeekendGiftLimit; tier++)
            {
                var subId = ZeusGiftPolicy.ProgressSubId(round, tier);
                var body = (subId - 9) / 1000;
                Check.True(
                    (body - (body % 100)) / 100 == round && body % 100 == tier,
                    $"round {round} and level {tier} stay in their own field of {subId}");
            }
        }
        Check.Equal(
            402009,
            ZeusGiftPolicy.ProgressSubId(round: 4, tier: 2),
            "the fourth delivery paying level two is the field's own 第4次/第2级 line");

        // NpcFunZeus.lua:516 renders the reward level as (SubID - 13) / 10000, and
        // the capture shows 10013 and 20013.
        Check.Equal(10013, ZeusGiftPolicy.RewardSubId(1), "captured reward line, tier 1");
        Check.Equal(20013, ZeusGiftPolicy.RewardSubId(2), "captured reward line, tier 2");
        for (var tier = 1; tier <= ZeusGiftPolicy.WeekendGiftLimit; tier++)
        {
            var subId = ZeusGiftPolicy.RewardSubId(tier);
            Check.True(
                subId % 10000 == 13 && (subId - 13) / 10000 == tier,
                $"the script prints reward tier {tier} back out of {subId}");
        }

        // The luck lines, each read back the way the script reads it.
        Check.True(
            ZeusGiftPolicy.LuckRolledSubId(777) % 10000 == 3 &&
            (ZeusGiftPolicy.LuckRolledSubId(777) - 3) / 10000 == 777,
            "NF_Z_T4021 prints the rolled score back out of a tail-3 number");
        Check.True(
            ZeusGiftPolicy.LuckOwnScoreSubId(500) % 10000 == 2 &&
            (ZeusGiftPolicy.LuckOwnScoreSubId(500) - 2) / 10000 == 500,
            "NF_Z_T3013 prints the player's own score back out of a tail-2 number");
        Check.True(
            ZeusGiftPolicy.LuckHighestScoreSubId(500) % 10000 == 1 &&
            (ZeusGiftPolicy.LuckHighestScoreSubId(500) - 1) / 10000 == 500,
            "NF_Z_T3011 prints an item's highest score back out of a tail-1 number");
        Check.True(
            ZeusGiftPolicy.LuckFinalOwnSubId(500) % 10000 == 4 &&
            ZeusGiftPolicy.LuckFinalHighestSubId(500) % 10000 == 5,
            "the settled lines use the script's own tails 4 and 5");
        Check.True(
            ZeusGiftPolicy.LuckChancesSubId(3) % 10000 == 6 &&
            (ZeusGiftPolicy.LuckChancesSubId(3) - 6) / 10000 - 1 == 3,
            "NF_Z_T2 prints the chances back out of a tail-6 number");

        // The "投出" clock: the script takes the first digit of the whole SubID for
        // 周六/周日, then divides by 1000 and takes the digits after the first of the
        // quotient as seconds of the day.
        var saturday = ZeusGiftPolicy.LuckPlacedAtSubId(dayFlag: 1, secondsOfDay: 3600, fourthText: false);
        Check.True(
            saturday % 1000 == 901 &&
            saturday.ToString().StartsWith('1') &&
            int.Parse(((saturday - 901) / 1000).ToString()[1..]) == 3600,
            "a Saturday throw encodes its clock behind 901 and the leading 1");
        var sunday = ZeusGiftPolicy.LuckPlacedAtSubId(dayFlag: 2, secondsOfDay: 3600, fourthText: true);
        Check.True(
            sunday % 1000 == 902 &&
            sunday.ToString().StartsWith('2') &&
            int.Parse(((sunday - 902) / 1000).ToString()[1..]) == 3600,
            "a Sunday throw uses 902 and the leading 2");
        Check.True(
            ZeusGiftPolicy.LuckPlacedAtSubId(dayFlag: 1, secondsOfDay: 86399, fourthText: false)
                .ToString().StartsWith('1') &&
            ZeusGiftPolicy.LuckPlacedAtSubId(dayFlag: 2, secondsOfDay: 86399, fourthText: false)
                .ToString().StartsWith('2'),
            "the day flag survives the last second of the day");

        Check.True(
            ZeusGiftPolicy.IsLuckyNumber(111) &&
            ZeusGiftPolicy.IsLuckyNumber(999) &&
            !ZeusGiftPolicy.IsLuckyNumber(176) &&
            !ZeusGiftPolicy.IsLuckyNumber(1000) &&
            !ZeusGiftPolicy.IsLuckyNumber(5555),
            "the nine lucky numbers pay dusts back and 176 is the item roll instead");
    }

    private static void CheckRequirementLadder()
    {
        var previous = 0;
        for (var round = 1; round <= ZeusGiftPolicy.WeekendGiftLimit; round++)
        {
            var maximum = ZeusGiftPolicy.MaximumDifficultyForRound(round);
            Check.True(
                maximum >= previous,
                $"round {round}'s requirement pool is never easier than round {round - 1}'s");
            previous = maximum;

            var pool = ZeusGiftPolicy.RequirementsForRound(round);
            Check.True(pool.Count > 0, $"round {round} can draw at least one requirement");
            foreach (var requirement in pool)
            {
                var subId = ZeusGiftPolicy.RequirementSubId(requirement);
                Check.True(
                    ZeusGiftPolicy.IsRequirementSubId(subId),
                    $"requirement {requirement.Label} encodes to a number the policy knows");

                // The script prints one as the item id and several as id * 10 + count,
                // which NF_L0_Z42002 ("2个1级红宝石") and NF_L0_Z42303 ("3个1级水晶")
                // both confirm.
                var expected = requirement.Count <= 1
                    ? requirement.ItemId
                    : (requirement.ItemId * 10) + requirement.Count;
                Check.Equal(expected, subId, $"requirement {requirement.Label} encoding");
            }
        }

        Check.True(
            ZeusGiftPolicy.RequirementsForRound(1).All(requirement => requirement.Difficulty <= 3) &&
            ZeusGiftPolicy.RequirementsForRound(1).Count <
                ZeusGiftPolicy.RequirementsForRound(ZeusGiftPolicy.WeekendGiftLimit).Count,
            "the first delivery draws from a smaller, cheaper pool than the twelfth");
    }

    private static void CheckShelves()
    {
        Check.Equal(
            ZeusGiftPolicy.OrdinaryShelfButtons.Length,
            ZeusGiftPolicy.Ordinary.Count,
            "the ordinary shelf has one prize per button");
        Check.Equal(
            ZeusGiftPolicy.LimitedShelfButtons.Length,
            ZeusGiftPolicy.Limited.Count,
            "the limited shelf has one prize per button");
        Check.Equal(
            ZeusGiftPolicy.LuckGoodButtons.Length,
            ZeusGiftPolicy.Luck.Count,
            "the luck page has one good per button");

        for (var index = 0; index < ZeusGiftPolicy.OrdinaryShelfButtons.Length; index++)
        {
            var prize = ZeusGiftPolicy.ResolveOrdinaryPrize(ZeusGiftPolicy.OrdinaryShelfButtons[index]);
            Check.True(
                prize is { ItemId: > 0, Count: > 0 } &&
                prize.Value.ItemId == ZeusGiftPolicy.Ordinary[index].ItemId,
                $"ordinary button {ZeusGiftPolicy.OrdinaryShelfButtons[index]} maps to its own prize");
        }

        for (var index = 0; index < ZeusGiftPolicy.LimitedShelfButtons.Length; index++)
        {
            var prize = ZeusGiftPolicy.ResolveLimitedPrize(ZeusGiftPolicy.LimitedShelfButtons[index]);
            Check.True(
                prize is { ItemId: > 0, Count: > 0 } &&
                prize.Value.ItemId == ZeusGiftPolicy.Limited[index].ItemId,
                $"limited button {ZeusGiftPolicy.LimitedShelfButtons[index]} maps to its own prize");
        }

        for (var index = 0; index < ZeusGiftPolicy.LuckGoodButtons.Length; index++)
        {
            var good = ZeusGiftPolicy.ResolveLuckGood(ZeusGiftPolicy.LuckGoodButtons[index]);
            Check.True(
                good is { ItemId: > 0, Count: > 0 } &&
                good.Value.ItemId == ZeusGiftPolicy.Luck[index].ItemId,
                $"luck button {ZeusGiftPolicy.LuckGoodButtons[index]} maps to its own good");
        }

        Check.True(
            ZeusGiftPolicy.ResolveOrdinaryPrize(999) is null &&
            ZeusGiftPolicy.ResolveLimitedPrize(200) is null &&
            ZeusGiftPolicy.ResolveLuckGood(1207) is null,
            "a number that is not on a shelf maps to nothing");

        Check.Equal(19, ZeusGiftPolicy.Dusts.Count, "the client names nineteen dusts");
        Check.True(
            ZeusGiftPolicy.Dusts.Distinct().Count() == ZeusGiftPolicy.Dusts.Count &&
            ZeusGiftPolicy.Dusts.All(ZeusGiftPolicy.IsDust) &&
            !ZeusGiftPolicy.IsDust(4230),
            "every dust is listed once and a crystal is not a dust");
    }

    /// <summary>
    /// Every reply the handler can build, paired with the page it is answered on.
    /// </summary>
    private static void CheckReplyPages()
    {
        // The follower: the level gate and the spent-quota line answer on page one,
        // the three delivery answers on page two, and the reward and the swap on
        // page three. The capture is the authority for all of them.
        AssertPage(1, GameClientHandler.ZeusGiftLevelReply, "follower level gate");
        AssertPage(1, [1509], "follower spent-quota line");
        AssertPage(1, GameClientHandler.ZeusGiftFollowerDeliveryEntries, "follower delivery entries");
        AssertPage(2, GameClientHandler.ZeusGiftBasketPage, "follower basket");
        AssertPage(2, GameClientHandler.ZeusGiftLazyAnswerPage, "follower lazy answer");
        AssertPage(2, GameClientHandler.ZeusGiftSwapQuestionPage, "follower swap question");
        AssertPage(3, GameClientHandler.ZeusGiftSwapAcceptedPage, "follower swap accepted");
        AssertPage(3, GameClientHandler.ZeusGiftRefusedPage, "follower refusal");
        AssertPage(3, GameClientHandler.ZeusGiftReconsideredPage, "follower reconsidered");
        AssertPage(3, GameClientHandler.ZeusGiftMissingGiftPage, "follower missing gift");
        AssertPage(3, [1313], "follower reward line");

        for (var tier = 1; tier <= ZeusGiftPolicy.WeekendGiftLimit; tier++)
        {
            AssertPage(
                3,
                [ZeusGiftPolicy.RewardSubId(tier)],
                $"follower reward level {tier}");
        }
        for (var round = 1; round <= ZeusGiftPolicy.WeekendGiftLimit; round++)
        {
            AssertPage(
                1,
                [ZeusGiftPolicy.ProgressSubId(round, round)],
                $"follower progress line for round {round}");
        }
        foreach (var requirement in ZeusGiftPolicy.RequirementsForRound(ZeusGiftPolicy.WeekendGiftLimit))
        {
            AssertPage(
                1,
                [ZeusGiftPolicy.RequirementSubId(requirement)],
                $"follower requirement {requirement.Label}");
        }

        // The saint: the menu on page one, the forms and shelves on two and three,
        // the dust box on four, and every exchange result on five. The captured
        // replies of 2026-10-04 03:19 are the authority for the first three pages.
        AssertPage(1, GameClientHandler.ZeusGiftSaintOpeningPage, "saint menu");
        AssertPage(2, GameClientHandler.ZeusGiftSaintDepositFormPage, "saint stone form");
        AssertPage(2, GameClientHandler.ZeusGiftSaintExchangePage, "saint exchange menu");
        AssertPage(
            2,
            GameClientHandler.ZeusGiftSaintLuckPage(chances: 2),
            "saint luck page with its chance line");
        Check.Equal(
            30006,
            GameClientHandler.ZeusGiftSaintLuckPage(chances: 2)[0],
            "the captured two-chance reply leads with the number the client reads back");
        AssertPage(2, GameClientHandler.ZeusGiftSaintWeekdayOnlyPage, "saint weekday-only line");
        AssertPage(2, GameClientHandler.ZeusGiftSaintWeekendOnlyPage, "saint weekend-only line");
        AssertPage(2, GameClientHandler.ZeusGiftSaintNotInEventPage, "saint not-in-event line");
        AssertPage(3, GameClientHandler.ZeusGiftSaintDepositDonePage, "saint deposit done");
        AssertPage(3, [1315], "saint not-enough-stones line");
        AssertPage(3, [1327], "saint amount-out-of-range line");
        AssertPage(3, GameClientHandler.ZeusGiftSaintOrdinaryShelfPage, "saint ordinary shelf");
        AssertPage(3, GameClientHandler.ZeusGiftSaintLimitedShelfPage, "saint limited shelf");
        AssertPage(3, GameClientHandler.ZeusGiftSaintFreeClaimedPage, "saint free claim");
        AssertPage(3, GameClientHandler.ZeusGiftLuckClosedPage, "saint luck-closed line");
        AssertPage(3, [GameClientHandler.ZeusGiftLuckClaimButton], "saint luck claim button");
        AssertPage(
            3,
            [
                ZeusGiftPolicy.LuckHighestScoreSubId(0),
                ZeusGiftPolicy.LuckOwnScoreSubId(0)
            ],
            "saint luck entry, its highest-score line and its own-score line");
        AssertPage(
            3,
            [
                ZeusGiftPolicy.LuckFinalHighestSubId(0),
                ZeusGiftPolicy.LuckFinalOwnSubId(0),
                GameClientHandler.ZeusGiftLuckClaimButton
            ],
            "saint settled luck page with the claim button");
        AssertPage(4, GameClientHandler.ZeusGiftSaintDustBoxPage, "saint dust box");
        AssertPage(4, GameClientHandler.ZeusGiftSaintOutOfStockPage, "saint out-of-stock line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckNoChancesPage, "luck out-of-chances line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckNoDustPage, "luck not-enough-dust line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckNotDustPage, "luck not-dust line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckLuckyNumberPage, "luck lucky-number line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckSuperLuckyPage, "luck item line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckClaimedPage, "luck claim line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckAlreadyClaimedPage, "luck already-claimed line");
        AssertPage(4, GameClientHandler.ZeusGiftLuckNotWinnerPage, "luck not-winner line");
        AssertPage(4, [ZeusGiftPolicy.LuckRolledSubId(1)], "luck rolled-score line");
        AssertPage(4, [ZeusGiftPolicy.LuckRolledSubId(1000)], "luck rolled-score ceiling");
        AssertPage(5, GameClientHandler.ZeusGiftSaintPrizeClaimedPage, "saint exchange won");
        AssertPage(5, GameClientHandler.ZeusGiftSaintExchangeLostPage, "saint exchange lost");
        AssertPage(5, GameClientHandler.ZeusGiftSaintExchangeNoDustPage, "saint exchange no dust");
        AssertPage(5, GameClientHandler.ZeusGiftSaintExchangeSpentPage, "saint exchange spent");
        AssertPage(5, GameClientHandler.ZeusGiftSaintExchangeFailedPage, "saint exchange failed");
        AssertPage(5, GameClientHandler.ZeusGiftSaintBagFullPage, "saint exchange bag full");
    }

    private static void AssertPage(int page, IReadOnlyList<int> reply, string what)
    {
        foreach (var subId in reply)
        {
            var literal = PagesOf.TryGetValue(subId, out var pages) && pages.Contains(page);
            var family = PageFamilies.Any(entry =>
                entry.Page == page && entry.Matches(subId));
            Check.True(
                literal || family,
                $"{what}: {subId} is not drawn on page {page} by NpcFunZeus.lua");
        }
    }

    /// <summary>
    /// The pages whose branches match a computed number rather than a literal one.
    /// The script reads each of these back out with the modulus in its condition:
    /// the delivery progress line by <c>mod 1000 == 9</c>, the reward level by
    /// <c>mod 10000 == 13</c>, the luck score by the tail-1/2/4/5 families, the
    /// rolled score by <c>mod 10000 == 3</c>, the chances line by
    /// <c>mod 10000 == 6</c> and the throw clock by <c>mod 1000 == 901/902</c>.
    /// </summary>
    private static readonly (int Page, Func<int, bool> Matches, string Name)[] PageFamilies =
    [
        (1, static subId => subId % 1000 == 9, "mod 1000 == 9"),
        (2, static subId => subId % 10000 == 6, "mod 10000 == 6"),
        (3, static subId => subId % 10000 == 13, "mod 10000 == 13"),
        (3, static subId => subId % 10000 is 1 or 2 or 4 or 5, "mod 10000 == 1/2/4/5"),
        (3, static subId => subId % 1000 is 901 or 902, "mod 1000 == 901/902"),
        (4, static subId => subId % 10000 == 3, "mod 10000 == 3")
    ];

    private static Dictionary<int, int[]> BuildPageMap()
    {
        var map = new Dictionary<int, List<int>>();
        void Page(int page, params int[] subIds)
        {
            foreach (var subId in subIds)
            {
                if (!map.TryGetValue(subId, out var pages))
                {
                    pages = [];
                    map[subId] = pages;
                }
                if (!pages.Contains(page))
                {
                    pages.Add(page);
                }
            }
        }

        // Index == 1, NpcFunZeus.lua:14.
        Page(
            1,
            100, 1000, 1200, 1201, 101, 1511, 1514, 2001, 2002, 5104, 5105, 1510, 1509,
            1001, 1002, 1003,
            1, 2, 3, 4, 5, 6, 7,
            4230, 4010, 4040, 4150, 4200, 4210, 4211, 4220, 4221, 3819, 4231, 4201,
            42303, 42002, 42102, 42202);
        // Index == 2, NpcFunZeus.lua:166.
        Page(
            2,
            1101, 1105, 1106, 1110, 1202, 1203, 1204, 1205, 1306, 4511, 9960, 1517,
            2100, 2101, 5105, 1102, 1005, 1006, 1007, 1008, 1009, 1512, 1513,
            200, 201, 202, 203, 204, 205, 206, 207);
        // Index == 3, NpcFunZeus.lua:294.
        Page(
            3,
            1315, 300, 207, 302, 1602, 2000, 1521, 1107, 1515, 1206,
            1207, 1208, 1209, 1210, 1211,
            1300, 1301, 1611, 1212, 1213, 1214, 1215, 1216, 1217, 1218, 1219, 1220,
            1505, 1506, 4000, 4001, 4002, 4003, 4004, 4005, 4006,
            1518, 1516, 1503, 1104, 1011, 1313, 1103, 1350, 1108, 1014, 1327, 1019);
        // Index == 4, NpcFunZeus.lua:582.
        Page(
            4,
            5101, 5102, 408, 400, 401, 402, 403, 404, 405, 406, 407, 409, 900, 1504, 1103,
            1230, 1231, 1232, 1233, 1234, 1235, 1236, 1113, 3000, 3001);
        // Index == 5, NpcFunZeus.lua:677.
        Page(5, 1115, 3002, 3003, 3004, 3005, 3006, 3007);
        return map.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray());
    }
}
