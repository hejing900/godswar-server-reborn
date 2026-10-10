#本基础项目来自于https://github.com/p5y-Ph3R/Godswar-Reborn.


If you're willing to help me, please contact hejing900@outlook.com

如果你愿意帮助我，请联系hejing900@outlook.com


此项目单人无力负担所需测试时间，不再更新实际代码，只更新当前进度


2026/10/09

补全所有生活技能学习、升级、配方、制造。

修复所有药水效果、补齐所有任务卷轴传送卷轴效果、修复加成药水高阶效果到期后依然无法使用低阶的BUG、修复所有消耗物品没有发送删除图标封包的BUG

2026/10/10

添加所有地图刷怪数据，包含各地图卫兵，不包含迈锡尼以及某些世界BOSS，同时怪物的攻击防御属性尚不明确，暂无获取渠道。

2026/10/11

更改怪物攻击=等级基础攻击 × 技能系数”写入物攻和魔攻，再按技能类型选择哪种防御结算


<img width="1031" height="788" alt="image" src="https://github.com/user-attachments/assets/e629d794-b6f4-4f2b-b44f-e6aa49e0da3c" />



![工会系统界面：人物属性、装备面板与工会列表](docs/images/guild-system.png)


<img width="1059" height="790" alt="image" src="https://github.com/user-attachments/assets/dcf97dbf-014b-45b2-aa80-aff9c35455ff" />


任务系统已补齐接近全量的客户端任务规则，包含击杀、收集、探索、捕捉、对话交付、任务卷轴及部分战场任务。新增任务查询（Search），修复查询文字重叠和任务删除问题，同时可接任务上限为20个。

主线按各自独立的任务链发布，支持每日任务、严格同等级发布及已接任务升级后继续完成。收集任务每次击杀匹配怪物有15%几率获得一个任务计数；奖励优先使用GM工具覆盖。保留已有可完成任务的目标和数量，缺失数量的参考来源已记录，未补刷缺失怪物。客户端正文修正和完整游戏内验收仍待完成。详见 [任务系统规则与来源](docs/quest-objective-source-audit-20261007.md) 和 [独立主线规则](docs/quest-independent-mainlines-20261007.md)。

更新圣衣系统：保留装备自身属性的百分比加成，套装加成按圣衣积分达到门槛后逐点增长并受上限限制；新增数据库升级兼容处理，保持已封存物品配置不变。

技能书许愿以实现.

仓库右键存取以实现.

商城以实现.

刷怪技术以实现（待后续添加刷怪）.

怪物掉落以实现（绝大部分未添加）掉落GM工具在tool文件夹内.

NPC对话链路技术文档以总结（后续可添加所有NPC对话链路根据对话详情添加服务器功能）.

经验加成道具技术文档以总结（目前以添加宠物，人物经验，工会总体没做，专长没做）.

荣誉，徽记，奖章以实现，相关NPC以实现.
利兰丁农场活动捐献积分广播都已经实现（结束奖励，怪物刷新未实现）.
修复部分宠物无法使用.
修复范围技能秒杀怪物时没有伤害显示.
修复炎域技能没有后续伤害，现在持续10秒1秒一次伤害.
修复第五属性无法显示.
修复宠物收回会影响祭坛的BUG.
新增港湾副本（未刷怪未配置掉落），所有副本现在都可以组队进入，并且可以使用邀请功能.
完全恢复工会系统所有功能（入会申请与审批、工会列表与简介、成员与职务管理、退出与解散、公告与工会文本、捐献与资金、工会建筑、祭坛供奉与加成结算）.

All guild system features are fully restored (join applications and approval, guild list and profile, member and duty management, leaving and disbanding, proclamation and guild text, donations and funds, guild buildings, and altar worship with bonus settlement).

The Harbor Attack instance is added (no monster spawning and no drop configuration yet), and all instances can now be entered as a party with the invite function.

The Lelantine Farm event donation point broadcast is implemented (end rewards and monster respawn are not implemented). The issue where some pets could not be used is fixed. The missing damage display when an area skill one-shots monsters is fixed. The Flame Field skill now deals follow-up damage for 10 seconds, once per second. The fifth attribute now displays correctly. The bug where recalling a pet affected the guild altar is fixed.
宠物所有技能，所有功能已经实现，创建工会建筑，供奉，加成以实现.

All pet skills and all pet features are implemented; guild building creation, worship, and bonuses are implemented.
更多技术路线请阅读docs文件夹.

