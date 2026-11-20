using System;
using System.Collections.Generic;
using System.Threading;

namespace LargerInventory.Backend.Storage;

/// <summary>
/// 执行方共享使用页，修复方独占页。获取失败时立即返回，不等待页的持有方。
/// 共享使用可嵌套，独占锁可由所属线程重入，两种模式不能混用。
/// </summary>
internal sealed class PageLockTable
{
    private sealed class PageGate
    {
        public int UseCount;
        public int ExclusiveThreadId;
        public int ExclusiveDepth;
    }

    // This lock protects bookkeeping only. Page use and repair happen outside it.
    private readonly Dictionary<int, PageGate> _gates = new();

    public bool TryEnterUse(int pageIndex)
    {
        lock (_gates)
        {
            var gate = GetOrCreateGate(pageIndex);
            if (gate.ExclusiveDepth != 0)
                return false;

            gate.UseCount++;
            return true;
        }
    }

    public void ExitUse(int pageIndex)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(pageIndex, out var gate) || gate.UseCount == 0)
                throw new SynchronizationLockException();

            gate.UseCount--;
        }
    }

    public bool TryEnter(int pageIndex)
    {
        var threadId = Environment.CurrentManagedThreadId;
        lock (_gates)
        {
            var gate = GetOrCreateGate(pageIndex);
            if (gate.UseCount != 0 || (gate.ExclusiveDepth != 0 && gate.ExclusiveThreadId != threadId))
                return false;

            gate.ExclusiveThreadId = threadId;
            gate.ExclusiveDepth++;
            return true;
        }
    }

    public void Exit(int pageIndex)
    {
        var threadId = Environment.CurrentManagedThreadId;
        lock (_gates)
        {
            if (!_gates.TryGetValue(pageIndex, out var gate) || gate.ExclusiveThreadId != threadId)
                throw new SynchronizationLockException();

            if (--gate.ExclusiveDepth == 0)
                gate.ExclusiveThreadId = 0;
        }
    }

    private PageGate GetOrCreateGate(int pageIndex)
    {
        if (!_gates.TryGetValue(pageIndex, out var gate))
        {
            gate = new PageGate();
            _gates.Add(pageIndex, gate);
        }

        return gate;
    }
}
