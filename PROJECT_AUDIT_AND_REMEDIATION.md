# LargerInventory 项目核查与修订任务书

> 用途：交由后续工作 AI 按本文对项目进行修订。本文是代码审查和实施计划，不代表下列问题已经修复。
> 审查范围：当前仓库中的 C# 源码、项目配置、README、存储/指令/网络/生命周期实现。
> 重要约束：本模组管理玩家物品数据；修改存储或执行流程时必须优先保证“不丢物、不复制物品、不静默清空存档”，性能优化不能凌驾于数据正确性之上。

## 1. 项目概况与核查结论

项目是 Terraria/tModLoader 1.4.5 系列的超大物品存储模组，当前标记为 v0.5/WIP。主要组成：

- `Backend/Storage`：页目录、页缓存、摘要、全局类型索引、`.lipg` 页文件及 `store.meta`。
- `Backend/Commands`：命令模型、批处理、强/弱访问、冲突分组和执行线程。
- `Backend/Integration`：玩家/世界生命周期、自动拾取/消耗测试钩子、Debug 命令。
- `Backend/Network`：客户端命令请求、服务端执行和结果响应序列化。
- `Backend/Legacy`：不参与编译的旧实现参考代码。

### 已确认的优点

- 存储、命令和 tModLoader 接入有基本分层，旧代码通过项目文件排除编译。
- 页文件有 magic/version/CRC，并先写临时文件再替换。
- 有页摘要和类型索引，支持页卸载后查询。
- 对页加载失败有错误路径，原则上不会把“索引显示非空但页文件丢失”当作空页继续运行。
- Debug 与 Release 的自动接管策略不同，且仓库未跟踪 `bin/obj` 等构建产物。

### 构建验证

本次执行：

- `dotnet build --no-restore`：成功，0 警告、0 错误。
- `dotnet build -c Release --no-restore`：成功，0 警告、0 错误。

这只证明当前配置下能编译和打包，不证明存档、游戏线程安全、多人协议和实战生命周期正确。仓库没有独立测试项目；项目文档也说明若干验证仍依赖游戏内测试。

## 2. 修订优先级

| 优先级 | 含义 |
|---|---|
| **P0 — 修复后才能验收** | 可能使扩容失效、联机协议不可用、物品数据受线程竞态影响或存档保存不一致。 |
| **P1 — 发布前应完成** | 数据恢复、网络防护、生命周期和失败处理不够可靠。 |
| **P2 — 质量改进** | 可维护性、性能、文档和测试覆盖改善。 |

建议按第 8 节的顺序实施，避免并行改动存储格式、线程模型和协议格式。

## 3. P0：重启后页分配器没有恢复分配位置

**证据**

- `Backend/Storage/PageDirectory.cs:16`：`_nextPageIndex` 默认从 `0` 开始。
- `Backend/Storage/PageDirectory.cs:20-28`：`Allocate()` 直接对 `_nextPageIndex` 调用 `GetOrCreate()` 并递增，没有检查该页是否已存在。
- `Backend/InventoryStore.cs:179-184`：启动时从 `store.meta` 对每个旧页调用 `GetOrCreate(pair.Key)`，但没有更新目录分配游标。

**影响**

重启后若已有页都没有空槽，第一次扩容仍可能把已存在的页作为“新页”返回。Put 会再次检查槽位，因此通常不会直接覆盖已有物品；但会做无效扫描、可能返回 `NoSpace`，并使“需要新增页”的命令不能按预期立即成功。旧页索引不连续时，问题也会持续到分配游标走到空闲索引。

**修订建议**

1. 让 `PageDirectory` 在加载已存在页时同步分配状态。推荐维护明确的已占用位图/空闲索引结构，或从已加载索引安全重建下一个可分配索引。
2. `Allocate()` 必须保证返回的是此前未分配的页；如已达到上限，返回明确容量错误，不得静默返回旧页。
3. 不要简单地只设置为“最大页索引 + 1”而不考虑稀疏目录，否则仍可能浪费此前存在的空闲索引；选择策略后需写测试固定语义。
4. 将分配和 `GetOrCreate` 的状态更新保持在同一同步边界内，避免未来多个生产者导致重复分配。

**验收测试**

- 构造已有页索引 `{0,1}`，重启后分配新页，必须得到未占用索引。
- 构造稀疏已有页索引 `{0,2}`，新页不可返回 0 或 2；策略允许返回 1 或大于 2 的空闲索引。
- 目录中所有可用索引占满时，分配稳定地返回明确容量错误。
- 通过实际存档流程：填满页、保存、重启、再次 Put，物品成功落入新页；旧页物品完全不变。

## 4. P0：多人网络入口缺失，结果包字段不匹配

**证据**

- `Backend/Network/NetworkSystem.cs:66-78` 有静态 `HandlePacket()`。
- 根目录 `LargerInventory.cs:12-19` 的 `Mod` 子类只设置 `Instance`，没有重写 `HandlePacket(BinaryReader, int)` 并转发给 `NetworkSystem.HandlePacket()`。
- `NetworkSystem.cs:127-131` 的 `HandleResult()` 在 sequence 前读取了一个 `scope` 字节。
- `NetworkSystem.cs:152-158` 的 `SendResult()` 写入 `OpResult` 后直接写 sequence，没有写 `scope`。

**影响**

当前仓库未见 ModPacket 的实际分发入口，因此网络 API 在当前实现中没有完整接入点。即使后续接通入口，客户端结果解析也会因为读写字段不一致而偏移：客户端会把 sequence 的第一个字节当作 scope，后续 sequence/tag 长度也随之错位，回调不能可靠收到结果。

**修订建议**

1. 在 Mod 入口实现 tModLoader 对应的 `HandlePacket(BinaryReader reader, int whoAmI)` override，并仅把当前 Mod 的包交给 `NetworkSystem.HandlePacket()`；未知 opcode 应安全拒绝或记录。
2. 定义唯一的协议布局，例如：
   - 请求：`opcode | protocolVersion | scope | sequence | payloadLength | payload`
   - 响应：`opcode | protocolVersion | sequence | payloadLength | payload`

   若响应确实需要 scope，则发送端和接收端都必须包含它；不要由两个位置分别手写不一致的字段。
3. 将包读写集中到协议帮助函数或结构化方法中，避免请求/响应逻辑各自维护格式。
4. 检查并处理未知版本、未知 scope、负数/过大长度、短读和 Tag 解码异常；包解析失败不能让异常越过网络处理边界。
5. 删除 `HandleRequest()` 中为“验证”而先 `CommandSerializer.Load(tag)`、随后又再次 Load 的重复反序列化；应一次性解析、验证并附加回调。

**验收测试**

- 对每种支持命令执行序列化 → 服务端解析 → 结果序列化 → 客户端解析的往返测试。
- 覆盖空/短包、未知 opcode/version/scope、缺字段、长度越界和 Tag 解码失败。
- 游戏内单机以外至少做一次客户端—专用服务器往返；确认 callback 只触发一次且 sequence 匹配。
- 确认 `HandlePacket` 确实由 Mod 入口调用，而非只存在一个未引用的静态方法。

## 5. P0：在后台线程调用 Terraria/tModLoader 物品 API

**证据**

