using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public partial class OperationStateService
{
    // Read and written only under _admissionGate, so every grant is decided in one place. A
    // semaphore would grant in arrival order and let an import that queued first pass a waiting step.
    private LogFileLock? _logHolder;
    private int _logStepWaiters;
    private int _ingestWaiters;
    private bool _ingestTurnOwed;
    private bool _speedTrackerRunning;

    /// <summary>
    /// A step waits for the current import pass to end and for the speed tracker to stop; no new
    /// pass starts while a step waits, and an import that waited through a step goes next.
    /// </summary>
    public async Task<LogFileLock> LockLogFilesAsync(
        Guid? operationId,
        OperationType operationType,
        LogFileLockKind kind,
        CancellationToken cancellationToken)
    {
        var request = new LogFileLock(this, operationId, operationType, kind);
        var step = kind != LogFileLockKind.Ingest;
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            if (step)
            {
                _logStepWaiters++;
            }
            else
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
                    granted = _logHolder is null &&
                        (step ? !_ingestTurnOwed : _logStepWaiters == 0 || _ingestTurnOwed);
                    if (granted)
                    {
                        _logHolder = request;
                        if (step)
                        {
                            _logStepWaiters--;
                        }
                        else
                        {
                            _ingestWaiters--;
                            _ingestTurnOwed = false;
                        }
                        // A waiter that re-tested between the release and this grant sleeps on the
                        // signal the release created; without this one it never sees the new holder.
                        SignalWorkChanged();
                        break;
                    }
                    holder = _logHolder;
                    workChanged = _workChanged.Task;
                }
                finally
                {
                    _admissionGate.Release();
                }

                waited = true;
                if (operationId.HasValue && holder?.OperationId is { } holderOperationId)
                {
                    _operationTracker.SetBlockedByName(
                        operationId.Value,
                        _operationTracker.GetOperation(holderOperationId)?.Name);
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
                    else if (--_ingestWaiters == 0)
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
