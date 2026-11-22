using System;
using System.Collections.Generic;

namespace LargerInventory.Backend.Commands;

/// <summary>
/// 指令队列：FIFO + 最早截止时间跟踪。队列本身只负责排序与拉取，不执行任何指令；
/// 执行由 <see cref="CommandExecutor"/> 在主线程上通过 Pump 驱动。
/// </summary>
internal sealed class CommandQueue
{
    internal readonly record struct ControlInterval(int Start, int End);

    private readonly object _gate = new();
    private readonly Queue<Command> _queue = new();
    private readonly Queue<long> _readyBoundaries = new();
    private readonly Queue<(long Start, long End, int CommandCount)> _controlIntervals = new();
    private readonly LinkedList<(long Position, long Deadline)> _deadlines = new();
    private long _headPosition;
    private int _queuedCommandCount;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _queuedCommandCount;
            }
        }
    }

    internal static bool RequiresBatch(CommandKind kind) => kind is CommandKind.Label or CommandKind.Br
        or CommandKind.TransactionAction or CommandKind.ControlTransaction;

    public void Push(Command command)
    {
        if (!TryPush(command, int.MaxValue))
            throw new ArgumentException("Command cannot be enqueued individually.", nameof(command));
    }

    public bool TryPush(Command command, int maxCount)
    {
        lock (_gate)
        {
            if (RequiresBatch(command.Kind) || _queuedCommandCount >= maxCount)
                return false;

            EnqueueLocked(command);
            _queuedCommandCount++;
            return true;
        }
    }

    /// <summary>批量压入：整批连续入队，期间持锁，其它压入不会插入其中。</summary>
    public void PushBatch(IReadOnlyList<Command> commands)
    {
        if (!TryPushBatch(commands, int.MaxValue))
            throw new ArgumentException("Batch cannot be enqueued.", nameof(commands));
    }

    public bool TryPushBatch(IReadOnlyList<Command> commands, int maxCount) =>
        TryGetControlIntervals(commands, int.MaxValue, out var intervals) &&
        TryPushBatch(commands, maxCount, intervals);

    internal bool TryPushBatch(IReadOnlyList<Command> commands, int maxCount, List<ControlInterval> intervals)
    {
        lock (_gate)
        {
            if (commands.Count > maxCount - _queuedCommandCount)
                return false;

            var index = 0;
            if (intervals is not null)
            {
                var tail = _headPosition + _queue.Count;
                foreach (var interval in intervals)
                {
                    for (; index < interval.Start; index++)
                        EnqueueLocked(commands[index]);

                    // A protected segment is independently ready, with no scheduling boundaries inside it.
                    _controlIntervals.Enqueue((tail + interval.Start, tail + interval.End,
                        interval.End - interval.Start + 1));
                    for (; index <= interval.End; index++)
                        _queue.Enqueue(commands[index]);
                }
            }

            for (; index < commands.Count; index++)
                EnqueueLocked(commands[index]);

            _queuedCommandCount += commands.Count;
            return true;
        }
    }

    internal bool TryPushControlTransaction(Command envelope, int commandCount, int maxCount)
    {
        lock (_gate)
        {
            // An envelope occupies one queue position and charges capacity for its entire body.
            if (commandCount > maxCount - _queuedCommandCount)
                return false;

            var position = _headPosition + _queue.Count;
            _controlIntervals.Enqueue((position, position, commandCount));
            _queue.Enqueue(envelope);
            _queuedCommandCount += commandCount;
            return true;
        }
    }

    /// <summary>验证同一次提交的分支目标，并合并有交集的保护区间。</summary>
    internal static bool TryGetControlIntervals(IReadOnlyList<Command> commands, int maxControlCommands,
        out List<ControlInterval> intervals, bool transactional = false)
    {
        intervals = null;
        if (transactional && commands.Count > maxControlCommands)
            return false;

        Dictionary<BranchLabel, int> labels = null;
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.Kind == CommandKind.ControlTransaction ||
                (command.Kind == CommandKind.TransactionAction &&
                    (!transactional || command.Access.IsStrong || command.InputPayload is not Action<BranchContext>)))
                return false;
            if (command.Kind != CommandKind.Label)
                continue;
            labels ??= new Dictionary<BranchLabel, int>(ReferenceEqualityComparer.Instance);
            if (command.Access.IsStrong || command.InputPayload is not BranchLabel label || !labels.TryAdd(label, i))
                return false;
        }

        HashSet<BranchLabel> targets = null;
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.Kind != CommandKind.Br)
                continue;
            if (command.Access.IsStrong || command.InputPayload is not BranchPayload { Target: { } target, Predicate: not null } ||
                labels is null || !labels.TryGetValue(target, out var targetPosition))
                return false;

            if (!(targets ??= new HashSet<BranchLabel>(ReferenceEqualityComparer.Instance)).Add(target))
                return false;
            (intervals ??= new List<ControlInterval>()).Add(new ControlInterval(
                Math.Min(i, targetPosition), Math.Max(i, targetPosition)));
        }

        if (labels is not null && (targets is null || labels.Count != targets.Count))
            return false;

        if (intervals is not null)
        {
            // A backward branch can begin before a previously visited branch's interval.
            intervals.Sort(static (left, right) => left.Start.CompareTo(right.Start));
            var mergedCount = 1;
            for (var i = 1; i < intervals.Count; i++)
            {
                var current = intervals[i];
                var previous = intervals[mergedCount - 1];
                if (previous.End >= current.Start)
                    intervals[mergedCount - 1] = previous with { End = Math.Max(previous.End, current.End) };
                else
                    intervals[mergedCount++] = current;
            }

            intervals.RemoveRange(mergedCount, intervals.Count - mergedCount);
            foreach (var interval in intervals)
                if (interval.End - interval.Start + 1 > maxControlCommands)
                    return false;
        }

        return true;
    }

    /// <summary>按立即边界或截止时间取出普通前缀；保护区间到达队首后整段取出。</summary>
    public bool TryTakeBatch(long now, int maxCommands, out List<Command> batch)
    {
        batch = null;
        lock (_gate)
        {
            var hasControl = _controlIntervals.TryPeek(out var interval);
            int take;
            int commandCount;
            if (hasControl && interval.Start == _headPosition)
            {
                take = (int)(interval.End - interval.Start + 1);
                commandCount = interval.CommandCount;
                _controlIntervals.Dequeue();
            }
            else
            {
                long endPosition;
                if (_readyBoundaries.TryPeek(out var ready) && (!hasControl || ready < interval.Start))
                {
                    endPosition = ready;
                }
                else if (hasControl)
                {
                    endPosition = interval.Start - 1;
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
                commandCount = take;
            }

            batch = new List<Command>(take);
            for (var i = 0; i < take; i++)
            {
                batch.Add(_queue.Dequeue());
            }

            _headPosition += take;
            _queuedCommandCount -= commandCount;
            while (_readyBoundaries.TryPeek(out var covered) && covered < _headPosition)
            {
                _readyBoundaries.Dequeue();
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
            _controlIntervals.Clear();
            _deadlines.Clear();
            _headPosition = 0;
            _queuedCommandCount = 0;
            return all;
        }
    }

    private void EnqueueLocked(Command command)
    {
        var position = _headPosition + _queue.Count;
        _queue.Enqueue(command);
        if (command.TimeoutMs == 0)
        {
            _readyBoundaries.Enqueue(position);
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
