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
    private readonly Queue<long> _boundaries = new();
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

    /// <summary>取出到第一个立即边界的前缀；没有立即边界时，取出到最早到期指令的前缀。</summary>
    public bool TryTakeBatch(long now, int maxCommands, out List<Command> batch)
    {
        batch = null;
        lock (_gate)
        {
            if (!_boundaries.TryPeek(out var endPosition))
            {
                if (_deadlines.First is not { } first || first.Value.Deadline > now)
                {
                    return false;
                }

                endPosition = first.Value.Position;
            }

            var take = (int)Math.Min(endPosition - _headPosition + 1, Math.Max(1, maxCommands));
            batch = new List<Command>(take);
            for (var i = 0; i < take; i++)
            {
                batch.Add(_queue.Dequeue());
            }

            _headPosition += take;
            if (_boundaries.TryPeek(out var boundary) && boundary < _headPosition)
            {
                _boundaries.Dequeue();
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
            _boundaries.Clear();
            _deadlines.Clear();
            _headPosition = 0;
            return all;
        }
    }

    private void EnqueueLocked(Command command)
    {
        var position = _headPosition + _queue.Count;
        _queue.Enqueue(command);
        if (command.TimeoutMs == 0)
        {
            _boundaries.Enqueue(position);
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
