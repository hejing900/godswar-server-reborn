# 携带宠物饱食度耗尽导致登录掉线

## 已核实的原因

2026-10-07，角色 `test`（character_id=2）的登录在 `EnterUiReady`（10357）中断，服务端报告：

```text
InvalidDataException: The carried pet could not be called out during login.
```

数据库中的携带宠物为 id=8，饱食度为 0，剩余寿命 1360，携带状态为 true，召唤状态为 false，未合体。最近两次登录的 `pet_presence_transition` 持久化回执均返回 `Status=118`（`PetCareExhausted`），携带状态 true、召唤状态 false。

`PetCareDecayPolicy.CanBeSummoned` 要求饱食度和剩余寿命均大于 0。拒绝召唤符合现有规则；错误在于登录恢复代码要求自动召唤必须成功，把正常的宠物状态拒绝转换为致命异常，导致角色退出世界。

本次任务清理只删除了该角色的 18 条 state=0 的任务记录，没有修改宠物数据。

## 修复逻辑

1. 登录自动召唤仍通过原有持久化命令执行。
2. 命令返回 `PetCareExhausted`，且刷新后的宠物仍携带、未召唤、未合体，饱食度或剩余寿命耗尽时，保留未出战状态。
3. 原有命令投影发送召唤失败结果；登录恢复继续发送携带成功结果，不生成宠物世界模型，然后继续角色登录。
4. 正常召唤成功、已出战宠物、合体宠物、切图恢复的行为沿用原逻辑。
5. 数据提供器失败或回执与快照不一致仍按原逻辑处理，避免把无法确认的状态当作正常恢复。

没有通过补充饱食度、修改寿命或清除宠物来绕过问题。

## 验证

- Release 构建：0 个警告、0 个错误。
- 在实际 `RestorePetPresenceAsync` 协议检查中增加饱食度为 0、寿命为 0、健康宠物却收到耗尽回执三个场景。
- 核实耗尽场景正常返回，按顺序发送召唤失败和携带成功，不发送宠物世界模型。
- 包含原有宠物召唤、重试幂等、切图召回、合体，以及任务相关检查：16 通过、0 失败、1 跳过。跳过项为未配置测试连接串的 PostgreSQL 任务奖励集成检查。
- 结果文件：`artifacts/pet-login-care-fix-checks.json`。

部署信息记录在 `artifacts/pet-login-care-fix-current.json`；客户端实际登录效果待重新登录核实。
