using Godswar.Server.Application.Realms;

namespace Godswar.Server.Application.ZeusGift;

/// <summary>
/// One character's delivery progress for one realm day.
/// </summary>
/// <remarks>
/// <see cref="Tier"/> is the reward level the next delivery pays and
/// <see cref="Round"/> is the delivery number the client prints. They are equal
/// in the capture and diverge only after an abandoned requirement, which the event
/// page says resets the reward level without giving the deliveries back ("之前的
/// 奖励级别将归零…但依然可以再进行5次献礼，但奖励将按照1-5次来计算").
/// </remarks>
internal readonly record struct ZeusGiftDailyState(
    DateOnly Day,
    int Round,
    int Tier,
    int RequiredItemId,
    int RequiredCount)
{
    /// <summary>A realm day with nothing delivered yet.</summary>
    public static ZeusGiftDailyState Fresh(DateOnly day) =>
        new(day, Round: 0, Tier: 0, RequiredItemId: 0, RequiredCount: 0);

    public bool HasRequirement => RequiredItemId != 0;
}

/// <summary>
/// One character's praying-stone week: how many stones were handed in, how many
/// dust exchanges were spent, how many claims were taken, and the experience and
/// talent points those stones banked until the claim hands them over.
/// </summary>
internal readonly record struct ZeusGiftWeeklyState(
    DateOnly WeekStart,
    int StonesDeposited,
    int ExchangesUsed,
    int LimitedClaimed,
    int ClaimsTaken,
    int LuckDrawsUsed,
    long PendingExperience,
    int PendingTalentPoints)
{
    public static ZeusGiftWeeklyState Fresh(DateOnly weekStart) =>
        new(weekStart, StonesDeposited: 0, ExchangesUsed: 0, LimitedClaimed: 0,
            ClaimsTaken: 0, LuckDrawsUsed: 0, PendingExperience: 0,
            PendingTalentPoints: 0);

    /// <summary>
    /// The dust exchanges the week buys: five for everybody, plus one for every five
    /// stones handed in ("All players have 5 base chances to exchange their Dust for
    /// gifts (plus 1 extra for every 5 Praying Stones deposited during the week)").
    /// </summary>
    public int ExchangeLimit => ZeusGiftPolicy.ExchangesForStones(StonesDeposited);

    public int ExchangesLeft => Math.Max(0, ExchangeLimit - ExchangesUsed);

    /// <summary>
    /// The luck chances the week's deposits buy: one per stone, which the client
    /// states twice (<c>NF_Z_T2</c> "每缴纳1个祈祷石，才可以增加1次碰运气的机会" and
    /// <c>NF_Z_T407</c>) and prints beside the number on the luck page.
    /// </summary>
    public int LuckChances => Math.Max(0, StonesDeposited - LuckDrawsUsed);

    /// <summary>Whether the claim line has anything banked to hand over.</summary>
    public bool HasPendingReward => PendingExperience > 0 || PendingTalentPoints > 0;
}

/// <summary>
/// One character's entry in the luck contest for one item of one week.
/// </summary>
internal readonly record struct ZeusLuckEntry(
    int ItemId,
    int Score,
    DateTimeOffset PlacedAt,
    bool Claimed);

/// <summary>
/// What one delivery did, as the dialogue needs to word it.
/// </summary>
internal enum ZeusGiftDeliveryOutcome
{
    /// <summary>The gift was taken; the rewards are owed.</summary>
    Accepted,

    /// <summary>The character has used the day's deliveries.</summary>
    NoDeliveriesLeft,

    /// <summary>The character does not hold what the follower asked for.</summary>
    MissingRequirement,

    /// <summary>The bag has no room for the rewards.</summary>
    BagFull,

    /// <summary>The storage could not be reached.</summary>
    Unavailable
}

/// <summary>Everything one accepted delivery hands back.</summary>
internal readonly record struct ZeusGiftDeliveryReward(
    int Round,
    int Tier,
    int Experience,
    int TalentExperience,
    int PrayingStones,
    bool Wine);

/// <summary>
/// What one dust exchange did.
/// </summary>
internal enum ZeusGiftExchangeOutcome
{
    /// <summary>The prize was won.</summary>
    Won,

