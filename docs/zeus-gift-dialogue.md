# 宙斯献礼（Zeus' Gift Event）多级对话实现

> **本文多处已被抓包推翻，请以 [zeus-gift-capture-analysis.md](zeus-gift-capture-analysis.md) 为准。**
> 至少三处过时：功能列表不是 `[26, 6, 7]`（抓包实测 `[26]`，代码也早已改成 `[26]`）；
> 第 4、5 页并非"不可达"；两个 NPC 的角色在本文里是反的
> （抓包实测：`Athens_113` 是收献礼的那个，`Athens_114` 才收祈祷石）。

NPC：`[Event]Zeus' Loyal Believer`，脚本名 `Athens_113` / `Sparta_113`，
两首都坐标 `(154, -59, 3)`。
功能号 `26`（客户端 `NpcFun.lua` 的 `NPC_FLAG_SYS_ZEUS`）。

对话机制通则见 `docs/npc-function-dialog-mechanism.md`；本文只讲这个功能的落地。

---

## 1. 脚本页与功能号

客户端 `NpcFunZeus.lua` 内部有 5 个 `Index` 页。服务端一次对话最多打包 3 个功能号，
所以用 3 个号覆盖前 3 页：

| 打包位 | 功能号 | 客户端 `Index` | 客户端脚本页 |
| --- | --- | --- | --- |
| 1 | `26` | 1 | 献礼主菜单 |
| 2 | `6` | 2 | 献礼/祈祷石页 |
| 3 | `7` | 3 | 兑换/幸运货架页 |

`6` 与 `7` 是客户端常量表里**未定义的空隙**（`NpcFun.lua` 定义了 1–84、89 等，
独缺 6、7、85、86、88），因此它们不指向任何别的脚本。
`NpcFunZeus_SetText` 的分支从不读取 `Type`，所以这三个号都会落到同一个脚本，
只是 `Index` 不同。

```csharp
private static readonly int[] ZeusGiftFunctionList = [26, 6, 7];
```

打开对话用 `flags 0x200`（功能菜单）+ 打包列表 + 脚本名：

```csharp
PacketBuilder.NpcDialogOpenAck(npc.InteractionId, ZeusGiftFunctionList, npc.NpcKey)
```

**第 4、5 页（`Index==4/5`）当前不可达**：打包位只有 3 个，
要覆盖它们必须把 `85`/`86` 这类号加进来，而那需要先把换页行为实测清楚。

---

## 2. 事件实际内容（从脚本与文案读出）

| 分支 | 内容 |
| --- | --- |
| 献礼（礼物） | 交指定材料/装备，每天 10 次、周末 12 次，奖励等级逐步提升 |
| 祈祷石存入 | 周一 12:00 – 周五 23:55 存入，每颗换 1 点天赋点，周末结算 |
| 兑换好礼 | 普通货物 / 限量物品 / 经验与天赋点 |
| 运气游戏 "Luck matters" | 周六 12:00 – 周日 23:55，每 10 粉尘换一次随机点数（1–1000），周日 12:00 比最高分 |

**服务端只回数字。** `NpcFunZeus.lua` 的 152 个分支里，每一个都只设置按钮文案/位置
或显示一行文字，没有任何发放逻辑。因此本实现**不发物品、不扣材料、不计次数**——
事件的数值经济完全没有实现，这一点是刻意的。

---

## 3. 应答表结构

`Game/ZeusGiftDialogueCatalog.cs`：

```csharp
internal readonly record struct ZeusDialogueEntry(
    ZeusDialogueKind Kind,   // Button / Result / Note / Flavour / Detail
    int SubId,               // 发给客户端的号
    int NextPage,
    string LabelKey);        // 仅作核对用，对应 LuaText.lua 的键
```

三张表：

| 表 | 作用 |
| --- | --- |
| `ZeusGiftOpeningMenu` | 主菜单 4 项：献礼 `1000`、祈祷石 `1200`、兑换 `1201`、运气 `101` |
| `ZeusDialogueMenus` | 「点击 → 展开」：`(页, 点击号) → 下一组号` |
| `ZeusDialogueResults` | 「点击 → 单行结果」：`(页, 号) → 一行文案` |

