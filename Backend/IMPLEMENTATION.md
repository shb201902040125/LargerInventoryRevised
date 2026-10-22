# LargerInventory 实现说明（v0.5 落地）

本目录是 **v0.5.0** 深化方案的代码实现。
设计文档：`D:\Codes\LargerInventory方案设计\泰拉瑞亚超大物品储存模组-方案深化-v0.5.0-20261022-0957.docx`。

## 本轮实现（M10–M14）

### M10 作用域与生命周期（已实现）

- 玩家 Store：`InventoryPlayer`，跟随玩家存档路径的 `{stem}.LI/`；`LoadData` 重建 Store 并加载快速视图，`SaveData` 执行 `FlushForSave`。
- 世界 Store：`InventorySystem`，跟随世界存档路径的 `{stem}.LI/`；`LoadWorldData/SaveWorldData` 绑定/刷新。
- 删除关联：`On_Main.ErasePlayer` / `On_Main.EraseWorld` 之后删除对应 `.LI` 文件夹；失败只记日志。
- 联机客户端不创建 Store（服务器权威；客户端请求结果）。
- 路径解析：`Main.GetPlayerPathFromName`/`Main.playerPathName`、`Main.ActiveWorldFileData.Path`/`Main.worldPathName`，同目录 + 文件 stem + `.LI`。

### M11 持久化（已实现）

- `FilePageFileStore`：每页一个 `.lipg`，Header（magic/version/pageIndex/CRC/长度）+ QuickView + Items；空槽不落盘；临时文件 + 原子替换 + `.bak`。
- `store.meta`：页目录/每页快速视图/版本索引；加载时只读 meta（Item 页按需加载）。
- 快速视图随页保存；加载时以 Items 重建摘要并比对快速视图。
- 卸载模组物品：`UnloadedItem` 按 `FullName` 在 `PageSummary.UnloadedTypes`/`SlotUnloadedKeys` 聚合；ItemIO 保存/恢复，不丢弃、不清理。
- 页保存失败保持 Dirty 且不卸载；页文件缺失且 meta 显示非空时报 `PageLoadException`，不静默清空。
- `EnablePageEviction` 在绑定磁盘 Store 时默认开启；`FlushForSave` 在存档钩子内暂停执行线程、保存脏页与 meta。

### M13 并行（已实现，强指定优先）

- 批次分段：强指定且只操作已有槽位的 `QuerySlot/Take/Move` 可并行；弱指定/Put/Escape 作为屏障串行段。
- 段内按槽位冲突分组；组件数 ≥ `ParallelMinComponents` 且估算耗时 ≥ `ParallelMinCostUs` 时用 `Parallel.ForEach`（`MaxParallelism` 限制）并行执行组件。
- 页在批次前统一加载/pin；并行后 join、同步摘要/类型聚合、解 pin、按队列顺序回调。
- 共享页集合访问用 `_pageSetGate` 串行化；Item 槽位互斥（B1.1–B1.5）。
- 注意：当前并行仅覆盖强指定、无分配指令；弱指定/Put 仍串行（安全优先）。

### M14 联机（请求/结果层已实现，钩子尚未接远程）

- `NetworkSystem`：客户端 `RequestPlayerCommand`/`RequestWorldCommand` → 服务器在对应 Store 执行 → 回 `CommandResult`；只同步结果，不同步实例/页。
- `CommandSerializer`：原生指令请求 TagCompound 序列化；`CommandResultSerializer`：原生结果序列化。ESCAPE 不序列化，第三方自行同步。
- 客户端无 Store；结果回调在主线程。
- 尚未完成：权限/限流/PostAction 细化、客户端拾取/消耗钩子转远程请求（按 v0.5 发布策略，自动钩子仅 Debug 测试）、重连/序号去重。

## 已验证

- `dotnet build`（Debug）：0 警告 0 错误。
- `dotnet build -c Release`：0 警告 0 错误。
- 外部反射冒烟（不启动游戏）：
  - PUT/QueryType；
  - `TAKE int.MaxValue` → `PARTIAL`；
  - `CommandResultSerializer` 往返；
  - Store 持久化：PUT → Flush → 新建 Store → 从 `store.meta` 快速视图恢复 count；`store.meta`/`pages` 生成。
- 受限于游戏外无法初始化 `Terraria.Main`，**页内 Item 反序列化/查询需要在游戏内验证**（ItemIO/Main 初始化依赖）。

## 文件地图

```text
Backend/
  InventoryStore.cs              储存实例；Push/PushBatch；FlushForSave；meta 快速视图加载
  StoreConfig.cs                 运行参数
  Log.cs                         tML Mod.Logger 封装
  Storage/
    ItemPage.cs / PageSummary.cs / PageDirectory.cs / PageCache.cs / TypeIndex.cs
    PageFileStore.cs             页文件格式 + FilePageFileStore + Null + CRC
    StoreMeta.cs                 store.meta（页目录/快速视图）
    StoreScope.cs                StoreScope + StoreLocator（玩家/世界 .LI 路径）
  Commands/
    Command.cs / CommandQueue.cs / CommandExecutor.cs
    CommandSerializer.cs         原生指令请求序列化
    CommandResultSerializer.cs   原生结果序列化
  Integration/
    InventoryPlayer.cs           玩家 Store（跟随玩家存档）
    InventorySystem.cs           世界 Store、主线程队列、Erase On_ 钩子
    InventoryGlobalItem.cs       异步拾取/消耗（仅 DEBUG 测试钩子）
    InventoryDebugCommand.cs     /li 调试命令（仅 DEBUG）
  Network/
    NetworkSystem.cs             客户端请求/服务器结果（玩家/世界 Store）
  LegacyReference.md / Legacy/   旧实现参考与存档（不参与编译）
```

## 调试（仅 Debug 构建）

```text
/li fill <type> <count>
/li take <type> <count>
/li q <type>
/li slot <slotId>
/li move <srcSlot> <dstSlot>
/li stats
/li netq <type>          （仅 MP 客户端；服务器执行并回结果）
/li netfill <type> <count> （仅 MP 客户端）
```

## 责任边界（发布说明/兼容性测试必须复述）

> 本模组会在非主线程调用 Item / ItemLoader / GlobalItem 钩子（包括 `CanStack`、`OnStack` 等）。
> 第三方 mod 必须自行保证这些钩子的线程安全。该声明是责任边界，不是行为兜底。

## 下一步（验证与收尾）

1. 游戏内验证：玩家/世界 `.LI` 文件夹生成、重启后数据恢复、删除玩家/世界关联删除、页卸载/重载。
2. 游戏内验证异步拾取失败补发、消耗补货、并行段与回调顺序。
3. M14 收尾：服务器权限/限流、PostAction、客户端远程拾取/消耗（若上层需要）、序号去重。
4. 页文件损坏/`.bak` 恢复/版本升级策略的实测。
5. UI/配方任务由上层事务层接入指令 API（后续）。

