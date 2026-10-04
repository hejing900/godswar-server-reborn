using Godswar.Server.Application.ZeusGift;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// The event's function number, <c>NPC_FLAG_SYS_ZEUS = 26</c> in the client's
    /// own <c>NpcFun.lua</c>. It is the only function either endpoint advertises:
    /// all sixteen captured openings carry <c>flags = 0x200</c> and a one-entry
    /// list, <c>[26]</c>, and the script name is the NPC's own key
    /// (<c>Athens_113</c>, <c>Sparta_113</c>, <c>Athens_114</c>,
    /// <c>Sparta_114</c>).
    /// </summary>
    private const int ZeusGiftFunctionNumber = 26;

    /// <summary>
    /// The function list both endpoints open with. The client dispatches on this
    /// and the script pages are reached by answering with their numbers, never by
    /// advertising more entries.
    /// </summary>
    internal static readonly int[] ZeusGiftFunctionList = [ZeusGiftFunctionNumber];

    /// <summary>The level both endpoints turn away, <c>NF_L0_Z100</c>.</summary>
    internal const int ZeusGiftMinimumLevel = ZeusGiftPolicy.MinimumLevel;

    /// <summary>
    /// What the client sends when it wants the open page's entries rather than
    /// reporting a click: every click slot is <c>-1</c>.
    /// </summary>
    private const int ZeusGiftOpeningRequest = -1;

    /// <summary>
    /// What the client sends when the player presses the window's confirm button
    /// instead of one of the numbers. Captured 2026-10-04 01:02:40: the follower's
    /// basket page was up, the player dropped the gift in and confirmed, and the
    /// second click slot carried <c>0</c>.
    /// </summary>
    private const int ZeusGiftConfirmSelection = 0;

    /// <summary>
    /// The follower's window when the character is below the level, which the
    /// capture answers with for <c>Athens_113</c>, <c>Athens_114</c> and
    /// <c>Sparta_114</c>. It is the script's first-page <c>SubID == 100</c> branch.
    /// </summary>
    internal static readonly int[] ZeusGiftLevelReply = [100];

    // ---- the follower: [Event]Zeus' Loyal Believer (Athens_113 / Sparta_113) ----
    //
    // The capture (2026-10-04, session 825354ec) is the whole of this half:
    //   open   -> [<requirement>, 1001, 1002, 1003, <progress>]
    //   1001   -> [1005]
    //   1002   -> [1512, 1006, 1008]
    //   1003   -> [1513, 1009, 1007]
    //   1006   -> [1313, <tier>*10000+13]
    //   1007   -> [1014]
    // and the reference answered 1008 with nothing at all.

    /// <summary>
    /// Page 2 of the delivery: "快把天神需要的礼物献上来吧" plus the slot the gift
    /// goes in (<c>NpcFunZeus.lua:262</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftBasketPage = [1005];

    /// <summary>
    /// Page 2: the follower offers to take ten level-one crystals instead
    /// (<c>NpcFunZeus.lua:287</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftLazyAnswerPage = [1512, 1006, 1008];

    /// <summary>
    /// Page 2: the follower warns that swapping the requirement resets the reward
    /// level (<c>NpcFunZeus.lua:290</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftSwapQuestionPage = [1513, 1009, 1007];

    /// <summary>
    /// Page 3: the swap went through (<c>NpcFunZeus.lua:535</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftSwapAcceptedPage = [1014];

    /// <summary>
    /// Page 3: "你又懒又没水晶给我…回家打酱油去吧！" and the window closes
    /// (<c>NpcFunZeus.lua:531</c>). It answers the "太黑了,再见!" entry, which the
    /// reference server left unanswered; the number is the script's own dismissal.
    /// </summary>
    internal static readonly int[] ZeusGiftRefusedPage = [1108];

    /// <summary>
    /// Page 3: "请选择" and the window closes (<c>NpcFunZeus.lua:523</c>). It
    /// answers the "让我再考虑下吧" entry, which the reference also left unanswered.
    /// </summary>
    internal static readonly int[] ZeusGiftReconsideredPage = [1103];

    /// <summary>
    /// Page 3: the character does not hold what the follower asked for
    /// (<c>NF_L0_Z1104</c>, <c>NpcFunZeus.lua:504</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftMissingGiftPage = [1104];

    /// <summary>
    /// Page 3: the day's deliveries are spent — the script's "本神给你一次换的机
    /// 会", which is also the only closing line the first page offers for a spent
    /// quota on this half.
    /// </summary>
    internal static readonly int[] ZeusGiftNoDeliveriesPage = [1014];

    // ---- the saint: [Event]Praying Saint (Athens_114 / Sparta_114) ----
    //
    // No capture covers this endpoint past the level gate, so every number below is
    // transcribed from NpcFunZeus.lua with the page it sits on, and the page a
    // reply lands on is the reply's own ordinal (see the page rule on
    // NpcFunctionActionResponseAsync).

    /// <summary>
    /// Page 1: the saint's own description and the three services.
    /// </summary>
    /// <remarks>
    /// The line is <c>1514</c>, not the believer's <c>1511</c>: captured
    /// 2026-10-04 03:19:22 answered <c>[1514, 1200, 1201, 101]</c>, and
    /// <c>NF_L0_Z1514</c> is the saint's own "亲爱的朋友，你要时刻牢记一点…在每周六
    /// 12：00-周日23：55，宙斯都会让我派发无数的奇珍异宝" while <c>NF_L0_Z1511</c>
    /// is the believer's invitation to give gifts.
    /// </remarks>
    internal static readonly int[] ZeusGiftSaintOpeningPage = [1514, 1200, 1201, 101];

    /// <summary>Page 2: the praying-stone input form (<c>:198</c>).</summary>
    internal static readonly int[] ZeusGiftSaintDepositFormPage = [1202];

    /// <summary>
    /// Page 2: the three exchange entries and their blurb, in the captured order
    /// (<c>:209</c>, <c>:214</c>, <c>:219</c>, <c>:243</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftSaintExchangePage = [1203, 1204, 1205, 1517];

    /// <summary>
    /// Page 2: the eight luck goods, preceded by the line that prints how many
    /// chances the week's deposits have left.
    /// </summary>
    internal static readonly int[] ZeusGiftLuckGoodButtons =
    [
        200, 201, 202, 203, 204, 205, 206, 207
    ];

    /// <summary>
    /// Page 2's chance line: <c>NF_Z_T2</c> reads its number back as
    /// <c>(SubID - 6) / 10000 - 1</c>, and the captured reply for a character with
    /// two chances left was <c>30006</c>.
    /// </summary>
    internal static int[] ZeusGiftSaintLuckPage(int chances) =>
        [ZeusGiftPolicy.LuckChancesSubId(chances), .. ZeusGiftLuckGoodButtons];

    /// <summary>Page 2: the deposit only runs Monday to Friday (<c>NF_L0_Z1306</c>, <c>:224</c>).</summary>
    internal static readonly int[] ZeusGiftSaintWeekdayOnlyPage = [1306];

    /// <summary>Page 2: the exchange only runs on the weekend (<c>NF_L0_Z1110</c>, <c>:194</c>).</summary>
    internal static readonly int[] ZeusGiftSaintWeekendOnlyPage = [1110];

    /// <summary>
    /// Page 2: "你明明没参加过宙斯献礼活动还想来兑换,做梦去吧!" (<c>:258</c>), the
    /// saint's answer to a character that has never delivered.
    /// </summary>
    internal static readonly int[] ZeusGiftSaintNotInEventPage = [1102];

    /// <summary>Page 3: the deposit went through (<c>:349</c>).</summary>
    internal static readonly int[] ZeusGiftSaintDepositDonePage = [1206];

    /// <summary>
    /// Page 3: the ordinary shelf, in the captured order
    /// (<c>:353</c>–<c>:377</c>, <c>:494</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftSaintOrdinaryShelfPage =
    [
        1207, 1208, 1209, 1210, 1211, 1518
    ];

    /// <summary>
    /// Page 3: the limited shelf, in the captured order
    /// (<c>:459</c>–<c>:493</c>, <c>:497</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftSaintLimitedShelfPage =
    [
        4000, 4001, 4002, 4003, 4004, 4005, 4006, 1516
    ];

    /// <summary>Page 3: the free experience and talent claim went through (<c>:400</c>).</summary>
    internal static readonly int[] ZeusGiftSaintFreeClaimedPage = [1611];

    /// <summary>
    /// Page 4: the dust box. <c>3000</c> raises the slot and clears the text line,
    /// so it has to travel before <c>3001</c>, whose instruction must stay up
    /// (<c>:668</c>, <c>:673</c>).
    /// </summary>
    internal static readonly int[] ZeusGiftSaintDustBoxPage = [3000, 3001];

    /// <summary>Page 4: the limited shelf ran out for the week (<c>5102</c>, <c>:588</c>).</summary>
    internal static readonly int[] ZeusGiftSaintOutOfStockPage = [5102];

    /// <summary>Page 5: the prize was handed over (<c>3007</c>, <c>NF_L0_Z1222</c>).</summary>
    internal static readonly int[] ZeusGiftSaintPrizeClaimedPage = [3007];

    /// <summary>Page 5: the exchange failed (<c>3004</c>, <c>NF_L0_Z1303</c>).</summary>
    internal static readonly int[] ZeusGiftSaintExchangeLostPage = [3004];

    /// <summary>Page 5: not enough material (<c>3003</c>, <c>NF_L0_Z1504</c>).</summary>
    internal static readonly int[] ZeusGiftSaintExchangeNoDustPage = [3003];

    /// <summary>Page 5: the week's exchanges are spent (<c>3006</c>, <c>NF_L0_Z1328</c>).</summary>
    internal static readonly int[] ZeusGiftSaintExchangeSpentPage = [3006];

    /// <summary>Page 5: the server could not run the exchange (<c>3005</c>, <c>NF_L0_Z1305</c>).</summary>
    internal static readonly int[] ZeusGiftSaintExchangeFailedPage = [3005];

    /// <summary>Page 5: the bag had no room for the prize.</summary>
    internal static readonly int[] ZeusGiftSaintBagFullPage = [3005];

    // ---- the luck contest ----

    /// <summary>Page 3: the contest runs Saturday noon to Sunday noon (<c>NF_Z_T300</c>, <c>:300</c>).</summary>
    internal static readonly int[] ZeusGiftLuckClosedPage = [300];

    /// <summary>Page 4: the week's chances are spent (<c>NF_Z_T407</c>, <c>:304</c>).</summary>
    internal static readonly int[] ZeusGiftLuckNoChancesPage = [407];

    /// <summary>Page 4: the box does not hold ten dusts (<c>NF_Z_T401</c>, <c>:401</c>).</summary>
    internal static readonly int[] ZeusGiftLuckNoDustPage = [401];

    /// <summary>Page 4: the box holds something that is not dust (<c>NF_Z_T400</c>, <c>:400</c>).</summary>
    internal static readonly int[] ZeusGiftLuckNotDustPage = [400];

    /// <summary>Page 4: a lucky number paid 99 dusts back (<c>NF_Z_T402</c>, <c>:402</c>).</summary>
    internal static readonly int[] ZeusGiftLuckLuckyNumberPage = [402];

    /// <summary>Page 4: the 176 roll handed the item over (<c>NF_Z_T403</c>, <c>:403</c>).</summary>
    internal static readonly int[] ZeusGiftLuckSuperLuckyPage = [403];

    /// <summary>Page 4: the claim went through (<c>NF_Z_T406</c>, <c>:404</c>).</summary>
    internal static readonly int[] ZeusGiftLuckClaimedPage = [406];

    /// <summary>Page 4: the prize was already taken (<c>NF_Z_T405</c>).</summary>
    internal static readonly int[] ZeusGiftLuckAlreadyClaimedPage = [405];

    /// <summary>
    /// Page 4: the claim was refused because the score only tied the highest.
    /// </summary>
    /// <remarks>
    /// <c>NF_Z_T408</c>, "很遗憾，尽管您投出的分数与最高分相同，但只有最先投出最高分
    /// 的玩家才可以得到奖励哦". The script gives 408 a branch of its own ahead of the
    /// 404-409 run (<c>:592</c>), and the reference answers it for an untouched board:
    /// a zero ties the zero it finds and was not the first either.
    /// </remarks>
    internal static readonly int[] ZeusGiftLuckTiedPage = [408];

    /// <summary>Page 4: the claim came from someone who is not the week's winner (<c>NF_Z_T409</c>).</summary>
    internal static readonly int[] ZeusGiftLuckNotWinnerPage = [409];

    /// <summary>Page 4: the bag had no room for the claimed item.</summary>
    internal static readonly int[] ZeusGiftLuckBagFullPage = [409];

    /// <summary>The claim button's number, drawn on the contest's third page (<c>:324</c>).</summary>
    internal const int ZeusGiftLuckClaimButton = 302;
}