- `Backend/Commands/CommandExecutor.cs` 为每个 Store 启动专用后台线程；并行路径在 `:218-225` 使用 `Parallel.ForEach`。
- 执行命令会操作 `Item`，并调用 `ItemLoader.CanStack`、`ItemLoader.OnStack`、`Item.Clone()` 等；这些可能触发其他模组实现。
- `Backend/Storage/PageFileStore.cs:292,322` 在页保存/加载时调用 `ItemIO.Save()`/`ItemIO.Load()`。
- 页面加载在命令预加载过程中触发，因此 Item 反序列化也可能发生在执行线程上。
- `Backend/IMPLEMENTATION.md` 已明确记录目前无法保证这些 Hook 的线程安全。

**影响**

即使不启用并行，单一后台线程也可能在错误线程上访问游戏对象或调用模组 Hook。并行执行则进一步允许多个 Hook 同时运行。可能结果包括随机崩溃、模组状态竞态、物品摘要和真实 Item 不一致，以及难以复现的存档损坏。

**修订建议**

1. 明确并强制线程模型：所有 Terraria/tModLoader API、`Item`/`ItemLoader`/`ItemIO` 和模组 Hook 操作只在主线程执行。
2. 后台线程只做不依赖游戏对象的工作，例如对已经在主线程生成的不可变字节快照做文件 I/O、CRC 和纯数据校验。
3. 如短期内不能完成线程隔离，先关闭 `Parallel.ForEach`，并将整个 Item 读写及 Hook 操作迁回主线程；不要以“多数 Hook 看起来可用”作为安全保证。
4. 将 callback 执行线程纳入公开契约。若 callback 可能在后台执行，API 文档和类型命名必须明确；若面向游戏接入层，优先统一通过主线程动作队列派发。
5. 不要持有锁后调用不受本模组控制的 Mod Hook，避免 Hook 回入 Store 形成死锁。

**验收测试**

- 加断言/调试诊断，能够发现游戏 API 在非主线程调用。
- 加一个会记录线程 ID 的测试 Hook/模拟层，确认加载、保存、CanStack、OnStack 和 callback 符合约定。
- 并行暂时关闭时，仍需验证同槽冲突、跨页操作和命令顺序正确；日后重新启用并行须独立做压力和竞态测试。
- 在多模组环境中做游戏内测试，不能只以纯 C# 单元测试替代 Hook 验证。

## 6. P0：Flush 的暂停等待没有确认成功

**证据**

- `Backend/Commands/CommandExecutor.cs:61-72`：`Pause()` 等待 `_paused.Wait(5000)`，但忽略返回值。
- `Backend/InventoryStore.cs:96-120`：`FlushForSave()` 调用 `Pause()` 后无条件保存 Dirty 页和 `store.meta`。
- `CommandExecutor.cs:50-57`：Dispose 仅 `Join(1000)`，不检查线程是否退出。

**影响**

若执行线程由于存档、Hook、慢 I/O 或死锁未能在 5 秒内停下，Flush 仍会与其并发读取/写入页和摘要。Dispose 的 1 秒 Join 也不能证明后台线程已终止。这与“存档屏障”承诺不符。

**修订建议**

1. `Pause()` 返回成功/失败，并区分“线程已停在边界”“执行线程不存在”“等待超时”等状态。
2. Flush 只允许在确认执行线程暂停后开始；超时则失败并向调用方报告，不得假装已保存成功。
3. 保存期间要保证页内容、摘要和 meta 来自同一一致性时点。
4. Dispose 需要有明确的停止、排空/取消、等待退出和错误报告语义；不要在 Join 超时后把仍运行的线程当作已经销毁。
5. 防止从执行线程回调中同步请求暂停自身；当前代码对此直接 return，需在接口契约中说明这不构成实际暂停。

**验收测试**

- 执行线程忙于长操作时触发 Flush，确认超时会返回失败而不是并发保存。
- 正常暂停、保存、恢复后队列仍可继续处理，保存结果可重载。
- Dispose 返回后确认线程已经退出，且没有迟到的回调或文件写入。
- 使用可控阻塞的假存储/假执行器覆盖等待超时，不依赖不稳定的真实时序。

## 7. P1：网络命令缺少严格校验、权限和待处理请求清理

**证据**

- `NetworkSystem.HandleRequest()` 对 `scope == ScopeWorld` 使用 WorldStore；其他所有值都会进入玩家 Store 分支，未知 scope 未拒绝。
- 代码没有看到服务端权限策略、请求频率限制或按玩家配额。
- 网络请求允许多种命令和 Item 列表；解析后没有集中进行参数/数量上限校验。
- `ReadTag()` 在 `NetworkSystem.cs` 只检查声明长度在 1..16 MiB，但未确认 `ReadBytes(length)` 实际返回同样长度；TagIO 异常也没有统一包解析保护。
- `Pending` 是静态并发字典，成功响应才移除；没有超时、断线或 Mod 卸载清理路径。

**影响**

无效或畸形输入可能导致错误路由、异常传播或过量内存/CPU 工作。多人世界存储的写权限和共享规则尚未定义。服务器不回包、断线或命令卡住时，客户端的 Pending 回调会长期占用内存。

**修订建议**

1. 对 scope 做严格枚举校验；未知值立即拒绝，不能默认映射到任何 Store。
2. 明确多人共享 Store 的授权模型：世界 Store 是否允许所有玩家写、是否区分只读/管理操作；权限必须在服务端执行，不能依赖客户端 UI 隐藏入口。
3. 服务器对命令种类、类型 ID、数量、slot 范围、数组长度、Put Item 数量和总序列化字节数设上限。
4. `ReadTag` 使用精确长度读取；短读、Tag 解码错误、缺字段和版本不匹配应转为受控拒绝结果或记录并丢弃。
5. 加入每玩家速率/并发限制和服务端队列背压。达到上限时拒绝并给出可诊断结果。
6. Pending 记录保存提交时间和取消/超时策略；断线、世界切换和 Mod 卸载时清理，并确保迟到响应不会调用已失效的 callback。
7. 对网络请求只反序列化一次，并在入队前做完整的语义校验。

**验收测试**

- 未知 scope/命令/版本/参数均被拒绝且服务器继续正常运行。
- 未授权玩家无法执行策略禁止的操作；授权策略在服务端测试。
- 超限请求被拒绝，不导致无界队列、内存增长或长时间卡顿。
- 断线、服务器不响应和世界卸载后 Pending 数量可回落到 0。

## 8. P1：持久化备份文件未用于恢复，文件输入校验不完整

**证据**

- `Backend/Storage/PageFileStore.cs:172-179` 在替换页文件时生成 `.bak`，但 `Load()` 只尝试正式页路径（`:69-72`），未从 `.bak` 恢复。
- `Backend/Storage/StoreMeta.cs:save` 同样生成 meta 备份；`Load()` 只读取正式 `store.meta`。
- 页头中的 `quickViewLength`、`itemsLength` 相加后才检查总长度；未强制两段长度分别非负、未强制文件长度与声明长度严格一致。
- `ReadItems()` 对 `ReadBytes(length)` 未检查短读；`slot` 读出后未在索引数组前显式验证 `< ItemPage.SlotCount`。
- `StoreMeta.Load()` 读取的 pageIndex 在 `InventoryStore.LoadQuickViews()` 中进入 `GetOrCreate()`；应在恢复边界验证范围和重复项。

**影响**

