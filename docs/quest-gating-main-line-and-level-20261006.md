# 任务门禁新规则：主线串行、非主线按等级（2026-10-06）

把「所有任务穿在一条线上」换成两条并行的门禁：

- **主线**（客户端 `UIQuestSort == 0`，即 `Text\Quest.dat` 的 `QuestSortStory` / 剧情）
  必须把上一条主线交付掉，才能接下一条；
- **其余任务**（悬赏 / 收集 / 探索 / 每日 / 公会 …）**只看等级**：`MinLevel ≤ 角色等级 ≤ MaxLevel`
  就能接，与主线进度**无关**，可以多条同时挂在同一 NPC 上。

例：野猪王这类重复任务，到级就能接，不必先做完主线。

## 1. 数据来源（客户端 = 权威）

| 字段 | 来源 | 说明 |
| --- | --- | --- |
| `MinLevel` / `MaxLevel` | `Localization\zh_cn\Settings\Sys\Quest.xml` | 可接等级区间 |
| `IsMainLine` | 同上，`UIQuestSort == "0"` | 客户端自己的剧情分类 |
| `GiverName` / `ResponderName` | 同上 | 发放 / 交付 NPC |
| 奖励（Exp/TP/Silver/Gold） | `Localization\en_us\Text\Quest\<id>.dat` 的 `Append` | 文本里没有物品奖励 |

`tools/gen_sparta_quest_chain.py` 现在把这三个新字段一起生成进
`src/Godswar.Server/Domain/World/Content/StarterQuestChain.cs`（`Step` 记录新增
`MinLevel`、`MaxLevel`、`IsMainLine`）。

### 生成时剔除的行（参考服从不发放）

| 剔除原因 | 条数 | 依据 |
| --- | --- | --- |
| `MinLevel > MaxLevel` 废行 | 130 | 等级区间永远无法满足 |
| `200/200` 占位副本 | 230 | 同一任务的第二份拷贝；参考服在 105 个"有抓包 10077 表"的 NPC 上一条都没发，而同一批 NPC 的普通任务填满了那些表 |
| 发放者是物品而非 NPC（`QuestScroll`） | 246 | 由物品触发，没有 NPC 菜单 |

> 前两项与 `docs/quest-roster-and-prerequisites-20261006.md` 第 1.2 节的抓包结论一致
> （那份文档记的是 180 + 52 条"被剔除/反被列入"，口径按抓包表统计；这里按 Quest.xml 全表统计）。

### 生成结果

| 项 | 数值 |
| --- | --- |
| 生成行数 | **570**（斯巴达 284 / 雅典 286） |
| 其中主线行 | **258**（每阵营 129） |
| 按等级发布的行 | **312** |
| 剔除 | 606 |

校验脚本 `artifacts/quest-client-analysis/q16_validate_chain.py` 逐条核对：
等级与 `Quest.xml` 完全一致、`IsMainLine` 与 `UIQuestSort` 一致、NPC 键一致、
废行未残留、**每阵营主线恰好一个链首一个链尾且 129/129 走通**、
**主线等级序列单调不降**。结论：全部通过。

## 2. 门禁实现

改动集中在 `src/Godswar.Server/Game/GameClientHandler.Quests.cs`。

| 方法 | 作用 |
| --- | --- |
| `AcceptableQuests()` | 本角色当前**全部**可接任务。主线取「链上第一个未完成的」，等级任务取「在区间内且未完成未携带」。**排序 = 主线优先，其后按 MinLevel 升序** |
| `IsMainLineQuestOpen(step)` | 主线判定，转调 `IsChainUnlocked` |
| `IsLevelEligible(step)` | `MinLevel ≤ Level ≤ MaxLevel` |
| `IsQuestAcceptable(step)` | 非主线判定：未完成 + 未携带 + `IsLevelEligible` + NPC 可解析 |
| `IsQuestPublishable(step)` | 发放者与交付者都能解析（NPC 解析不出来会让客户端崩，见 `StarterQuestChain` 注释） |

`IsChainUnlocked` **只被主线行门禁**：遍历时跳过 `!IsMainLine` 的行，
所以夹在两段剧情之间的每日/公会/悬赏任务既不阻塞剧情，也不被剧情阻塞。

