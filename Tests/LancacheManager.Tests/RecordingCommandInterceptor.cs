using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LancacheManager.Tests;

internal sealed class RecordingCommandInterceptor : DbCommandInterceptor
{
    private readonly object _sync = new();
    private readonly List<string> _commands = [];

    public IReadOnlyList<string> Commands
    {
        get
        {
            lock (_sync)
            {
                return _commands.ToList();
            }
        }
    }

    public Action<DbCommand>? OnExecuting { get; set; }

    public void Clear()
    {
        lock (_sync)
        {
            _commands.Clear();
        }
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData evt,
        InterceptionResult<DbDataReader> result)
    {
        Record(command);
        return base.ReaderExecuting(command, evt, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData evt,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ReaderExecutingAsync(command, evt, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData evt,
        InterceptionResult<int> result)
    {
        Record(command);
        return base.NonQueryExecuting(command, evt, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData evt,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.NonQueryExecutingAsync(command, evt, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData evt,
        InterceptionResult<object> result)
    {
        Record(command);
        return base.ScalarExecuting(command, evt, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData evt,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Record(command);
        return base.ScalarExecutingAsync(command, evt, result, cancellationToken);
    }

    private void Record(DbCommand command)
    {
        OnExecuting?.Invoke(command);
        lock (_sync)
        {
            _commands.Add(command.CommandText);
        }
    }
}
