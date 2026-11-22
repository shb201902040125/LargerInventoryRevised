# 仓库事务示例

以下四个方法都调用当前的 `InventoryStore.PushControlTransaction`，在权威仓库所在的游戏线程提交，由仓库正常推进。 [C# 示例](<InventoryTransactionExamples.cs>)

| 方法 | 演示 |
| --- | --- |
| `DepositBoth` | 两笔存入都成功才提交。第一笔失败时，`Br` 前跳并跳过第二笔。回滚或拒绝时归还两笔原输入。 |
| `WithdrawExactly` | 只接受足量取出。若只取到一部分，事务回滚并恢复库存。 |
| `MoveWithFallback` | 用保存点撤销首选槽位的尝试，包括部分移动，再尝试备用槽位。 |
| `WithdrawInRounds` | 用 `Label` 和 `Br` 后跳重复取货。每轮取足才继续，所有轮成功后一次交付物品。 |

`settle` 读取 `ExecutionStatus` 和批内结果，再明确返回 `Commit` 或 `Rollback`。`Take` 的批内结果包含当次取出的件数，最终回调才交付事务保留的物品。整笔回滚时，`Put` 的回调会收到归还的原物品。

提交前由调用方扣留待存物品，提交后由事务接管这些输入。背包交付或世界掉落应放在最终结果回调里。控制事务确认内存变更，写盘由仓库保存流程处理。 [实现说明](<../Backend/IMPLEMENTATION.md>)
