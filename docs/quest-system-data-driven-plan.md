# 任务系统：数据驱动的实现方案

> 目标：用**一张表 + 一个生成器**支撑全部 1176 个任务，避免每个任务写一套逻辑。

## 一、从抓包确认的帧规律（这是方案的基础）

对比参考服三组接受帧（抓包 247 / 664 / 1373）：

| 对比 | 差异字节数 | 差异内容 |
| --- | ---: | --- |
| 第 1 组 vs 第 2 组 | **0** | 完全相同（同一任务的重复应答） |
| 第 2 组 vs 第 3 组 | **84** | `+4` NPC、`+8` 任务、`+12` 序号、`+72` 起的目标/奖励区 |

`S2C 10084`（12 字节）的差异只有 **2 处**：`+4` 任务、`+8` 序号。

**结论：任何任务的应答帧 = 一个固定模板 + 少量字段回填。** 不需要为任务写帧。

## 二、帧结构（`S2C 10082`，648 字节）

| 偏移 | 含义 |
| ---: | --- |
| `+0` | u16 帧长 648 |
| `+2` | u16 opcode 10082 |
| `+4` | u32 **NPC**（交互 ID） |
| `+8` | u32 **任务 ID** |
| `+12` | u32 **序号** |
| `+16`..`+71` | 头部保留区（抓包中为 0） |
| `+72` 起 | **任务状态区**：任务 ID + 目标行 |

`S2C 10084`（12 字节）：`+4` 任务 ID、`+8` 序号。
`S2C 10092`（48 字节）：`+4` 可接任务条数（u32），`+8` 起 20 个 u16 任务 ID。
**不是固定全零** —— 接受任务路径当时恰好没有可接任务才全零；任务窗口「查找」按钮
（同一 opcode `C2S 10091`，`+4=16`）拿到的是本角色真正的可接任务表。
见 [quest-lookup-panel-20261006.md](quest-lookup-panel-20261006.md)。

## 三、数据来源（全部是现成数据，不需要手写）

| 数据 | 来源 | 覆盖 |
| --- | --- | --- |
| 任务 ID | `Quest.xml` 的 `ID` | 1176 条 |
| 给予者 NPC | `Quest.xml` `GiverName` | 1174 条 |
| 回复者 NPC | `Quest.xml` `ResponderName` | 1174 条 |
| 等级/职业/阵营/排序 | `Quest.xml` `MinLevel/MaxLevel/Class/Faction/UIQuestSort` | 全部 |
| 怪物/物品目标 | `Quest.xml` `CreatureMapID` / `ItemMapID` | 754 / 60 条 |
| **奖励** | `Text/Quest/<id>.dat` 的 `Append` 块 | **1098 条** |
| 任务文案 | 同文件的 `Title`/`Details`/`Objectives` | 1306 个文件 |
| NPC 交互 ID | 数据库 `npc_spawn_definitions.interaction_id` | 运行时查表 |

奖励字段实测分布（出现次数）：

```
经验 980   专长点 772   银币 262   公会银币 32
公会贡献 32   金币 20   公会金币 16
```

**所以奖励不需要每个任务写代码，只要解析 `Append` 的「名称：数量」行。**

## 四、建议的最小实现（一个表 + 一个生成器）

### 4.1 一张表

```
QuestTableEntry(
    uint   QuestId,          // Quest.xml ID
    uint   GiverNpcId,       // npc_spawn_definitions 解析
    uint   ResponderNpcId,
    uint?  NextQuestId,      // 由「上一任务的回复者 == 下一任务的给予者」推导
    int    Experience,
    int    TalentPoints,
    int    Silver,
    int    Gold, ...)
```

### 4.2 一个生成器（替换掉现在写死的帧）

```
byte[] QuestAnswer(QuestTableEntry q, uint sequence)   // 648 字节
byte[] QuestConfirm(QuestTableEntry q, uint sequence)  // 12 字节
```

现在的 `PacketBuilder.QuestFlow.cs` 里那 20 个常量可以整体删掉，
只留 **1 个 10082 模板 + 1 个 10084 模板 + 1 个 10092 模板**。

### 4.3 流程（三个入口，全部查表）

| 客户端动作 | 服务端 |
| --- | --- |
| 点 NPC | `10067`：`flags=3` + 该 NPC 的脚本键（查表得） |
| 请求接受 | 查表得任务 → `QuestAnswer` + `10092` + `QuestConfirm` |
| 请求交付 | 查表得奖励 → 走 `ApplyMonsterKillRewardAsync` 等统一入口 → 应答帧 |

**奖励统一走已有的持久化入口**（`ApplyMonsterKillRewardAsync` 处理经验/专长点，
银币/金币走商店那套钱包改动），不需要每个任务写发放代码。

## 五、下一步（按顺序）

1. **先测当前构建**：确认「接取 → 交付 → 拿奖励 → 接下一环」是否通
2. **写基于 Quest.xml 的抽取脚本**，生成任务表（这一步可以离线做，零风险）
3. **把写死的帧替换成生成器**，删掉 20 个常量
4. 补目标进度（`+72` 状态区）的生成规则——目前只从抓包知道"已完成"的样子，
   需要再抓一个"进行中"的样本才能定死

## 六、当前代码里需要清理的部分

| 文件 | 处理 |
| --- | --- |
| `PacketBuilder.QuestFlow.cs` | 20 个写死常量 → 缩减为 3 个模板 |
| `PacketBuilder.Quests.cs` | 保留（场景/交接帧），去重 |
| `PacketBuilder.QuestHandIn.cs` | 合并进 QuestFlow |
| `PacketBuilder.QuestSnapshot.cs` | 由生成器接管 |
| `QuestContentBaseline.cs` | 写死的 5091/5103/518 → 改为查表 |