`.bak` 目前主要是残留文件，不构成自动恢复保障。正式文件损坏时，即使备份可用，也会按加载失败处理。格式边界不严会让损坏文件进入更深层解析；某些 meta 错误可能使 Store 初始化失败。

**修订建议**

1. 定义明确的恢复优先级：先验证正式文件；失败后验证 `.bak`；两者均失败时保留原文件、报告错误，绝不能静默生成空数据覆盖旧数据。
2. 对页文件和 meta 文件都实现同一套恢复与日志策略；恢复成功后可在下一次安全保存时修复正式文件。
3. 对页格式验证 magic、版本、页索引范围、各长度非负且不溢出、精确总长度、CRC、槽位范围和重复槽位。
4. 对 Item tag 做有界精确读取；设置合理的单 Item、整页和 meta 大小上限。
5. Meta 加载逐项校验页索引、数组长度、摘要计数、重复页项和版本。遇到局部无效项时按明确定义处理，不能让异常绕过 Store 初始化边界。
6. 存储格式已有用户数据，若需改变格式，增加显式版本迁移和旧版本回归样本；不要直接改变字段含义。

**验收测试**

- 正常页、截断页、CRC 错、版本未知、长度溢出/不匹配、非法 slot 和损坏 meta。
- 正式页损坏但 `.bak` 正常时可恢复；两者都坏时不会静默清空。
- 新旧版本样本迁移后 Item 类型、stack、favorited、模组卸载物品 FullName 均保持。
- 恢复失败日志包含页索引/文件路径/失败原因，但不记录 Item 敏感内容。

## 9. P1：异步拾取的失败补偿需要明确无损语义

**证据**

- `Backend/Integration/InventoryGlobalItem.cs` 在单机 Debug 下先移除地面物品，再异步 Put。
- callback 在有 remaining 列表时重新生成；若失败结果没有 remaining payload，则只记录错误（`OnPickupPutCompleted`）。
- Put 可在中途发生内部异常；执行器的通用异常处理会返回 `InternalError`，不保证将已处理/未处理 Item 作为补偿数据返回。
- Release 下自动钩子关闭，这是当前的测试策略，应保留，直到异步交易语义验收完成。

**影响**

“先从世界移除，再异步提交存储”的事务在异常路径上可能失去可恢复的原始 Item。仅记录日志无法补偿物品。

**修订建议**

1. 把拾取视为事务：记录原 Item 快照/唯一交易 ID；只有 Store 确认接收后才完成移除，或保证所有失败分支都能回滚到原状态。
2. 定义 Put 的原子性：要么完整接受、要么返回逐 Item 的精确未接收部分；若允许部分提交，结果必须精确说明已提交和未提交部分。
3. 避免 callback 重试导致重复入库/重复生成；可使用幂等交易 ID。
4. 保存失败、玩家离开、世界卸载和 callback 队列停止时，仍需有明确补偿或安全持久化策略。
5. 不要在事务语义未完成前于 Release 开启自动拾取接管。

**验收测试**

- Put 成功、无空间、部分成功、页加载失败、存储失败、异常、玩家离开和世界切换。
- 任意失败注入后，“地面 + Store + 玩家背包”中的总物品数与交易前后预期一致。
- callback 重复到达时不会复制 Item；失败恢复可重启后继续。

## 10. P1/P2：命令输入边界、线程契约和调度语义

### 参数与槽位验证

`CommandFactory` 可接收重复/越界槽位；`Move(source, destination)` 允许源和目标相同。强指定 Take/Put/QuerySlot 也应对重复槽位有一致语义。网络加载可构造无效 `CommandKind` 或与 Kind 不匹配的 Payload。

建议在命令创建或统一执行入口做验证：

- slot ID 必须在 `0 <= slotId < PageDirectory.MaxPageCount * ItemPage.SlotCount`。
- 对需要唯一访问集合的操作去重或拒绝重复 slot。
- `Move(x, x)` 明确为 Nop 或 `InvalidArg`，不能返回看似成功的移动数量。
- `CommandKind`、Payload、AccessSet 强/弱模式必须匹配。
- 数量、type ID、Put 列表、null Item 和 Item stack 均有明确边界。

### 命令 callback 线程

执行器在执行线程上调用 `command.Callback`。部分集成层主动把游戏操作转投主线程队列，但公共 `InventoryStore` API 没有在类型/文档层显式约定 callback 线程。

建议明确 API 契约：callback 在哪个线程、是否可能晚于 Dispose、是否保证只调用一次；对需要调用 Terraria API 的 callback 提供统一主线程派发方法。

### timeout=0 与批次语义

`CommandQueue.TryTakeBatch()` 将 `TimeoutMs == 0` 当作立即边界；默认命令工厂大量使用 `timeoutMs = 0`。这可能使默认命令逐条成批，降低批处理和并行触发机会。请先确认这是预期的“立即执行”语义，还是与“批处理窗口”混淆。

建议：

- 将“排队等待/合并窗口”和“执行截止时间”拆成不同概念，不要让一个 timeout 字段同时充当批次边界。
- 使用可控时钟测试队列顺序、截止时间、Boundary、最大批次和新命令插入行为。
- 不以打开并行作为解决吞吐量的第一步；先测量串行执行、摘要重建和文件 I/O 的实际耗时。

## 11. P2：性能和资源占用方向

在正确性和线程模型稳定后再实施：

1. `PageFileStore.Save()` 同时构造 quick bytes、item bytes、合并 payload；Load 也有整文件和 payload 拷贝。可考虑分块/流式 CRC 与写入，但必须保持原子替换和格式兼容。
2. 每页序列化 Item 时单独创建 `MemoryStream`，分配较多；可复用受控缓冲区，但需防止复用生命周期和异常时数据串扰。
3. `PageDirectory.Allocate()` 的修复应避免每次扩容从 0 线性扫描大量已分配页；采用清楚的数据结构并做容量压力测试。
4. `BuildConflictComponents()` 对每对命令做二重比较，`SlotsOverlap()` 又对 slot 列表二重比较。只在并行经过安全论证且确有收益后，考虑基于 slot→命令的映射建冲突图。
5. `ExecuteBatch()` 在每个 segment 后同步摘要，finally 又对累计 affected pages 再同步一次，可能造成重复重算、版本递增和重复 Dirty 标记。检查是否可以保证异常路径正确后只同步一次。
6. 将配置参数做构造时验证，例如 `MaxLoadedPages > 0`、批次大小范围、并行度范围和非负等待时间。
7. 为队列长度和每 Store 内存/页数量定义监控与背压，避免仅限制单次 batch 数而允许队列无限增长。

任何优化都需要性能基线和正确性对照；不要仅凭理论减少分配或增加线程。

## 12. P2：项目交付和文档

- `Localization/en-US_Mods.LargerInventory.hjson` 当前为空；补充至少一个有效的英文资源文件，并验证 tModLoader 加载。
- README 含本机绝对设计文档路径，不适合外部协作；改为仓库内相对链接或明确说明该文档不随仓库发布。
- README、Workshop 描述和 `Backend/IMPLEMENTATION.md` 应明确当前功能、非目标、已知限制、测试方式、存档目录和兼容策略；不要宣称未经过联机/数据恢复验收的能力已完成。
- 检查 Markdown/HJSON 使用统一 UTF-8 编码，消除乱码/替换字符；不要仅凭 PowerShell 控制台显示判断编码，需按 UTF-8 读取后核对原始内容。
- 明确项目许可证；没有授权前不擅自添加或推断 License。
- 删除文档对外部本机路径、过期里程碑或尚未实现功能的误导性描述，但不要删除用户的设计资料。
- 确认这是独立模组、供其他模组引用的后端库，还是两者兼有；README、API 可见性和发布包应与定位一致。

