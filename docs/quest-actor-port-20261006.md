# 补上 209 / 555 / 1209 / 1555：两个任务 NPC 的移植（2026-10-06）

上一轮分析（[quest-roster-and-prerequisites-20261006.md](quest-roster-and-prerequisites-20261006.md)）
结论是：客户端 `Quest.xml` 里 **NPC 发放**的任务我们基本齐了，只差 4 条，全部卡在同两个未发布的 NPC 上。
本轮把这 4 条补上。**结论是这两个 NPC 根本没缺数据，缺的只是一次发布和一处命名桥接。**

## 1. 缺口的真实形状

`Quest.xml` 1176 条 vs `StarterQuestChain` 920 行，差 256 条，拆开是：

| 类别 | 条数 |
| --- | --- |
| `GiverName=QuestScroll`（物品触发） | 246 |
| **NPC 发放、因 NPC 未发布被丢弃** | **10** |
| 无发放者 | 2 |

那 10 条由两个 NPC 未发布造成：

| 任务 | 标题 | 阵营 | 发放 / 接手 |
| --- | --- | --- | --- |
| 209 | `[Lv40]Message Delivery` | 斯巴达 | Sparta_039 / **Peloponnese_All_006** |
| 555 | `[Lv31]Spiders Attack!` | 斯巴达 | **Peloponnese_All_006** ×2 |
| 63 / 64 / 65 | `[Lv200]…` | 斯巴达 | Peloponnese_All_005 / **Peloponnese_All_006** |
| 1209 | `[Lv40]Message Delivery` | 雅典 | Athens_038 / **Marathon_All_006** |
| 1555 | `[Lv31]Lion Attack!` | 雅典 | **Marathon_All_006** ×2 |
| 1063 / 1064 / 1065 | `[Lv200]…` | 雅典 | Marathon_All_005 / **Marathon_All_006** |

63/64/65 与 1063/1064/1065 是**连带**回来的：它们的发放者早已发布，接手者正是这两个 NPC。

## 2. 两个 NPC 的数据其实都在

| 项 | Marathon_All_006 | Peloponnese_All_006 |
| --- | --- | --- |
| 名字 | Crophi | Meges |
| 名字来源 | `Text/NpcName.dat`，`NPC.INI` 段内 `name=Crophi` | 同左，`name=Meges` |
| 外观模板 | `Marathon_006_AthenianWarrior1` | `Peloponnese_006_SpartanWarrior1` |
| 模板来源 | **参考服 10020 生成帧**（object 5480） | 客户端 `NPC.INI` 段，名字与 NpcName.dat 一致 |
| 交互 ID | **5480**（抓包） | 服务端自分配 5485 |
| 地图 / 坐标 | map 11 / (-1, -38)，朝向 2.0（抓包） | map 13 / (-41, -4)，朝向 1（Quest.xml + 同图惯例） |
| 外观字 | 0x211（抓包） | 0x11（同图 9 个 NPC 全是这个值） |
| 文本/外观模板行 | `database/postgres/007_npcs.sql` 已有 | 已有 |

也就是说 `007_npcs.sql` 里文本行和外观模板行**本来就存在**，抓包导出
`captured-npcs-map11.txt` 里也**本来就有 5480 这一行**。真正断掉的是两处：

1. **命名桥接**：`tools/gen_captured_npc_placements.py` 用「模板名去掉最后一个 `_模型` 段」当 npc key，
   于是 `Marathon_006_AthenianWarrior1` 被记成 `Marathon_006`，而任务表、`NpcName.dat`、
   `npc_text_templates` 都叫它 `Marathon_All_006`。放置表里那个 key 谁都不引用，任务侧自然查不到人。
2. **发布**：`npc_spawn_definitions` 里没有这两行。放置策略只改写**已存在的行**，
   不会把抓包里多出来的 NPC 补进去，所以哪怕抓到了 5480 也没人放置它。

