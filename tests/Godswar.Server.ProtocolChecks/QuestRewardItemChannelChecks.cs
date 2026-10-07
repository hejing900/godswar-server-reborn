using System.Buffers.Binary;
using Godswar.Server.Application.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Locks the quest reward item channel: what the answer promises, what the
/// client announces back, and the claim table that makes the item survive a
/// relog.
/// </summary>
/// <remarks>
/// The bug this pins: the client pays a quest reward into its own bag and
/// announces it on opcode 10056, and while this server only acknowledged that
/// announcement the item lived in the client alone - the next relog replaced the
/// bag with the server's copy and the reward was gone.
/// </remarks>
internal static class QuestRewardItemChannelChecks
{
    public const string CheckName = "Quest reward item channel";

    public static Task RunAsync()
    {
        CheckCapturedRewardItems();
        CheckCapturedAnnouncements();
        CheckBagRecordEchoIsNotAPickup();
        CheckClaimTableIsRegistered();
        CheckGmOverrides();
        CheckRewardStoreIsForwarded();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The broad game store implements the reward interface by forwarding to the
    /// dedicated store, and an interface member it does not forward silently falls
    /// back to the default implementation, which refuses the work. That is exactly
    /// how a quest reward ended up answered with <c>Unsupported</c> and never
    /// written, so every member has to be forwarded.
    /// </summary>
    private static void CheckRewardStoreIsForwarded()
    {
        foreach (var member in new[]
                 {
                     "PickupMonsterLootAsync",
                     "GrantQuestRewardItemAsync",
                     "ApplyPetMonsterKillExperienceAsync"
                 })
        {
            var declaration = typeof(PostgresGameStore).GetMethod(
                member,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            Check.True(
                declaration?.DeclaringType == typeof(PostgresGameStore),
                $"the game store declares {member} instead of inheriting the interface default");
        }
    }

    /// <summary>
    /// The GM tool's overrides: a replaced menu rewrites the answer's slots, and
    /// a quest with no override keeps the captured bytes exactly.
    /// </summary>
    /// <remarks>
    /// The catalog is process-wide, so this check installs its own snapshot and
    /// puts the empty one back before it returns.
    /// </remarks>
    /// <summary>临时：把一个帧的头部和奖励区打成可对比的形式。</summary>
    private static void PrintFrame(string label, byte[] packet, int rewardOffset)
    {
        var from = Math.Max(0, rewardOffset - 8);
        var count = Math.Min(packet.Length - from, 176);
        Console.WriteLine(
            $"[frame-dump] {label} len={packet.Length} " +
            $"head={Convert.ToHexString(packet.AsSpan(0, Math.Min(20, packet.Length)))}");
        Console.WriteLine(
            $"[frame-dump] {label} @{from}..+{count} = " +
            Convert.ToHexString(packet.AsSpan(from, count)));
        for (var slot = 0; slot < 4; slot++)
        {
            var start = rewardOffset + (slot * 72);
            if (start + 36 > packet.Length)
            {
                break;
            }

            Console.WriteLine(
                $"[frame-dump] {label} slot{slot} item=" +
                $"{BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(start + 8, 4))} " +
                $"quality={packet[start + 32]} star={packet[start + 33]}");
        }
    }

    private static void CheckGmOverrides()
    {
        const int answerRecordOffset = 64;
        const int recordBytes = 72;
        var captured = PacketBuilder.QuestAnswer(5091u, 5103u, 518u);
        Check.Equal(
            3876u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                captured.AsSpan(answerRecordOffset + 8, 4)),
            "without an override quest 518 answers with the gift bag");

        try
        {
            QuestRewardContentCatalog.Install(new QuestRewardContentSnapshot(
                [
                    new QuestRewardSlotOverride(518u, 0, 9999u, ItemGrantAttributes.None),
                    new QuestRewardSlotOverride(518u, 1, 8888u, ItemGrantAttributes.None)
                ],
                [new QuestRewardValueOverride(518u, 4321, 7, 1234, 56)]));

            var replaced = PacketBuilder.QuestAnswer(5091u, 5103u, 518u);
            Check.Equal(
                9999u,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    replaced.AsSpan(answerRecordOffset + 8, 4)),
                "the override replaces reward slot 0");
            Check.Equal(
                8888u,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    replaced.AsSpan(
                        answerRecordOffset + recordBytes + 8,
                        4)),
                "the override fills reward slot 1");
            Check.Equal(
                uint.MaxValue,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    replaced.AsSpan(
                        answerRecordOffset + 2 * recordBytes + 8,
                        4)),
                "a slot the override leaves out is free");