## 13. 推荐实施阶段

### 阶段 A：修复阻塞性正确性

1. 修复 `PageDirectory` 已有页恢复与唯一分配。
2. 明确线程模型，先关闭未证明安全的并行路径。
3. 让 Pause/Flush/Dispose 具有可验证的成功/失败语义。
4. 建立基础测试项目，先覆盖页目录、队列、页格式校验和命令边界。

### 阶段 B：完成网络垂直切片

1. 接通 Mod `HandlePacket`。
2. 固定协议字段和版本，修复结果读写不一致。
3. 增加严格解析、权限、限流、Pending 超时和清理。
4. 以多人服务器完成端到端验证后，才将网络功能标记为可用。

### 阶段 C：保护玩家数据

1. 实现 `.bak` 和 meta 恢复策略。
2. 加入格式边界与数据迁移测试。
3. 明确异步拾取的事务/回滚语义。
4. 做保存中断、损坏文件和游戏内重启恢复验证。

### 阶段 D：优化、文档与发布

1. 收集性能与内存基线。
2. 在安全前提下优化序列化、冲突图和摘要同步。
3. 修订 README、HJSON、Workshop 文本和 License 状态。
4. 完成 Debug/Release、单机、专用服务器和多模组兼容矩阵。

## 14. 后续工作 AI 的操作约束

1. 开始修改前重新检查 `git status` 和相关文件；保留其他工作者已有改动，不执行 reset/checkout 等丢弃操作。
2. 一次处理一个阶段，先修复并测试 P0，再开始 P1/P2；不要将网络协议重写、存储格式迁移和线程模型重构混成一个无边界大补丁。
3. 不删除 `.LI` 用户数据、不自动清空无法解析的存档、不覆盖失败恢复所需的备份。
4. 修改页文件或 meta 格式时必须增加版本号/迁移策略，并保留至少一份旧格式测试样本。
5. 优先新增自动测试；只有构建成功不算通过。需要游戏内验证的功能，应将“未验证”作为明确结果。
6. 除非用户另外授权，不要擅自新增外部依赖、改许可证、发布 Workshop 包或重写项目架构。
7. 修订完成后更新本文对应问题状态，并在交付说明中列出：改了什么、测试命令及结果、未解决风险、需要人工游戏内验证的项目。

## 15. 最终验收清单

- [ ] 页目录重启后只分配新页，不误选已有页；满页扩容通过游戏内验证。
- [ ] Mod 网络入口已接通；请求/响应往返字段一致且多人端到端通过。
- [ ] Terraria/tModLoader/Item/ItemLoader/ItemIO 的线程访问有清晰且经过验证的约束。
- [ ] Flush 只会在确实暂停执行线程时报告成功；Dispose 后没有活动后台线程。
- [ ] 存档损坏可尝试备份恢复；恢复失败绝不静默清空或覆盖用户数据。
- [ ] 网络 scope、权限、参数、长度、速率、Pending 生命周期均有服务端保护。
- [ ] 拾取/存储事务失败路径不会丢失或复制物品。
- [ ] 有可重复的自动测试，覆盖存档、命令、协议和错误注入。
- [ ] Debug 与 Release 构建通过；单机、专用服务器、多人及必要模组兼容测试有记录。
- [ ] README、HJSON、Workshop 描述与真实实现相符，编码和许可证状态明确。

---

## 16. 修订状态（v0.6 修订记录）

> 本节由实施修订的 AI 更新。"已完成"表示代码已修改且通过 Debug/Release 构建及自动化测试；"需游戏内验证"表示尚需在 tModLoader 中实测。

| 编号 | 问题 | 状态 | 实际做法 / 剩余事项 |
|---|---|---|---|
| P0-1 | 页分配器不跳过已有页 | **已完成** | `PageDirectory.Allocate()` 跳过目录中所有已存在的页索引（含稀疏空缺），测试 `PageDirectory_*` 覆盖。需游戏内验证"满页扩容"流程。 |
| P0-2 | 网络入口缺失、响应字段不一致 | **已完成** | `LargerInventory.HandlePacket` 转发到 `NetworkSystem.HandlePacket`；请求与响应统一为 `opcode, version, scope, sequence, tag`，结果会校验 scope。**协议有变更，客户端与服务端必须同为 v0.6。** 多人端到端往返需游戏内验证。 |
| P0-3 | 后台线程调用 Item/Hook API | **已完成（方案 B：迁回主线程）** | 删除执行器后台线程与 `Parallel.ForEach`；由 `InventorySystem.PostUpdateEverything` 调用 `Pump()`；回调也在主线程。需游戏内验证多模组环境下的 Hook 行为。 |
| P0-4 | Flush 暂停超时仍继续保存；Dispose 不确认退出 | **已完成** | 存档与执行共用执行锁，超时返回失败且不写入；`Dispose` 先 `Drain` 再保存；销毁后 Push 立即失败。 |
| P1-5 | 网络 scope 校验、权限、限流、Pending 清理 | **部分完成** | 已完成：未知 scope 拒绝、Query/Put/Move 白名单、类型/槽位/数量/堆叠总量校验、单玩家在途上限 32、客户端待处理上限 64、30 秒超时、卸载/换世界时取消、`ReadTag` 精确长度、请求单次反序列化。**远程 Take 暂时关闭**，避免服务端先移除物品后因结果包过大而无法送达。**未完成：多人授权模型（需要产品决策：世界 Store 谁可写）。** |
| P1-6 | `.bak` 未用于恢复、格式校验不完整 | **已完成（仍需游戏内验证）** | 页文件与 `store.meta` 均支持 `.bak` 回退；页段长度精确校验、槽位范围与重复检测、物品段短读检测；损坏页标记 `LoadFailed`，不会被写回；元数据缺失、损坏或从备份恢复时，从正式页和 `.bak` 页文件重建索引，并逐页修剪缓存。元数据重建会读取每页真实 Item 以校准摘要/TypeIndex。增加测试覆盖“只有 `.lipg.bak` 的页仍会被发现”。格式版本仍为 1，无需迁移。 |
| P1-7 | 指令中途异常时物品补偿不完整 | **未解决（需优先设计）** | 未执行即失败（队列满/销毁剩余队列）的 Put 会返回全部物品；自动拾取仍仅 Debug 单机启用。**执行途中**若 Take 已移除一部分后异常，`ExecuteCommandSafe` 可能只返回 `InternalError` 而丢失已取物品结果；Put/Move/Escape 也缺少通用回滚/精确部分结果。远程 Take 暂时禁用可避免网络回包过大造成的远程丢物，但不解决本地事务原子性。 |
| P1/P2-8 | 命令输入边界、线程契约、timeout 语义 | **部分完成** | 已完成：`Move` 同槽/负槽位拒绝、远程指令白名单（远程 Take 关闭）、回调线程契约（主线程）。**未完成：把"排队窗口"与"截止时间"拆分为两个概念**（当前仍沿用 timeout=0 为边界的语义，保持不变）。 |
| P2-9 | 性能与资源 | **未完成（有意推迟）** | 并行路径已删除，摘要同步改为逐条执行（正确性优先，可能比 v0.5 慢）；`Allocate` 已改为游标扫描。序列化缓冲复用、冲突图等需先建立基准。 |
| P2-10 | 文档与发布 | **部分完成** | README 已重写（去除本机绝对路径，补充线程模型、协议、测试、限制）；`Backend/IMPLEMENTATION.md` 已按当前代码重写。**未完成：** HJSON 资源文件仍为空（当前代码无可本地化的文本，需决定是否保留）；Workshop 描述未改；**许可证未确定**，未添加。 |
| 配置校验 | 配置值未约束（审计 P2-6） | **已完成** | `StoreConfig.Normalize()` 钳制取值范围；移除未使用的并行参数。 |
| 测试 | 缺少自动化测试（审计 §14/§15） | **已完成（部分）** | 新增 `tests/LargerInventory.Tests`（11 项，控制台程序，无第三方依赖），覆盖页分配、仅备份页枚举、Pump 线程、非 owner thread 拒绝、队列背压、销毁拒绝与排空、拒绝的 Put、Move/槽位校验。页文件、Item 序列化、网络往返仍需游戏内验证。 |