更多内容请参照https://github.com/p5y-Ph3R/Godswar-Reborn.


修复宠物召回.
修复更换装备没有删除旧世界呈现的问题.
新增宙斯的献礼所有功能（每日献礼、每周祈祷石、忠诚信徒与祈祷圣徒两处 NPC 对话、等级与时间窗限制、代替品与概率、两套货架、幸运 8 件套与 19 种粉尘、限量库存与全服祈祷石进度）.

All Zeus Gift features are added (daily gift, weekly prayer stone, the Loyal Devotee and Praying Saint NPC dialogues, level and time-window limits, substitutes and odds, two shelves, the 8-piece lucky set and 19 kinds of dust, limited stock, and server-wide prayer stone progress).

Pet recall is fixed. Replacing equipment no longer leaves the old world presence behind.







# Godswar .NET Server

Minimal .NET 10 server-side emulator for the Godswar Origin client protocol.

## Run

Checked-in settings fail closed with legacy raw authentication disabled.
For the unmodified local client, use the explicit loopback Docker rollback
profile described below. Use the secure profile for TLS plus authenticated
UDP.

Focused Phase 2 codec check:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 2"
```

Focused Slice 9 protected-UDP checks:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 3 UDP"
```

Focused Phase 4 authoritative-movement checks:

```powershell
dotnet run --project tests/Godswar.Server.ProtocolChecks/Godswar.Server.ProtocolChecks.csproj --configuration Release -- "Secure Phase 4"
```

Phase 5A deterministic replay, metrics, decoder fuzz, and bounded local
load/soak gate:

```powershell
dotnet restore GodswarServer.sln
dotnet build GodswarServer.sln --configuration Release --no-restore --nologo
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll
dotnet tests/Godswar.Server.ProtocolChecks/bin/Release/net10.0/Godswar.Server.ProtocolChecks.dll "Secure Phase 5A"
.\tools\TestPhase5ABaseline.ps1 -Bots 64 -SoakSeconds 10 -Seed 20260728
```

The load runner is in-process and bounded; it opens no sockets or configurable
network target. Phase 4, Slice 9, and Phase 5A results and limitations are in
the [authoritative-movement record](docs/network-infrastructure-phase4-authoritative-movement.md)
and [replay/load record](docs/network-infrastructure-phase5a-replay-load-observability.md).
Checked-in secure activation remains default-off after acceptance rollback.

## Local relay/worker proof

B18C1 can run the original raw login/game ports in a bounded relay process
while a separate authoritative worker listens on private ports. The
Docker-free smoke starts both real processes against an explicitly supplied,
isolated PostgreSQL smoke database, exercises encrypted login and game
bootstrap, restarts only the worker, and checks that the relay PID remains
stable:

```powershell
$env:GODSWAR_B18C_POSTGRES_CONNECTION_STRING = `
  'Host=127.0.0.1;Database=godswar_b18c_smoke;Username=godswar;Password=local-only'
.\tools\InvokeB18CTwoProcessSmoke.ps1
```

The checked-in local relay route is
[`appsettings.relay-gateway.json`](appsettings.relay-gateway.json). Its direct
process command is:

```powershell
dotnet .\src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll `
  --relay-gateway .\appsettings.relay-gateway.json
```

This is a local process-boundary proof, not the final semantic gateway. The
worker still owns TLS/authentication, sessions, UDP, gameplay, and all world
instances; remote placement remains a later milestone.

## Local semantic gateway/worker

B18C2 gives the unchanged client a loopback-only semantic edge and an mTLS
private worker hop. It authenticates locally and routes one single-use
admission to the exact realm/map/instance/node. Reconnect requires a full
login.

Build and validate the development-certificate generator:

```powershell
dotnet build .\GodswarServer.sln --configuration Release --nologo
.\tools\TestDevelopmentBackhaulCertificates.ps1
```

Create short-lived development material once; the generator neither
overwrites its output nor installs trust:

```powershell
$env:GODSWAR_BACKHAUL_DEVELOPMENT_CERTIFICATE_PASSWORD = `
  'replace-with-a-local-development-password'
.\tools\NewDevelopmentBackhaulCertificates.ps1
Get-Content .\artifacts\backhaul-development-tls\backhaul-development-manifest.json
```

Copy the examples to ignored local files:

```powershell
Copy-Item .\appsettings.semantic-gateway.example.json `
  .\appsettings.semantic-gateway.local.json
Copy-Item .\appsettings.backhaul-worker.example.json `
  .\appsettings.backhaul-worker.local.json
