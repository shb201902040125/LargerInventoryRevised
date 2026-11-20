# LargerInventory（超大物品储存模组，WIP）

Terraria / tModLoader 物品仓库模组。当前为 v0.7，仍处于验证阶段。修订背景见 [项目审计](<PROJECT_AUDIT_AND_REMEDIATION.md>)，游戏内验证步骤见 [人工测试计划](<MANUAL_TEST_PLAN.md>)。

## 构建与测试

使用 tModLoader 1.4.4.9（已针对 2026.9.3.1 编译）、.NET SDK 8.x 或更高。将项目放在 `tModLoader/ModSources/LargerInventory` 下，以便导入上一级的构建配置。

```powershell
# 仅编译 Debug 并运行回归，不写入游戏的 Mods 目录。
dotnet build LargerInventory.csproj -p:BuildMod=false -p:TargetFramework=net8.0 -p:LangVersion=12.0
dotnet run --project tests/LargerInventory.Tests
```

不传 `BuildMod=false` 的普通构建会把模组包写入游戏的 `Mods` 目录。Debug 和 Release 使用相同的功能开关。

## 功能与配置

- `Auto pickup` 和 `Auto refill` 分别默认关闭，当前在单机使用。自动拾取先走原版背包拾取，仓库只接收余量。消耗补货按目标槽的实际缺口补充，跳过仓中收藏的物品。
- 单机取出的物品先进入背包，再尝试掉落在世界。暂时无法交付的物品保存在角色待交付列表中。
- `/li` 由默认关闭的 `Enable test commands` 控制，多人时以服务器配置为准。单机可测试 `fill`、`take`、`q`、`slot`、`move`、`stats`、`repair`、`pause`。多人服务器禁止修复及暂停。
- 多人使用同版协议。已入场的角色用持久 UUID 绑定世界中的个人仓。同世界、同 UUID 的连接共享一个实例。世界仓对已入场玩家开放读写。
- 多人测试命令包括 `/li netq <type> [world]`、`/li netfill <type> <count>`、`/li netmove <srcSlot> <dstSlot>`、`/li nettake <type> <count>` 和 `/li netresult <operation-id>`。普通 Put 由服务端事务来源提供物品。仓库无法容纳整笔存入时，服务器拒绝请求，全部物品归还原来源。共享世界仓通过显式请求存入。`netfill` 是独立的调试入口。联机取出由服务器安排背包交付，背包放不下的部分由服务器在玩家附近掉落。

## 指令接入

单机和服务端的 C# 调用方式见 [指令示例](<docs/InventoryCommandExamples.cs>)，包含放入、取出、查询、批量入队与边界。优化优先级和验收方法见 [性能优化方案](<docs/Performance-Optimization.md>)。

## 存档与修复

- 本地玩家仓和世界仓通常位于对应存档旁的 `.LI` 目录。多人个人仓位于该世界仓的 `Players/<角色 UUID>` 下。在同一目录内改名时，仓库按身份标记跟随。游戏确认完成存档删除后，模组尝试将关联 `.LI` 送往系统回收站。回收失败时保留目录。
- 仓库页和元数据使用 v2 格式。旧测试存档中的仓库文件不再迁移。一次仓库提交使用同一代际的页、摘要及事务日志。遇到中断时，下一次访问先完成待发布的代际。
- `/li repair` 修复单机角色仓，`/li repair world` 修复单机世界仓。有效备份可恢复主文件。确认损坏且无有效备份时，将原始字节隔离到按请求时间命名的 `repair-archive` 子目录，再以同页号的空白页替换。版本不支持、模组无法加载或 I/O 异常的页保留待处理。

## 验证边界

控制台回归使用隔离目录，覆盖提交重放、备份与修复、命令和索引等核心行为。游戏内还需验证实际物品序列化、拾取与补货钩子、联机交付和系统回收。角色背包与模组回执由 tModLoader 分文件写入，崩溃跨越两次写入时存在交付恢复窗口。联机世界掉落与事务账本的写入也分别进行，进程异常退出可能打断结算。详细约定见 [后端实现说明](<Backend/IMPLEMENTATION.md>)。