## 3. 本轮改动

| 文件 | 改动 |
| --- | --- |
| `tools/gen_captured_npc_placements.py` | 新增 `NPC_KEY_OVERRIDE`，把 `Marathon_006_AthenianWarrior1` 归到 `Marathon_All_006` |
| `src/Godswar.Server/Infrastructure/WorldContent/CapturedNpcPlacements.Generated.cs` | 重生成：`["Marathon_All_006"] = …5480u, 0x211u, -1, -38, 2.0` |
| `src/Godswar.Server/Infrastructure/WorldContent/NpcContentBaselineV11.cs` | **新增**：发布 `Peloponnese_All_006`（map 13）与 `Marathon_All_006`（map 11） |
| `src/Godswar.Server/Infrastructure/WorldContent/PostgresNpcContentBaselinePublisher.cs` | 当前版本 V10 → V11；V10 进入「已审阅前驱」白名单；发布者串 v11 |
| `D:\Godswar Origin\npc-translation\published-npcs.txt` | 390 → 392，加入两个 key |
| `src/Godswar.Server/Domain/World/Content/StarterQuestChain.cs` | 重生成：920 → 930 行（斯巴达 464 / 雅典 466） |
| `src/Godswar.Server/Domain/World/Content/StarterQuestObjectives.cs` | 重生成：402 → 405（新增 64、555、1555 三个击杀目标） |
| `tests/Godswar.Server.ProtocolChecks/QuestActorContentChecks.cs` | **新增**自检：V11 条目数、加法性、golden revision、抓包与发布一致 |
| `tests/Godswar.Server.ProtocolChecks/QuestProtocolChecks.cs` | 击杀目标基线 402 → 405；新增 555/1555 的显式断言 |

新增内容：

```
[555u]  = 40x Huge Spiders (monster 1030) @ map 13 (100,12)
[1555u] = 40x Huge Lions   (monster 1033) @ map 11 (-32,-96)
[64u]   = 1x  Huge Skeletons (monster 1043) @ map 13 (-82,126)
```

209 与 1209 在客户端表里 `CreatureMapID` 为空（纯对话任务），所以没有击杀目标 —— 这点也写进了自检。

V11 的 golden revision：

```
11F3F145343059ACE88D0CFF953AAD7F4FD10988BB253278975EDDE6DB96AF66
```

## 4. 验证

- `dotnet build src/Godswar.Server/Godswar.Server.csproj -c Release` → 0 警告 0 错误。
- `Quest actor NPC content release` / `Quest protocol framing` / `Cursed Land map 29 content and transports` → 全 PASS。
- 全量自检：改动前 472 通过 / 24 失败 / 101 跳过；改动后 **475 通过 / 24 失败 / 101 跳过**，
  失败集合逐条相同（本次只多出 1 条自检，另 2 条是本次基线之前就已在工作树里的）。

## 5. 部署（2026-10-06 17:46 已完成）

`docker compose --profile legacy-raw up --build -d server`，容器 `godswar-server` 已 `Up (healthy)`，
`127.1.1.110:5999`（登录）与 `127.1.1.110:7000`（游戏）均已监听，`outcome=ready`。

### 5.1 第一次部署失败：加 NPC 会连带作废对话基线

V11 第一次发布时 NPC 内容成功了，但服务端随即崩溃重启：

```
[npc-content] published reviewed database baseline revision=11F3F145… entries=416
[npc-dialogue] publication refused: InvalidDataException: The reviewed NPC dialogue
               baseline does not target the currently published NPC spawn revision.
server_lifecycle outcome=startup_failed
```

原因是**对话发布钉在 NPC 生成修订上**：
`NpcDialogueBaselineV26.ExpectedSpawnRevision = NpcContentBaselineV10.ExpectedRevision`，
而对话发布的数量门禁是「文本数 = NPC 生成条目数」。V11 把条目数从 414 抬到 416，
V26 就再也对不上了。**所以每次加 NPC 都必须同时出一个新的对话版本**——这一点 V26 的注释里就写着
（它自己就是为 V10 的 7 个 actor 而生的「只换目标、不加内容」版本）。