### 复核评估与补充修正

对上一轮 AI 的修改复核后，发现并修正了以下问题：

1. **元数据重建后 TypeIndex 可能仍使用旧摘要**：`InventoryStore.EnsurePage()` 现会比较旧摘要与从实际 Item 重建的新摘要，并更新全局索引。
2. **只有 `.lipg.bak` 的页未纳入索引重建**：`FilePageFileStore.ListPages()` 现在同时枚举 `.lipg` 和 `.lipg.bak`，新增 `PageStore_ListsBackupOnlyPages` 回归测试。
3. **meta 从 `.bak` 恢复可能过时**：现在标记 `RecoveredFromBackup`，从所有页文件重新建立摘要，而不是盲用旧备份计数；重建逐页修剪缓存，避免全部 Item 页长期驻留。
4. **远程 Take 的返回包可能太大**：服务端执行 Take 后才序列化结果，结果超过网络/客户端限制会导致服务端已移除物品而客户端未收到。当前已临时禁用远程 Take；恢复前必须实现有界、可确认的分块/事务协议。
5. **成功页加载却返回 null/错误长度 Item 数组时可能被当成空页**：现在作为 `PageLoadException` 处理，禁止静默补成空页。
6. **队列上限检查原本与入队分开**：现在队列锁内原子检查并入队；Store 生命周期锁避免 Dispose 与并发 Push 竞态。
7. **卸载时主线程动作队列清理顺序**：先 Dispose/Drain Store，再排空主线程动作，降低拾取补偿 callback 被丢弃的可能。
8. **版本信息**：`build.txt` 更新为 0.6.0，与修订文档一致。
9. **Store callback 线程边界**：公共 Store 操作现在要求在创建 Store 的线程（tML 主线程）调用；防止后台调用时队列满导致 callback 在后台同步执行，并新增自动测试。

**复核结论：**目前可以开始本计划限定范围内的人工冒烟测试，但**不等于达到发布/正式存档可用标准**。仍有三项重要限制：多人世界写权限、Put/Take/Move 执行中途异常的事务回滚、实时游戏及多模组验证。测试仅限新建/可丢弃角色与私有服务器；远程 Take 已关闭。

### 修订中发现的新风险（需后续关注）

1. **每条指令后同步摘要**：正确性更好，但大批量操作的 CPU 开销会增加，需基准测试。
2. **`LoadFailed` 页在元数据中被省略**：该页的类型索引暂时缺失，直到页文件修复或恢复后才会重新纳入；页文件本身不会被改动。
3. **协议不兼容**：v0.5 与 v0.6 之间的网络包互不兼容（已通过版本号检测并拒绝，不会误解析）。
4. **元数据重建时会扫描全部页文件**：通过逐页加载并修剪缓存限制常驻内存，但启动耗时与磁盘页数相关。
5. **远程 Take 暂停**：当前响应包没有分块/确认机制，不能安全地把任意大小的物品结果从服务端取回。
6. **指令执行中的异常缺少事务回滚**：尤其 Take 已移除部分物品后若后续 Item/Hook 抛异常，结果 payload 可能不能完整返回。
7. **Escape 指令仍不可序列化**，且仅供本地使用，这是设计如此。

### 人工测试流程

详细、可照做的步骤见仓库根目录 [`MANUAL_TEST_PLAN.md`](./MANUAL_TEST_PLAN.md)。包含测试角色/世界准备、基础 Put/Take/Move、重启持久化、页扩容、页与 meta 备份恢复、私有多人 netfill/netq、Debug/Release 差异，以及异常结果的反馈模板。

人工操作最低安全要求：

- 使用全新/可丢弃测试角色和世界，测试前备份对应 `.LI` 文件夹。
- 页/meta 损坏测试必须先完全关闭 tModLoader，完成后按计划恢复副本。
- 不在远程测试中执行 Take；远程 Take 当前被服务端拒绝。
- 出现数量不符、保存失败、页恢复异常或崩溃时立即停止，不要继续保存/反复操作，附最新日志、操作步骤、预期/实际结果和测试数据副本。

### 交付验证记录

- `dotnet build --no-restore`（Debug）：成功，0 警告，0 错误。
- `dotnet build --no-restore -c Release`：成功，0 警告，0 错误。
- `dotnet run --project tests/LargerInventory.Tests`：12 项通过，0 项失败。
- 未执行：游戏内存档、多人、损坏文件恢复、多模组兼容测试（需人工在 tModLoader 中验证）。

---

## 17. v0.6.1 复核：`/li` 无效与 1.4.4 兼容修复

### 现象

在 tModLoader **1.4.4.9**（2026.9.3.1，net8.0）中运行后，聊天测试指令 `/li` 无效。

### 根因（两项，均已确认）

1. **最后一次构建是 Release，覆盖了本地 `.tmod`**
   - `/li` 由 `#if DEBUG` 包住，Release 不编译。
   - 每次 `dotnet build`（无论 Debug/Release）都会把产物写到 `Mods/LargerInventory.tmod`；先 Debug 后 Release 会让 `/li` 消失。
2. **P0-3 的“创建线程绑定”在 1.4.4 上是错误的**
   - 日志证据：
     - `World store opened` 出现在 `.NET TP Worker`（异步世界加载线程）。
     - `SaveWorldData` / `SaveData` 也在工作线程执行。
   - 因此 `EnsureOwnerThread` 在 `PostUpdateEverything`（每帧 `WorldStore.Pump()`）、`SaveData`、`SaveWorldData` 中持续抛 `InvalidOperationException`：
     - tML 提示“上次保存该世界/人物时报告了以下错误，相关模组中的数据可能会丢失”；
     - 玩家 Store 的 Pump、网络更新和主线程后处理被跳过。

### 修复

1. **移除 Store 的创建线程绑定**（`EnsureOwnerThread` 及 `_ownerThreadId` 全部删除）。
2. **异步加载安全化**：Store 构造时只读取 `store.meta`；页文件扫描与 `ItemIO.Load` 推迟到主线程首次 `Pump()`（`ReconcilePages`）。
3. **存档线程兼容**：`FlushForSave` 允许在工作线程执行，但仍需取得执行锁；`Drain` 改为有超时，避免阻塞主线程。
4. **Store 创建幂等**：世界/玩家 Store 按路径只创建一次，避免 `LoadWorldData` 与 `OnWorldLoad` 重复打开。
5. **故障隔离**：`PostUpdateEverything` 对世界/每个玩家 Store 的 `Pump()` 分别 try/catch。
6. **Debug 可诊断性**：加载时日志明确输出 Debug/Release：
   - `[LI] Debug build loaded: /li debug commands are enabled.`
   - `[LI] Release build loaded: /li debug commands are NOT compiled.`
   - `/li` 每次调用也会写入日志。