```

From the manifest, put `workers[0].leafSha256` in the gateway copy and
`gateway.leafSha256` in the worker copy. Start the worker:

```powershell
$env:GODSWAR_BACKHAUL_DEVELOPMENT_CERTIFICATE_PASSWORD = `
  'replace-with-the-same-local-development-password'
dotnet .\src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll `
  .\appsettings.backhaul-worker.local.json
```

In another terminal, start the gateway:

```powershell
$env:GODSWAR_BACKHAUL_DEVELOPMENT_CERTIFICATE_PASSWORD = `
  'replace-with-the-same-local-development-password'
$env:GODSWAR_AUTH_ALLOW_LEGACY_RAW_AUTHENTICATION = 'true'
dotnet .\src\Godswar.Server\bin\Release\net10.0\Godswar.Server.dll `
  --semantic-gateway .\appsettings.json `
  .\appsettings.semantic-gateway.local.json
```

The examples have one static route. Add required routes and co-locate maps
joined by direct portals until transfer exists. This adds no Redis, UDP
gateway, live transfer, production placement, HA, or capacity guarantee. See
the
[B18C2 evidence](docs/data-architecture-b18c2-semantic-gateway-backhaul-20260731.md).
B17 Redis coordination is now opt-in; `Local` remains the default. See the
[B17 evidence](docs/data-architecture-b17-redis-coordination-20260731.md).

## Reconciliation and local recovery

See the B19 reconciliation/isolated-recovery
[evidence](docs/data-architecture-b19-reconciliation-restore-20260731.md) and
[runbook](docs/operations/b19-reconciliation-restore-runbook.md).

## Docker

```powershell
docker compose `
  -f docker-compose.yml `
  --profile legacy-raw `
  up --build -d server
```

The opt-in TLS plus authenticated-UDP Docker profile is documented in
[`docs/network-infrastructure-secure-docker.md`](docs/network-infrastructure-secure-docker.md).
It replaces the raw server container, publishes only loopback secure ports,
and keeps certificate material in read-only Compose secrets.

This explicit local rollback starts:

- `godswar-postgres`: PostgreSQL initialized only by the server's embedded,
  checksum-verified migration path
- `godswar-server`: .NET 10 server using PostgreSQL storage

If Windows already has listeners on `5999` or `7000`, free those ports first or override the host ports in `.env`.

```powershell
Copy-Item .env.example .env
docker compose --profile legacy-raw up --build -d server
```

The default listener matches `C:\Godswar Origin\config.ini`:

```ini
[SERVER]
PORT=5999
IP=127.1.1.110
```

Both checked-in appsettings files set legacy raw authentication to `false`.
The raw Docker server starts only with `--profile legacy-raw`, explicitly
enables the rollback capability, and publishes only loopback host ports. It
preserves the unmodified client's plaintext-compatible login and username-only
game binding, so it is not a production security boundary.

The mutually exclusive secure command is:

```powershell
docker compose `
  --env-file .env.secure.local `
  -f docker-compose.yml `
  -f docker-compose.secure.yml `
  --profile secure `
  up --build -d server
```

Certificate/password setup is in the
[secure Docker runbook](docs/network-infrastructure-secure-docker.md).

All runtime profiles are PostgreSQL-only; JSON is test-only. `Production`
also requires a connection string, secure TLS, and disabled plaintext
credential migration. Invalid runtime/storage values, runtime JSON, raw TCP
or plaintext migration in `Production`, and malformed security settings stop
startup before storage initialization. Select `runtimeProfile` or
`GODSWAR_RUNTIME_PROFILE`; never use `LocalDevelopment` in production. Evidence:
[B20E](docs/data-architecture-b20e-postgresql-only-runtime-20260801.md),
[B20F](docs/data-architecture-b20f-bootstrap-projection-cutover-20260801.md),
[B20G world](docs/data-architecture-b20g-content-publication-capture-separation-20260801.md),
[items](docs/data-architecture-b20g-item-template-content-cutover-20260801.md), and
[pets](docs/data-architecture-b20g-pet-content-cutover-20260801.md).
[B20H](docs/data-architecture-b20h-observation-gate-20260801.md) remains local;
deployed observation and deletion are pending.

The ECS gameplay architecture, completed cutovers, parity gates, and
reversible monster/player runtime selectors (both default to `Ecs`) are
documented in
[`docs/ecs-migration.md`](docs/ecs-migration.md). Forward-only PostgreSQL
migrations and the recoverable table-cleanup policy are documented in
[`docs/database-migrations.md`](docs/database-migrations.md).