修法照 V26 的样子做 `NpcDialogueBaselineV27`：内容全部继承 V26，只把
`ExpectedSpawnRevision` 换成 V11、`ExpectedTextCount` 加 2，并把 V26（原始形式与依赖绑定形式）
加进发布器的「已审阅前驱」白名单。

| 项 | 值 |
| --- | --- |
| V27 原始修订（文本+路线） | `73093A49AC5A9F5E04FA4A1A60D62F6456494E39DEAA52DD8B812AF1941B8534` |
| 发布器串 | `server-baseline-v27-manifest-v1` |

### 5.2 部署后的验证

```
[npc-content]  using/published revision=11F3F145343059ACE88D0CFF953AAD7F4FD10988BB253278975EDDE6DB96AF66 entries=416
[npc-dialogue] published reviewed database baseline revision=5C40A7D7FD9C3C6C3FBD8D99F418D4C5F22C741B7E1A5CBE2186F30504E128B9 texts=416 routes=36
```

数据库 `godswar_local`（`.env` 里 `POSTGRES_DB=godswar_local`）里两行已经落地：

```
 map_id |       npc_key       |          template_key           | object_id | interaction_id | appearance |  x  |  z  | facing
      11 | Marathon_All_006    | Marathon_006_AthenianWarrior1   |      5480 |           5480 |        529 |  -1 | -38 |      2
      13 | Peloponnese_All_006 | Peloponnese_006_SpartanWarrior1 |      5485 |           5485 |         17 | -41 |  -4 |      1
```

map 11 那行与抓包逐字段一致（object 5480、外观 0x211、(-1,-38)、朝向 2.0），
说明放置策略确实把 V11 的行改写成了参考服自己的数字。

### 5.3 部署时动过的外部状态

- **停掉了抓包代理**：`Godswar.CaptureProxy` 当时占着 `0.0.0.0:7000`，与容器的游戏端口冲突。
  连同它的守护循环（每 5 秒重启一次）一起停了。已写入数据库/日志的抓包不受影响；
  需要再抓包时按 `.proxy-supervisor.log` 里那串参数重起即可（参考服 `gworiginsv` 已关闭）。
- 客户端 `config.ini` 本来就已是 `IP=127.1.1.110 / PORT=5999`，无需改动。
- 全量自检：**475 通过 / 24 失败 / 101 跳过**，与部署前逐条相同。

## 6. 尚未验证

1. **没有起服务端实测**。`npc_spawn_definitions` 当前发布版本是 `server-baseline-v7`，
   服务端下次启动时才会升级到 V11 并写入这两行。启动后应能看到：
   `Peloponnese_All_006`（map 13）与 `Marathon_All_006`（map 11，被放置策略改写成 id 5480、外观 0x211）。
2. **`Peloponnese_All_006` 的交互 ID 5485 是服务端自分配的**。参考服从未在抓包里出现过这个 NPC
   （map 13 没有导出），所以拿不到它的原始 ID。放置策略本来就对未抓到的 NPC 自分配 ID
   （「id 是客户端回显的句柄，换一个空闲值无害，重复才会断线」），这里沿用了同一条规则。
3. **它的朝向 1.0 取的是同图 9 个 NPC 的一致值**，不是抓包值（无抓包）。
4. **63/64/65/1063/1064/1065 这 6 条 `Lv200` 任务一并进了链**。它们是真实内容（有完整任务文本），
   之前因为接手 NPC 未发布而被丢弃；发布 NPC 后生成器按既有规则（「Quest.xml 里 NPC 发放的任务」）一并收进来。
   参考服自己的 10077 表里没有它们（表里 `Marathon_All_005` 只有 1554），所以这是一处**已知差异**。