替换掉的旧语义：`NextAcceptableQuest()`（单值、纯位置门禁）已删除。
需要"下一个"的地方（场景查询 `10083`、掉落刷新）改取 `AcceptableQuests()` 的表头，
但**只有主线是单值**，其余全部照发。

### 协议侧

| 包 | 改动 |
| --- | --- |
| `10077`（NPC 给出的任务 + available 标记） | `MergeMarkerEntries` / `QuestMarkerEntries` 改成收「整份可接列表」，**同一 NPC 可以有多条 `available=1`** |
| `10078`（哪些 NPC 有可接任务） | `SendQuestNpcMarksAsync` 现在收集**所有**可接任务的发放者，不再只发一个 |
| `10092`（查找面板） | **不再截断**。帧按客户端自己的列表容器（`QuestInfoUI.xml` 的 `Quest1..Quest30`，30 行）扩展；20 条以内仍与抓包逐字节相同 |
| 升级 | `GameClientHandler.Progression.cs` 在 `LevelUps.Count > 0` 后调用 `SendQuestLevelUpRefreshAsync`，重发 `10078/10079` 与本角色相关的 NPC 的 `10077/10080` |

**注意**：`MaximumCarriedQuests = 20`（可同时持有的任务上限）与查找面板的显示条数
**是两件事**。面板显示「当前所有可接任务」，不受这个上限约束。

## 3. 验证

```powershell
# 重新生成目录
python tools/gen_sparta_quest_chain.py

# 校验生成结果（等级/主线标记/链路完整性）
python artifacts/quest-client-analysis/q16_validate_chain.py

# 构建 + 任务协议检查
dotnet build src/Godswar.Server/Godswar.Server.csproj -c Release
dotnet build tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj -c Release
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll "Quest protocol framing"
```

结果：

- 服务端 `0 警告 0 错误`；
- `Quest protocol framing` **PASS**；
- 全部任务相关检查 `13 passed / 0 failed / 1 skipped`（跳过项是未配 PostgreSQL 的持久化检查）。

`CheckQuestGatingRules()` 新增的断言（全部基于客户端真实数据）：

| 断言 | 期望 |
| --- | --- |
| 1 级新号的开放列表 | 恰好 1 条 = `518`（主线首环） |
| 交付 `518` 后 | 恰好 1 条 = `519`；`520` 仍然关闭 |
| 5 级 | `207`（3-17 公会行）开放，且与 `518` **同时**开放 |
| 20 级 | 开放 `518` + `192`(12-20) + `208`(18-32) + `210`(14-28) = 4 条；`211`(29-43)、`209`(33-47) 关闭 |
| 30 级 | `192`、`210` 因超上限重新关闭；`208`、`211` 开放 |
| 携带中的等级行 | 不再重复列出 |
| 已完成的等级行 | 不再重复列出 |
| 140 级 | 开放 > 10 条（旧实现恒为 1 条） |
| 雅典 1 级 | 走自己的主线 `1518` |

## 4. 仍未做 / 未确定

1. **NPC 铺放范围决定发布面**。能生成的行要求发放者与交付者都在
   `npc-translation\published-npcs.txt` 里（353 个）。往后再铺 NPC 需要重跑生成器。
2. **`UIQuestSort` 数值语义**：0 有 `Text\Quest.dat` 的 `QuestSortStory`/剧情直证；
   1（悬赏）、2、3（每日）、4（公会）由发放者名字与内容特征判定，
   **数值↔名字的枚举顺序没有 exe 直证**（详见
   `artifacts/quest-client-analysis/客户端任务系统与主线梳理.md` §8）。
3. **主线的前置关系仍是"一条线性链"**。客户端没有前置字段，链序由生成器按
   「等级、ID」排出；参考服的运行期多链开放状态仍未拿到，
   `docs/quest-roster-and-prerequisites-20261006.md` §3.2 的三条路线依然有效。
4. **`10092` 帧超过 30 条会截断**（客户端容器只有 30 行）。
   当前 312 条等级行按等级分布，单等级最高开放 40 条（110 级雅典侧），
   极端等级下面板尾部会被截掉；截掉的是等级最高的那批。
5. **查找面板的空表**仍与接取路径模板逐字节相同（既有不变式，未改）。
