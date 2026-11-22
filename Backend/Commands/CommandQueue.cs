using System;
using System.Collections.Generic;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 指令队列：FIFO + 最早截止时间跟踪。队列本身只负责排序与拉取，不执行任何指令；
/// 执行由 <see cref="CommandExecutor"/> 在主线程上通过 Pump 驱动。
/// </summary>
internal sealed class CommandQueue
{
    private readonly object _gate = new();
    private readonly Queue<Command> _queue = new();
    private readonly Queue<(long Position, bool IsSkip)> _readyBoundaries = new();
    private readonly Queue<long> _explicitBoundaries = new();
    private readonly LinkedList<(long Position, long Deadline)> _deadlines = new();
    private long _headPosition;

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
            EnqueueLocked(command);
        }
    }

    public bool TryPush(Command command, int maxCount)
    {
        lock (_gate)
        {
            if (_queue.Count >= maxCount)
            {
                return false;
            }

            EnqueueLocked(command);
            return true;
        }
    }

    /// <summary>批量压入：整批连续入队，期间持锁，其它压入不会插入其中。</summary>
    public void PushBatch(IReadOnlyList<Command> commands)
    {
        lock (_gate)
        {
            for (var i = 0; i < commands.Count; i++)
            {
                EnqueueLocked(commands[i]);
            }
        }
    }

    public bool TryPushBatch(IReadOnlyList<Command> commands, int maxCount)
    {
        lock (_gate)
        {
            if (commands.Count > maxCount - _queue.Count)
            {
                return false;
            }

            for (var i = 0; i < commands.Count; i++)
            {
                EnqueueLocked(commands[i]);
            }

            return true;
        }
    }

    /// <summary>按立即边界或截止时间取出普通前缀。队首为 SkipIf 时，整段取出至下一个显式边界。</summary>
    public bool TryTakeBatch(long now, int maxCommands, out List<Command> batch)
    {
        batch = null;
        lock (_gate)
        {
            var hasReadyBoundary = _readyBoundaries.TryPeek(out var ready);
            int take;
            if (hasReadyBoundary && ready.IsSkip && ready.Position == _headPosition)
            {
                if (!_explicitBoundaries.TryPeek(out var boundary))
                {
                    return false;
                }

                // An explicit NopBoundary is the only terminator once a SkipIf reaches the head.
                take = (int)(boundary - _headPosition + 1);
            }
            else
            {
                long endPosition;
                if (hasReadyBoundary)
                {
                    endPosition = ready.IsSkip ? ready.Position - 1 : ready.Position;
                }
                else if (_deadlines.First is { } first && first.Value.Deadline <= now)
                {
                    endPosition = first.Value.Position;
                }
                else
                {
                    return false;
                }

                take = (int)Math.Min(endPosition - _headPosition + 1, Math.Max(1, maxCommands));
            }

            batch = new List<Command>(take);
            for (var i = 0; i < take; i++)
            {
                batch.Add(_queue.Dequeue());
            }

            _headPosition += take;
            while (_readyBoundaries.TryPeek(out var covered) && covered.Position < _headPosition)
            {
                _readyBoundaries.Dequeue();
            }

            while (_explicitBoundaries.TryPeek(out var coveredBoundary) && coveredBoundary < _headPosition)
            {
                _explicitBoundaries.Dequeue();
            }

            while (_deadlines.First is { } expired && expired.Value.Position < _headPosition)
            {
                _deadlines.RemoveFirst();
            }

            return true;
        }
    }

    /// <summary>取出全部剩余指令（用于销毁时逐条明确失败）。</summary>
    public List<Command> TakeAll()
    {
        lock (_gate)
        {
            var all = new List<Command>(_queue);
            _queue.Clear();
            _readyBoundaries.Clear();
            _explicitBoundaries.Clear();
            _deadlines.Clear();
            _headPosition = 0;
            return all;
        }
    }

    private void EnqueueLocked(Command command)
    {
        var position = _headPosition + _queue.Count;
        _queue.Enqueue(command);
        if (command.Kind == CommandKind.NopBoundary)
        {
            _explicitBoundaries.Enqueue(position);
        }

        var isSkip = command.Kind == CommandKind.SkipIf;
        if (isSkip || command.TimeoutMs == 0)
        {
            _readyBoundaries.Enqueue((position, isSkip));
            return;
        }

        // A later position with a strictly earlier deadline cannot leave the queue before
        // the dominated entry does. Keep equal deadlines in FIFO order.
        var deadline = command.Deadline;
        while (_deadlines.Last is { } last && last.Value.Deadline > deadline)
        {
            _deadlines.RemoveLast();
        }

        _deadlines.AddLast((position, deadline));
    }
}
