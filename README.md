# LargerInventoryRevised

Terraria / tModLoader 超大物品储存模组（重构版）。

当前仓库保存 **v0.5 方案的实现代码**，设计文档单独放在
`D:\Codes\LargerInventory方案设计`，不随本仓库提交。

## 环境

- tModLoader 1.4.5.8（Terraria 1.4.5 代）
- .NET SDK 10.x
- 仓库应放在 tModLoader 的 `ModSources/LargerInventory` 目录下，因为
  `LargerInventory.csproj` 会导入上级目录的 `tModLoader.targets`。

## 构建

```powershell
dotnet build
dotnet build -c Release
```

## 当前实现状态

- M10：玩家/世界作用域 `.LI` 目录、ModPlayer/ModSystem 存档生命周期、
  `ErasePlayer` / `EraseWorld` 删除关联。
- M11：每页 `.lipg` 文件（Header + QuickView + Items + CRC + 原子替换）、
  `store.meta` 快速视图、卸载模组物品保留。
- M12：异步拾取/消耗；Debug 单机测试钩子；主线程后处理队列。
- M13：强指定指令的无冲突组件并行；弱指定/Put/Escape 作为屏障。
- M14：客户端请求 → 服务器 Store 执行 → 结果包；不同步实例/页。

## Release / Debug 差异

- **Debug**：包含 `/li` 调试命令；单机下启用自动拾取/消耗测试钩子。
- **Release**：不注册任何玩家命令；不自动接管拾取/消耗。
  正式扩展背包/UI 由上层事务层通过指令 API 或网络 API 调用。

调试命令（仅 Debug）：

```text
/li fill <type> <count>
/li take <type> <count>
/li q <type>
/li slot <slotId>
/li move <srcSlot> <dstSlot>
/li stats
/li netq <type>           # 仅多人客户端
/li netfill <type> <count> # 仅多人客户端
```

## 数据目录

- 玩家：`{玩家存档目录}/{角色名}.LI/{store.meta, pages/*.lipg}`
- 世界：`{世界存档目录}/{世界名}.LI/{store.meta, pages/*.lipg}`

## 说明

- 当前实现仍在验证阶段；页内 Item 反序列化、`.LI` 删除关联、联机权限/限流
  等需要游戏内测试。
- 仓库暂未添加开源许可证；如需开源发布，请先确定 License。