7. **构建目标对齐 1.4.4**：
   - 清理 `bin/obj` 中 1.4.5 遗留的 `net10.0` 产物；
   - 测试工程改为 `net8.0` 并引用 `bin/Debug/net8.0/LargerInventory.dll`；
   - `build.txt` 增加 `side = Both` 与 `buildIgnore`，`.tmod` 从 **20.3 MB 降到约 70 KB**（此前把 `tests/` 的 97 MB 构建产物打进包里）。
8. **测试更新**：移除“非创建线程 Push 必须被拒绝”的错误契约，新增：
   - `Store_PumpAfterCreationOnOtherThread`
   - `Store_FlushWorksFromWorkerThread`

### 复核结论与使用要求

- 当前可按本计划继续进行人工测试，但**必须使用 Debug 构建**，并确认日志出现 `Debug build loaded: /li debug commands are enabled.`。
- 若聊天仍提示未知指令，优先检查：最后一次构建是否为 Release、是否在游戏内 “Build + Reload”、`Mods/LargerInventory.tmod` 时间戳是否为最新 Debug 构建。
- 1.4.4 的异步存档意味着 `SaveData` 不保证在主线程执行；任何“必须主线程”的约束都不能用创建线程绑定实现，只能通过执行锁串行化。

### 本次验证记录

- `dotnet build`（Debug，net8.0）：成功，0 警告，0 错误。
- `dotnet build -c Release`（net8.0）：成功，0 警告，0 错误。
- `dotnet run --project tests/LargerInventory.Tests`：12 项通过，0 项失败。
- 打包产物：`Mods/LargerInventory.tmod` 约 70 KB（Debug，含 `/li`）。
- 未执行：游戏内 `/li` 实测（需启动 1.4.4.9 并重新加载 Debug 构建）。

---

## 18. v0.6.2：`/li take` 取出后只得到 1 个物品

### 现象

`/li take 12 30` 回复显示已取出 30 个，但玩家背包/地面只有 1 个铜矿。

### 根因（已用 IL 确认）

Terraria 1.4.4 的 API：

```csharp
int Player.QuickSpawnItem(IEntitySource source, Item item, int stack = 1)
```

其实现直接调用 `QuickSpawnClonedItem(source, item, stack)`，而 `QuickSpawnClonedItem` 会：

1. 用传入 Item 克隆出一个世界物品；
2. **把该物品的 `stack` 覆盖为第三个参数 `stack`**（IL 中的 `ldarg.3` + `stfld Item::stack`）。

原代码调用 `QuickSpawnItem(null, item)`，未传第三个参数，默认值为 **1**，因此无论 `Item.stack` 是多少都只生成 1 个。`got=` 统计的是 Store 返回的真实堆叠数量，所以出现“显示 30、实际 1 个”。

### 影响范围

`QuickSpawnItem(null, item)` 共 3 处，均只生成 1 个：

- `/li take`（调试命令）
- 消耗后取回物品（`InventoryGlobalItem.OnConsumeTakeCompleted`）
- 拾取失败回滚/重生（`InventoryGlobalItem.RespawnItem`）

### 修复

统一改为传入真实堆叠数量：

```csharp
player.QuickSpawnItem(null, item, item.stack);
```

> 后续 v0.6.4 已把交付改为直接写入背包（原品），不再依赖 `QuickSpawnItem`；本节保留为问题定位记录，最终行为见第 20 节。

`/li take` 的回复增加 `spawned=`，用于区分“Store 实际取出”和“实际生成”：

```text
take type=12 requested=30 status=Ok got=30 spawned=30
```

### 验证

- Debug 构建：0 警告，0 错误。
- 自动测试：12 项通过，0 项失败（该 API 依赖游戏运行时，需游戏内实测）。
- 游戏内复测步骤：Debug 构建 → 重载模组 → `/li fill 12 100` → `/li take 12 30` → 回复应为 `got=30 spawned=30`，背包出现 30 个铜矿 → 保存退出再进入，`/li q 12` 应为 70。

---

## 19. v0.6.3：Take 输入 payload 与结果 payload 混淆风险

### 类型契约复核

Take 命令包含两个方向完全不同的 payload：

| 对象 | 属性 | Take 类型 | 用途 |
|---|---|---|---|
| `Command` | `InputPayload` | `TakePayload` | 输入：物品 type、目标数量、是否避开收藏物品 |
| `CommandResult` | `ResultPayload` | `List<Item>` | 输出：从 Store 实际取出的物品堆 |

调用链已复核：

1. `CommandFactory.Take(...)` 创建 `CommandKind.Take`，其 `InputPayload` 必须是 `TakePayload`。
2. `CommandExecutor.ExecuteTake()` 读取 `command.InputPayload as TakePayload`。
3. 执行完成后返回 `new CommandResult(status, results)`，其中 `results` 是 `List<Item>`。
4. `CommandExecutor.ExecuteCommandSafe()` 把这个结果写入 `command.Result`。
5. Debug `/li take` 通过 `Run()` 返回 `Command.Result`，再读取 `CommandResult.ResultPayload`。

因此，调试器若显示 `command.InputPayload`/旧别名 `command.Payload` 是 `TakePayload`，这是正确的输入，不应将它转换为 `List<Item>`。应检查的是 `command.Result.ResultPayload`（或 `CommandResult.ResultPayload`）。执行失败/未完成时结果 payload 可能是 `null`，而不是 TakePayload。

### 本次发现并修复的实际风险

虽然 `ExecuteTake()` 的返回类型路径正确，但 Debug `Run()` 原先只调用一次 `Pump()`。`Pump()` 每次最多处理 16 个 batch；如果 Take 前有较长立即执行队列，`Run()` 可能在 Take 尚未完成时返回 `InternalError`，而 Take 之后仍可能执行。这样调用方没拿到物品列表，却可能在稍后改变 Store。

修复：

- Debug `Run()` 现在在 `MaxQueuedCommands` 对应的有限预算内反复 Pump，直到目标 Command 完成；无进度或超预算才失败。
- `/li take` 现在使用 `TryGetResultPayload<List<Item>>()` 做强类型检查。类型不符时会显示 `status` 和实际 `resultPayload` 类型，并记入日志，不再静默按 0 个处理。
- 所有执行器和序列化路径使用明确的 `Command.InputPayload` / `CommandResult.ResultPayload` 名称。
- 旧 `Payload` 仅保留为带 `Obsolete` 的兼容别名，避免立即破坏外部源码，同时让新代码避免输入/输出混淆。
- 自动测试新增 `Command_InputAndResultPayloadTypesAreDistinct`，确认 Take 输入是 `TakePayload`，结果容器可承载 `List<Item>`。

### 验证状态

- Debug/net8.0 构建：0 警告、0 错误。
- 自动测试：13 项通过、0 项失败。
- 仍需游戏内确认 `/li take` 回复 `got=30 spawned=30` 且玩家实际收到 30 个。