判定顺序（`HandleZeusGiftGuideActionAsync`）：

1. `subId == -1` → 回该页自己的菜单。
2. 命中 `ZeusDialogueMenus` → 回展开的那组号。
3. 命中 `ZeusDialogueResults` → 回那一行。
4. 都没有 → 回该页的常驻菜单，打 `[npc] zeus gift unmapped`。

### 3.1 等级门槛

客户端脚本对「我也要献礼」这一项要求 55 级（`NF_L0_Z100` =
"You have to be Level 55+ to do that."）。服务端在这一个入口上把关：

```csharp
if (subId == 1000 && _character.Level < ZeusGiftMinimumLevel)
    → 回 [100]
```

### 3.2 菜单链（已抄录的部分）

```
第1页  26  1000 献礼 ──→ 1001/1002/1003（献礼对话）
           1200 祈祷石 ─→ 1203/1204/1205（兑换货架）
           1201 兑换好礼 ─→ 1203/1204/1205
           101  运气 ───→ 1203/1204/1205

第2页  6   1203 普通货物 ─→ 装备/材料等级表
           1204 限量物品 ─→ 4000..4006 + 302
           1205 经验天赋 ─→ 装备/材料等级表
```

结果文案全部按页归类，例如第 2 页的 `1300` 背包满、`1306` 只能在周一到周五存入、
`1517` 活动说明；第 3 页的 `1315` 祈祷石不足、`1327` 请输入 1–99、
`1503` 未放入物品、`1506` 已兑换过。

---

## 4. 抄录保真度

- `NpcFunZeus.lua` 共 **714 行 / 152 个分支**（Index1=42、Index2=25、Index3=57、
  Index4=20、Index5=8），其中 58 个带 `NPCFUN:EndMessage(true)`。
- 脚本自身有 2 处**不可达重复分支**：第 1 页的 `SubID == 4220`（119 行、143 行）
  和 `SubID == 3819`（134 行、146 行）各写了两遍。抄录时按可达的那一份。
- `NpcFunZeus.lua` 引用 **110 个** `NF_L0_Z*` 文案键，其中 **7 个在
  `LuaText.lua` 里根本不存在**：
  `NF_L0_Z1214` `Z1215` `Z1216` `Z1217` `Z1218` `Z1219` `Z1220`
  （第 416–436 行的按钮文案）。en_us、zh_cn 及所有备份里都没有，
  客户端那 7 个按钮会显示空白 —— 这是客户端自身的缺失，不是服务端问题。
- `LuaText.lua` 里 `NF_L0_Z*` 共 **253 个**键。

---

## 5. 服务端不负责的部分

客户端脚本自己负责，服务端**发不出也管不了**：

| 行为 | 归属 |
| --- | --- |
| 按钮文案、坐标、可见性 | 客户端脚本 |
| 按钮禁用 `Button:Enable(false)`、`UIAPI:SetChecked` | 客户端脚本 |
| 输入框（`InputText1`/`Input11-13`） | 客户端脚本 |
| 物品槽 `FirstWin_ItemBtn1` | 客户端脚本 |
| 关窗（`EndMessage(true)`） | 客户端脚本 |
| 带数字拼接的文案（幸运点数、剩余次数、开放时间） | 客户端脚本按 `SubID` 反解 |

最后一条举例：第 3 页 `math.mod(SubID,10000)==2` 会画
`"Your number is " + (SubID-2)/10000 + " points."`，
所以服务端要把点数 `N` 编成 `N*10000 + 2`。

---

## 6. 当前状态

| 项 | 状态 |
| --- | --- |
| 主菜单、献礼/兑换/运气三条链的应答 | 已实现并编译通过 |
| 第 4、5 页 | **不可达**，需先实测换页行为 |
| 事件数值（材料消耗、次数、点数、奖励） | **未实现** |
| 实测 | **未做**，需客户端点击后用 `[npc] zeus gift` 日志核对 |
