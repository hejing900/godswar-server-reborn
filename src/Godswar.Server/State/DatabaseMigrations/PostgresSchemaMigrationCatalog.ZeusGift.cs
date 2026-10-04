namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    /// <summary>
    /// Per-character state for the Zeus gift event's delivery loop
    /// (<c>NPC_FLAG_SYS_ZEUS = 26</c>, the follower <c>Athens_113</c> /
    /// <c>Sparta_113</c>): how many deliveries the realm day has already had, what
    /// the follower is asking for this round, and the reward level the next
    /// delivery pays.
    /// </summary>
    /// <remarks>
    /// The client's own lines set the quota and the ladder ("每天最多可以献礼10次,
    /// 周末则是12次", "献礼次数越多…获得的奖励也将越高", <c>NF_L0_Z1401</c> and
    /// <c>NF_L0_Z1511</c>), and the event page says abandoning a requirement resets
    /// the reward level while the deliveries already made still count ("之前的奖励
    /// 级别将归零"), which is why the round and the tier are separate columns. The
    /// row carries the realm day it belongs to, so a row from an earlier day reads
    /// as an untouched day and no scheduled reset is needed.
    /// </remarks>
    private static PostgresSchemaMigration CreateZeusGiftDailyState() =>
        new(
            "20261003_220_zeus_gift_daily_state",
            "Persist per-character Zeus gift delivery round, tier and current requirement",
            """
            CREATE TABLE public.character_zeus_gift_daily (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                usage_date date NOT NULL,
                gift_round integer NOT NULL
                    DEFAULT 0
                    CHECK (gift_round >= 0 AND gift_round <= 12),
                reward_tier integer NOT NULL
                    DEFAULT 0
                    CHECK (reward_tier >= 0 AND reward_tier <= 12),
                required_item_id integer NOT NULL
                    DEFAULT 0
                    CHECK (required_item_id >= 0),
                required_item_count integer NOT NULL
                    DEFAULT 0
                    CHECK (required_item_count >= 0 AND required_item_count <= 9),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (character_id)
            );

            COMMENT ON TABLE
                public.character_zeus_gift_daily IS
                'Authoritative Zeus gift delivery state: usage_date is the realm day the round belongs to, gift_round is how many deliveries that day has had, reward_tier is the level the next one pays (reset by an abandoned requirement), required_item_id/count is what the follower is asking for.';
            """);

    /// <summary>
    /// Per-character weekly state for the saint half of the event
    /// (<c>Athens_114</c> / <c>Sparta_114</c>): praying stones handed in, dust
    /// exchanges spent and free experience claims taken.
    /// </summary>
    /// <remarks>
    /// The event page sets every window and quota: stones are taken Monday to
    /// Friday ("周一0:00至周五23:55"), the exchange opens Saturday noon to Sunday
    /// night, each week's exchange count is spent rather than carried ("每一周的
    /// 奖励兑换次数都只可在当周使用，无法累积至下一周"), and the stones handed in
    /// during the week are what buys the weekend exchanges and the luck chances.
    /// The row carries the Monday of the week it belongs to.
    /// </remarks>
    private static PostgresSchemaMigration CreateZeusGiftWeeklyState() =>
        new(
            "20261003_221_zeus_gift_weekly_state",
            "Persist per-character Zeus praying stone deposits, dust exchanges and free claims",
            """
            CREATE TABLE public.character_zeus_gift_weekly (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                week_start date NOT NULL,
                stones_deposited integer NOT NULL
                    DEFAULT 0
                    CHECK (stones_deposited >= 0),
                exchanges_used integer NOT NULL
                    DEFAULT 0
                    CHECK (exchanges_used >= 0),
                limited_claimed integer NOT NULL
                    DEFAULT 0
                    CHECK (limited_claimed >= 0),
                free_claims_used integer NOT NULL
                    DEFAULT 0
                    CHECK (free_claims_used >= 0),
                luck_draws_used integer NOT NULL
                    DEFAULT 0
                    CHECK (luck_draws_used >= 0),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (character_id),
                CONSTRAINT ck_character_zeus_gift_weekly_monday
                    CHECK (EXTRACT(ISODOW FROM week_start) = 1)
            );

            COMMENT ON TABLE
                public.character_zeus_gift_weekly IS
                'Authoritative Zeus saint state for one week: week_start is always a Monday, stones_deposited is what the character handed in Monday-Friday, exchanges_used and limited_claimed are the weekend dust exchanges, free_claims_used is the free experience claim, luck_draws_used is how many of the stone-bought luck chances have been spent.';
            """);

    /// <summary>
    /// The realm's own weekly praying-stone total. The event page makes the free
    /// claim a realm-wide reward: "全服缴纳"祈祷石"的总个数越多，全服所有玩家能够
    /// 免费领取专长奖励的次数也越多".
    /// </summary>
    private static PostgresSchemaMigration CreateZeusGiftRealmStoneTotal() =>
        new(
            "20261003_222_zeus_gift_realm_stones",
            "Persist the realm-wide Zeus praying stone total for one week",
            """
            CREATE TABLE public.zeus_gift_realm_stones (
                realm_id integer NOT NULL,
                week_start date NOT NULL,
                stones_deposited bigint NOT NULL
                    DEFAULT 0
                    CHECK (stones_deposited >= 0),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (realm_id, week_start),
                CONSTRAINT ck_zeus_gift_realm_stones_monday
                    CHECK (EXTRACT(ISODOW FROM week_start) = 1)
            );

            COMMENT ON TABLE
                public.zeus_gift_realm_stones IS
                'Realm-wide Zeus praying stone total for one week: the client promises every player more free experience and talent claims as the whole realm hands in more stones.';
            """);

    /// <summary>
    /// One character's entries in the luck contest: the score it threw at one item
    /// of one week, when, and whether the prize has been taken.
    /// </summary>
    /// <remarks>
    /// The client's own rules (<c>NF_Z_T2</c>, <c>NF_Z_T3011</c>, <c>NF_Z_T404</c>)
    /// are that a draw books one score per item, that only the highest counts, that
    /// the highest score of the faction wins the item when the contest closes on
    /// Sunday noon, and that a tie is won by whoever threw first - which is why the
    /// winner is decided by ordering on <c>score DESC, placed_at ASC</c>.
    /// </remarks>
    private static PostgresSchemaMigration CreateZeusLuckEntries() =>
        new(
            "20261003_223_zeus_luck_entries",
            "Persist per-character Zeus luck contest scores, one row per item per week",
            """
            CREATE TABLE public.character_zeus_luck_entries (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                week_start date NOT NULL,
                item_id integer NOT NULL
                    CHECK (item_id > 0),
                camp smallint NOT NULL,
                score integer NOT NULL
                    CHECK (score >= 1 AND score <= 1000),
                placed_at timestamptz NOT NULL,
                claimed_at timestamptz,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (character_id, week_start, item_id),
                CONSTRAINT ck_character_zeus_luck_entries_monday
                    CHECK (EXTRACT(ISODOW FROM week_start) = 1)
            );

            CREATE INDEX ix_character_zeus_luck_entries_board
                ON public.character_zeus_luck_entries (
                    week_start, item_id, camp, score DESC, placed_at ASC);

            COMMENT ON TABLE
                public.character_zeus_luck_entries IS
                'Zeus luck contest entries: one row per character per week per item, score is the highest that character threw at that item, claimed_at records the Sunday prize pickup.';
            """);

    /// <summary>
    /// How much of each limited shelf the realm has already handed out this week.
    /// </summary>
    /// <remarks>
    /// The event page makes the limited shelves first come, first served: "限量版
    /// 的奖励被兑换完就不可再兑换，先到才有机会得哦". The stock itself is a
    /// server-side choice, because neither the capture nor the client states a
    /// number; the row is what makes the promise true across the realm.
    /// </remarks>
    private static PostgresSchemaMigration CreateZeusGiftLimitedStock() =>
        new(
            "20261003_224_zeus_gift_limited_stock",
            "Persist the realm-wide Zeus limited shelf stock handed out this week",
            """
            CREATE TABLE public.zeus_gift_limited_stock (
                realm_id integer NOT NULL,
                week_start date NOT NULL,
                item_id integer NOT NULL
                    CHECK (item_id > 0),
                claimed integer NOT NULL
                    DEFAULT 0
                    CHECK (claimed >= 0),
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (realm_id, week_start, item_id),
                CONSTRAINT ck_zeus_gift_limited_stock_monday
                    CHECK (EXTRACT(ISODOW FROM week_start) = 1)
            );

            COMMENT ON TABLE
                public.zeus_gift_limited_stock IS
                'Realm-wide Zeus limited shelf stock: how many of each limited prize the whole realm has already taken this week, which is what makes the shelf first come, first served.';
            """);

    private static PostgresSchemaMigration CreateZeusGiftBankedReward() =>
        new(
            "20261003_225_zeus_gift_banked_reward",
            "Bank the experience and talent points each praying stone is worth until the claim pays them",
            """
            ALTER TABLE public.character_zeus_gift_weekly
                ADD COLUMN pending_experience bigint NOT NULL
                    DEFAULT 0
                    CHECK (pending_experience >= 0),
                ADD COLUMN pending_talent_points integer NOT NULL
                    DEFAULT 0
                    CHECK (pending_talent_points >= 0);

            COMMENT ON COLUMN
                public.character_zeus_gift_weekly.pending_experience IS
                'Experience the week''s praying stones have banked but not yet paid: the claim line hands the whole bank over and empties it.';
            COMMENT ON COLUMN
                public.character_zeus_gift_weekly.pending_talent_points IS
                'Talent points the week''s praying stones have banked but not yet paid.';

            COMMENT ON TABLE public.character_zeus_gift_weekly IS
                'Authoritative Zeus saint state for one week: week_start is always a Monday, so a new week reads as empty and the whole week - stones, exchanges, claims and the banked reward - is cleared when Monday comes around. stones_deposited is what the character handed in, exchanges_used and limited_claimed are the weekend dust exchanges, free_claims_used counts the claims taken, luck_draws_used is how many of the stone-bought luck chances have been spent, and pending_experience and pending_talent_points are the reward the stones banked.';
            """);

    private static PostgresSchemaMigration CreateZeusGiftLimitedStockPerCamp() =>
        new(
            "20261003_226_zeus_gift_limited_stock_camp",
            "Keep the Zeus limited shelf stock per camp, so Athens and Sparta do not share a shelf",
            """
            ALTER TABLE public.zeus_gift_limited_stock
                DROP CONSTRAINT zeus_gift_limited_stock_pkey;

            ALTER TABLE public.zeus_gift_limited_stock
                ADD COLUMN camp smallint NOT NULL
                    DEFAULT 0
                    CHECK (camp >= 0 AND camp <= 1);

            ALTER TABLE public.zeus_gift_limited_stock
                ADD CONSTRAINT zeus_gift_limited_stock_pkey
                    PRIMARY KEY (realm_id, camp, week_start, item_id);

            COMMENT ON COLUMN public.zeus_gift_limited_stock.camp IS
                'The camp whose shelf this stock belongs to: 0 Sparta and 1 Athens. The two sides count separately, so one running a prize out leaves the other side''s shelf untouched.';

            COMMENT ON TABLE public.zeus_gift_limited_stock IS
                'Realm-and-camp-wide Zeus limited shelf stock: how many of each limited prize one side of one realm has already taken this week, which is what makes the shelf first come, first served.';
            """);
}