            // Everything outside the item ids keeps the captured shape, which is
            // the one the client already renders.
            Check.True(
                captured.AsSpan(answerRecordOffset + 12, 4)
                    .SequenceEqual(
                        replaced.AsSpan(answerRecordOffset + 12, 4)),
                "the replacement keeps the captured slot shape");

            // 品质（和等级 grade）在奖励档位的 +32 上：客户端就是拿它拼"精致的"这种
            // 前缀（名字表里只有"轻皮护胸"）。被覆盖的槽位必须写进去，没覆盖的槽位
            // 不能被动 - 520 的抓包本来就是 03 01，所以没覆盖时它仍是 03。
            QuestRewardContentCatalog.Install(new QuestRewardContentSnapshot(
                [
                    new QuestRewardSlotOverride(
                        520u,
                        0,
                        2100u,
                        ItemGrantAttributes.None with
                        {
                            Quality = 3,
                            Attribute1 = 80,
                            Attribute3 = 40
                        })
                ],
                []));
            var withAttributes = PacketBuilder.ResolveQuestRewardArea(520u, 8u);
            Check.Equal(
                (byte)3,
                withAttributes[32],
                "the override writes the configured quality into the reward slot");
            Check.Equal(
                (byte)1,
                withAttributes[33],
                "an unconfigured star stays 1 (the tool's 0/1 default)");
            Check.Equal(
                80u,
                BinaryPrimitives.ReadUInt32LittleEndian(withAttributes.AsSpan(12, 4)),
                "attribute slot 1 carries the configured attribute id");
            Check.Equal(
                uint.MaxValue,
                BinaryPrimitives.ReadUInt32LittleEndian(withAttributes.AsSpan(16, 4)),
                "an attribute slot the override skips is empty");
            Check.Equal(
                40u,
                BinaryPrimitives.ReadUInt32LittleEndian(withAttributes.AsSpan(20, 4)),
                "attribute slot 3 carries its own id");
            Check.Equal(
                uint.MaxValue,
                BinaryPrimitives.ReadUInt32LittleEndian(withAttributes.AsSpan(28, 4)),
                "the fifth attribute slot is empty too");
            Check.Equal(
                (byte)3,
                PacketBuilder.ResolveBaseQuestRewardArea(520u, 8u)[32],
                "a quest the tool did not touch keeps its captured quality");
            Check.Equal(
                (byte)1,
                PacketBuilder.ResolveBaseQuestRewardArea(520u, 8u)[33],
                "and its captured star");
            Check.Equal(
                (byte)1,
                PacketBuilder.ResolveQuestRewardArea(520u, 8u)[34],
                "the two bytes after quality and star are left alone");