## Current Scope

Implemented:

- Login server on TCP `5999`
- Game server redirect to TCP `7000`
- Stream XOR cipher and packet framing
- Account auto-create
- Character list, create, delete, and preview
- Working-server-compatible 63-record post-login bootstrap manifest, including the trailing client version record; the V4 avatar-preload experiment was rejected and rolled back after its final cold smoke failed before character selection
- Camp-aware character starts: Sparta/camp 0 enters map 0 and Athens/camp 1 enters map 1 at the captured `(165, -97)` starting position
- PostgreSQL-backed accounts and characters under Docker
- Enter-game packet stream based on the Go reference
- Ping echo, server time, and map-scoped talk/walk broadcast
- Two-client world synchronization using server-built remote-player spawn, equipment/appearance, weapon and armor aura, position, and derived-status packets
- World presence registration only after both the client-ready signal and the player-detail exchange have completed, so other sessions do not see a partially initialized character
- Server-built static NPC definitions assembled from authoritative actor tables first, then validated captured/normalized fallbacks; Sparta uses all 108 unique actors from `C:\Users\Iamc1\Downloads\Sparta\Sparta\NPC.INI` (SHA-256 `A7DFDF9D3C90D27960F730B4B65A7EA37D7F41FC80F7788E584AD80E59BFF340`)
- Generation-safe ECS gameplay state for monsters, map membership/NPCs, player movement, status/recovery/Ride, outgoing combat, and incoming monster damage; protocol and transactional persistence remain boundary adapters, with restart-level `Legacy` rollback selectors
- Movement-driven NPC visibility matching the working server's `32x32` world sectors: each client receives only its current sector and the eight neighboring sectors, with remove/spawn diffs when crossing a boundary
- Movement-driven captured-monster visibility on the same `32x32`/`3x3` sector model, using the raw appearance packet's coordinates as authoritative, validating captured metadata at map load, and sending removals before newly visible monsters
- Server-owned monster roaming, retaliation, extended chase, authoritative leash despawn/fresh full-health replacement at home, ordinary attacks, learned-skill damage, death, revival, and persisted fighter/talent rewards
- Live server time plus full Zodiac state synchronization and persistent five-minute continuous-login energy accounting across reconnects
- Ordinary equipment forging with the client's 611 `EquipForge` rules, Sapphire quality upgrades through Q20/Boundless, Emerald grade upgrades through G25, optional Crystal probability boosts, atomic inventory/silver persistence, and an allowlisted material-grant command
- Authoritative Gear Mentor Add/Enhance/Delete, decomposition, 99-dust Attribute Stone creation, Crystal downgrade transformation, and Level-4/5 gem-piece combination workflows
- Map-specific NPC interaction IDs, including Holy Stone Artisan dialog/action routing in both Sparta and Athens
- Secure networking: PreviewReadyV6 completed original-client TLS authentication, authenticated encrypted UDP binding, authoritative movement, forced one-way TLS fallback/correction, and a ten-minute Soak. Exact rollback restored stock files and disabled activation; protected receipt `completion-0a73fd79-961b-42c7-82cc-9e4a6f9e3355.json` has SHA-256 `5EB6E369...F4A6F` ([acceptance record](docs/network-infrastructure-controlled-host-acceptance.md)). The secure Docker profile publishes only loopback `6599/TCP`, `7443/TCP`, and `7444/UDP`; viewer parity was `Unavailable`, and production security/capacity gates remain ([Slice 9 overview](docs/network-infrastructure-phase3-slice9c-protected-udp.md), [Phase 4 record](docs/network-infrastructure-phase4-authoritative-movement.md)).
- Raw-authentication retirement: checked-in defaults reject raw startup; the unsafe original-client path now requires the explicit loopback-only `legacy-raw` Docker profile. The playable `7FB43C8D...BA07F9` Origin plus deterministic secure Net `A26096B0...D50AA4` pair passed exact offline gates, but is not installed or live re-accepted. An Origin hash is compatibility metadata rather than authentication or anti-cheat ([B14 evidence](docs/data-architecture-b14-raw-auth-retirement-20260731.md)).
- Pets: all pet skills and all pet features are implemented, including learned-skill activation from pet skill books, innate talents, rebirth, care/decay, owner merge, discard/delete, presence audit, and durable pet persistence
- Guilds: guild building creation, worship, and bonuses are implemented, including the guild registrar, buildings, member duties, donations, proclamation, guild text, and altar worship with settlement and decay ([guild system](docs/工会系统技术文档.md), [pet skills/talents](docs/pet-skills-talents.md))
- Lelantine Farm event: donation point broadcast is implemented. End-of-event rewards and monster respawn are not implemented ([record](docs/lelantine-farm-defense-20260926.md))
- Pets: fixed the issue where some pets could not be used, and fixed the bug where recalling a pet affected the guild altar
- Combat: fixed the missing damage display when an area skill one-shots monsters
- Flame Field: follow-up damage is now applied for 10 seconds, once per second
- Items: fixed the fifth attribute not displaying
- Harbor Attack: the instance entry route is implemented (no monster spawning and no drop configuration yet) ([record](docs/harbor-attack-entry-20260929.md))
- Instances: all instances can now be entered as a party, and the invite function works
- Guilds: all guild system features are fully restored, including join applications and approval, guild list and profile, member and duty management, leaving and disbanding, proclamation and guild text, donations and funds, guild buildings, and altar worship with bonus settlement ([record](docs/工会系统技术文档.md))
- Pets: pet recall is fixed
- Equipment: replacing equipment now removes the previous world presence instead of leaving it behind
- Zeus Gift: all features are implemented, including the daily gift, the weekly prayer stone, the Loyal Devotee and Praying Saint NPC dialogues, level and time-window limits, substitutes and odds, two shelves, the 8-piece lucky set and 19 kinds of dust, limited stock, and server-wide prayer stone progress ([analysis](docs/zeus-gift-capture-analysis.md), [dialogue](docs/zeus-gift-dialogue.md))

