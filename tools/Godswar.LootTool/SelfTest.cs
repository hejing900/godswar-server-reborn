using System.Globalization;
using System.Text;

namespace Godswar.LootTool;

/// <summary>
/// Headless verification of the tool's data layer, so the database behaviour can
/// be checked without clicking through the window. Writes a report next to the
/// executable and (when launched from a console) to standard output.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        var report = new StringBuilder();
        var failures = 0;
        void Line(string text = "")
        {
            report.AppendLine(text);
            Console.WriteLine(text);
        }

        var settings = LootToolSettings.Load();
        Line($"数据库      : {settings.Host}:{settings.Port}/{settings.Database}（用户 {settings.Username}）");
        Line($"客户端目录  : {settings.ClientRoot}");
        Line();

        await using var store = new LootStore();
        try
        {
            store.Connect(settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            Line($"[失败] 无法连接数据库: {ex.Message}");
            return Finish(report, 1);
        }

        var catalog = ClientTextCatalog.Load(settings.ClientRoot);
        Line($"[信息] 数据库 {store.DatabaseName}");
        Line($"[信息] 客户端中文名: 怪物 {catalog.MonsterNameCount} 条 / 物品 {catalog.ItemNameCount} 条");
        if (catalog.MonsterNameCount == 0)
        {
            Line("[警告] 没有读到怪物中文名，请检查客户端目录是否正确。");
            failures++;
        }

        List<MonsterRow> monsters;
        List<ItemRow> items;
        try
        {
            monsters = await store.LoadMonstersAsync();
            items = await store.LoadItemsAsync();
        }
        catch (Exception ex)
        {
            Line($"[失败] 读取怪物/物品目录失败: {ex.Message}");
            return Finish(report, 1);
        }

        Line($"[信息] 怪物模板 {monsters.Count} 个，其中已配掉落 {monsters.Count(static m => m.HasLootTable)} 个，" +
            $"能刷出来 {monsters.Count(static m => m.IsSpawned)} 个（刷怪点合计 {monsters.Sum(static m => m.SpawnCount)}）");
        Line($"[信息] 物品目录 {items.Count} 个");
        Line();

        var spawnedWithoutLoot = monsters
            .Where(static m => m.IsSpawned && !m.HasLootTable)
            .OrderByDescending(static m => m.SpawnCount)
            .ToList();
        if (spawnedWithoutLoot.Count > 0)
        {
            Line("── 会刷出来但还没配掉落的怪（可直接配） ─────────");
            foreach (var monster in spawnedWithoutLoot)
            {
                Line($"  {monster.TemplateKey,-30} 刷怪点 {monster.SpawnCount,3}  " +
                    $"{catalog.MonsterName(monster.TemplateKey, monster.EnglishName)}");
            }

            Line();
        }

        var itemsById = items.ToDictionary(static item => item.Id);
        var chineseNames = monsters.Count(m =>
            !string.IsNullOrWhiteSpace(catalog.MonsterName(m.TemplateKey, string.Empty)));
        Line($"[信息] 怪物模板中能解析出中文名的: {chineseNames} 个");
        Line();

        Line("── 现有掉落表内容 ──────────────────────────────");
        var configured = monsters.Where(static m => m.HasLootTable).ToList();
        if (configured.Count == 0)
        {
            Line("（当前数据库没有任何掉落表）");
        }

        foreach (var monster in configured)
        {
            var chinese = catalog.MonsterName(monster.TemplateKey, monster.EnglishName);
            var rules = await store.LoadRulesAsync(monster.TemplateKey);
            Line($"{monster.TemplateKey}  中文名={chinese}  英文名={monster.EnglishName}  " +
                $"区域={monster.Scenes}  地图={monster.Maps}  上限={monster.MaximumDrops}  " +
                $"启用={monster.Enabled}  刷怪点={monster.SpawnCount}" +
                (monster.IsSpawned ? string.Empty : "  ⚠ 该怪不会刷出来，掉落不会生效"));
            foreach (var rule in rules)
            {
                itemsById.TryGetValue(rule.ItemId, out var item);
                var itemName = item is null
                    ? "⚠ 未知物品"
                    : catalog.ItemName(item.NameKey, item.DisplayName);
                Line($"    序号 {rule.LootIndex,2}  物品 {rule.ItemId,6}  {itemName}  " +
                    $"概率 {(rule.ChanceBasisPoints / 100d).ToString("0.##", CultureInfo.InvariantCulture)}%  " +
                    $"数量 {rule.MinimumQuantity}-{rule.MaximumQuantity}  启用={rule.Enabled}");
            }
        }

        Line();
        Line("── 写入回环测试 ────────────────────────────────");
        var target = monsters.FirstOrDefault(static m => !m.HasLootTable);
        if (target is null)
        {
            Line("[跳过] 找不到未配置掉落的怪物，无法做写入回环。");
        }
        else
        {
            var probeItemId = itemsById.ContainsKey(4001) ? 4001 : items.Min(static i => i.Id);
            var probeKey = target.TemplateKey;
            Line($"目标怪物 {probeKey}（原本未配置），临时写入 1 条规则后删除。");
            try
            {
                await store.SaveLootAsync(
                    probeKey,
                    1,
                    true,
                    [new LootRuleInput(0, probeItemId, 2500, 1, 1, true)]);
                var header = await store.LoadHeaderAsync(probeKey);
                var rules = await store.LoadRulesAsync(probeKey);
                var ok = header is { MaximumDrops: 1, Enabled: true } &&
                    rules.Count == 1 &&
                    rules[0].ItemId == probeItemId;
                Line(ok
                    ? $"[通过] 写入后读回一致：上限={header!.MaximumDrops} 规则={rules.Count} 物品={rules[0].ItemId}"
                    : "[失败] 写入后读回不一致。");
                if (!ok)
                {
                    failures++;
                }

                await store.SaveLootAsync(
                    probeKey,
                    2,
                    true,
                    [
                        new LootRuleInput(0, probeItemId, 2500, 1, 1, true),
                        new LootRuleInput(1, probeItemId, 5000, 2, 3, true)
                    ]);
                var updated = await store.LoadRulesAsync(probeKey);
                Line(updated.Count == 2
                    ? "[通过] 追加规则成功，共 2 条。"
                    : $"[失败] 追加规则后只有 {updated.Count} 条。");
                if (updated.Count != 2)
                {
                    failures++;
                }

                await store.SaveLootAsync(
                    probeKey,
                    1,
                    true,
                    [new LootRuleInput(0, probeItemId, 2500, 1, 1, true)]);
                var pruned = await store.LoadRulesAsync(probeKey);
                Line(pruned.Count == 1
                    ? "[通过] 删除规则成功，被移除的序号已清理。"
                    : $"[失败] 删除规则后仍有 {pruned.Count} 条。");
                if (pruned.Count != 1)
                {
                    failures++;
                }

                // 「拾取后绑定」三态回环：true / false / NULL 都要原样写回，
                // NULL 绝不能被落成 false（那会把「跟随物品模板」改成「可交易」）
                if (!await store.HasBoundOnPickupColumnAsync())
                {
                    Line("[跳过] 数据库的 monster_loot_rules 还没有 bound_on_pickup 列" +
                         "（服务端迁移 20260927_213 未应用），跳过「拾取后绑定」回环。");
                    // 列不在时也不许静默按默认值写：选了「可交易 / 拾取绑定」必须被拦住
                    try
                    {
                        await store.SaveLootAsync(
                            probeKey,
                            1,
                            true,
                            [
                                new LootRuleInput(
                                    0,
                                    probeItemId,
                                    2500,
                                    1,
                                    1,
                                    true,
                                    true)
                            ]);
                        Line("[失败] 库里还没有 bound_on_pickup 列，却把「拾取绑定」写进去了。");
                        failures++;
                    }
                    catch (LootValidationException ex)
                    {
                        Line($"[通过] 缺列时写「拾取绑定」被拦住：{ex.Message}");
                    }
                }
                else
                {
                    foreach (var (bound, label) in new (bool?, string)[]
                             {
                                 (true, "拾取绑定"),
                                 (false, "可交易"),
                                 (null, "跟随物品(默认)")
                             })
                    {
                        await store.SaveLootAsync(
                            probeKey,
                            1,
                            true,
                            [
                                new LootRuleInput(
                                    0,
                                    probeItemId,
                                    2500,
                                    1,
                                    1,
                                    true,
                                    bound)
                            ]);
                        var readBack = (await store.LoadRulesAsync(probeKey))[0]
                            .BoundOnPickup;
                        var boundOk = readBack == bound;
                        Line(boundOk
                            ? $"[通过] 拾取后 = {label}：" +
                              $"写入 {DescribeBound(bound)} 读回 {DescribeBound(readBack)}。"
                            : $"[失败] 拾取后 = {label} 写回不一致：" +
                              $"期望 {DescribeBound(bound)}，实际 {DescribeBound(readBack)}。");
                        if (!boundOk)
                        {
                            failures++;
                        }
                    }
                }

                // 12 列「物品属性」回环：品质10 / 等级12 / 属性 24 等级5、133、90 → 读回一致 → 还原 NULL
                if (!await store.HasItemAttributeColumnsAsync())
                {
                    Line("[跳过] 数据库的 monster_loot_rules 还没有那 12 列物品属性" +
                         "（服务端迁移 20260927_214 未应用），跳过掉落物品属性回环。");
                    // 列不在时也不许静默写：配过属性必须被拦住
                    try
                    {
                        await store.SaveLootAsync(
                            probeKey,
                            1,
                            true,
                            [
                                new LootRuleInput(
                                    0,
                                    probeItemId,
                                    2500,
                                    1,
                                    1,
                                    true,
                                    null,
                                    ProbeAttributes)
                            ]);
                        Line("[失败] 库里还没有那 12 列物品属性，却把属性写进去了。");
                        failures++;
                    }
                    catch (LootValidationException ex)
                    {
                        Line($"[通过] 缺列时写物品属性被拦住：{ex.Message}");
                    }
                }
                else
                {
                    await store.SaveLootAsync(
                        probeKey,
                        1,
                        true,
                        [
                            new LootRuleInput(
                                0,
                                probeItemId,
                                2500,
                                1,
                                1,
                                true,
                                null,
                                ProbeAttributes)
                        ]);
                    var withAttributes = (await store.LoadRulesAsync(probeKey))[0].Attributes;
                    var attributesOk = withAttributes == ProbeAttributes;
                    Line(attributesOk
                        ? $"[通过] 掉落物品属性写入后读回一致：{withAttributes!.Summary}"
                        : $"[失败] 掉落物品属性写回不一致：期望 {ProbeAttributes.Summary}，" +
                          $"实际 {withAttributes?.Summary ?? "NULL"}。");
                    if (!attributesOk)
                    {
                        failures++;
                    }

                    // 还原成 NULL：不配置就是要回到 NULL，不能留下 0
                    await store.SaveLootAsync(
                        probeKey,
                        1,
                        true,
                        [new LootRuleInput(0, probeItemId, 2500, 1, 1, true)]);
                    var restored = (await store.LoadRulesAsync(probeKey))[0].Attributes;
                    var restoredOk = restored is { IsEmpty: true };
                    Line(restoredOk
                        ? "[通过] 掉落物品属性已还原成不配置（12 列 NULL）。"
                        : $"[失败] 掉落物品属性没有还原干净：{restored?.Summary ?? "NULL"}。");
                    if (!restoredOk)
                    {
                        failures++;
                    }
                }

                try
                {
                    await store.SaveLootAsync(
                        probeKey,
                        3,
                        true,
                        [new LootRuleInput(0, probeItemId, 2500, 1, 1, true)]);
                    Line("[失败] 上限大于规则数竟然通过了校验。");
                    failures++;
                }
                catch (LootValidationException)
                {
                    Line("[通过] 上限大于规则数被校验拦截（服务端启动不变量）。");
                }

                var doubled = await store.MultiplyChancesAsync(2d, probeKey);
                var afterFactor = await store.LoadRulesAsync(probeKey);
                var factorOk = doubled == 1 && afterFactor[0].ChanceBasisPoints == 5000;
                Line(factorOk
                    ? "[通过] 批量按倍率调整：25% × 2 = 50%。"
                    : $"[失败] 批量倍率调整结果异常：{afterFactor[0].ChanceBasisPoints}bp");
                if (!factorOk)
                {
                    failures++;
                }

                var uniform = await store.SetChancesAsync(1234, probeKey);
                var afterUniform = await store.LoadRulesAsync(probeKey);
                var uniformOk = uniform == 1 && afterUniform[0].ChanceBasisPoints == 1234;
                Line(uniformOk
                    ? "[通过] 批量统一概率：12.34%。"
                    : $"[失败] 批量统一概率结果异常：{afterUniform[0].ChanceBasisPoints}bp");
                if (!uniformOk)
                {
                    failures++;
                }

                var clamped = await store.SetChancesAsync(99999, probeKey);
                var afterClamp = await store.LoadRulesAsync(probeKey);
                var clampOk = clamped == 1 && afterClamp[0].ChanceBasisPoints == 10000;
                Line(clampOk
                    ? "[通过] 超出范围的统一概率被夹到 100%。"
                    : $"[失败] 概率夹取异常：{afterClamp[0].ChanceBasisPoints}bp");
                if (!clampOk)
                {
                    failures++;
                }
            }
            finally
            {
                await store.DeleteLootTableAsync(probeKey);
                var restored = await store.LoadHeaderAsync(probeKey);
                Line(restored is null
                    ? $"[通过] 已清理临时掉落表，{probeKey} 恢复未配置状态。"
                    : $"[失败] 临时掉落表未清理干净：{probeKey}");
                if (restored is not null)
                {
                    failures++;
                }
            }
        }

        Line();
        Line("── 结构与一致性自检 ────────────────────────────");
        var problems = await store.SelfCheckAsync();
        foreach (var problem in problems)
        {
            Line($"[{problem.Severity}] {(problem.TemplateKey.Length > 0 ? problem.TemplateKey + "：" : string.Empty)}{problem.Message}");
        }

        failures += problems.Count(static p => p.Severity == "致命");

        failures += await CheckFarmAsync(settings, Line);

        failures += await CheckQuestRewardAsync(settings, items, Line);

        failures += await CheckCharacterAndWaypointsAsync(settings, Line);

        failures += await CheckMonsterOverridesAsync(settings, Line);

        failures += await CheckExportImportAsync(settings, Line);

        failures += await CheckNpcDialogueAsync(settings, Line);

        failures += CheckQuestCatalogue(settings, Line);

        Line();
        Line(failures == 0
            ? "结论：全部通过。"
            : $"结论：{failures} 项失败。");
        return Finish(report, failures == 0 ? 0 : 1);
    }

    /// <summary>
    /// The farm tab's data layer: the view the faction totals come from, the two
    /// ledgers, the published roster, the rule constants read back out of the
    /// server's source, and a write probe that runs inside a transaction it then
    /// rolls back - so the live score board is measured without being changed.
    /// </summary>
    private static async Task<int> CheckFarmAsync(
        LootToolSettings settings,
        Action<string> line)
    {
        var failures = 0;
        line(string.Empty);
        line("── 利兰丁农场（本次新增） ──────────────────────");

        await using var farm = new FarmStore();
        try
        {
            farm.Connect(settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            line($"[失败] 农场页无法连接数据库：{ex.Message}");
            return 1;
        }

        if (!await farm.HasFarmSchemaAsync())
        {
            line("[失败] 该库没有 lelantine_farm_personal_points 视图/两张流水表；" +
                 "服务端迁移 20260926_210 未应用。");
            return 1;
        }

        line("[通过] 农场积分 schema 存在（视图 + 两张流水表）。");

        var totals = await farm.LoadTotalsAsync();
        line($"[信息] 阵营总分：斯巴达 {totals.SpartaPoints}｜雅典 {totals.AthensPoints}｜" +
             $"有分角色 {totals.Members} 个｜捐卵流水 {totals.DonationRows} 行｜击杀行 {totals.KillRows} 行");

        // The invariant the whole feature rests on: each camp's total is the sum
        // of that camp's personal scores, and the activity's high score is the
        // highest personal score.
        var characters = await farm.LoadCharactersAsync(null, limit: 500);
        var sparta = characters.Where(static row => row.Faction == LelantineFarmRules.SpartaCamp)
            .Sum(static row => row.Personal);
        var athens = characters.Where(static row => row.Faction == LelantineFarmRules.AthensCamp)
            .Sum(static row => row.Personal);
        var highest = characters.Count == 0 ? 0 : characters.Max(static row => row.Personal);
        var invariant = sparta == totals.SpartaPoints && athens == totals.AthensPoints;
        line(invariant
            ? $"[通过] 阵营分 = 同阵营个人积分之和（斯巴达 {sparta}、雅典 {athens}）；" +
              $"最高个人积分 {highest}。"
            : $"[失败] 阵营分与个人分之和不一致：视图 斯巴达 {totals.SpartaPoints}/雅典 {totals.AthensPoints}，" +
              $"逐人求和 斯巴达 {sparta}/雅典 {athens}。");
        if (!invariant)
        {
            failures++;
        }

        var ranked = characters.Where(static row => row.Personal > 0).ToList();
        var rankOk = ranked.All(row => row.Rank >= 1);
        line(rankOk
            ? $"[通过] 有分角色 {ranked.Count} 个都拿到了排名。"
            : "[失败] 有分角色的排名出现 0。");
        if (!rankOk)
        {
            failures++;
        }

        var npcs = await farm.LoadPublishedNpcsAsync();
        var npcOk = npcs.Count > 0 &&
            npcs.All(static npc => npc.MapId == LelantineFarmRules.MapId) &&
            npcs.Select(static npc => npc.ObjectId).Distinct().Count() == npcs.Count;
        line(npcOk
            ? $"[通过] 当前发布的农场 NPC {npcs.Count} 个：" +
              string.Join("、", npcs.Select(static npc => npc.NpcKey + "/" + npc.ObjectId))
            : $"[失败] 已发布农场 NPC 异常：{npcs.Count} 个。");
        if (!npcOk)
        {
            failures++;
        }

        var constants = FarmStore.LoadRuleConstants();
        var missing = constants.Where(static constant =>
            constant.Value.StartsWith("（未找到源码", StringComparison.Ordinal)).ToList();
        line(missing.Count == 0
            ? $"[通过] 从服务端源码读到 {constants.Count} 条农场规则常量。"
            : $"[警告] {missing.Count} 条常量未读到源码（工具不在仓库内？）：" +
              string.Join("、", missing.Select(static constant => constant.Name)));
        if (missing.Count > 0)
        {
            failures++;
        }

        // A write probe that cannot leave a trace: everything happens in a
        // transaction that is rolled back, and the totals are read again after
        // the rollback to prove the score board is untouched.
        var probe = characters.FirstOrDefault();
        if (probe is null)
        {
            line("[信息] 库里没有任何角色，跳过写入探针。");
            return failures;
        }

        var faction = probe.Faction is 0 or 1
            ? probe.Faction
            : (byte)LelantineFarmRules.SpartaCamp;
        try
        {
            await farm.AddAdjustmentAsync(
                probe.Id,
                faction,
                12345,
                dryRun: true);
            var after = await farm.LoadTotalsAsync();
            var rolledBack = after.SpartaPoints == totals.SpartaPoints &&
                after.AthensPoints == totals.AthensPoints &&
                after.DonationRows == totals.DonationRows;
            line(rolledBack
                ? $"[通过] 演练写入（给 {probe.Name} +12345 分）没有落库，总分不变。"
                : "[失败] 演练写入竟然改了数据。");
            if (!rolledBack)
            {
                failures++;
            }
        }
        catch (Exception ex)
        {
            line($"[失败] 演练写入抛错：{ex.Message}");
            failures++;
        }

        var bag = await farm.LoadFarmBagAsync(probe.Id);
        line($"[信息] {probe.Name} 背包里的活动物品：{(bag.Count == 0 ? "无" : string.Join(
            "、",
            bag.Select(static row =>
                $"{row.DisplayName}({row.ItemId}) 品质{row.Quality}×{row.Stack} 槽{row.Slot}")))}");

        try
        {
            var plan = await farm.GrantItemAsync(
                probe.Id,
                LelantineFarmRules.HoundEggItemId,
                quantity: 99,
                quality: LelantineFarmRules.EggRungs[2].Aptitude,
                note: "selftest-dry-run",
                dryRun: true);
            var planned = plan.Placements.Sum(static placement => placement.Added);
            line(planned == 99
                ? $"[通过] 发放演练：{plan.CharacterName} 的 99 个忠犬卵（资质 {plan.Quality}）" +
                  $"落在 {plan.Placements.Count} 个堆位，未写库。"
                : $"[失败] 发放演练只规划了 {planned} 个。");
            if (planned != 99)
            {
                failures++;
            }
        }
        catch (FarmToolException ex) when (
            ex.Message.Contains("背包已满", StringComparison.Ordinal))
        {
            // A full bag is the tool reporting a real blocker, not a defect; the
            // operator has to free a slot before any handout can land.
            line($"[信息] 发放演练跳过（{probe.Name} 的背包已满，工具已正确拒绝写入）：{ex.Message}");
        }

        return failures;
    }

    /// <summary>
    /// 任务奖励页签的数据层：两张覆盖表在（服务端迁移跑过）时做一次
    /// 「写入 → 读回 → 删除」回环，目标任务取自本库真实存在的任务 ID；
    /// 结束时把写进去的行删干净，不留垃圾数据。旧库没有这两张表时只报跳过。
    /// </summary>
    /// <summary>
    /// 「任务奖励」页签的搜索源：客户端自己带的 Quest.xml + Text\Quest\*.dat。
    /// 客户端目录不存在时跳过（工具在没装客户端的机器上照样能跑其它段落）。
    /// </summary>
    private static int CheckQuestCatalogue(LootToolSettings settings, Action<string> line)
    {
        var failures = 0;
        line(string.Empty);
        line("── 任务目录解析（本次新增） ────────────────────");

        if (string.IsNullOrWhiteSpace(settings.ClientRoot) ||
            !Directory.Exists(settings.ClientRoot))
        {
            line($"[跳过] 客户端目录不可用（{settings.ClientRoot}），跳过任务目录解析。");
            return failures;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var catalog = QuestCatalog.Load(settings.ClientRoot);
        stopwatch.Stop();

        if (catalog.Error is { Length: > 0 })
        {
            line($"[失败] 任务目录解析失败：{catalog.Error}");
            return 1;
        }

        line($"[通过] 任务表 {catalog.QuestXmlPath}");
        line($"[信息] 语言目录 {catalog.Language}｜解析到 {catalog.Quests.Count} 个任务" +
             $"（标题来自 Text\\Quest\\*.dat 的 {catalog.TitleCount} 条，" +
             $"其余回落为 LvX 任务ID）｜耗时 {stopwatch.ElapsedMilliseconds} ms");

        var countOk = catalog.Quests.Count > 0;
        line(countOk
            ? "[通过] 任务条数 > 0。"
            : "[失败] 任务表里一条都没解析出来。");
        if (!countOk)
        {
            failures++;
        }

        var sparta = catalog.Quests.Count(static quest => quest.Faction == 0);
        var athens = catalog.Quests.Count(static quest => quest.Faction == 1);
        var unknown = catalog.Quests.Count - sparta - athens;
        line($"[信息] 阵营分布：斯巴达 {sparta}｜雅典 {athens}｜未标注 {unknown}");

        var levels = catalog.Quests.Select(static quest => quest.MinLevel).ToList();
        var levelOk = levels.Count > 0 && levels.Min() >= 0 && levels.Max() <= 1000;
        line(levelOk
            ? $"[通过] 等级区间合理：{levels.Min()}–{levels.Max()}。"
            : $"[失败] 等级区间异常：{levels.Min()}–{levels.Max()}。");
        if (!levelOk)
        {
            failures++;
        }

        // 抽几条逐项校验：ID/等级/阵营/标题都要像样
        var samples = catalog.Quests
            .Where(static quest => quest.TitleFromClient)
            .OrderBy(static _ => Guid.NewGuid())
            .Take(5)
            .ToList();
        if (samples.Count == 0)
        {
            line("[失败] 一条标题都没从 Text\\Quest\\*.dat 里读出来（格式变了？）。");
            failures++;
        }

        foreach (var quest in samples)
        {
            var ok = quest.Id > 0 &&
                quest.MinLevel >= 0 &&
                quest.Faction is -1 or 0 or 1 &&
                quest.Title.Contains("Lv", StringComparison.Ordinal);
            line(ok
                ? $"[通过] 抽样 ID={quest.Id} 等级={quest.MinLevel} " +
                  $"阵营={quest.FactionName} 发布NPC={quest.GiverName} 标题={quest.Title}"
                : $"[失败] 抽样异常：ID={quest.Id} 等级={quest.MinLevel} " +
                  $"阵营={quest.Faction} 标题={quest.Title}");
            if (!ok)
            {
                failures++;
            }
        }

        // 已知的真实任务：新手任务 518 是斯巴达的 1 级任务（客户端文本 [Lv1]初入斯巴达）
        var starter = catalog.Quests.FirstOrDefault(static quest => quest.Id == 518);
        if (starter is null)
        {
            line("[信息] 没找到任务 518（这份客户端可能不含新手任务），跳过该抽样。");
        }
        else
        {
            var starterOk = starter.Faction == 0 && starter.MinLevel == 1;
            line(starterOk
                ? $"[通过] 任务 518：等级 {starter.MinLevel}、阵营 {starter.FactionName}、" +
                  $"标题 {starter.Title}。"
                : $"[失败] 任务 518 的资料不对：等级 {starter.MinLevel}、" +
                  $"阵营 {starter.Faction}、标题 {starter.Title}。");
            if (!starterOk)
            {
                failures++;
            }
        }

        // 附加属性表（「属性…」弹窗的下拉）：清单来自客户端 ItemAppendAttribute.xml
        // （真实运行时还会并上服务端 item_attribute_templates），中文名来自 EquipDescription.dat
        var attributes = ItemAttributeCatalog.Load(settings.ClientRoot, []);
        var attributesOk = attributes.Attributes.Count > 0;
        line(attributesOk
            ? $"[通过] 附加属性表：{attributes.Attributes.Count} 条，其中 " +
              $"{attributes.ChineseNameCount} 条有中文名（{attributes.ClientTextPath}）"
            : $"[失败] 没读到附加属性表：{attributes.Error}");
        if (!attributesOk)
        {
            failures++;
        }
        else
        {
            foreach (var sample in attributes.Attributes.Take(2))
            {
                line($"[信息] 属性 {sample.Id} → {sample.DisplayName}" +
                     $"（{sample.NameKey}，最高 {sample.MaxLevel} 级" +
                     $"{(sample.Percent ? "，百分比" : string.Empty)}）");
            }

            if (attributes.ChineseNameCount == 0)
            {
                line("[警告] 属性中文名一条都没读到（" + attributes.ClientTextPath +
                     "），下拉里只能显示英文标签。");
            }
        }

        return failures;
    }

    /// <summary>
    /// 「角色与标记点」页的数据层：按名字查角色（含地图/坐标）、地图选择表，
    /// 以及标记点表的增/查/挪/改名/删一整个回环。
    /// </summary>
    /// <remarks>
    /// 回环用带时间戳的探针名字，跑完自己删掉，绝不碰操作者存的标记点；
    /// 表不存在（旧库还没跑迁移）时按「跳过」处理，和任务奖励页一个规矩。
    /// </remarks>
    private static async Task<int> CheckCharacterAndWaypointsAsync(
        LootToolSettings settings,
        Action<string> line)
    {
        var failures = 0;
        line(string.Empty);
        line("── 角色与标记点（本次新增） ────────────────────");

        await using var store = new CharacterStore();
        try
        {
            store.Connect(settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            line($"[失败] 角色页无法连接数据库：{ex.Message}");
            return 1;
        }

        List<MapChoice> maps;
        try
        {
            maps = await store.LoadMapChoicesAsync();
        }
        catch (Exception ex)
        {
            line($"[失败] 读取 map_templates 失败：{ex.Message}");
            return failures + 1;
        }
        if (maps.Count == 0)
        {
            line("[失败] map_templates 是空的，地图选择器没有内容。");
            failures++;
        }
        else
        {
            line($"[通过] 地图表 {maps.Count} 张（例：{maps[0]}）。");
        }

        List<CharacterRow> characters;
        try
        {
            characters = await store.SearchCharactersAsync(null);
        }
        catch (Exception ex)
        {
            line($"[失败] 按名字查角色失败：{ex.Message}");
            return failures + 1;
        }

        var located = characters.Count(c => maps.Any(m => m.MapId == c.MapId));
        line($"[信息] 角色 {characters.Count} 个，其中地图能在 map_templates 里对上的 {located} 个。");
        foreach (var character in characters.Take(5))
        {
            line($"  {character.Name,-16} {character.ProfessionName} {character.Level,3} 级 " +
                 $"{character.CampName} map{character.MapId} {character.PositionText}");
        }

        if (!await store.HasWaypointSchemaAsync())
        {
            line($"[跳过] {CharacterStore.MissingSchemaMessage(store.DatabaseName)}");
            return failures;
        }

        line("[通过] 标记点 schema 存在（gm_waypoints）。");

        List<WaypointRow> existing;
        try
        {
            existing = await store.LoadWaypointsAsync();
        }
        catch (Exception ex)
        {
            line($"[失败] 读取 gm_waypoints 失败：{ex.Message}");
            return failures + 1;
        }

        line(existing.Count == 0
            ? "[信息] 当前没有任何标记点。"
            : $"[信息] 已有标记点 {existing.Count} 个：" +
              string.Join("、", existing.Take(8).Select(static w => $"{w.Name}(map{w.MapId})")));

        // 探针名字带时间戳，绝不会和操作者的命名撞车。
        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var probeName = $"__selftest__{stamp}";
        var renamedName = $"{probeName}_r";
        var probeMap = maps.Count > 0 ? maps[0].MapId : (short)0;
        long probeId = 0;
        try
        {
            var saved = await store.SaveWaypointAsync(new WaypointInput(
                probeName, "自检探针", "selftest", probeMap, -196f, 44f, 1.5f, "manual", "selftest"));
            probeId = saved.Id;
            if (saved.Name != probeName ||
                Math.Abs(saved.X - (-196f)) > 0.001f ||
                Math.Abs(saved.Z - 44f) > 0.001f ||
                saved.MapId != probeMap)
            {
                line($"[失败] 标记点写入后读回的值不一致：{saved.Name} map{saved.MapId} {saved.PositionText}");
                failures++;
            }
            else
            {
                line($"[通过] 新增标记点 → id={saved.Id} map{saved.MapId} {saved.PositionText}。");
            }

            var afterSave = await store.LoadWaypointsAsync();
            if (afterSave.Count != existing.Count + 1 ||
                afterSave.All(w => w.Name != probeName))
            {
                line("[失败] 新增后列表没有多出这一行。");
                failures++;
            }
            else
            {
                line("[通过] 列表里能查到新标记点。");
            }

            var moved = await store.MoveWaypointAsync(
                probeId, probeMap, 180f, 150f, 3f, "selftest");
            if (Math.Abs(moved.X - 180f) > 0.001f || Math.Abs(moved.Z - 150f) > 0.001f)
            {
                line($"[失败] 挪位后坐标不对：{moved.PositionText}");
                failures++;
            }
            else
            {
                line($"[通过] 覆盖坐标 → {moved.PositionText}。");
            }

            await store.RenameWaypointAsync(probeId, renamedName, "自检探针改名", "selftest");
            var afterRename = await store.LoadWaypointsAsync();
            if (afterRename.All(w => w.Name != renamedName))
            {
                line("[失败] 改名后找不到新名字。");
                failures++;
            }
            else
            {
                line($"[通过] 改名 → {renamedName}。");
            }
        }
        catch (Exception ex)
        {
            line($"[失败] 标记点回环抛异常：{ex.Message}");
            failures++;
        }
        finally
        {
            if (probeId != 0)
            {
                try
                {
                    await store.DeleteWaypointAsync(probeId);
                    var afterDelete = await store.LoadWaypointsAsync();
                    if (afterDelete.Any(w => w.Id == probeId))
                    {
                        line("[失败] 探针标记点没删掉。");
                        failures++;
                    }
                    else
                    {
                        line("[通过] 删除探针标记点，列表恢复原样。");
                    }
                }
                catch (Exception ex)
                {
                    line($"[失败] 删除探针标记点失败：{ex.Message}");
                    failures++;
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// 「刷怪」页的数据层：可选模板（该图已发布的怪）、GM 刷怪点的增/查/启停/删，
    /// 以及按模板属性覆盖的写入与清除。
    /// </summary>
    /// <remarks>
    /// 回环挑该图**真实存在**的模板当底稿，写完自己删干净；表不存在（旧库还没跑迁移）
    /// 时按「跳过」处理。
    /// </remarks>
    private static async Task<int> CheckMonsterOverridesAsync(
        LootToolSettings settings,
        Action<string> line)
    {
        var failures = 0;
        line(string.Empty);
        line("── 刷怪（本次新增） ────────────────────────────");

        await using var store = new MonsterStore();
        try
        {
            store.Connect(settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            line($"[失败] 刷怪页无法连接数据库：{ex.Message}");
            return 1;
        }

        if (!await store.HasOverrideSchemaAsync())
        {
            line($"[跳过] {MonsterStore.MissingSchemaMessage(store.DatabaseName)}");
            return 0;
        }

        line("[通过] 刷怪 schema 存在（gm_monster_spawns + edits + attributes）。");

        var maps = await store.LoadMapsAsync();
        short probeMap = 0;
        List<MonsterTemplateChoice> templates = [];
        foreach (var map in maps)
        {
            var candidates = await store.LoadTemplatesAsync(map.MapId);
            if (candidates.Count > 0)
            {
                probeMap = map.MapId;
                templates = candidates;
                break;
            }
        }

        if (templates.Count == 0)
        {
            line("[失败] 没有任何地图能列出可选怪物模板。");
            return failures + 1;
        }

        var template = templates[0];
        line($"[信息] 底稿地图 map{probeMap}｜可选模板 {templates.Count} 个，" +
             $"例如 {template.TemplateKey}（该图已有点 {template.SpawnCount} 个，" +
             $"对象 {template.SampleObjectId}）。");

        var existing = await store.LoadSpawnRowsAsync(probeMap);
        var existingAttributes = await store.LoadTemplateAttributesAsync(probeMap);
        line($"[信息] 该图现有 GM 刷怪点 {existing.Count} 个，模板属性覆盖 {existingAttributes.Count} 条。");

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var prefix = $"__selftest__{stamp}";
        var created = new List<GmMonsterSpawnRow>();
        var wroteAttributes = false;
        try
        {
            created = await store.CreateSpawnsAsync(new SpawnBatchRequest(
                MapId: probeMap,
                WaypointId: null,
                NamePrefix: prefix,
                TemplateKey: template.TemplateKey,
                DisplayName: template.DisplayName,
                Count: 2,
                X: template.SampleX + 5f,
                Z: template.SampleZ + 5f,
                Spread: 3f,
                Facing: 1.5f,
                Level: 7,
                CurrentHealth: null,
                MaximumHealth: 321,
                Attributes: new MonsterAttributeValues(PhysicalAttack: 12, Hit: 34),
                UpdatedBy: "selftest"));

            if (created.Count != 2)
            {
                line($"[失败] 期望生成 2 个点，实际 {created.Count} 个。");
                failures++;
            }
            else
            {
                line($"[通过] 生成 2 个点：{string.Join("、", created.Select(static row => $"id={row.ObjectId}"))}。");
            }

            foreach (var row in created)
            {
                if (row.ObjectId < MonsterStore.FirstGmObjectId ||
                    row.ObjectId > MonsterStore.LastGmObjectId)
                {
                    line($"[失败] 对象 ID {row.ObjectId} 落在 GM 段之外。");
                    failures++;
                }
            }

            var after = await store.LoadSpawnRowsAsync(probeMap);
            if (after.Count != existing.Count + created.Count)
            {
                line($"[失败] 生成后列表数量不对：期望 {existing.Count + created.Count}，实际 {after.Count}。");
                failures++;
            }
            else
            {
                line("[通过] 列表里能查到新生成的点。");
            }

            if (created.Count > 0)
            {
                await store.SetSpawnEnabledAsync(created[0].Id, false, "selftest");
                var toggled = await store.LoadSpawnRowsAsync(probeMap);
                var target = toggled.FirstOrDefault(row => row.Id == created[0].Id);
                if (target is null || target.Enabled)
                {
                    line("[失败] 停用没有生效。");
                    failures++;
                }
                else
                {
                    line("[通过] 停用刷怪点生效。");
                }
            }

            await store.SaveTemplateAttributesAsync(
                probeMap,
                template.TemplateKey,
                template.DisplayName,
                new MonsterAttributeValues(PhysicalAttack: 999, CriticalResistance: 55),
                "selftest");
            wroteAttributes = true;
            var attributes = await store.LoadTemplateAttributesAsync(probeMap);
            var saved = attributes.FirstOrDefault(row => row.TemplateKey == template.TemplateKey);
            if (saved is null || saved.Values.PhysicalAttack != 999)
            {
                line("[失败] 模板属性覆盖没有写进去。");
                failures++;
            }
            else
            {
                line($"[通过] 模板属性覆盖 → {saved.Summary}。");
            }
        }
        catch (Exception ex)
        {
            line($"[失败] 刷怪回环抛异常：{ex.Message}");
            failures++;
        }
        finally
        {
            foreach (var row in created)
            {
                try
                {
                    await store.DeleteSpawnAsync(row.Id);
                }
                catch (Exception ex)
                {
                    line($"[失败] 清理探针刷怪点失败：{ex.Message}");
                    failures++;
                }
            }

            if (wroteAttributes)
            {
                try
                {
                    await store.SaveTemplateAttributesAsync(
                        probeMap,
                        template.TemplateKey,
                        template.DisplayName,
                        new MonsterAttributeValues(),
                        "selftest");
                }
                catch (Exception ex)
                {
                    line($"[失败] 清理探针属性覆盖失败：{ex.Message}");
                    failures++;
                }
            }

            var final = await store.LoadSpawnRowsAsync(probeMap);
            var finalAttributes = await store.LoadTemplateAttributesAsync(probeMap);
            if (final.Count == existing.Count && finalAttributes.Count == existingAttributes.Count)
            {
                line("[通过] 清理完成，刷怪点与属性覆盖都恢复原样。");
            }
            else
            {
                line($"[失败] 清理不干净：刷怪点 {existing.Count}→{final.Count}，" +
                     $"属性覆盖 {existingAttributes.Count}→{finalAttributes.Count}。");
                failures++;
            }
        }

        return failures;
    }

    /// <summary>
    /// 「导出/导入」页的数据层：整体导出的条数与库一致、JSON 往返不丢字段，
    /// 以及导入是**幂等**的（同一份文件导两次结果一样）。
    /// </summary>
    /// <remarks>
    /// 导入只喂一个**探针专用**的小 bundle（不是整库导出），所以不会碰操作者已有的行：
    /// 探针的标记点/刷怪点/属性都是自检自己造的，跑完自己删干净并核对数量回到原样。
    /// </remarks>
    private static async Task<int> CheckExportImportAsync(
        LootToolSettings settings,
        Action<string> line)
    {
        var failures = 0;
        line(string.Empty);
        line("── 导出/导入（本次新增） ──────────────────────");

        await using var content = new GmContentStore();
        await using var monsters = new MonsterStore();
        try
        {
            content.Connect(settings.BuildConnectionString());
            monsters.Connect(settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            line($"[失败] 导出页无法连接数据库：{ex.Message}");
            return 1;
        }

        if (!await monsters.HasOverrideSchemaAsync())
        {
            line($"[跳过] {MonsterStore.MissingSchemaMessage(content.DatabaseName)}");
            return 0;
        }

        var bundle = await content.ExportAsync();
        line($"[信息] 整体导出：{bundle.Summary}（库 {bundle.Database}，版本 {bundle.Version}）。");

        var json = GmContentStore.Serialize(bundle);
        var restored = GmContentStore.Deserialize(json);
        if (restored.Waypoints.Count != bundle.Waypoints.Count ||
            restored.Spawns.Count != bundle.Spawns.Count ||
            restored.SpawnAttributes.Count != bundle.SpawnAttributes.Count ||
            restored.TemplateAttributes.Count != bundle.TemplateAttributes.Count)
        {
            line("[失败] JSON 往返后条数不一致。");
            failures++;
        }
        else
        {
            line($"[通过] JSON 往返不丢条目（{json.Length} 字符）。");
        }

        if (bundle.Spawns.Count > 0)
        {
            var original = bundle.Spawns[0];
            var packet = Convert.FromBase64String(original.ClearBytesBase64);
            var roundTripped = restored.Spawns[0];
            if (packet.Length != Convert.FromBase64String(roundTripped.ClearBytesBase64).Length ||
                roundTripped.ObjectId != original.ObjectId ||
                roundTripped.TemplateKey != original.TemplateKey)
            {
                line("[失败] JSON 往返后包体或关键字丢了。");
                failures++;
            }
            else
            {
                line($"[通过] 包体 base64 往返完好（{packet.Length} 字节）。");
            }
        }

        // 找一张能列模板的图，用来造探针刷怪点。
        short probeMap = 0;
        MonsterTemplateChoice? template = null;
        foreach (var map in await monsters.LoadMapsAsync())
        {
            var candidates = await monsters.LoadTemplatesAsync(map.MapId);
            if (candidates.Count > 0)
            {
                probeMap = map.MapId;
                template = candidates[0];
                break;
            }
        }

        if (template is null)
        {
            line("[失败] 没有任何地图能列出可选怪物模板，探针造不出来。");
            return failures + 1;
        }

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var waypointName = $"__selftest_export__{stamp}";
        var spawnName = $"__selftest_export__{stamp}";
        var before = await monsters.LoadSpawnRowsAsync(probeMap);
        var beforeAttributes = await monsters.LoadTemplateAttributesAsync(probeMap);
        var probeSpawnId = 0L;

        try
        {
            var created = await monsters.CreateSpawnsAsync(new SpawnBatchRequest(
                MapId: probeMap,
                WaypointId: null,
                NamePrefix: $"__selftest_export__{stamp}",
                TemplateKey: template.TemplateKey,
                DisplayName: template.DisplayName,
                Count: 1,
                X: template.SampleX + 9f,
                Z: template.SampleZ + 9f,
                Spread: 0f,
                Facing: 0f,
                Level: null,
                CurrentHealth: null,
                MaximumHealth: null,
                Attributes: null,
                UpdatedBy: "selftest"));
            if (created.Count != 1)
            {
                line($"[失败] 探针刷怪点没造出来（{created.Count} 个）。");
                return failures + 1;
            }

            spawnName = created[0].Name;
            probeSpawnId = created[0].Id;

            // 只导出这一条刷怪点，绝不把整库内容喂回导入。
            var probePacket = (await content.ExportAsync()).Spawns
                .FirstOrDefault(row => row.ObjectId == created[0].ObjectId)
                ?? throw new InvalidOperationException("刚写的探针刷怪点没被导出到。");
            var probe = new GmContentBundle(
                GmContentBundle.CurrentVersion,
                DateTime.UtcNow,
                content.DatabaseName,
                Waypoints:
                [
                    new GmWaypointEntry(
                        waypointName,
                        "自检导入探针",
                        "selftest",
                        probeMap,
                        template.SampleX + 9f,
                        template.SampleZ + 9f,
                        0f,
                        "manual")
                ],
                Spawns: [probePacket with { Name = spawnName, WaypointName = waypointName }],
                SpawnAttributes:
                [
                    new GmSpawnAttributeEntry(
                        probeMap,
                        created[0].ObjectId,
                        PhysicalAttack: 41,
                        MagicAttack: null,
                        PhysicalDefense: null,
                        MagicDefense: null,
                        Hit: null,
                        Dodge: null,
                        Critical: null,
                        CriticalResistance: null)
                ],
                TemplateAttributes:
                [
                    new GmTemplateAttributeEntry(
                        probeMap,
                        template.TemplateKey,
                        template.DisplayName,
                        Level: null,
                        CurrentHealth: null,
                        MaximumHealth: null,
                        PhysicalAttack: 42,
                        MagicAttack: null,
                        PhysicalDefense: null,
                        MagicDefense: null,
                        Hit: null,
                        Dodge: null,
                        Critical: null,
                        CriticalResistance: null)
                ]);

            var first = await content.ImportAsync(probe, "selftest");
            line($"[通过] 导入一次：{first.Summary}");

            var afterFirst = await monsters.LoadSpawnRowsAsync(probeMap);
            var attributesFirst = await monsters.LoadTemplateAttributesAsync(probeMap);
            var waypoints = await content.ExportAsync();
            if (!waypoints.Waypoints.Any(row => row.Name == waypointName))
            {
                line("[失败] 导入没有写进标记点。");
                failures++;
            }
            else
            {
                line("[通过] 导入写进了标记点。");
            }

            if (!attributesFirst.Any(row =>
                    row.TemplateKey == template.TemplateKey &&
                    row.Values.PhysicalAttack == 42))
            {
                line("[失败] 导入没有写进模板属性覆盖。");
                failures++;
            }
            else
            {
                line("[通过] 导入写进了模板属性覆盖（物攻=42）。");
            }

            var second = await content.ImportAsync(probe, "selftest");
            var afterSecond = await monsters.LoadSpawnRowsAsync(probeMap);
            var attributesSecond = await monsters.LoadTemplateAttributesAsync(probeMap);
            if (afterSecond.Count != afterFirst.Count ||
                attributesSecond.Count != attributesFirst.Count ||
                second.Spawns != first.Spawns)
            {
                line($"[失败] 导入不幂等：刷怪点 {afterFirst.Count}→{afterSecond.Count}，" +
                     $"属性覆盖 {attributesFirst.Count}→{attributesSecond.Count}。");
                failures++;
            }
            else
            {
                line("[通过] 同一份文件导两次结果一样（按名字/键覆盖）。");
            }
        }
        catch (Exception ex)
        {
            line($"[失败] 导出/导入回环抛异常：{ex.Message}");
            failures++;
        }
        finally
        {
            try
            {
                if (probeSpawnId != 0)
                {
                    await monsters.DeleteSpawnAsync(probeSpawnId);
                }

                await monsters.SaveTemplateAttributesAsync(
                    probeMap,
                    template.TemplateKey,
                    template.DisplayName,
                    new MonsterAttributeValues(),
                    "selftest");
                await DeleteWaypointAsync(settings, waypointName);
            }
            catch (Exception ex)
            {
                line($"[失败] 清理探针失败：{ex.Message}");
                failures++;
            }

            var final = await monsters.LoadSpawnRowsAsync(probeMap);
            var finalAttributes = await monsters.LoadTemplateAttributesAsync(probeMap);
            var finalWaypoints = await content.ExportAsync();
            if (final.Count == before.Count &&
                finalAttributes.Count == beforeAttributes.Count &&
                finalWaypoints.Waypoints.All(row => row.Name != waypointName))
            {
                line("[通过] 清理完成，刷怪点/属性覆盖/标记点都恢复原样。");
            }
            else
            {
                line($"[失败] 清理不干净：刷怪点 {before.Count}→{final.Count}，" +
                     $"属性覆盖 {beforeAttributes.Count}→{finalAttributes.Count}。");
                failures++;
            }
        }

        return failures;
    }

    /// <summary>删掉一个自检标记点（<c>GmContentStore</c> 只做整份导入，没有单删）。</summary>
    private static async Task DeleteWaypointAsync(LootToolSettings settings, string name)
    {
        // 借「角色与标记点」那条已验证的单删路径，而不是往 GmContentStore 里加"单删"：
        // 导出页不需要它，加进去只会是死代码。
        await using var store = new CharacterStore();
        store.Connect(settings.BuildConnectionString());
        var existing = await store.LoadWaypointsAsync();
        var target = existing.FirstOrDefault(row => row.Name == name);
        if (target is not null)
        {
            await store.DeleteWaypointAsync(target.Id);
        }
    }

    /// <summary>
    /// 「NPC 对话」页的数据层与**客户端补丁生成**：校验规则、生成出来的 Lua 结构，
    /// 以及在客户端目录上的幂等性（第二次跑什么都不改）。
    /// </summary>
    /// <remarks>
    /// 补丁只往**临时目录**里那份真实客户端文件的副本上打，绝不碰游戏客户端本体。
    /// 生成结果同时留一份到 <c>logs\npc-patch-preview.lua</c>，方便用真 Lua 解释器过语法。
    /// </remarks>
    private static async Task<int> CheckNpcDialogueAsync(
        LootToolSettings settings,
        Action<string> line)
    {
        var failures = 0;
        line(string.Empty);
        line("── NPC 对话与客户端补丁（本次新增） ────────────");

        await using var store = new NpcDialogueStore();
        try
        {
            store.Connect(settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            line($"[失败] NPC 页无法连接数据库：{ex.Message}");
            return 1;
        }

        if (!await store.HasSchemaAsync())
        {
            line($"[跳过] {NpcDialogueStore.MissingSchemaMessage(store.DatabaseName)}");
            return 0;
        }

        line("[通过] NPC schema 存在（dialogues / pages / buttons / spawns）。");

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var key = $"__selftest_npc__{stamp}";
        var tree = new GmNpcTree(
            new GmNpcDialogue(key, "自检对话", NpcDialogueStore.DefaultFunctionFlag, 90001, "", true),
            Pages:
            [
                new GmNpcDialoguePage(key, 90001, "第一页：选一个\"选项\"。", ""),
                new GmNpcDialoguePage(key, 90002, "第二页：到此为止。", "")
            ],
            Buttons:
            [
                new GmNpcDialogueButton(key, 90001, 1, "去第二页", 90002),
                new GmNpcDialogueButton(key, 90001, 2, "什么都不做", 0)
            ]);

        var created = false;
        var tempRoot = Path.Combine(Path.GetTempPath(), $"gm-npc-patch-{stamp}");
        try
        {
            // 校验必须是"该拒的拒"。
            var badCases = new (string Name, GmNpcTree Tree)[]
            {
                ("入口页不存在", tree with
                {
                    Dialogue = tree.Dialogue with { EntryPage = 90099 }
                }),
                ("按钮槽位越界", tree with
                {
                    Buttons = [new GmNpcDialogueButton(key, 90001, 13, "第13个", 90002)]
                }),
                ("按钮指向不存在的页", tree with
                {
                    Buttons = [new GmNpcDialogueButton(key, 90001, 1, "去不存在的页", 90088)]
                }),
                ("有页从入口走不到", tree with
                {
                    Pages =
                    [
                        new GmNpcDialoguePage(key, 90001, "第一页", ""),
                        new GmNpcDialoguePage(key, 90002, "第二页", ""),
                        new GmNpcDialoguePage(key, 90003, "孤岛页", "")
                    ]
                }),
                ("按钮没有文字", tree with
                {
                    Buttons = [new GmNpcDialogueButton(key, 90001, 1, " ", 90002)]
                })
            };
            var rejected = 0;
            foreach (var (name, bad) in badCases)
            {
                try
                {
                    NpcDialogueStore.Validate(bad);
                }
                catch (InvalidDataException)
                {
                    rejected++;
                }
                catch (InvalidOperationException)
                {
                    rejected++;
                }
            }

            if (rejected == badCases.Length)
            {
                line($"[通过] 校验挡住了全部 {rejected} 种非法配置（入口缺失/槽位越界/指向空页/不可达页/空按钮文字）。");
            }
            else
            {
                line($"[失败] 校验只挡住了 {rejected}/{badCases.Length} 种非法配置。");
                failures++;
            }

            NpcDialogueStore.Validate(tree);
            line("[通过] 合法配置通过校验。");

            await store.SaveTreeAsync(tree, "selftest");
            created = true;
            var reloaded = await store.LoadTreesAsync();
            var saved = reloaded.FirstOrDefault(row => row.Dialogue.DialogueKey == key);
            if (saved is null || saved.Pages.Count != 2 || saved.Buttons.Count != 2)
            {
                line($"[失败] 存回来的树不对：找到 {saved?.Pages.Count} 页 / {saved?.Buttons.Count} 按钮。");
                failures++;
            }
            else
            {
                line("[通过] 存库并读回：2 页、2 按钮。");
            }

            var lua = ClientNpcPatch.GenerateLua([tree]);
            var previewPath = Path.Combine(
                AppContext.BaseDirectory,
                "logs",
                "npc-patch-preview.lua");
            Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
            File.WriteAllText(previewPath, lua, new UTF8Encoding(false));

            var expected = new (string Snippet, string Why)[]
            {
                ($"NPC_FLAG_SYS_GMTOOL = {NpcDialogueStore.DefaultFunctionFlag}", "功能号声明"),
                ("function NpcFunGMTOOL_SetText(Type,Index,BtnID,SubID)", "分派入口"),
                ("Index == 90001", "第一页"),
                ("Index == 90002", "第二页"),
                ($"SubID == {NpcDialogueStore.BodySubId(90001)}", "第一页正文条目"),
                ($"SubID == {NpcDialogueStore.ButtonSubId(90001, 1)}", "第一页 1 号按钮"),
                ($"SubID == {NpcDialogueStore.ButtonSubId(90001, 2)}", "第一页 2 号按钮"),
                ("FirstWin_Text1:SetText(\"第一页：选一个\\\"选项\\\"。\")", "正文里的引号被转义"),
                ("Button:SetText(\"去第二页\")", "按钮文字"),
                ("Button:SetPosition(25,135)", "第一个按钮的位置"),
                ("Button:SetPosition(25,155)", "第二个按钮的位置"),
                ("NPCFUN:EndMessage(true)", "终点页自己收尾")
            };
            var missing = expected
                .Where(row => !lua.Contains(row.Snippet, StringComparison.Ordinal))
                .ToList();
            if (missing.Count == 0)
            {
                line($"[通过] 生成的 Lua 含全部 {expected.Length} 个结构要点。");
            }
            else
            {
                line($"[失败] 生成的 Lua 缺少：{string.Join("；", missing.Select(static row => row.Why))}。");
                failures++;
            }

            // 终点页才允许 EndMessage：菜单页若也收尾，窗口会一闪而过。
            var endCount = lua.Split("NPCFUN:EndMessage(true)").Length - 1;
            if (endCount == 1)
            {
                line("[通过] 只有没有按钮的那一页调了 EndMessage。");
            }
            else
            {
                line($"[失败] EndMessage 出现了 {endCount} 次，应该只有 1 次（终点页）。");
                failures++;
            }

            // 重名页必须被拒（客户端只按 Index 找内容）。
            try
            {
                ClientNpcPatch.GenerateLua([
                    tree,
                    tree with { Dialogue = tree.Dialogue with { DialogueKey = key + "_b" } }
                ]);
                line("[失败] 两棵树用同一批页号，生成时没有报错。");
                failures++;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("全局唯一"))
            {
                line("[通过] 两棵树抢同一页号时生成报错。");
            }

            // NPC 放置：模板来自该图已发布内容，写完读回再删掉。
            var maps = await store.LoadMapsAsync();
            short placeMap = 0;
            NpcTemplateChoice? template = null;
            foreach (var map in maps)
            {
                var candidates = await store.LoadNpcTemplatesAsync(map.MapId);
                if (candidates.Count > 0)
                {
                    placeMap = map.MapId;
                    template = candidates[0];
                    break;
                }
            }

            if (template is null)
            {
                line("[跳过] 没有任何地图能列出已发布的 NPC 外观模板，放置那一步没验。");
            }
            else
            {
                line($"[信息] 放置底稿地图 map{placeMap}：外观模板 {template.TemplateKey}" +
                     $"（外观 {template.AppearanceType}，该图已在用 {template.UsedCount} 个）。");
                var spawnName = $"__selftest_npc_spawn__{stamp}";
                var existingSpawns = await store.LoadSpawnRowsAsync(placeMap);
                var probeObjectId = 47_900u;
                while (existingSpawns.Any(row => row.ObjectId == probeObjectId))
                {
                    probeObjectId++;
                }

                await store.SaveSpawnAsync(
                    new GmNpcSpawnInput(
                        spawnName,
                        placeMap,
                        $"gm_selftest_{stamp}",
                        template.TemplateKey,
                        key,
                        null,
                        probeObjectId,
                        template.AppearanceType,
                        template.SampleX + 3f,
                        template.SampleZ + 3f,
                        1.5f,
                        true,
                        "自检"),
                    "selftest");
                var afterSpawn = await store.LoadSpawnRowsAsync(placeMap);
                var savedSpawn = afterSpawn.FirstOrDefault(row => row.Name == spawnName);
                if (savedSpawn is null ||
                    savedSpawn.ObjectId != probeObjectId ||
                    savedSpawn.DialogueKey != key ||
                    savedSpawn.TemplateKey != template.TemplateKey)
                {
                    line("[失败] NPC 放置存回来的内容不对。");
                    failures++;
                }
                else
                {
                    line($"[通过] NPC 放置存库读回：对象 {savedSpawn.ObjectId}、绑定对话 {savedSpawn.DialogueKey}。");
                }

                await store.DeleteSpawnAsync(savedSpawn!.Id);
                var afterDelete = await store.LoadSpawnRowsAsync(placeMap);
                if (afterDelete.Count == existingSpawns.Count &&
                    afterDelete.All(row => row.Name != spawnName))
                {
                    line("[通过] 删除放置后列表恢复原样。");
                }
                else
                {
                    line($"[失败] 删除放置没干净：{existingSpawns.Count}→{afterDelete.Count}。");
                    failures++;
                }
            }

            // 在临时目录里拿**真实客户端文件**的副本试补丁。
            var realRoot = settings.ClientRoot;
            var realDispatch = Path.Combine(realRoot, ClientNpcPatch.DispatchRelativePath);
            var realLoad = Path.Combine(realRoot, ClientNpcPatch.LoadRelativePath);
            if (!File.Exists(realDispatch) || !File.Exists(realLoad))
            {
                line($"[跳过] 客户端目录 {realRoot} 里找不到 NpcFun.lua / NpcFunLoad.xml，" +
                     "补丁落地这一步没验。");
            }
            else
            {
                var tempNpcFun = Path.Combine(tempRoot, "Localization", "en_us", "UI", "XML", "NpcFun");
                Directory.CreateDirectory(tempNpcFun);
                File.Copy(realDispatch, Path.Combine(tempNpcFun, "NpcFun.lua"));
                File.Copy(realLoad, Path.Combine(tempRoot, ClientNpcPatch.LoadRelativePath));

                var first = ClientNpcPatch.Apply(tempRoot, [tree]);
                var dispatchText = File.ReadAllText(Path.Combine(tempNpcFun, "NpcFun.lua"));
                var loadText = File.ReadAllText(
                    Path.Combine(tempRoot, ClientNpcPatch.LoadRelativePath));
                var branchCount = dispatchText.Split("NPC_FLAG_SYS_GMTOOL").Length - 1;
                var scriptCount = loadText
                    .Split(ClientNpcPatch.GeneratedLuaFileName).Length - 1;
                if (first.LuaChanged && first.DispatchChanged && first.LoadChanged &&
                    branchCount == 1 && scriptCount == 1)
                {
                    line("[通过] 补丁落地：新 Lua 已写、分派插了 1 个分支、加载表登记了 1 行。");
                }
                else
                {
                    line($"[失败] 补丁落地不对：Lua={first.LuaChanged} 分派={first.DispatchChanged} " +
                         $"加载={first.LoadChanged} 分支数={branchCount} 脚本名出现={scriptCount}。");
                    failures++;
                }

                if (branchCount == 1 && dispatchText.Contains(
                        "Type == NPC_FLAG_SYS_TRANMIT then",
                        StringComparison.Ordinal))
                {
                    line("[通过] 原有分派链没被动过（TRANMIT 那支还在）。");
                }
                else
                {
                    line("[失败] 原有分派链被改坏了。");
                    failures++;
                }

                var second = ClientNpcPatch.Apply(tempRoot, [tree]);
                if (!second.LuaChanged && !second.DispatchChanged && !second.LoadChanged)
                {
                    line("[通过] 再跑一次补丁：三个文件都没变化（幂等）。");
                }
                else
                {
                    line($"[失败] 补丁不幂等：Lua={second.LuaChanged} 分派={second.DispatchChanged} " +
                         $"加载={second.LoadChanged}。");
                    failures++;
                }

                if (second.Backups.Count == 0 && first.Backups.Count == 2)
                {
                    line("[通过] 只在真正改动前备份（第一次 2 个，第二次 0 个）。");
                }
                else
                {
                    line($"[失败] 备份数量不对：第一次 {first.Backups.Count}，第二次 {second.Backups.Count}。");
                    failures++;
                }
            }
        }
        catch (Exception ex)
        {
            line($"[失败] NPC 回环抛异常：{ex.Message}");
            failures++;
        }
        finally
        {
            try
            {
                if (created)
                {
                    await store.DeleteDialogueAsync(key);
                }
            }
            catch (Exception ex)
            {
                line($"[失败] 清理探针对话失败：{ex.Message}");
                failures++;
            }

            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }

            var left = (await store.LoadTreesAsync())
                .Any(row => row.Dialogue.DialogueKey == key);
            if (left)
            {
                line("[失败] 探针对话没删掉。");
                failures++;
            }
            else
            {
                line("[通过] 清理完成，探针对话已删除。");
            }
        }

        return failures;
    }

    private static async Task<int> CheckQuestRewardAsync(
        LootToolSettings settings,
        IReadOnlyList<ItemRow> items,
        Action<string> line)
    {
        var failures = 0;
        line(string.Empty);
        line("── 任务奖励（本次新增） ────────────────────────");

        await using var store = new QuestRewardStore();
        try
        {
            store.Connect(settings.BuildConnectionString());
        }
        catch (Exception ex)
        {
            line($"[失败] 任务奖励页无法连接数据库：{ex.Message}");
            return 1;
        }

        if (!await store.HasQuestRewardSchemaAsync())
        {
            // 服务端迁移可能还没跑：这是旧库的正常状态，跳过而不是失败
            line($"[跳过] {QuestRewardStore.MissingSchemaMessage(store.DatabaseName)}");
            return 0;
        }

        line("[通过] 任务奖励 schema 存在（quest_reward_slots + quest_reward_values）。");

        var overridden = await store.LoadQuestRowsAsync();
        line(overridden.Count == 0
            ? "[信息] 当前没有任何任务被覆盖（两张表都是空的）。"
            : $"[信息] 已覆盖任务 {overridden.Count} 个：" + string.Join(
                "、",
                overridden.Select(static row => $"{row.QuestId}（{row.Status}）")));

        // 回环目标：本库真实存在的任务（character_quests / quest_monster_references），
        // 取不到才退回抓包奖励记录里的新手任务 518/519/520（服务端 StarterQuestRewardRecords）。
        var probeQuestId = await store.FindProbeQuestIdAsync(
            overridden.Select(static row => row.QuestId).ToHashSet(),
            [518, 519, 520]);
        if (probeQuestId == 0)
        {
            line("[跳过] 找不到既真实存在、又没有覆盖的任务 ID，跳过回环（不动现有 GM 数据）。");
            return failures;
        }

        if (items.Count < 2)
        {
            line($"[跳过] 物品目录只有 {items.Count} 条，无法构造两个槽位，跳过回环。");
            return failures;
        }

        var firstItemId = items.Any(static item => item.Id == 4001)
            ? 4001
            : items.Min(static item => item.Id);
        var secondItemId = items.First(item => item.Id != firstItemId).Id;
        var firstSlot = (short)0;
        var secondSlot = (short)3;
        var probeValues = new QuestRewardValues(1234, 5, 678, 9);
        line($"[信息] 回环目标任务：{probeQuestId}（当前未覆盖，测完删干净）；" +
            $"物品用 {firstItemId} / {secondItemId}。");

        // 校验必须是写库前拦截，所以这里顺手验一遍错误输入不会落库
        async Task ExpectRejectedAsync(
            IReadOnlyList<QuestRewardSlotInput> badSlots,
            bool overrideValues,
            QuestRewardValues badValues,
            string what)
        {
            try
            {
                await store.SaveAsync(
                    probeQuestId,
                    badSlots,
                    overrideValues,
                    badValues);
                line($"[失败] {what} 竟然通过了校验。");
                failures++;
            }
            catch (QuestRewardToolException ex)
            {
                line($"[通过] {what} 被校验拦截：{ex.Message}");
            }
        }

        try
        {
            await store.SaveAsync(
                probeQuestId,
                [
                    new QuestRewardSlotInput(firstSlot, firstItemId),
                    new QuestRewardSlotInput(secondSlot, secondItemId)
                ],
                overrideValues: true,
                probeValues);
            var slots = await store.LoadSlotsAsync(probeQuestId);
            var values = await store.LoadValuesAsync(probeQuestId);
            // 读回来的行永远带一个（可能是空的）属性记录，所以比较时也要带上
            var roundTripOk = slots.Count == 2 &&
                slots[0] == new QuestRewardSlot(firstSlot, firstItemId, new ItemAttributeValues()) &&
                slots[1] == new QuestRewardSlot(secondSlot, secondItemId, new ItemAttributeValues()) &&
                values == probeValues;
            line(roundTripOk
                ? $"[通过] 写入后读回一致：槽位 {slots[0].SlotIndex}/{slots[1].SlotIndex} " +
                  $"物品 {slots[0].ItemId}/{slots[1].ItemId}，" +
                  $"数值 经验 {values!.Experience}/TP {values.TalentPoints}/" +
                  $"银币 {values.Silver}/金币 {values.Gold}"
                : $"[失败] 写入后读回不一致：槽位 {slots.Count} 个，" +
                  $"数值 {(values is null ? "无" : "有")}。");
            if (!roundTripOk)
            {
                failures++;
            }

            var listed = (await store.LoadQuestRowsAsync())
                .FirstOrDefault(row => row.QuestId == probeQuestId);
            var listedOk = listed is { SlotCount: 2, HasValues: true };
            line(listedOk
                ? $"[通过] 并集列表认出了刚写入的任务：{probeQuestId} → {listed!.Status}。"
                : $"[失败] 并集列表没有正确反映 {probeQuestId}（{(listed is null ? "没出现" : listed.Status)}）。");
            if (!listedOk)
            {
                failures++;
            }

            await store.SaveAsync(
                probeQuestId,
                [new QuestRewardSlotInput((short)1, secondItemId)],
                overrideValues: false,
                new QuestRewardValues(0, 0, 0, 0));
            var replaced = await store.LoadSlotsAsync(probeQuestId);
            var uncovered = await store.LoadValuesAsync(probeQuestId);
            var replaceOk = replaced.Count == 1 &&
                replaced[0] == new QuestRewardSlot(1, secondItemId, new ItemAttributeValues()) &&
                uncovered is null;
            line(replaceOk
                ? "[通过] 槽位是全量替换：改成只留槽位 1 后，原来的槽位 0/3 已删除；" +
                  "取消「覆盖数值」后 quest_reward_values 的行也没了（恢复内置）。"
                : $"[失败] 全量替换语义不对：剩余槽位 {replaced.Count} 个，" +
                  $"数值行 {(uncovered is null ? "已删除" : "仍在")}。");
            if (!replaceOk)
            {
                failures++;
            }

            await store.SaveAsync(
                probeQuestId,
                [],
                overrideValues: true,
                probeValues);
            var valuesOnly = (await store.LoadQuestRowsAsync())
                .FirstOrDefault(row => row.QuestId == probeQuestId);
            var valuesOnlyOk = valuesOnly is { SlotCount: 0, HasValues: true };
            line(valuesOnlyOk
                ? $"[通过] 只覆盖数值、不覆盖物品也能保存：{valuesOnly!.Status}。"
                : $"[失败] 只覆盖数值的结果不对（{(valuesOnly is null ? "没出现" : valuesOnly.Status)}）。");
            if (!valuesOnlyOk)
            {
                failures++;
            }

            // 12 列「物品属性」回环（任务奖励这一侧）
            if (!await store.HasItemAttributeColumnsAsync())
            {
                line("[跳过] 数据库的 quest_reward_slots 还没有那 12 列物品属性" +
                     "（服务端迁移 20260927_214 未应用），跳过奖励物品属性回环。");
                try
                {
                    await store.SaveAsync(
                        probeQuestId,
                        [new QuestRewardSlotInput(firstSlot, firstItemId, ProbeAttributes)],
                        overrideValues: false,
                        new QuestRewardValues(0, 0, 0, 0));
                    line("[失败] 库里还没有那 12 列物品属性，却把奖励物品属性写进去了。");
                    failures++;
                }
                catch (QuestRewardToolException ex)
                {
                    line($"[通过] 缺列时写奖励物品属性被拦住：{ex.Message}");
                }
            }
            else
            {
                await store.SaveAsync(
                    probeQuestId,
                    [new QuestRewardSlotInput(firstSlot, firstItemId, ProbeAttributes)],
                    overrideValues: false,
                    new QuestRewardValues(0, 0, 0, 0));
                var readSlots = await store.LoadSlotsAsync(probeQuestId);
                var withAttributes = readSlots
                    .FirstOrDefault(slot => slot.SlotIndex == firstSlot)
                    ?.Attributes;
                var attributesOk = withAttributes == ProbeAttributes;
                line(attributesOk
                    ? $"[通过] 奖励物品属性写入后读回一致：{withAttributes!.Summary}"
                    : $"[失败] 奖励物品属性写回不一致：期望 {ProbeAttributes.Summary}，" +
                      $"实际 {withAttributes?.Summary ?? "NULL"}。");
                if (!attributesOk)
                {
                    failures++;
                }

                // 还原成 NULL
                await store.SaveAsync(
                    probeQuestId,
                    [new QuestRewardSlotInput(firstSlot, firstItemId)],
                    overrideValues: false,
                    new QuestRewardValues(0, 0, 0, 0));
                var restored = (await store.LoadSlotsAsync(probeQuestId))
                    .FirstOrDefault(slot => slot.SlotIndex == firstSlot)
                    ?.Attributes;
                var restoredOk = restored is { IsEmpty: true };
                line(restoredOk
                    ? "[通过] 奖励物品属性已还原成不配置（12 列 NULL）。"
                    : $"[失败] 奖励物品属性没有还原干净：{restored?.Summary ?? "NULL"}。");
                if (!restoredOk)
                {
                    failures++;
                }
            }

            await ExpectRejectedAsync(
                [new QuestRewardSlotInput((short)8, firstItemId)],
                overrideValues: false,
                new QuestRewardValues(0, 0, 0, 0),
                "槽位 8 超出 0-7");
            await ExpectRejectedAsync(
                [new QuestRewardSlotInput((short)0, 0)],
                overrideValues: false,
                new QuestRewardValues(0, 0, 0, 0),
                "勾选的槽位没有物品");
            await ExpectRejectedAsync(
                [new QuestRewardSlotInput(firstSlot, int.MaxValue)],
                overrideValues: false,
                new QuestRewardValues(0, 0, 0, 0),
                "item_templates 里不存在的物品");
            await ExpectRejectedAsync(
                [],
                overrideValues: true,
                new QuestRewardValues(-1, 0, 0, 0),
                "负数经验值");
        }
        finally
        {
            // 不管中间哪一步炸了，写进去的行都要删干净
            try
            {
                await store.DeleteOverridesAsync(probeQuestId);
                var leftSlots = await store.LoadSlotsAsync(probeQuestId);
                var leftValues = await store.LoadValuesAsync(probeQuestId);
                var clean = leftSlots.Count == 0 && leftValues is null;
                line(clean
                    ? $"[通过] 已清理自测写入的行，任务 {probeQuestId} 回到未覆盖（沿用内置）。"
                    : $"[失败] 任务 {probeQuestId} 还有残留：槽位 {leftSlots.Count} 个，" +
                      $"数值行 {(leftValues is null ? "无" : "有")}。");
                if (!clean)
                {
                    failures++;
                }
            }
            catch (Exception ex)
            {
                line($"[失败] 清理自测写入失败：{ex.Message}");
                failures++;
            }
        }

        return failures;
    }

    /// <summary>三态在报告里怎么念：NULL 就是「NULL（跟随物品模板）」。</summary>
    private static string DescribeBound(bool? boundOnPickup) => boundOnPickup switch
    {
        true => "true（拾取绑定）",
        false => "false（可交易）",
        null => "NULL（跟随物品模板）"
    };

    /// <summary>
    /// 自测用的物品属性：品质10 / 等级12 / 属性 24 等级5、133（不配等级）、90（不配等级）。
    /// 摘要正好是「品质10 等级12 属性:24/5、133/-、90/-」。
    /// </summary>
    private static ItemAttributeValues ProbeAttributes { get; } = new(
        Quality: 10,
        Grade: 12,
        Attribute1: 24,
        AttributeLevel1: 5,
        Attribute2: 133,
        Attribute3: 90);

    private static int Finish(StringBuilder report, int exitCode)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, report.ToString());
            Console.WriteLine($"报告已写入：{path}");
        }
        catch (IOException)
        {
            // The console output is enough when the report cannot be written.
        }

        return exitCode;
    }
}