---

## 20. v0.6.4：Take 改为直接背包交付（原品），不再依赖 QuickSpawnItem

### 问题

即使修正了 `QuickSpawnItem` 的堆叠参数，交付仍然走“生成世界物品 → 玩家拾取”的路径，存在两个不可控因素：

1. 物品先成为世界掉落物，才开始拾取；受拾取延迟、位置、背包判定影响。
2. Debug 构建启用了自动拾取钩子。物品被拾取时会再次经过 `GlobalItem.OnPickup`；若 `ItemLoader.ItemSpace` 判定不可放入，物品会被重新存回 Store，表现为“取出了但背包没有”。

另外，复核时在 `/li take` 路径发现一行遗留调试输出 `Main.NewText(caller.Player.name);`（位于逐物品循环内，会按物品堆数量刷屏）；该行已在重写交付逻辑时移除。它不是取物失败的原因，但会干扰观察。

### 修复

新增 `Backend/Integration/PlayerItemDelivery.cs`：

1. 先尝试合并到背包中可堆叠的同类物品（`ItemLoader.CanStack` / `OnStack`）；
2. 再放入主背包空槽（`Main.InventoryItemSlotsStart` 起 50 格，不使用钱币/弹药格）；
3. **整堆放入时直接把 Store 取出的 Item 实例赋给背包槽位**，不会克隆；
4. 超过单槽上限时才拆分，最后一段仍使用原实例；
5. 背包确实放不下的部分才掉落到玩家脚下。

`Take` 的存储侧语义也调整为“原品取出”：

- 整堆取出：返回原 Item 实例，Store 槽位复位；
- 部分取出：原 Item 实例承载被取出的数量，Store 保存剩余部分的克隆。

`/li take` 回复改为：

```text
take type=12 requested=30 status=Ok got=30 delivered=30 overflow=0
```

- `got`：Store 实际取出数量
- `delivered`：直接放入背包的数量
- `overflow`：背包放不下、掉落到脚下的数量

交付过程加入异常保护：若 `Give` 中途异常，剩余物品按 overflow 掉落，不会因交付失败而丢失。

`InventoryGlobalItem.OnConsumeTakeCompleted` 也改用同一交付逻辑；`RespawnItem` 仍用于回滚世界物品。

### 验证

- 自动测试新增 `Delivery_PutsOriginalItemIntoInventory`：在空背包中交付 30 个物品，断言数量为 30、无 overflow，且背包槽位引用的是**同一个 Item 实例**。
- Debug / Release 构建：0 警告、0 错误。
- 自动测试：14 项通过、0 项失败。
- 仍需游戏内确认：`/li take 12 30` 回复 `got=30 delivered=30 overflow=0`，背包立即出现 30 个铜矿，`/li q 12` 剩余 70。

---

## 21. v0.6.5：`/li slot -1` 空引用异常

### 现象

人工测试第 3 项执行 `/li slot -1` 时抛出 `NullReferenceException`，调试器显示 `info[0]` 为 `null`。

### 根因

`CommandExecutor.ExecuteQuerySlot()` 为每个请求槽位分配 `SlotInfo[]`，但当槽位非法或页加载失败时只设置了 `CommandStatus.InvalidArg` 并 `continue`，对应数组元素仍为 `null`：

```csharp
var result = new SlotInfo[payload.Slots.Length];
...
if (!TryGetPageForSlot(slotId, pinnedPages, out var page))
{
    status |= CommandStatus.InvalidArg;
    continue; // result[i] 保持 null
}
```

随后 `/li slot` 直接访问 `info[0].IsAir`，触发空引用。

同样的空元素还会影响 `CommandResultSerializer.Save()` 的 `SlotInfo[]` 分支（`Array.ConvertAll` 读取 `s.SlotId` 等字段）。

### 修复

1. `ExecuteQuerySlot()` 先把每个元素初始化为 `new SlotInfo { SlotId = slotId, IsAir = true }`；非法槽位/页失败时保留该“空槽”对象，并通过 `status` 表达失败。
2. `CommandResultSerializer.Save()` 对 `SlotInfo[]` 的每个元素做空值兜底（`s?.Field ?? default`），网络结果不再因空元素抛异常。
3. `/li slot` 使用模式匹配 `info[0] is { IsAir: false }`，即使未来出现空元素也不会崩溃。
4. 同时修复非法槽位的页分配副作用：`TryGetPageForSlot` / `TryPinPage` 现在只访问**已存在**的页。此前 `int.MaxValue` 的页索引在 `PageDirectory.MaxPageCount` 范围内，`EnsurePage` 会顺手创建一个空页并进入 `store.meta`；现在只有 Put spill 扩容会显式创建新页。
5. 新增测试 `Executor_InvalidSlotQueryReturnsAirEntry`：
   - 查询 `-1` 与 `int.MaxValue`；
   - 状态包含 `InvalidArg`；
   - 返回数组长度正确、每个元素非 null 且 `IsAir == true`；
   - 断言非法查询没有创建任何页（`AllocatedPages.Count == 0`）。

### 验证

- Debug 构建：0 警告、0 错误。
- 自动测试：15 项通过、0 项失败。
- 游戏内预期：`/li slot -1` 回复 `slot=-1 air/empty status=InvalidArg`，不抛异常。

---

## 22. v0.6.6：测试 8（页损坏且无备份）行为复核

### 实际日志

```
[LI] Page 0 unavailable; operations on it are skipped.
PageLoadException: page file missing for non-empty page 0
...
[LI] Page 0 unavailable; slot 0 skipped.
```

`pages` 目录当时为空，说明 `.lipg` 与 `.lipg.bak` 都已被移走/删除，而不是“文件存在但 CRC 错误”。“尝试两次失败”来自同一次 `/li slot 0` 的**预加载 + 按槽位访问**两次尝试，不是备份尝试了两次。

### 当时行为是否符合预期

- 损坏/缺失页：记录错误并跳过，不崩溃、不清空磁盘文件 —— **符合预期**。
- 新物品写入新页 —— **符合预期**。
- 但旧实现有一个不应有的副作用：`store.meta` 会跳过 `LoadFailed` 页，导致该页索引从元数据中消失。下次启动如果页文件已不在，分配器可能复用同一索引（例如重新生成 `0000000.lipg`），看起来就是“覆盖”了旧页。这是**不符合预期**的：应保留“该索引曾存在但不可用”的记录。

### 修复

1. **保留不可用页的索引**：`StoreMeta.Save` 不再跳过 `LoadFailed` 页，而是写入 `failed=1` 与最后已知摘要；`PageSummary.Unavailable` 记录该状态。
2. **禁止复用不可用索引**：启动时把该页重新标记为 `LoadFailed` 并放入页目录；`PageDirectory.Allocate` 因而永远不会分配该索引。
3. **不可用页不计入查询**：`TypeIndex` 不收录不可用页；`QueryType` 的摘要回退、`EnumeratePagesWithType`、空槽枚举都跳过 `LoadFailed` 页。访问失败时也会把旧摘要从 `TypeIndex` 中移除，`q` 不再显示无法取出物品的数量。
4. **保留恢复机会**：页文件原样保留；把原文件放回并再次访问时，`EnsureLoaded` 会重新尝试读取，成功后清除 `LoadFailed` 并重建索引。
5. **减少重复日志**：`QuerySlot` 不再预加载页，一次查询只产生一次失败记录。
6. **修复 NBT `bool` 序列化问题**：1.4.4 的 `TagIO` 不支持 `bool`/`bool[]` 标签（测试中 `TagIO.ToFile` 报 `NBT Serialization ... Boolean`）。`store.meta` 的 `failed`、`CommandSerializer` 的 `strong/weakAvoid/takeAvoid/putSpill`、`CommandResultSerializer` 的 `slotFavorited/slotAir` 全部改为 `byte`/`byte[]`。这也修复了此前联机请求序列化会失败的问题。
7. 测试工程补充 `DotNetZip` 引用（`store.meta` 使用压缩 TagIO）。

