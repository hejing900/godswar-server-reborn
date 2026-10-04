using Godswar.Server.Application.Realms;
using Godswar.Server.Application.ZeusGift;
using Npgsql;

namespace Godswar.Server.Infrastructure.ZeusGift;

/// <summary>
/// The Zeus gift event's durable state in Postgres.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation locks the character row first, so two clicks arriving together
/// cannot both spend the same delivery, the same dusts or the same chance. The item
/// moves - taking the requirement and handing the prizes over - happen in that same
/// transaction as the counter that authorises them, which is what keeps a
/// disconnect from minting a free reward.
/// </para>
/// <para>
/// The kit bag is the server's own storage: location 1, ninety-six slots, and a
/// stack cap read from the item template's <c>Overlap</c>. Prizes are granted
/// bound, because the event page says every exchange reward is untradeable
/// ("不可交易").
/// </para>
/// </remarks>
internal sealed class PostgresZeusGiftStore : IZeusGiftStore
{
    /// <summary>The kit bag's own item location.</summary>
    private const short KitBagLocation = 1;

    /// <summary>Ninety-six kit-bag slots, the same bound the other grants use.</summary>
    private const int KitBagSlotCount = 96;

    /// <summary>What one stack can hold when the template does not say.</summary>
    private const int DefaultStackCap = 99;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresZeusGiftStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<ZeusGiftDailyState> ReadDailyAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadDailyAsync(connection, null, characterId, day, cancellationToken);
    }

    public async Task<ZeusGiftDailyState> AssignRequirementAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int round,
        int tier,
        int itemId,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        await WriteDailyAsync(
            connection, transaction, characterId, day, round, tier, itemId, count, now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ZeusGiftDailyState(day, round, tier, itemId, count);
    }

    public async Task<ZeusGiftDailyState> AbandonRequirementAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int round,
        int itemId,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);
        // The reward level restarts; the round keeps counting, which is the event
        // page's own rule for giving a requirement up.
        await WriteDailyAsync(
            connection, transaction, characterId, day, round, tier: 0, itemId, count, now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ZeusGiftDailyState(day, round, Tier: 0, itemId, count);
    }

    public async Task<ZeusGiftDelivery> DeliverAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        ZeusGiftDeliverySource source,
        int placedItemId,
        int level,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var day = calendar.GetDay(now);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        var state = await ReadDailyAsync(connection, transaction, characterId, day, cancellationToken);
        var limit = ZeusGiftPolicy.GiftLimit(day);
        if (state.Round >= limit || !state.HasRequirement)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ZeusGiftDelivery(
                ZeusGiftDeliveryOutcome.NoDeliveriesLeft,
                default);
        }

        // The basket decides the type and the bag decides the count: the item the
        // player put in has to be the one the follower asked for, and the whole bag
        // then has to hold enough of it. The follower's own "算了给你水晶" answer
        // places nothing at all and spends the ten-crystal substitute instead.
        var taken = source == ZeusGiftDeliverySource.CrystalSubstitute
            ? await ConsumeItemAsync(
                connection,
                transaction,
                characterId,
                ZeusGiftPolicy.CrystalSubstituteItemId,
                ZeusGiftPolicy.CrystalSubstituteCount,
                cancellationToken)
            : placedItemId != 0 && placedItemId != state.RequiredItemId
                ? false
                : await ConsumeItemAsync(
                    connection,
                    transaction,
                    characterId,
                    state.RequiredItemId,
                    state.RequiredCount,
                    cancellationToken);
        if (!taken)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ZeusGiftDelivery(
                ZeusGiftDeliveryOutcome.MissingRequirement,
                default);
        }

        var round = state.Round + 1;
        var tier = state.Tier + 1;
        var multiplier = ZeusGiftPolicy.RewardMultiplier(day);
        var experience = ZeusGiftPolicy.ExperienceForTier(level, tier) * multiplier;
        // The curve pays talent points; the progression path takes talent experience.
        var talent = ZeusGiftPolicy.TalentPointsForTier(level, tier) *
            multiplier *
            ZeusGiftPolicy.TalentExperiencePerPoint;
        var stones = ZeusGiftPolicy.PrayingStonesForTier(tier) * multiplier;
        var wine = ZeusGiftPolicy.CanPayWine(round) &&
            Random.Shared.Next(100) < ZeusGiftPolicy.WineChancePercent;

        var granted = await GrantItemAsync(
            connection, transaction, characterId, ZeusGiftPolicy.PrayingStoneItemId, stones,
            cancellationToken);
        if (granted && wine)
        {
            granted = await GrantItemAsync(
                connection, transaction, characterId, ZeusGiftPolicy.WineItemId, 1,
                cancellationToken);
        }
        if (!granted)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ZeusGiftDelivery(ZeusGiftDeliveryOutcome.BagFull, default);
        }

        await WriteDailyAsync(
            connection, transaction, characterId, day, round, tier,
            itemId: 0, count: 0, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        Console.WriteLine(
            $"[zeus] delivery character={characterId} account={accountId} day={day:yyyy-MM-dd} " +
            $"round={round}/{limit} tier={tier} exp={experience} talent={talent} " +
            $"stones={stones} wine={wine}");
        return new ZeusGiftDelivery(
            ZeusGiftDeliveryOutcome.Accepted,
            new ZeusGiftDeliveryReward(round, tier, experience, talent, stones, wine));
    }

    public async Task<ZeusGiftWeeklyState> ReadWeeklyAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadWeeklyAsync(connection, null, characterId, week, cancellationToken);
    }

    public async Task<int> DepositPrayingStonesAsync(
        int characterId,
        RealmCalendar calendar,
        int realmId,
        DateTimeOffset now,
        int stones,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (stones <= 0)
        {
            return 0;
        }

        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        // All or nothing: a request for more stones than the bag holds takes none of
        // them and buys none of the week's luck chances either.
        if (!await ConsumeItemAsync(
                connection,
                transaction,
                characterId,
                ZeusGiftPolicy.PrayingStoneItemId,
                stones,
                cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }

        var taking = stones;
        var state = await ReadWeeklyAsync(connection, transaction, characterId, week, cancellationToken);
        // The stones pay nothing here: each one banks its experience and talent
        // point, and the claim line hands the whole bank over.
        var bankedExperience = state.PendingExperience +
            ((long)taking * ZeusGiftPolicy.ExperiencePerPrayingStone);
        var bankedTalent = state.PendingTalentPoints +
            (taking * ZeusGiftPolicy.TalentPointsPerPrayingStone);
        await WriteWeeklyAsync(
            connection, transaction, characterId, week,
            state.StonesDeposited + taking,
            state.ExchangesUsed,
            state.LimitedClaimed,
            state.ClaimsTaken,
            state.LuckDrawsUsed,
            bankedExperience,
            bankedTalent,
            now,
            cancellationToken);

        await using (var addRealm = new NpgsqlCommand("""
            INSERT INTO public.zeus_gift_realm_stones (
                realm_id, week_start, stones_deposited, updated_at
            )
            VALUES (@realmId, @weekStart, @stones, @now)
            ON CONFLICT (realm_id, week_start) DO UPDATE
            SET stones_deposited =
                    public.zeus_gift_realm_stones.stones_deposited + EXCLUDED.stones_deposited,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction))
        {
            addRealm.Parameters.AddWithValue("realmId", realmId);
            addRealm.Parameters.AddWithValue("weekStart", week);
            addRealm.Parameters.AddWithValue("stones", taking);
            addRealm.Parameters.AddWithValue("now", now);
            await addRealm.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[zeus] stones deposited character={characterId} week={week:yyyy-MM-dd} " +
            $"taken={taking} total={state.StonesDeposited + taking} " +
            $"banked-exp={bankedExperience} banked-talent={bankedTalent} " +
            $"exchanges={ZeusGiftPolicy.ExchangesForStones(state.StonesDeposited + taking)}");
        return taking;
    }

    /// <summary>
    /// Whether one camp has handed out the week's whole stock of one limited prize,
    /// which the fourth page answers with <c>5102</c> before the dust box is even
    /// raised.
    /// </summary>
    public async Task<bool> IsLimitedShelfEmptyAsync(
        int realmId,
        int camp,
        RealmCalendar calendar,
        DateTimeOffset now,
        int itemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var claimed = await ReadLimitedStockAsync(
            connection, null, realmId, camp, week, itemId, cancellationToken);
        return claimed >= ZeusGiftPolicy.LimitedStockForItem(itemId);
    }

    public async Task<ZeusGiftExchangeResult> ExchangeDustAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        int realmId,
        int camp,
        DateTimeOffset now,
        int buttonSubId,
        bool limited,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var prize = limited
            ? ZeusGiftPolicy.ResolveLimitedPrize(buttonSubId)
            : ZeusGiftPolicy.ResolveOrdinaryPrize(buttonSubId);
        if (prize is not { } chosen)
        {
            return new ZeusGiftExchangeResult(
                ZeusGiftExchangeOutcome.Unavailable, 0, string.Empty, 0);
        }

        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        var state = await ReadWeeklyAsync(connection, transaction, characterId, week, cancellationToken);
        // No exchange count is spent here: the reference takes the twenty dusts and
        // nothing else, so a character with dust can keep exchanging. The count the
        // week's stones buy is still recorded below, for the log and for a later
        // tightening, but it does not gate anything.

        if (limited)
        {
            var claimed = await ReadLimitedStockAsync(
                connection, transaction, realmId, camp, week, chosen.ItemId, cancellationToken);
            if (claimed >= ZeusGiftPolicy.LimitedStockForItem(chosen.ItemId))
            {
                await transaction.CommitAsync(cancellationToken);
                return new ZeusGiftExchangeResult(
                    ZeusGiftExchangeOutcome.OutOfStock, 0, chosen.Label, state.ExchangesLeft);
            }
        }

        // The dusts go first: the client warns that clicking a material consumes it
        // at once and that the exchange can still fail.
        var dustId = await ResolveDustAsync(connection, transaction, characterId, cancellationToken);
        if (dustId == 0 ||
            !await ConsumeItemAsync(
                connection, transaction, characterId, dustId,
                ZeusGiftPolicy.DustPerExchange, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ZeusGiftExchangeResult(
                ZeusGiftExchangeOutcome.MissingDust, 0, chosen.Label, state.ExchangesLeft);
        }

        var exchangesUsed = state.ExchangesUsed + 1;
        var successChance = limited
            ? ZeusGiftPolicy.LimitedExchangeSuccessPercent
            : ZeusGiftPolicy.OrdinaryExchangeSuccessPercent;
        var won = Random.Shared.Next(100) < successChance;
        var limitedClaimed = state.LimitedClaimed;
        var outcome = ZeusGiftExchangeOutcome.Lost;
        if (won)
        {
            if (await GrantItemAsync(
                    connection, transaction, characterId, chosen.ItemId, chosen.Count,
                    cancellationToken))
            {
                outcome = ZeusGiftExchangeOutcome.Won;
                if (limited)
                {
                    limitedClaimed++;
                    await AddLimitedStockAsync(
                        connection, transaction, realmId, camp, week, chosen.ItemId, now,
                        cancellationToken);
                }
            }
            else
            {
                // The bag had no room: give the exchange back rather than eat it.
                await transaction.RollbackAsync(cancellationToken);
                return new ZeusGiftExchangeResult(
                    ZeusGiftExchangeOutcome.BagFull, 0, chosen.Label, state.ExchangesLeft);
            }
        }

        await WriteWeeklyAsync(
            connection, transaction, characterId, week,
            state.StonesDeposited,
            exchangesUsed,
            limitedClaimed,
            state.ClaimsTaken,
            state.LuckDrawsUsed,
            state.PendingExperience,
            state.PendingTalentPoints,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[zeus] exchange character={characterId} account={accountId} " +
            $"button={buttonSubId} limited={limited} prize={chosen.ItemId}x{chosen.Count} " +
            $"won={won} used={exchangesUsed}");
        return new ZeusGiftExchangeResult(
            outcome,
            chosen.ItemId,
            chosen.Label,
            state.ExchangeLimit - exchangesUsed,
            outcome == ZeusGiftExchangeOutcome.Won ? chosen.Count : 0);
    }

    public async Task<ZeusGiftClaimResult> ClaimBankedRewardAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        var state = await ReadWeeklyAsync(connection, transaction, characterId, week, cancellationToken);
        if (!state.HasPendingReward)
        {
            // NF_L0_Z1506, "你已经领取过奖励了": the bank is empty, either because
            // nothing was handed in or because the claim already drained it.
            await transaction.CommitAsync(cancellationToken);
            return new ZeusGiftClaimResult(
                ZeusGiftExchangeOutcome.NoExchangesLeft, 0, 0, state.ClaimsTaken);
        }

        var claimsTaken = state.ClaimsTaken + 1;
        await WriteWeeklyAsync(
            connection, transaction, characterId, week,
            state.StonesDeposited,
            state.ExchangesUsed,
            state.LimitedClaimed,
            claimsTaken,
            state.LuckDrawsUsed,
            pendingExperience: 0,
            pendingTalentPoints: 0,
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[zeus] claim character={characterId} week={week:yyyy-MM-dd} " +
            $"exp={state.PendingExperience} talent-points={state.PendingTalentPoints} " +
            $"claims={claimsTaken} stones={state.StonesDeposited}");
        return new ZeusGiftClaimResult(
            ZeusGiftExchangeOutcome.Won,
            state.PendingExperience,
            state.PendingTalentPoints,
            claimsTaken);
    }

    public async Task<IReadOnlyList<ZeusLuckEntry>> ReadLuckAsync(
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT item_id, score, placed_at, claimed_at
            FROM public.character_zeus_luck_entries
            WHERE character_id = @characterId AND week_start = @weekStart
            ORDER BY item_id;
            """, connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("weekStart", week);
        var entries = new List<ZeusLuckEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new ZeusLuckEntry(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                !reader.IsDBNull(3)));
        }
        return entries;
    }

    public async Task<int> ReadHighestScoreAsync(
        int camp,
        RealmCalendar calendar,
        DateTimeOffset now,
        int itemId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(MAX(score), 0)
            FROM public.character_zeus_luck_entries
            WHERE week_start = @weekStart AND item_id = @itemId AND camp = @camp;
            """, connection);
        command.Parameters.AddWithValue("weekStart", week);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("camp", checked((short)camp));
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is int value ? value : 0;
    }

    public async Task<ZeusLuckDrawResult> DrawLuckAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int itemId,
        int camp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        var state = await ReadWeeklyAsync(connection, transaction, characterId, week, cancellationToken);
        if (state.LuckChances <= 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ZeusLuckDrawResult(
                ZeusLuckDrawOutcome.NoChances, 0, 0, 0, 0);
        }

        var dustId = await ResolveDustAsync(connection, transaction, characterId, cancellationToken);
        if (dustId == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ZeusLuckDrawResult(
                ZeusLuckDrawOutcome.MissingDust, 0, state.LuckChances, 0, 0);
        }
        if (!await ConsumeItemAsync(
                connection, transaction, characterId, dustId,
                ZeusGiftPolicy.DustPerLuckDraw, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new ZeusLuckDrawResult(
                ZeusLuckDrawOutcome.MissingDust, 0, state.LuckChances, 0, 0);
        }

        var score = Random.Shared.Next(1, ZeusGiftPolicy.MaximumLuckScore + 1);
        var refunded = 0;
        var luckItem = 0;
        if (score == ZeusGiftPolicy.LuckyItemRoll)
        {
            // The super lucky number hands the chosen item over at once, which the
            // client promises in NF_Z_T403.
            if (await GrantItemAsync(
                    connection, transaction, characterId, itemId, 1, cancellationToken))
            {
                luckItem = itemId;
            }
        }
        else if (ZeusGiftPolicy.IsLuckyNumber(score))
        {
            refunded = ZeusGiftPolicy.LuckyNumberDustRefund;
            await GrantItemAsync(
                connection, transaction, characterId, dustId, refunded, cancellationToken);
        }

        var draws = state.LuckDrawsUsed + 1;
        await WriteWeeklyAsync(
            connection, transaction, characterId, week,
            state.StonesDeposited,
            state.ExchangesUsed,
            state.LimitedClaimed,
            state.ClaimsTaken,
            draws,
            state.PendingExperience,
            state.PendingTalentPoints,
            now,
            cancellationToken);

        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO public.character_zeus_luck_entries (
                character_id, week_start, item_id, camp, score, placed_at, claimed_at, updated_at
            )
            VALUES (@characterId, @weekStart, @itemId, @camp, @score, @now, NULL, @now)
            ON CONFLICT (character_id, week_start, item_id) DO UPDATE
            SET score = GREATEST(
                    public.character_zeus_luck_entries.score,
                    EXCLUDED.score),
                placed_at = CASE
                    WHEN EXCLUDED.score > public.character_zeus_luck_entries.score
                    THEN EXCLUDED.placed_at
                    ELSE public.character_zeus_luck_entries.placed_at
                END,
                camp = EXCLUDED.camp,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction))
        {
            upsert.Parameters.AddWithValue("characterId", characterId);
            upsert.Parameters.AddWithValue("weekStart", week);
            upsert.Parameters.AddWithValue("itemId", itemId);
            upsert.Parameters.AddWithValue("camp", checked((short)camp));
            upsert.Parameters.AddWithValue("score", score);
            upsert.Parameters.AddWithValue("now", now);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[zeus] luck draw character={characterId} account={accountId} " +
            $"item={itemId} score={score} refunded={refunded} item-won={luckItem} " +
            $"chances-left={state.LuckChances - 1}");
        return new ZeusLuckDrawResult(
            ZeusLuckDrawOutcome.Rolled,
            score,
            state.LuckChances - 1,
            refunded,
            luckItem,
            RefundedDustItemId: refunded > 0 ? dustId : 0,
            ItemCount: luckItem != 0 ? 1 : 0);
    }

    public async Task<ZeusGiftExchangeResult> ClaimLuckPrizeAsync(
        int accountId,
        int characterId,
        RealmCalendar calendar,
        DateTimeOffset now,
        int itemId,
        int camp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        var week = RealmCalendar.GetWeekStart(calendar.GetDay(now));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockCharacterAsync(connection, transaction, characterId, cancellationToken);

        // The board: the highest score of the camp for that item and week, ties going
        // to whichever character threw it first. That is the client's own rule
        // ("如果最高分有2个以上，则只取最先投出的那个").
        var ownScore = 0;
        await using (var mine = new NpgsqlCommand("""
            SELECT score
            FROM public.character_zeus_luck_entries
            WHERE character_id = @characterId AND week_start = @weekStart
                AND item_id = @itemId;
            """, connection, transaction))
        {
            mine.Parameters.AddWithValue("characterId", characterId);
            mine.Parameters.AddWithValue("weekStart", week);
            mine.Parameters.AddWithValue("itemId", itemId);
            if (await mine.ExecuteScalarAsync(cancellationToken) is int score)
            {
                ownScore = score;
            }
        }

        int winnerCharacter = 0;
        int winningScore = 0;
        var anyEntry = false;
        await using (var board = new NpgsqlCommand("""
            SELECT character_id, score
            FROM public.character_zeus_luck_entries
            WHERE week_start = @weekStart AND item_id = @itemId AND camp = @camp
            ORDER BY score DESC, placed_at ASC, character_id ASC
            LIMIT 1;
            """, connection, transaction))
        {
            board.Parameters.AddWithValue("weekStart", week);
            board.Parameters.AddWithValue("itemId", itemId);
            board.Parameters.AddWithValue("camp", checked((short)camp));
            await using (var reader = await board.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    anyEntry = true;
                    winnerCharacter = reader.GetInt32(0);
                    winningScore = reader.GetInt32(1);
                }
            }
        }

        if (!anyEntry)
        {
            // Nothing was thrown for this item: a zero matches the zero it finds and
            // is not the first either, which is NF_Z_T408 and is what the reference
            // answers for an untouched board. The reader is closed before the commit:
            // Npgsql refuses a command on a connection whose reader is still open,
            // which is what dropped the session the first time this branch was
            // reached.
            await transaction.CommitAsync(cancellationToken);
            return new ZeusGiftExchangeResult(
                ZeusGiftExchangeOutcome.Tied, 0, string.Empty, 0);
        }

        if (winnerCharacter != characterId)
        {
            await transaction.CommitAsync(cancellationToken);
            return new ZeusGiftExchangeResult(
                ownScore == winningScore
                    ? ZeusGiftExchangeOutcome.Tied
                    : ZeusGiftExchangeOutcome.Lost,
                0,
                string.Empty,
                0);
        }

        await using (var claim = new NpgsqlCommand("""
            UPDATE public.character_zeus_luck_entries
            SET claimed_at = @now, updated_at = @now
            WHERE character_id = @characterId
                AND week_start = @weekStart
                AND item_id = @itemId
                AND claimed_at IS NULL;
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue("characterId", characterId);
            claim.Parameters.AddWithValue("weekStart", week);
            claim.Parameters.AddWithValue("itemId", itemId);
            claim.Parameters.AddWithValue("now", now);
            if (await claim.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.CommitAsync(cancellationToken);
                // NF_Z_T405: the prize was already taken.
                return new ZeusGiftExchangeResult(
                    ZeusGiftExchangeOutcome.OutOfStock, 0, string.Empty, 0);
            }
        }

        if (!await GrantItemAsync(
                connection, transaction, characterId, itemId, 1, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return new ZeusGiftExchangeResult(
                ZeusGiftExchangeOutcome.BagFull, 0, string.Empty, 0);
        }

        await transaction.CommitAsync(cancellationToken);
        Console.WriteLine(
            $"[zeus] luck claim character={characterId} account={accountId} item={itemId} " +
            $"score={winningScore} camp={camp}");
        return new ZeusGiftExchangeResult(
            ZeusGiftExchangeOutcome.Won, itemId, string.Empty, 0, Count: 1);
    }

    private static async Task LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT true FROM public.character_base WHERE id = @characterId FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new InvalidOperationException(
                $"Character {characterId} does not exist.");
        }
    }

    private static async Task<ZeusGiftDailyState> ReadDailyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int characterId,
        DateOnly day,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT usage_date, gift_round, reward_tier, required_item_id, required_item_count
            FROM public.character_zeus_gift_daily
            WHERE character_id = @characterId;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return ZeusGiftDailyState.Fresh(day);
        }

        // A row from an earlier realm day is an untouched day.
        return reader.GetFieldValue<DateOnly>(0) == day
            ? new ZeusGiftDailyState(
                day, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4))
            : ZeusGiftDailyState.Fresh(day);
    }

    private static async Task WriteDailyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        DateOnly day,
        int round,
        int tier,
        int itemId,
        int count,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.character_zeus_gift_daily (
                character_id, usage_date, gift_round, reward_tier,
                required_item_id, required_item_count, updated_at
            )
            VALUES (@characterId, @usageDate, @round, @tier, @itemId, @count, @now)
            ON CONFLICT (character_id) DO UPDATE
            SET usage_date = EXCLUDED.usage_date,
                gift_round = EXCLUDED.gift_round,
                reward_tier = EXCLUDED.reward_tier,
                required_item_id = EXCLUDED.required_item_id,
                required_item_count = EXCLUDED.required_item_count,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("usageDate", day);
        command.Parameters.AddWithValue("round", round);
        command.Parameters.AddWithValue("tier", tier);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("count", count);
        command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The Zeus gift daily row was not written.");
        }
    }

    private static async Task<ZeusGiftWeeklyState> ReadWeeklyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int characterId,
        DateOnly week,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT week_start, stones_deposited, exchanges_used, limited_claimed,
                   free_claims_used, luck_draws_used,
                   pending_experience, pending_talent_points
            FROM public.character_zeus_gift_weekly
            WHERE character_id = @characterId;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return ZeusGiftWeeklyState.Fresh(week);
        }

        return reader.GetFieldValue<DateOnly>(0) == week
            ? new ZeusGiftWeeklyState(
                week, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6),
                reader.GetInt32(7))
            : ZeusGiftWeeklyState.Fresh(week);
    }

    private static async Task WriteWeeklyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        DateOnly week,
        int stones,
        int exchanges,
        int limitedClaimed,
        int claims,
        int luckDraws,
        long pendingExperience,
        int pendingTalentPoints,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.character_zeus_gift_weekly (
                character_id, week_start, stones_deposited, exchanges_used,
                limited_claimed, free_claims_used, luck_draws_used,
                pending_experience, pending_talent_points, updated_at
            )
            VALUES (
                @characterId, @weekStart, @stones, @exchanges,
                @limited, @claims, @luckDraws, @pendingExperience,
                @pendingTalent, @now
            )
            ON CONFLICT (character_id) DO UPDATE
            SET week_start = EXCLUDED.week_start,
                stones_deposited = EXCLUDED.stones_deposited,
                exchanges_used = EXCLUDED.exchanges_used,
                limited_claimed = EXCLUDED.limited_claimed,
                free_claims_used = EXCLUDED.free_claims_used,
                luck_draws_used = EXCLUDED.luck_draws_used,
                pending_experience = EXCLUDED.pending_experience,
                pending_talent_points = EXCLUDED.pending_talent_points,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("weekStart", week);
        command.Parameters.AddWithValue("stones", stones);
        command.Parameters.AddWithValue("exchanges", exchanges);
        command.Parameters.AddWithValue("limited", limitedClaimed);
        command.Parameters.AddWithValue("claims", claims);
        command.Parameters.AddWithValue("luckDraws", luckDraws);
        command.Parameters.AddWithValue("pendingExperience", pendingExperience);
        command.Parameters.AddWithValue("pendingTalent", pendingTalentPoints);
        command.Parameters.AddWithValue("now", now);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException(
                "The Zeus gift weekly row was not written.");
        }
    }

    private static async Task<int> ReadLimitedStockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int realmId,
        int camp,
        DateOnly week,
        int itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT claimed
            FROM public.zeus_gift_limited_stock
            WHERE realm_id = @realmId AND camp = @camp
                AND week_start = @weekStart AND item_id = @itemId;
            """, connection, transaction);
        command.Parameters.AddWithValue("realmId", realmId);
        command.Parameters.AddWithValue("camp", checked((short)camp));
        command.Parameters.AddWithValue("weekStart", week);
        command.Parameters.AddWithValue("itemId", itemId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is int value ? value : 0;
    }

    private static async Task AddLimitedStockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int realmId,
        int camp,
        DateOnly week,
        int itemId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.zeus_gift_limited_stock (
                realm_id, camp, week_start, item_id, claimed, updated_at
            )
            VALUES (@realmId, @camp, @weekStart, @itemId, 1, @now)
            ON CONFLICT (realm_id, camp, week_start, item_id) DO UPDATE
            SET claimed = public.zeus_gift_limited_stock.claimed + 1,
                updated_at = EXCLUDED.updated_at;
            """, connection, transaction);
        command.Parameters.AddWithValue("realmId", realmId);
        command.Parameters.AddWithValue("camp", checked((short)camp));
        command.Parameters.AddWithValue("weekStart", week);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Takes <paramref name="count"/> of one item out of the kit bag, oldest slot
    /// first, deleting a stack that is emptied. All or nothing: when the bag does
    /// not hold enough, nothing is written.
    /// </summary>
    private static async Task<bool> ConsumeItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemId,
        int count,
        CancellationToken cancellationToken)
    {
        if (count <= 0 || itemId <= 0)
        {
            return count <= 0;
        }

        var stacks = new List<(short Slot, short Stack)>();
        await using (var read = new NpgsqlCommand("""
            SELECT slot_index, stack
            FROM public.character_items
            WHERE user_id = @characterId
                AND item_location = @location
                AND prop_id = @itemId
            ORDER BY slot_index
            FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            read.Parameters.AddWithValue("location", KitBagLocation);
            read.Parameters.AddWithValue("itemId", itemId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stacks.Add((reader.GetInt16(0), reader.GetInt16(1)));
            }
        }

        var held = stacks.Sum(static stack => (int)stack.Stack);
        if (held < count)
        {
            return false;
        }

        var remaining = count;
        foreach (var (slot, stack) in stacks)
        {
            if (remaining <= 0)
            {
                break;
            }

            var taken = Math.Min(remaining, stack);
            remaining -= taken;
            if (taken == stack)
            {
                await using var delete = new NpgsqlCommand("""
                    DELETE FROM public.character_items
                    WHERE user_id = @characterId
                        AND item_location = @location
                        AND slot_index = @slot;
                    """, connection, transaction);
                delete.Parameters.AddWithValue("characterId", characterId);
                delete.Parameters.AddWithValue("location", KitBagLocation);
                delete.Parameters.AddWithValue("slot", slot);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var update = new NpgsqlCommand("""
                    UPDATE public.character_items
                    SET stack = stack - @taken
                    WHERE user_id = @characterId
                        AND item_location = @location
                        AND slot_index = @slot;
                    """, connection, transaction);
                update.Parameters.AddWithValue("taken", checked((short)taken));
                update.Parameters.AddWithValue("characterId", characterId);
                update.Parameters.AddWithValue("location", KitBagLocation);
                update.Parameters.AddWithValue("slot", slot);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        return true;
    }

    /// <summary>
    /// The dust the character holds the most of, which is the one an exchange or a
    /// draw spends. The client's own line says any twenty of one kind do
    /// (<c>NF_L0_Z1213</c>: "只要有20个同类的粉尘就行了").
    /// </summary>
    private static async Task<int> ResolveDustAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT prop_id, SUM(stack) AS held
            FROM public.character_items
            WHERE user_id = @characterId
                AND item_location = @location
                AND prop_id = ANY(@dusts)
            GROUP BY prop_id
            ORDER BY SUM(stack) DESC, prop_id ASC
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("location", KitBagLocation);
        command.Parameters.AddWithValue("dusts", ZeusGiftPolicy.Dusts.ToArray());
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is int value ? value : 0;
    }

    /// <summary>
    /// Adds <paramref name="count"/> bound items, topping up existing stacks first
    /// and opening new slots only when they are full. Returns false when the bag
    /// cannot hold them all.
    /// </summary>
    private static async Task<bool> GrantItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemId,
        int count,
        CancellationToken cancellationToken)
    {
        if (count <= 0 || itemId <= 0)
        {
            return true;
        }

        var stackCap = await ReadStackCapAsync(connection, transaction, itemId, cancellationToken);
        var remaining = count;

        var topUp = new List<(short Slot, short Stack)>();
        var occupied = new HashSet<short>();
        await using (var read = new NpgsqlCommand("""
            SELECT slot_index
            FROM public.character_items
            WHERE user_id = @characterId AND item_location = @location
            ORDER BY slot_index
            FOR UPDATE;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("characterId", characterId);
            read.Parameters.AddWithValue("location", KitBagLocation);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                occupied.Add(reader.GetInt16(0));
            }
        }

        var sameItem = new List<(short Slot, short Stack)>();
        await using (var readSame = new NpgsqlCommand("""
            SELECT slot_index, stack
            FROM public.character_items
            WHERE user_id = @characterId AND item_location = @location AND prop_id = @itemId
            ORDER BY slot_index;
            """, connection, transaction))
        {
            readSame.Parameters.AddWithValue("characterId", characterId);
            readSame.Parameters.AddWithValue("location", KitBagLocation);
            readSame.Parameters.AddWithValue("itemId", itemId);
            await using var reader = await readSame.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sameItem.Add((reader.GetInt16(0), reader.GetInt16(1)));
            }
        }

        foreach (var (slot, stack) in sameItem)
        {
            if (remaining <= 0)
            {
                break;
            }
            if (stack >= stackCap)
            {
                continue;
            }

            var added = Math.Min(remaining, stackCap - stack);
            remaining -= added;
            await using var update = new NpgsqlCommand("""
                UPDATE public.character_items
                SET stack = stack + @added
                WHERE user_id = @characterId
                    AND item_location = @location
                    AND slot_index = @slot;
                """, connection, transaction);
            update.Parameters.AddWithValue("added", checked((short)added));
            update.Parameters.AddWithValue("characterId", characterId);
            update.Parameters.AddWithValue("location", KitBagLocation);
            update.Parameters.AddWithValue("slot", slot);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        while (remaining > 0)
        {
            var free = -1;
            for (short slot = 0; slot < KitBagSlotCount; slot++)
            {
                if (!occupied.Contains(slot))
                {
                    free = slot;
                    break;
                }
            }
            if (free < 0)
            {
                return false;
            }

            var stack = Math.Min(remaining, stackCap);
            remaining -= stack;
            occupied.Add((short)free);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.character_items (
                    user_id, item_location, slot_index, prop_id,
                    item_quality, item_grade, bound, stack, item_exp, holy_suit_code
                )
                VALUES (
                    @characterId, @location, @slot, @itemId,
                    1, 1, 1, @stack, 0, 0
                );
                """, connection, transaction);
            insert.Parameters.AddWithValue("characterId", characterId);
            insert.Parameters.AddWithValue("location", KitBagLocation);
            insert.Parameters.AddWithValue("slot", checked((short)free));
            insert.Parameters.AddWithValue("itemId", itemId);
            insert.Parameters.AddWithValue("stack", checked((short)stack));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        return true;
    }

    private static async Task<int> ReadStackCapAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int itemId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT stats ->> 'Overlap'
            FROM public.item_templates
            WHERE id = @itemId;
            """, connection, transaction);
        command.Parameters.AddWithValue("itemId", itemId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is string text &&
            int.TryParse(text, out var cap) &&
            cap is > 0 and <= 999
                ? cap
                : DefaultStackCap;
    }
}