    /// <summary>The exchange failed, which the client words as <c>NF_L0_Z1303</c>.</summary>
    Lost,

    /// <summary>
    /// The score matched the item's highest but was not the first one thrown, which
    /// the client words as <c>NF_Z_T408</c> ("尽管您投出的分数与最高分相同，但只有
    /// 最先投出最高分的玩家才可以得到奖励"). A board with no entry at all is this
    /// case too: a zero matches the zero it finds and is not the first either, which
    /// is what the reference answers when nothing was ever thrown.
    /// </summary>
    Tied,

    /// <summary>The character does not hold twenty dusts of one kind.</summary>
    MissingDust,

    /// <summary>The week's exchange count is spent.</summary>
    NoExchangesLeft,

    /// <summary>The limited shelf is empty for this week.</summary>
    OutOfStock,

    /// <summary>The bag has no room for the prize.</summary>
    BagFull,

    /// <summary>The storage could not be reached.</summary>
    Unavailable
}

/// <summary>Everything one exchange hands back.</summary>
/// <param name="Count">
/// How many of <paramref name="ItemId"/> were handed over, for the acquisition
/// notice the window has to raise. Zero when nothing was granted.
/// </param>
internal readonly record struct ZeusGiftExchangeResult(
    ZeusGiftExchangeOutcome Outcome,
    int ItemId,
    string Label,
    int ExchangesLeft,
    int Count = 0);

/// <summary>What one claim of the banked stones paid.</summary>
/// <param name="Experience">Experience the week's stones banked.</param>
/// <param name="TalentPoints">Talent points the week's stones banked.</param>
/// <param name="ClaimsTaken">How many claims the character has taken this week.</param>
internal readonly record struct ZeusGiftClaimResult(
    ZeusGiftExchangeOutcome Outcome,
    long Experience,
    int TalentPoints,
    int ClaimsTaken);

/// <summary>
/// What one luck draw did.
/// </summary>
internal enum ZeusLuckDrawOutcome
{
    /// <summary>The draw rolled a score.</summary>
    Rolled,

    /// <summary>The character has no chances; each praying stone buys one.</summary>
    NoChances,

    /// <summary>The box does not hold ten dusts.</summary>
    MissingDust,

    /// <summary>The box holds something that is not dust.</summary>
    NotDust,

    /// <summary>The storage could not be reached.</summary>
    Unavailable
}

/// <summary>Everything one luck draw produced.</summary>
/// <param name="RefundedDustItemId">
/// Which dust the lucky number paid back, so the acquisition notice names it.
/// </param>
/// <param name="ItemCount">How many of <paramref name="ItemId"/> the 176 paid.</param>
internal readonly record struct ZeusLuckDrawResult(
    ZeusLuckDrawOutcome Outcome,
    int Score,
    int ChancesLeft,
    int RefundedDusts,
    int ItemId,
    int RefundedDustItemId = 0,
    int ItemCount = 0);

/// <summary>What one delivery booked, and the rewards the caller still owes.</summary>
internal readonly record struct ZeusGiftDelivery(
    ZeusGiftDeliveryOutcome Outcome,
    ZeusGiftDeliveryReward Reward);

/// <summary>
/// Where a delivery's item comes from.
/// </summary>
internal enum ZeusGiftDeliverySource
{
    /// <summary>
    /// The basket, whose item the caller has already matched against the
    /// requirement. The count still comes from the whole bag.
    /// </summary>
    Basket,

    /// <summary>
    /// The follower's "算了给你水晶" answer, which places nothing and spends ten
    /// level-one crystals out of the bag instead.
    /// </summary>
    CrystalSubstitute,

    /// <summary>
    /// A basket confirm whose item the server could not read out of the frame. The
    /// bag alone answers, so a working placement is never turned away while the
    /// field is still being identified; the caller logs the raw word.
    /// </summary>
    UnreadBasket
}