### 验证

- Debug / Release 构建：0 警告、0 错误。
- 自动测试：**18 项通过、0 项失败**，新增：
  - `StoreMeta_KeepsFailedPageIndexAllocated`：失败页索引写入 meta 后不会被分配器复用；
  - `FailedPage_IsRemovedFromTypeIndexAfterAccess`：页文件缺失时访问失败，`QueryType` 不再统计该页；
  - `Serializers_RoundTripFlagsAndSlotInfo`：`bool` 改为 `byte` 后联机请求/结果可正确往返，空 `SlotInfo` 元素按空槽处理。
- 预期游戏内行为：页 0 不可用时，`slot 0` 返回 `InvalidArg`；后续 `fill` 使用页 1；`store.meta` 保留页 0 的失败标记；把原页文件放回后再次访问可重新加载。

---

## 23. v0.7：测试命令配置 + 损坏页修复指令

### 配置

新增 `LargerInventoryConfig : ModConfig`（`ConfigScope.ServerSide`）：

- `EnableTestCommands`，默认 `false`。
- 服务器权威：多人时由服务器同步；服务器关闭时客户端无法通过配置绕过。
- `/li` 在 Debug 和 Release 都会注册，但 `Action` 首先检查该配置；关闭时直接拒绝。

### `/li repair [world]`

- 单机：默认修复玩家 Store；带 `world` 参数修复世界 Store。
- 修复实现 `InventoryStore.TryRepairCorruptedPages`：
  1. 获取执行锁，阻止修复期间指令执行/存档交错；
  2. 枚举页文件与已知失败页，逐个尝试正常加载；
  3. 无法加载的页用空白页覆盖（`EnsureSlots` + `RebuildFrom`），写回页文件、删除损坏的 `.bak`；
  4. 从类型索引移除旧摘要、保留页索引（不会被分配器复用）、更新 `store.meta`；
  5. 返回 `RepairReport`（总页数、修复数、剩余失败数、页索引）。
- 这是破坏性操作：被覆盖页中的物品不可恢复，必须在测试/备份前提下使用。

### 联机规则

- **客户端 `/li repair`**：只发送玩家作用域修复请求；服务器检查自身 `EnableTestCommands`，通过后修复请求者的玩家 Store，并以 `RepairReport` 回包。
- **客户端 `/li repair world`**：不发送网络请求，直接拒绝并提示必须以单机形式启动该世界后修复；服务器运行中不可修复世界存档。
- **专用服务器控制台**：不做世界修复；玩家修复由客户端请求触发。
- 协议版本提升到 **2**，新增修复请求/响应 opcode 3/4。
- 修复回包包含页摘要快照（页数 ≤ 256 时）与 `InventoryClientState.LastRepair`；当前客户端没有本地 Store，快照供后续 UI 使用，实际物品操作仍以服务器为准。

### 验证

- Debug / Release 构建：0 警告、0 错误。
- 自动测试：**20 项通过、0 项失败**，新增：
  - `Repair_OverwritesCorruptedPageWithBlank`：主文件缺失、`.bak` 损坏时，修复后生成空白页、删除损坏备份、旧物品计数清零；
  - `Config_TestCommandsDisabledByDefault`：配置默认关闭；
  - `RepairReport` 联机序列化往返。
- 仍需游戏内验证：单机玩家/世界修复、多人玩家修复回包、服务器拒绝世界修复、配置关闭时拒绝请求。

---

## 24. v0.7.1：修复改为页级锁 + 独立后台服务 + 指令暂停

### 设计修正

修复不再进入指令队列，也不再整体持有执行锁。新增 `PageRepairService`：

- 后台逐页检查所有页（`PageDirectory` + 磁盘页文件）；
- 单页 `TryEnter(0)` 独占页锁，拿不到立即跳过；
- 最多 3 轮补扫；3 轮都拿不到锁的页列入 `LockFailedPageIndices`，最终报告提示人工介入；
- 主页有效 → 释放；主页无效但 `.bak` 有效 → `PromoteBackup` 把备份复制为主文件，原 `.bak` 保留；两者都不可用且页已知非空/文件损坏 → 空白覆盖并删除损坏 `.bak`；
- 不解析 Item；CRC 正确但 Item 语义损坏交给上层事务；
- 后台只做纯文件 I/O；主线程 `Pump()` 消费 journal，更新摘要/`TypeIndex`/`store.meta`。

### 页锁

- 新增 `PageLockTable`，每页独立锁。
- 指令在预加载阶段按页索引升序获取全部所需页锁，持有到执行和摘要同步完成。
- 页锁不可用时：
  - 强指定指令返回 `CommandStatus.PageBusy`；
  - 弱指定指令跳过该页，继续使用其它页。
- `PageCache.EnsureLoaded` / `SavePage` 也使用页锁；修复持有页锁时，指令/存档不会并发访问同页。
- `PageBusy` 和 `ManualInterrupt` 加入 `CommandStatus`。

### 指令暂停

- `InventorySystem.CommandsPaused` 为原子标识。
- `/li pause on`：
  - 拒绝所有新的 `Push`/`PushBatch`（返回 `ManualInterrupt`，Put 返回全部物品）；
  - 阻止新批次开始（`Pump`/`ExecuteBatch` 检查）；
  - 清空队列，未执行指令以 `ManualInterrupt` 完成；
- `/li pause off` 恢复。
- 当前指令在主线程同步执行，暂停只在批次/指令边界生效，不会强行打断正在执行的 Item/Hook 调用。

### 多人通知

- 协议版本提升到 **3**。
- 服务器记录玩家请求/访问过的页（`PageRequestRegistry`）。
- 修复完成页后，只向请求过该页的玩家发送 `PageChanged`（仅页索引，不含内容）；最终报告回给请求者。
- 客户端 `InventoryClientState` 记录页变更通知与最后一次报告；断线/换世界清理订阅。
- 世界修复没有网络入口；服务器运行中只能请求修复玩家 Store。

### 验证

- Debug / Release：0 警告、0 错误。
- 自动测试：**24 项通过、0 项失败**，新增/更新：
  - `PageStore_PromotesValidBackup`：`.bak` 有效时复制升级为主文件并保留备份；
  - `PageLock_BusyCommandReturnsPageBusy`：页被其它线程锁定时强指定指令返回 `PageBusy`；
  - `CommandsPaused_RejectsNewCommands`：暂停时新指令返回 `ManualInterrupt`；
  - `Repair_OverwritesCorruptedPageWithBlank`：后台服务逐页修复、blank 覆盖、`.bak` 删除。
- 仍需游戏内验证：大存档修复时主线程响应、多人页变更通知、3 轮锁失败报告、暂停期间队列/网络行为。
