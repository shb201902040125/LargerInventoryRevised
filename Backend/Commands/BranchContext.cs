using System;
using System.Collections.Generic;

namespace LargerInventory.Backend.Commands;

/// <summary>一次提交内 Label 和 Br 的配对标识，按引用比较。</summary>
public sealed class BranchLabel { }

public enum ControlTransactionDecision
{
    Rollback,
    Commit
}

public sealed class ControlSavepoint
{
    internal ControlSavepoint() { }
}

/// <summary>只在当前执行线程的分支或事务委托调用期间有效。</summary>
public sealed class BranchContext
{
    private readonly IReadOnlyDictionary<Command, CommandResult> _results;
    private readonly Func<ControlSavepoint> _save;
    private readonly Action<ControlSavepoint> _restore;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _active = true;
    public CommandStatus ExecutionStatus { get; }

    internal BranchContext(IReadOnlyDictionary<Command, CommandResult> results,
        Func<ControlSavepoint> save = null, Action<ControlSavepoint> restore = null,
        CommandStatus executionStatus = CommandStatus.Ok)
    {
        ExecutionStatus = executionStatus;
        _results = results;
        _save = save;
        _restore = restore;
    }

    /// <summary>读取最近一次保留的执行结果。Take 和 Put 返回只读预览。</summary>
    public bool TryGetResult(Command command, out CommandResult result)
    {
        EnsureActive();
        if (!_results.TryGetValue(command, out result))
            return false;
        if (result.ResultPayload is ControlPutResult put)
            result = new CommandResult(result.Status, put.Preview);
        return true;
    }

    public ControlSavepoint CreateSavepoint()
    {
        EnsureActive();
        return (_save ?? throw new InvalidOperationException("Savepoints require a transaction action."))();
    }

    public void Restore(ControlSavepoint savepoint)
    {
        EnsureActive();
        (_restore ?? throw new InvalidOperationException("Restore requires a transaction action."))(savepoint);
    }

    internal void Close() => _active = false;

    private void EnsureActive()
    {
        if (_thread != Environment.CurrentManagedThreadId || !_active)
            throw new InvalidOperationException("The branch context is only valid during its callback.");
    }
}

internal sealed record ControlPutResult(PutPreview Preview, List<Terraria.Item> Returned);

internal sealed record BranchPayload(BranchLabel Target, Func<BranchContext, bool> Predicate);
internal sealed record ControlTransactionPayload(Command[] Commands, Func<BranchContext, ControlTransactionDecision> Settle);