            // 519 的"后续详情"帧命中抓包（CapturedNextQuestDetails 里有 519、没有 520），
            // 修之前那条分支把抓包字节原样返回、奖励区根本不写 —— 这正是"520 能看到奖励、
            // 519 看不到"的唯一区别。这里断言覆盖现在能进到那条帧里。
            QuestRewardContentCatalog.Install(new QuestRewardContentSnapshot(
                [
                    new QuestRewardSlotOverride(
                        519u,
                        0,
                        4321u,
                        ItemGrantAttributes.None with { Quality = 3 })
                ],
                []));
            var detail = PacketBuilder.QuestNextDetail(5103u, 519u);
            Check.Equal(
                4321u,
                BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(60 + 8, 4)),
                "an override reaches the captured follow-up detail frame (519)");
            Check.Equal(
                (byte)3,
                detail[60 + 32],
                "and its quality byte travels with it");
            Check.Equal(uint.MaxValue,
                BinaryPrimitives.ReadUInt32LittleEndian(detail.AsSpan(60 + 72 + 8, 4)),
                "a GM menu does not retain an original reward in an omitted slot");

            // 临时对比：把"详情帧"和"后续详情帧"的头部与奖励区开头都打出来，
            // 用来看客户端那句话（名字取到第二个物品、品质取到第一个物品）是哪一种错位。
            var answer = PacketBuilder.QuestAnswer(5103u, 5104u, 519u);
            PrintFrame("10082 (quest answer)", answer, 64);
            PrintFrame("10076 (next detail)", detail, 60);

            QuestRewardContentCatalog.Install(new QuestRewardContentSnapshot(
                [
                    new QuestRewardSlotOverride(518u, 0, 9999u, ItemGrantAttributes.None),
                    new QuestRewardSlotOverride(518u, 1, 8888u, ItemGrantAttributes.None)
                ],
                [new QuestRewardValueOverride(518u, 4321, 7, 1234, 56)]));

            var payout = QuestRewardContentCatalog.Current.Resolve(
                518u,
                new QuestRewardPayout(1, 2, 3, 4));
            Check.True(
                payout == new QuestRewardPayout(4321, 7, 1234, 56),
                "the override replaces the quest payout");
            Check.True(
                QuestRewardContentCatalog.Current.Resolve(
                    519u,
                    new QuestRewardPayout(1, 2, 3, 4)) ==
                    new QuestRewardPayout(1, 2, 3, 4),
                "a quest with no values row keeps its own payout");

            // The original menu remains source data, but the active menu is owned
            // by the GM whenever an override exists.
            Check.True(
                PacketBuilder.ResolveBaseQuestRewardArea(518u, 4u)[8..12]
                    .SequenceEqual(Convert.FromHexString("240F0000")),
                "the built-in menu still carries the gift bag");
            Check.True(
                BinaryPrimitives.ReadUInt32LittleEndian(
                    PacketBuilder.ResolveQuestRewardArea(518u, 4u)
                        .AsSpan(8, 4)) == 9999u,
                "the overridden menu carries the replacement");
        }
        finally
        {
            QuestRewardContentCatalog.Install(QuestRewardContentSnapshot.Empty);
        }

        Check.True(
            QuestRewardContentCatalog.Current.TryGetSlots(518u, out _) == false,
            "installing the empty snapshot removes every override");

        Check.Throws<InvalidDataException>(
            () => new QuestRewardContentSnapshot(
                [new QuestRewardSlotOverride(0u, 0, 1u, ItemGrantAttributes.None)],
                []),
            "a slot override without a quest is rejected");
        Check.Throws<InvalidDataException>(
            () => new QuestRewardContentSnapshot(
                [new QuestRewardSlotOverride(1u, 8, 1u, ItemGrantAttributes.None)],
                []),
            "a slot outside the menu is rejected");
        Check.Throws<InvalidDataException>(
            () => new QuestRewardContentSnapshot(
                [],
                [new QuestRewardValueOverride(1u, -1, 0, 0, 0)]),
            "a negative payout is rejected");
    }

    /// <summary>
    /// The reward items the answers promise, read out of the same bytes the
    /// packet builder sends. These are the numbers a claim is checked against.
    /// </summary>
    private static void CheckCapturedRewardItems()
    {
        var quest518 = QuestRewardItemCatalog.Resolve(518);
        Check.True(
            quest518.Count == 1 &&
            quest518[0].SlotIndex == 0 &&
            quest518[0].ItemId == 3876u,
            "quest 518 promises the newbie gift bag in slot 0");

        var quest519 = QuestRewardItemCatalog.Resolve(519);
        Check.True(
            quest519.Count == 4 &&
            quest519.Select(static item => item.SlotIndex)
                .SequenceEqual([0, 1, 2, 3]) &&
            quest519.Select(static item => item.ItemId)
                .SequenceEqual([1000u, 1400u, 1700u, 1800u]),
            "quest 519 promises the four class weapons in slots 0-3");

        var quest520 = QuestRewardItemCatalog.Resolve(520);
        Check.True(
            quest520.Count == 1 &&
            quest520[0].SlotIndex == 0 &&
            quest520[0].ItemId == 2100u,
            "quest 520 promises the light armor in slot 0");

        Check.True(
            QuestRewardItemCatalog.Resolve(99999).Count == 0,
            "a quest with no captured reward area promises nothing");
    }

    /// <summary>
    /// The announcements the installed client actually sent after handing in
    /// quests 518 and 519, captured on the reference server (2026-09-24,
    /// packets 37889 and 38295). Both are 40-byte opcode-10056 frames in the
    /// ground-pickup shape, and each names a reward item its own quest promised.
    /// </summary>
    private static void CheckCapturedAnnouncements()
    {
        var giftBag = Convert.FromHexString(
            "280048274834CA21010000000000000001000000240F0000FA4FEA5A" +
            "000000001CFD1A009BE47F00");
        Check.True(
            giftBag.Length == 40 &&
            GameClientHandler.TryReadGroundLootPickup(
                giftBag.AsSpan(4),
                out var giftBagSlot,
                out var giftBagItem,
                out _,
                out _) &&
            giftBagItem == 3876u &&
            giftBagSlot == 1,
            "the captured 518 announcement names gift bag 3876");

        var longStaff = Convert.FromHexString(
            "280048274035CA2101000000000000000200000008070000FA4FEA5A" +
            "000000001CFD1A009BE47F00");
        Check.True(
            longStaff.Length == 40 &&
            GameClientHandler.TryReadGroundLootPickup(
                longStaff.AsSpan(4),
                out var longStaffSlot,
                out var longStaffItem,
                out _,
                out _) &&
            longStaffItem == 1800u &&
            longStaffSlot == 2,
            "the captured 519 announcement names long staff 1800");

        // The announcement is payable exactly because the quest promised the
        // item: this is the check the handler performs before it writes anything.
        Check.True(
            QuestRewardItemCatalog.Resolve(518)
                .Any(static item => item.ItemId == 3876u) &&
            QuestRewardItemCatalog.Resolve(519)
                .Any(static item => item.ItemId == 1800u),
            "both captured announcements name an item their quest promised");

        // The hand-in acknowledgement echoes the slot the client picked, so the
        // reward it names and the promise it is checked against cannot drift.
        var ack = Convert.FromHexString(
            "78006627E3130000EF1300000602000000000000000000000000000028000000" +
            "0000000002000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000" +
            "000000000000000000000000000000000000000000000000");
        Check.True(
            ack.Length == 120 &&
            BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(12)) == 518u &&
            BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(16)) == 0u,
            "the captured 518 acknowledgement echoes reward slot 0");
    }

    /// <summary>
    /// The other request that shares the 10056 shape: the client's echo of a bag
    /// record the server sent, which carries 0xFFFFFFFF where a pickup carries a
    /// client handle. A relog sends one per owned item, so a pickup path that
    /// claimed them would hand out whatever the character already owns.
    /// </summary>
    private static void CheckBagRecordEchoIsNotAPickup()
    {
        // Captured S2C 10056 bag records (2026-09-24, packets 79 and 81): the
        // long staff 1800 and the raw gem 12040 as the server announced them.
        var longStaffRecord = Convert.FromHexString(
            "28004827FFFFFFFF01000000000000000200000008070000B1223697" +
            "00000000C4FC19009BE47F00");
        Check.Equal(
            uint.MaxValue,
            BinaryPrimitives.ReadUInt32LittleEndian(
                longStaffRecord.AsSpan(4)),
            "a captured bag record carries 0xFFFFFFFF at +4");

        // The same frame still parses as the pickup descriptor - the shape is
        // shared on purpose - so the handler is what has to refuse it, by that
        // word, before any ground item is reserved.
        Check.True(
            GameClientHandler.TryReadGroundLootPickup(
                longStaffRecord.AsSpan(4),
                out var echoedSlot,
                out var echoedItem,
                out _,
                out _) &&
            echoedItem == 1800u &&
            echoedSlot == 2,
            "the bag record parses as the shared 10056 descriptor");

        // A real announcement carries a client handle there instead.
        var announcement = Convert.FromHexString(
            "280048274035CA2101000000000000000200000008070000FA4FEA5A" +
            "000000001CFD1A009BE47F00");
        Check.True(
            BinaryPrimitives.ReadUInt32LittleEndian(
                announcement.AsSpan(4)) != uint.MaxValue,
            "a real pickup announces a client handle at +4");
    }

    private static void CheckClaimTableIsRegistered()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static migration => migration.Id ==
                "20260927_211_quest_reward_item_claims");
        foreach (var fragment in new[]
                 {
                     "CREATE TABLE public.quest_reward_item_claims",
                     "REFERENCES public.character_base(id)",
                     "REFERENCES public.item_templates(id)",
                     "slot_index BETWEEN 0 AND 7",
                     "PRIMARY KEY (character_id, quest_id, slot_index)"
                 })
        {
            Check.True(
                migration.Sql.Contains(fragment, StringComparison.Ordinal),
                $"quest reward claim migration contains {fragment}");
        }

        // One row per reward slot is what makes the grant idempotent, so the key
        // has to name the quest and the slot, not just the character.
        Check.True(
            migration.Sql.Contains(
                "quest_id integer NOT NULL",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "quantity smallint NOT NULL CHECK (quantity > 0)",
                StringComparison.Ordinal),
            "the claim records the quest, the slot and the quantity");
    }
}