The multiplayer, NPC, and captured-monster synchronization above is server-side. It does not require game client code changes; a client already configured to connect to this server can use it as-is. The patches below cover separate extended-grade, rank, aura, talent, and native client-stability work.

Remote-player equipment inspection now follows the captured packed-record plus
source-slot-mask layout, preserves detailed Q20/G25 values, and uses stable
per-character/item identities so both rings and their grade, attributes, holy
suit, and four holy stones can be associated correctly. The packet layout,
capture evidence, and complete future-ceiling checklist are recorded in
[`docs/player-inspection-equipment-protocol.md`](docs/player-inspection-equipment-protocol.md).

Not complete yet:

- Full world simulation
- NPC coverage beyond the current static city baseline: the authoritative actor tables now place 108 Sparta and 111 Athens NPCs, while most NPC dialog scripts and quests remain unimplemented
- Monster coverage beyond the current 270 static captured appearances on Sparta/map 0; Athens and other maps do not yet have captured monster baselines
- Monster simulation currently covers local roaming, chase/retaliation, leash replacement at home, normal and learned-skill damage, death, and timed revival; parties, drops, multi-player threat selection, and broader skill effects remain incomplete
- Kill progression now persists carried fighter levels, fighter EXP, talent EXP, and talent points and refreshes the client through the captured monster-death/EXP/level packets; passive HP/MP recovery uses the captured six-second absolute-vitals update
- Talent upgrades now support each class's full ID range (including warrior node `0`) with server-owned persisted rank/cost validation; complete inventory, skill, and talent gameplay is still unfinished
- Ordinary equipment forging is implemented. Material combination and equipment combination (forge modes 1 and 2) remain unsupported. Level-5 Sapphire, Emerald, and Crystal are local extensions and require the matching patched client data.
- Packet coverage outside the mapped reference opcodes

## Current Reverse Engineering Notes

Detailed client patches, rank and forge extensions, effect experiments, item
persistence notes, and the current avatar-gate status are maintained in
[`docs/client-reverse-engineering-notes.md`](docs/client-reverse-engineering-notes.md).

Avatar-preview loading-gate V1 (`2D819908...E2AE0`) failed by starving native
processing; V2 (`73E65FBF...F2902FD`) failed after its timed unready handoff
recreated the blank model. Readiness-only V3 (`17A72198...D878D1`) is also
rejected: its immutable `20260724T043833399Z-2bd75dd7` run reproduced the
about-15-second server-unavailable path and `0x005F58BC` null-root crash.
Matched V4 Origin (`E0F5BC95...D22F81C`) and Net
(`EF531F8C...817597`) passed automated gates but failed the final cold smoke
before character selection: Origin connected to game TCP `7000`, but the server
received no `LoginGameServer`, so AfterLogin and the V4 preload never ran. The
sealed result is `20260724T095739213Z-db16daa7` / `Fail`; no dump was created.
V4 was rolled back. The client now has predecessor Origin
`753BE49F...9ED79`, stock Net `1CC3F9AA...BCA00C`, and no `NetLegacy.dll`.
The avatar issue is parked and Phase 2 proceeds without Phase 1 acceptance.
Current status and immutable incident records are in
[`docs/client-avatar-preview-loading-gate.md`](docs/client-avatar-preview-loading-gate.md).

