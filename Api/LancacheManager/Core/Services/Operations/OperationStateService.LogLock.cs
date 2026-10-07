using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public partial class OperationStateService
{
    // Read and written only under _admissionGate, so every grant is decided in one place. A
    // semaphore would grant in arrival order and let an import that queued first pass a waiting step.
    private LogFileLock? _logHolder;
    private LogFileLock? _reopenHolder;
    private int _logStepWaiters;
    private int _ingestWaiters;
    private bool _ingestTurnOwed;
    private bool _speedTrackerRunning;

    /// <summary>
    /// A step waits for the current import pass to end and for the speed tracker to stop; no new
    /// pass starts while a step waits, and an import that waited through a step goes next. A reopen
    /// waits only for a step, and a step waits for a reopen; neither the import nor the speed tracker
    /// waits for a reopen. A step that waits only behind a reopen does not hold off the live import's
    /// pass, since it cannot be granted while the reopen holds; a manual pass still waits for the step.
    /// </summary>
    public async Task<LogFileLock> LockLogFilesAsync(
        Guid? operationId,
        OperationType operationType,
        LogFileLockKind kind,
        CancellationToken cancellationToken)
    {
        var request = new LogFileLock(this, operationId, operationType, kind);
        var step = kind is LogFileLockKind.Rows or LogFileLockKind.Rewrite;
        // Only the live import's pass may start beside a reopen while a step waits; a manual pass or
        // batch datasource keeps the turn rule, so no new pass starts ahead of a waiting step.
        var liveIngest = kind == LogFileLockKind.Ingest
            && operationId is { } ingestId
            && _operationTracker.GetOperation(ingestId)?.LiveIngest == true;
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            if (step)
            {
                _logStepWaiters++;
            }
            else if (kind == LogFileLockKind.Ingest)
            {
                _ingestWaiters++;
            }
            SignalWorkChanged();
        }
        finally
        {
            _admissionGate.Release();
        }

        var granted = false;
        var waited = false;
        try
        {
            while (true)
            {
                Task workChanged;
                LogFileLock? holder;
                await _admissionGate.WaitAsync(cancellationToken);
                try
                {
                    granted = kind == LogFileLockKind.Reopen
                        ? _reopenHolder is null && (_logHolder is null || _logHolder.Kind == LogFileLockKind.Ingest)
                        : _logHolder is null
                            && (step ? _reopenHolder is null && !_ingestTurnOwed : _logStepWaiters == 0 || _ingestTurnOwed || (_reopenHolder is not null && liveIngest));
                    if (granted)
                    {
                        if (kind == LogFileLockKind.Reopen)
                        {
                            _reopenHolder = request;
                        }
                        else
                        {
                            _logHolder = request;
                        }
                        if (step)
                        {
                            _logStepWaiters--;
                        }
                        else if (kind == LogFileLockKind.Ingest)
                        {
                            _ingestWaiters--;
                            _ingestTurnOwed = false;
                        }
                        // A waiter that re-tested between the release and this grant sleeps on the
                        // signal the release created; without this one it never sees the new holder.
                        SignalWorkChanged();
                        break;
                    }
                    holder = _logHolder ?? (step ? _reopenHolder : null);
                    workChanged = _workChanged.Task;
                }
                finally
                {
                    _admissionGate.Release();
                }

                waited = true;
                // A holder with no operation, or an owed import turn, has no blocker to show, and the
                // previous holder's name, type and target would be false.
                if (operationId.HasValue)
                {
                    var holderOperation = holder?.OperationId is { } holderOperationId
                        ? _operationTracker.GetOperation(holderOperationId)
                        : null;
                    _operationTracker.SetBlockedByName(
                        operationId.Value, holderOperation?.Name, holderOperation?.Type, holderOperation?.Target,
                        holderOperation?.Metadata is CacheClearingRepair { FullRepair: true });
                }
                await workChanged.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            if (!granted)
            {
                await _admissionGate.WaitAsync(CancellationToken.None);
                try
                {
                    if (step)
                    {
                        _logStepWaiters--;
                    }
                    else if (kind == LogFileLockKind.Ingest && --_ingestWaiters == 0)
                    {
                        _ingestTurnOwed = false;
                    }
                    SignalWorkChanged();
                }
                finally
                {
                    _admissionGate.Release();
                }
            }

            // Cleared on a cancelled wait too, so a job that gave up stops naming the holder.
            if (waited && operationId.HasValue)
            {
                _operationTracker.SetBlockedByName(operationId.Value, null);
            }
        }

        if (!step)
        {
            return request;
        }

        try
        {
            while (true)
            {
                Task workChanged;
                await _admissionGate.WaitAsync(cancellationToken);
                try
                {
                    if (!_speedTrackerRunning)
                    {
                        return request;
                    }
                    workChanged = _workChanged.Task;
                }
                finally
                {
                    _admissionGate.Release();
                }

                await workChanged.WaitAsync(cancellationToken);
            }
        }
        catch
        {
            await ReleaseLogFileLockAsync(request);
            throw;
        }
    }

    internal async Task ReleaseLogFileLockAsync(LogFileLock held)
    {
        await _admissionGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_reopenHolder == held)
            {
                _reopenHolder = null;
                SignalWorkChanged();
                return;
            }
            if (_logHolder != held)
            {
                return;
            }
            _logHolder = null;
            if (held.Kind != LogFileLockKind.Ingest && _ingestWaiters > 0)
            {
                _ingestTurnOwed = true;
            }
            SignalWorkChanged();
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    /// <summary>Completes once a step holds the logs (<paramref name="active"/> true) or none does.</summary>
    public async Task WaitForLogStepAsync(bool active, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task workChanged;
            await _admissionGate.WaitAsync(cancellationToken);
            try
            {
                if ((_logHolder is { Kind: not LogFileLockKind.Ingest }) == active)
                {
                    return;
                }
                workChanged = _workChanged.Task;
            }
            finally
            {
                _admissionGate.Release();
            }

            await workChanged.WaitAsync(cancellationToken);
        }
    }

    /// <summary>False while a step holds the logs, so no speed tracker child starts during a step.</summary>
    public bool TryBeginSpeedTrackerRun()
    {
        _admissionGate.Wait();
        try
        {
            if (_logHolder is { Kind: not LogFileLockKind.Ingest })
            {
                return false;
            }
            _speedTrackerRunning = true;
            return true;
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    public void EndSpeedTrackerRun()
    {
        _admissionGate.Wait();
        try
        {
            _speedTrackerRunning = false;
            SignalWorkChanged();
        }
        finally
        {
            _admissionGate.Release();
        }
    }
}
