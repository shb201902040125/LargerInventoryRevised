using System;
using System.Collections.Generic;
using System.Threading;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 页级独占锁。指令在预加载阶段获取所需页锁并持有到执行结束；修复服务每次只 TryEnter 一页，
/// 拿不到立即跳过，绝不等待。同一个线程可重入（Monitor）。
/// </summary>
internal sealed class PageLockTable
{
    private readonly Dictionary<int, object> _gates = new();

    private object GetGate(int pageIndex)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(pageIndex, out var gate))
            {
                gate = new object();
                _gates[pageIndex] = gate;
            }

            return gate;
        }
    }

    public bool TryEnter(int pageIndex)
    {
        return Monitor.TryEnter(GetGate(pageIndex));
    }

    public void Exit(int pageIndex)
    {
        Monitor.Exit(GetGate(pageIndex));
    }
}