/// <summary>
/// The Zeus gift event's durable state: the follower's daily deliveries and the
/// saint's weekly stones, dust exchanges and luck entries.
/// </summary>
/// <remarks>
/// Every write books the counters before the caller pays anything, the same shape
/// the divine wish uses: a disconnect between the two halves costs the player a
/// prize rather than minting a second one. The daily and weekly rows carry the
/// realm day and the Monday they belong to, so an older row reads as an untouched
/// period and no scheduled reset is needed.
/// </remarks>
internal interface IZeusGiftStore
{
    /// <summary>The character's delivery state for the realm day of <paramref name="now"/>.</summary>
    Task<ZeusGiftDailyState> ReadDailyAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores the requirement the follower is asking for. The draw itself is the
    /// caller's, because the client wants a different one each round.
    /// </summary>
    Task<ZeusGiftDailyState> AssignRequirementAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int round,
        int tier,
        int itemId,
        int count,
        CancellationToken cancellationToken);

    /// <summary>
    /// Abandons the current requirement: the reward level drops back to the first
    /// and the deliveries already made keep counting, which is the event page's own
    /// rule ("之前的奖励级别将归零…但依然可以再进行5次献礼").
    /// </summary>
    Task<ZeusGiftDailyState> AbandonRequirementAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int round,
        int itemId,
        int count,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes the requirement and books one delivery, granting the praying stone and
    /// the wine itself. The experience and the talent experience come back for the
    /// caller to pay, because only the progression path can write them.
    /// </summary>
    /// <param name="placedItemId">
    /// The item the player put in the basket, which the caller has already matched
    /// against the requirement. It is only read for
    /// <see cref="ZeusGiftDeliverySource.Basket"/>.
    /// </param>
    Task<ZeusGiftDelivery> DeliverAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        ZeusGiftDeliverySource source,
        int placedItemId,
        int level,
        CancellationToken cancellationToken);

    /// <summary>The character's week state for the week of <paramref name="now"/>.</summary>
    Task<ZeusGiftWeeklyState> ReadWeeklyAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hands praying stones to the saint: consumes them from the bag all or nothing
    /// and banks what they are worth - <see cref="ZeusGiftPolicy.ExperiencePerPrayingStone"/>
    /// experience and <see cref="ZeusGiftPolicy.TalentPointsPerPrayingStone"/> talent
    /// point each - until the claim hands the bank over. Returns how many were taken,
    /// or zero when the bag does not hold that many.
    /// </summary>
    Task<int> DepositPrayingStonesAsync(
        int characterId,
        RealmCalendar calendar,
        int realmId,
        DateTimeOffset now,
        int stones,
        CancellationToken cancellationToken);

    /// <summary>
    /// One dust exchange. The dusts are taken before the roll, so a failed exchange
    /// costs them, which is the client's own warning ("一经点击马上消耗掉原料",
    /// <c>NF_L0_Z1515</c>).
    /// </summary>
    Task<ZeusGiftExchangeResult> ExchangeDustAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        int realmId,
        int camp,
        DateTimeOffset now,
        int buttonSubId,
        bool limited,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hands over everything the week's stones banked and empties the bank, which is
    /// what the claim line (<c>1205</c>) does. A week with nothing banked pays
    /// nothing, which is the "you have already claimed" answer.
    /// </summary>
    Task<ZeusGiftClaimResult> ClaimBankedRewardAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>The character's entries for the week, one per item it has thrown at.</summary>
    Task<IReadOnlyList<ZeusLuckEntry>> ReadLuckAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// The highest score the character's own camp has thrown at one item this week,
    /// which is the "该道具目前最高分是 N 分" line the luck page shows.
    /// </summary>
    Task<int> ReadHighestScoreAsync(
        int camp,
        RealmCalendar calendar,
        DateTimeOffset now,
        int itemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// One luck draw: takes ten dusts, spends one stone-bought chance, rolls a score
    /// and keeps it when it beats the character's own score on that item. Lucky
    /// numbers pay dusts back and the 176 roll hands the item over immediately,
    /// both inside the same transaction.
    /// </summary>
    Task<ZeusLuckDrawResult> DrawLuckAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int itemId,
        int camp,
        CancellationToken cancellationToken);

    /// <summary>
    /// The Sunday prize pickup. The item goes to the character whose entry is the
    /// highest of its camp for that week and item, ties going to whoever threw
    /// first; anything else is answered as a loss.
    /// </summary>
    Task<ZeusGiftExchangeResult> ClaimLuckPrizeAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int itemId,
        int camp,
        CancellationToken cancellationToken);
}
