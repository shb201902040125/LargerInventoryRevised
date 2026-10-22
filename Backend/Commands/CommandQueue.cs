using System;
using System.Collections.Generic;
using System.Threading;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 指令队列：Queue + 最早超时跟踪 + 批量压入锁。
/// 不需要最小堆；划批后线性扫描剩余队列重算最早超时。
/// </summary>
internal sealed class CommandQueue : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<Command> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private long _earliestDeadline = long.MaxValue;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _queue.Count;
            }
        }
    }

    public void Push(Command command)
    {
        lock (_gate)
        {
            _queue.Enqueue(command);
            Track(command);
        }

        _signal.Set();
    }

    /// <summary>
    /// 批量压入：只保证整批连续、顺序固定，并在入队期间持锁防止其它压入插入。
    /// 不自动追加边界指令、不强制立即调度；需要边界的调用方自行再 Push(Boundary())。
    /// </summary>
    public void PushBatch(IReadOnlyList<Command> commands)
    {
        lock (_gate)
        {
            for (var i = 0; i < commands.Count; i++)
            {
                _queue.Enqueue(commands[i]);
                Track(commands[i]);
            }
        }

        _signal.Set();
    }

    public bool TryTakeBatch(long now, int maxCommands, out List<Command> batch)
    {
        batch = null;
        lock (_gate)
        {
            if (_queue.Count == 0)
            {
                _earliestDeadline = long.MaxValue;
                return false;
            }

            var array = _queue.ToArray();
            var boundaryIndex = -1;
            for (var i = 0; i < array.Length; i++)
            {
                if (array[i].TimeoutMs == 0)
                {
                    boundaryIndex = i;
                    break;
                }
            }

            if (boundaryIndex < 0)
            {
                var minDeadline = long.MaxValue;
                var minIndex = -1;
                for (var i = 0; i < array.Length; i++)
                {
                    var deadline = array[i].Deadline;
                    if (deadline < minDeadline)
                    {
                        minDeadline = deadline;
                        minIndex = i;
                    }
                }

                if (minDeadline > now)
                {
                    _earliestDeadline = minDeadline;
                    return false;
                }

                boundaryIndex = minIndex;
            }

            if (boundaryIndex < 0)
            {
                return false;
            }

            var take = Math.Min(boundaryIndex + 1, Math.Max(1, maxCommands));
            batch = new List<Command>(take);
            for (var i = 0; i < take; i++)
            {
                batch.Add(_queue.Dequeue());
            }

            RecomputeEarliestLocked();
            return true;
        }
    }

    public int GetWaitMilliseconds(long now)
    {
        lock (_gate)
        {
            if (_queue.Count == 0 || _earliestDeadline == long.MaxValue)
            {
                return 1000;
            }

            if (_earliestDeadline == long.MinValue || _earliestDeadline <= now)
            {
                return 1;
            }

            var delta = _earliestDeadline - now;
            return (int)Math.Clamp(delta, 1, 1000);
        }
    }

    public bool Wait(int milliseconds)
    {
        return _signal.WaitOne(Math.Max(1, milliseconds));
    }

    public void Signal()
    {
        _signal.Set();
    }

    private void Track(Command command)
    {
        if (command.Deadline < _earliestDeadline)
        {
            _earliestDeadline = command.Deadline;
        }
    }

    private void RecomputeEarliestLocked()
    {
        var earliest = long.MaxValue;
        foreach (var command in _queue)
        {
            if (command.Deadline < earliest)
            {
                earliest = command.Deadline;
            }
        }

        _earliestDeadline = earliest;
    }

    public void Dispose()
    {
        _signal.Dispose();
    }
}