Important client-side files touched while allowing grade 25 / Boundless quality / rank testing:

- `C:\Godswar Origin\Origin.exe`
- `C:\Godswar Origin\Origin_sixsocket.exe`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\ItemBaseAttribute.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\ItemBaseAttribute.xml`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\ItemColor.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\ItemColor.xml`
- `C:\Godswar Origin\Localization\en_us\UI\Base\font.lua`
- `C:\Godswar Origin\Localization\zh_cn\UI\Base\font.lua`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\EquipForge.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\EquipForge.xml`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\BijouForge.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\BijouForge.xml`
- `C:\Godswar Origin\Localization\en_us\Settings\Sys\ItemAppendAttribute.xml`
- `C:\Godswar Origin\Localization\zh_cn\Settings\Sys\ItemAppendAttribute.xml`
- `C:\Godswar Origin\Localization\en_us\UI\Texture\Icon4.gwo`
- `C:\Godswar Origin\Localization\zh_cn\UI\Texture\Icon4.gwo`
- `C:\Godswar Origin\Localization\zh_cn\Text\EquipName.dat`
- `C:\Godswar Origin\Localization\en_us\Text\EquipDescription.dat`
- `C:\Godswar Origin\Localization\zh_cn\Text\EquipDescription.dat`

Important server/database files for the same work:

- `database/postgres/003_item_quality.sql`
- `database/postgres/004_equipment_scores.sql`
- `database/postgres/005_item_attributes.sql`
- `database/postgres/017_patch_item_1435_quality11.sql` through `database/postgres/030_item_grade_levels_25.sql`
- `database/postgres/046_character_item_audit.sql`
- `src/Godswar.Server/State/PostgresGameStore.*.cs`
- `src/Godswar.Server/State/ItemTemplateSeed.Generated.cs`
- `src/Godswar.Server/State/ItemAttributeTemplateSeed.Generated.cs`
- `src/Godswar.Server/Packets/PacketBuilder.*.cs`
- `tools/GenerateItemTemplates.ps1`
- `tools/GenerateItemAttributeTemplates.ps1`
- `tools/GenerateEquipmentForgeCatalog.ps1`
- `tools/PatchClientForgeBoundlessGrade25.ps1`
- `tools/PatchClientGlobalEquipmentRanks.ps1`
- `tools/PatchLevel135GearCaps.ps1`
- `tools/PatchEquipDescriptionRankLabels.ps1`
- `tools/PatchRemoteWorldEquipmentExtension.ps1`
- `tools/PatchClientAvatarPreviewGuard.ps1`
- `tools/RecolorArmorRank10Effect.py`
- `tools/RecolorArmorRank10Crimson.py`
- `tools/RecolorArmorRank10Gold.py`

Rank reference points:

- The inventory UI labels are in `C:\Godswar Origin\Localization\en_us\UI\XML\ItemBagsUI.xml` and `zh_cn\UI\XML\ItemBagsUI.xml`.
- Armor rank text/value widgets are `EquipEff` and `EquipEffV`.
- Weapon rank text/value widgets are `WepenEff` and `WepenEffV`.
- Weapon rank/aura is driven per template by `ItemBaseAttribute.xml` fields `BaseFraction`, `AppFraction`, `ArmEffFraction`, and `ArmEff`; it is not selected from a separate profession cap.
- Local weapon rank `9` is mapped as score `4000 -> effect 8`; rank `10` is mapped as score `8000 -> effect 9`; client backups include `ItemBaseAttribute.xml.pre-weapon-rank10-effect8.bak` and `ItemBaseAttribute.xml.pre-weapon-rank9-4000-rank10-effect9.bak`.
- Every ordinary forgeable weapon can reach WR10 at Q20/G25 with five append attributes. Four attributes intentionally stop at score `6780`/WR9. GM Spear `1499` and GM Armor `2190` retain their authored score and rank arrays exactly.
- Server-side rank mirrors are `equipment_rank_rules`, `character_equipment_scores`, and `character_rank_summary`.
- Current server weapon rules include ranks `8`, `9`, and `10`.
