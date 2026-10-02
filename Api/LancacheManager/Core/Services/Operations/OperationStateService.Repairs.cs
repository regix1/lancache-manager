using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

public partial class OperationStateService
{
    private const string InterruptedByRestartError = "Operation interrupted by application restart";
    private static readonly TimeSpan _repairRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _completedRepairRetention = TimeSpan.FromHours(48);
    // A repair program that prints no progress and uses no CPU time is blocked on one call. Every
    // database statement the app runs is bounded at 30 minutes (CacheManagementService.Removal.cs)
    // except the download history upgrade's index rebuild (DownloadHistoryUpgradeService.cs), which
    // cannot overlap a repair: a pending repair keeps the upgrade queued, and a retried repair waits
    // for a running one. Log rewrites and cache walks use CPU, so they never look silent.
    private readonly TimeSpan _repairSilenceLimit = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ProcessManager _processManager;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly SemaphoreSlim _repairStateGate = new(1, 1);
    private readonly SemaphoreSlim _repairGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, OperationRepair> _repairs = new();
    private readonly ConcurrentDictionary<Guid, Lazy<Task>> _repairTasks = new();
    private readonly ConcurrentDictionary<Guid, byte> _startupRepairs = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _completedRepairs = new();
    private readonly ConcurrentDictionary<Guid, Func<Task>> _pendingOutcomes = new();
    private readonly ConcurrentDictionary<Guid, Action<OperationRepair>> _unsavedChanges = new();
    private readonly ConcurrentDictionary<Guid, byte> _forceStoppedOwners = new();
    private readonly ConcurrentDictionary<Guid, int> _repairFailures = new();
    private readonly TaskCompletionSource<bool> _recoveryOwnership =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _workChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _recoveryTask;

    public async Task PrepareRepairAsync(OperationRepair repair, CancellationToken cancellationToken)
    {
        await WaitForRecoveryOwnershipAsync(cancellationToken);
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            var replacement = CopyRepair(repair);
            ValidateNewRepair(replacement);

            await _repairStateGate.WaitAsync(cancellationToken);
            try
            {
                if (_repairs.ContainsKey(replacement.Id))
                {
                    throw new InvalidOperationException($"Operation repair {replacement.Id} is already prepared.");
                }

                PersistRepairs(replacement);
                _repairs[replacement.Id] = replacement;
            }
            finally
            {
                _repairStateGate.Release();
            }
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    public async Task StartWorkAsync(
        Guid operationId,
        string? datasource,
        CancellationToken cancellationToken)
    {
        await WaitForRecoveryOwnershipAsync(cancellationToken);

        while (true)
        {
            Task? blocker = null;
            await _admissionGate.WaitAsync(cancellationToken);
            try
            {
                var current = GetRequiredRepair(operationId);
                if (current.Phase == OperationRepairPhase.Running)
                {
                    await SaveRepairCoreAsync(
                        current,
                        next => RecordWorkStart(next, datasource),
                        cancellationToken);
                    return;
                }
                if (current.Phase != OperationRepairPhase.Prepared)
                {
                    throw new InvalidOperationException(
                        $"Operation repair {operationId} cannot start mutation from phase {current.Phase}.");
                }

                // A log pass waits for no repair: the log file lock keeps it apart from a repair's
                // log steps.
                var pending = current.Type == OperationType.LogProcessing ? null : GetBlockingRepair();
                if (pending is not null)
                {
                    // An outcome that has not landed owes no repair yet, so it is waited for, never claimed.
                    blocker = _pendingOutcomes.ContainsKey(pending.Id)
                        ? _workChanged.Task
                        : ClaimRepairTask(pending.Id, _startupRepairs.ContainsKey(pending.Id));
                }
                else
                {
                    await SaveRepairCoreAsync(
                        current,
                        next =>
                        {
                            next.Phase = OperationRepairPhase.Running;
                            RecordWorkStart(next, datasource);
                        },
                        cancellationToken);
                    return;
                }
            }
            finally
            {
                _admissionGate.Release();
            }

            await blocker.WaitAsync(cancellationToken);
        }
    }

    public async Task SaveRepairAsync(
        Guid operationId,
        Action<OperationRepair> update,
        CancellationToken cancellationToken)
    {
        await WaitForRecoveryOwnershipAsync(cancellationToken);

        await _repairStateGate.WaitAsync(cancellationToken);
        try
        {
            var current = GetRequiredRepair(operationId);
            try
            {
                SaveRepairCore(
                    current,
                    next =>
                    {
                        update(next);
                        if (next.Phase != current.Phase)
                        {
                            throw new InvalidOperationException(
                                $"Operation repair {operationId} phase changes are owned by the lifecycle.");
                        }
                    });
            }
            catch (Exception exception) when (current.Phase == OperationRepairPhase.Running
                && exception is IOException or UnauthorizedAccessException)
            {
                // A checkpoint whose file write failed (a rejected change is never kept) is carried by
                // the outcome save, so the repair still sees what the owner recorded.
                _unsavedChanges.AddOrUpdate(operationId, update, (_, kept) => kept + update);
                throw;
            }
        }
        finally
        {
            _repairStateGate.Release();
        }
    }

    public Task MarkLogRewriteStartedAsync(Guid operationId, string datasource)
    {
        return SaveRepairSourceAsync(operationId, datasource, source => source.LogRewriteStarted = true);
    }

    public Task MarkLogPositionsKeptAsync(Guid operationId, string datasource)
    {
        return SaveRepairSourceAsync(operationId, datasource, source => source.LogPositionsKept = true);
    }

    private Task SaveRepairSourceAsync(
        Guid operationId,
        string datasource,
        Action<OperationRepairSource> change)
    {
        return SaveRepairAsync(
            operationId,
            repair => change(repair.Sources.SingleOrDefault(
                    candidate => string.Equals(candidate.Datasource, datasource, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Operation repair {repair.Id} does not contain datasource '{datasource}'.")),
            CancellationToken.None);
    }

    public Task FinishRepairAsync(
        Guid operationId,
        bool success,
        bool cancelled,
        string? error)
    {
        return FinishRepairAsync(operationId, success, cancelled, error, update: null);
    }

    internal async Task FinishRepairAsync(
        Guid operationId,
        bool success,
        bool cancelled,
        string? error,
        Action<OperationRepair>? update)
    {
        var stoppingToken = _applicationLifetime.ApplicationStopping;
        await WaitForRecoveryOwnershipAsync(stoppingToken);
        await RecordOutcomeAsync(operationId, success, cancelled, error, update, forceStop: false, stoppingToken);
    }

    // Force stop records the outcome in one call that never waits for a repair or a failed save, so
    // the request returns at once; the owner's own FinishRepairAsync still follows it.
    public async Task RecordForceStopAsync(Guid operationId)
    {
        var stoppingToken = _applicationLifetime.ApplicationStopping;
        await WaitForRecoveryOwnershipAsync(stoppingToken);
        await RecordOutcomeAsync(
            operationId,
            success: false,
            cancelled: true,
            error: null,
            update: null,
            forceStop: true,
            stoppingToken);
    }

    private async Task RecordOutcomeAsync(
        Guid operationId,
        bool success,
        bool cancelled,
        string? error,
        Action<OperationRepair>? update,
        bool forceStop,
        CancellationToken stoppingToken)
    {
        OperationRepair? saved = null;
        var nextPhase = OperationRepairPhase.Repairing;
        var refreshDownloads = false;
        var saveFailed = false;
        var startRetry = false;
        await _admissionGate.WaitAsync(stoppingToken);
        try
        {
            var phase = _completedRepairs.ContainsKey(operationId)
                ? OperationRepairPhase.Completed
                : GetRequiredRepair(operationId).Phase;
            if (phase is OperationRepairPhase.Repairing or OperationRepairPhase.Completed)
            {
                // A force stop that landed first saved no metrics, so the owner's final ones are saved
                // before the barrier opens and the repair reads them; a failed save still opens it.
                if (phase == OperationRepairPhase.Repairing && !forceStop && update is not null)
                {
                    try
                    {
                        await SaveRepairCoreAsync(GetRequiredRepair(operationId), update, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Could not save the final metrics for operation {OperationId}",
                            operationId);
                    }
                }
                if (!forceStop && _forceStoppedOwners.TryRemove(operationId, out _))
                {
                    SignalWorkChanged();
                }
                if (phase == OperationRepairPhase.Repairing)
                {
                    _ = ClaimRepairTask(operationId, _startupRepairs.ContainsKey(operationId));
                }
                return;
            }

            // A prepared record never started its work, so it owes no repair; a log pass that owes
            // only a downloads refresh sends it here instead of through a repair.
            var current = GetRequiredRepair(operationId);
            if (phase == OperationRepairPhase.Prepared || IsDownloadsRefreshOnly(current))
            {
                nextPhase = OperationRepairPhase.Completed;
            }
            try
            {
                await SaveRepairCoreAsync(
                    current,
                    next =>
                    {
                        if (!next.Outcome.HasValue)
                        {
                            _unsavedChanges.TryGetValue(operationId, out var kept);
                            (kept + update)?.Invoke(next);
                            next.Outcome = success
                                ? OperationStatus.Completed
                                : cancelled
                                    ? OperationStatus.Cancelled
                                    : OperationStatus.Failed;
                            next.Error = error;
                        }
                        next.Phase = nextPhase;
                        if (nextPhase == OperationRepairPhase.Completed)
                        {
                            next.CompletedAt = UtcNow;
                            refreshDownloads = NeedsDownloadsRefresh(next);
                        }
                        saved = next;
                    },
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to save the repair outcome for operation {OperationId}",
                    operationId);
                saveFailed = true;
                _operationTracker.BeginRepair(operationId);
                // The owner's call carries the real outcome and its update, so it replaces a stored
                // force stop: a retry that landed with force stop's forceStop: true after the owner had
                // already called would add an owner barrier nobody removes.
                Func<Task> retry = () => RecordOutcomeAsync(
                    operationId,
                    success,
                    cancelled,
                    error,
                    update,
                    forceStop,
                    stoppingToken);
                startRetry = _pendingOutcomes.TryAdd(operationId, retry);
                if (!startRetry && !forceStop)
                {
                    _pendingOutcomes[operationId] = retry;
                }
            }

            if (!saveFailed)
            {
                _unsavedChanges.TryRemove(operationId, out _);
                if (phase == OperationRepairPhase.Running)
                {
                    SignalWorkChanged();
                }
                if (nextPhase == OperationRepairPhase.Repairing)
                {
                    if (forceStop)
                    {
                        _forceStoppedOwners.TryAdd(operationId, 0);
                    }
                    _operationTracker.BeginRepair(operationId);
                    _ = ClaimRepairTask(operationId, recovery: false);
                }
            }
        }
        finally
        {
            _admissionGate.Release();
        }

        if (startRetry)
        {
            _ = Task.Run(async () =>
            {
                var wait = true;
                while (true)
                {
                    if (wait)
                    {
                        await WaitUntilAsync(UtcNow.Add(_repairRetryDelay), stoppingToken);
                    }

                    Func<Task> call;
                    await _admissionGate.WaitAsync(stoppingToken);
                    try
                    {
                        // An owner's outcome that landed on its own already settled this retry.
                        if (!_pendingOutcomes.TryGetValue(operationId, out var pending))
                        {
                            return;
                        }
                        call = pending;
                    }
                    finally
                    {
                        _admissionGate.Release();
                    }
                    await call();

                    OperationRepairPhase? landed = null;
                    await _admissionGate.WaitAsync(stoppingToken);
                    try
                    {
                        var stored = _completedRepairs.ContainsKey(operationId)
                            ? OperationRepairPhase.Completed
                            : GetRequiredRepair(operationId).Phase;
                        wait = stored is not (OperationRepairPhase.Repairing or OperationRepairPhase.Completed);
                        // An owner's call that replaced the stored one while it ran still has to run.
                        if (!wait && _pendingOutcomes.TryRemove(KeyValuePair.Create(operationId, call)))
                        {
                            SignalWorkChanged();
                            landed = stored;
                        }
                    }
                    finally
                    {
                        _admissionGate.Release();
                    }

                    if (landed.HasValue)
                    {
                        _operationTracker.NotifyBlockerCleared();
                        if (landed == OperationRepairPhase.Completed)
                        {
                            // A skipped record or a log pass has no repair task to end its row.
                            _operationTracker.EndRepair(operationId, null);
                        }
                        return;
                    }
                }
            });
        }
        if (saveFailed)
        {
            return;
        }

        if (nextPhase == OperationRepairPhase.Completed)
        {
            if (refreshDownloads)
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISignalRNotificationService>()
                    .NotifyAllAsync(SignalREvents.DownloadsRefresh);
            }
            CleanupCompletedRepairReceipts(saved!);
            await ForgetCompletedLogRepairAsync(operationId, stoppingToken);
        }
        if (forceStop)
        {
            return;
        }

        // The owner's landed outcome also settles a stored retry (a force stop whose save failed, or
        // this call replayed by the retry loop), so the record stops blocking now instead of after
        // that retry's delay. The outcome is already saved, so a stopping app still settles it.
        var settled = false;
        await _admissionGate.WaitAsync(CancellationToken.None);
        try
        {
            settled = _pendingOutcomes.TryRemove(operationId, out _);
            if (settled)
            {
                SignalWorkChanged();
            }
        }
        finally
        {
            _admissionGate.Release();
        }
        if (settled)
        {
            _operationTracker.NotifyBlockerCleared();
            if (nextPhase == OperationRepairPhase.Completed)
            {
                // A skipped record or a log pass has no repair task to end its row.
                _operationTracker.EndRepair(operationId, null);
            }
        }
    }

    public async Task WaitForRecoveryOwnershipAsync(CancellationToken cancellationToken)
    {
        await _recoveryOwnership.Task.WaitAsync(cancellationToken);
    }

    public IReadOnlyList<OperationRepair> GetPendingRepairs()
    {
        return _repairs.Values
            .Where(repair => repair.Phase != OperationRepairPhase.Completed)
            .OrderBy(repair => repair.StartedAt)
            .Select(CopyRepair)
            .ToList();
    }

    public OperationRepair? GetBlockingRepair()
    {
        return _repairs.Values
            .Where(repair => repair.Phase == OperationRepairPhase.Repairing && !HasFailedOut(repair.Id)
                || _pendingOutcomes.ContainsKey(repair.Id))
            .OrderBy(repair => repair.StartedAt)
            .Select(CopyRepair)
            .FirstOrDefault();
    }

    public async Task<bool> RetryRepairAsync(Guid operationId)
    {
        var stoppingToken = _applicationLifetime.ApplicationStopping;
        await WaitForRecoveryOwnershipAsync(stoppingToken);
        await _admissionGate.WaitAsync(stoppingToken);
        try
        {
            // While the failed run still holds its task entry, a claim would get that ending task
            // back and start nothing. A card closed elsewhere would leave the new run with no card.
            if (!_repairs.TryGetValue(operationId, out var repair)
                || repair.Phase != OperationRepairPhase.Repairing
                || !HasFailedOut(operationId)
                || _repairTasks.ContainsKey(operationId)
                || _operationTracker.GetOperation(operationId) is null or { Closed: true })
            {
                return false;
            }

            _repairFailures.TryRemove(operationId, out _);
            _operationTracker.BeginRepair(operationId);
            _ = ClaimRepairTask(operationId, _startupRepairs.ContainsKey(operationId));
            return true;
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    // The user-chosen limit: with one wait of _repairRetryDelay between attempts, a failing repair
    // holds the queue for about two minutes before it fails out.
    private bool HasFailedOut(Guid operationId)
    {
        return _repairFailures.TryGetValue(operationId, out var failures) && failures >= 3;
    }

    public bool OwnsRepair(Guid operationId)
    {
        return _repairs.ContainsKey(operationId);
    }

    private async Task ClaimRecoveryOwnershipAsync(CancellationToken cancellationToken)
    {
        await _admissionGate.WaitAsync(cancellationToken);
        try
        {
            if (_recoveryOwnership.Task.IsCompleted)
            {
                await _recoveryOwnership.Task;
                return;
            }

            try
            {
                List<OperationRepair> loaded;
                try
                {
                    loaded = _stateService.LoadOperationRepairs().Select(CopyRepair).ToList();
                    foreach (var repair in loaded)
                    {
                        ValidateStoredRepair(repair);
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                {
                    // Salvaging the readable rows would silently drop whichever pending repair sat
                    // in a damaged one, so every datasource gets one full repair instead.
                    var movedTo = _stateService.SetAsideOperationRepairs();
                    _logger.LogError(
                        ex,
                        "Moved the unreadable operation repair file to {Path}; every datasource gets a full cache repair",
                        movedTo);
                    await using var scope = _scopes.CreateAsyncScope();
                    var capabilityService = scope.ServiceProvider.GetRequiredService<DatasourceCapabilityService>();
                    loaded = scope.ServiceProvider.GetRequiredService<DatasourceService>().GetDatasources()
                        .Select(datasource =>
                        {
                            var capabilities = capabilityService.GetCapabilities(datasource);
                            return new OperationRepair
                            {
                                Id = Guid.NewGuid(),
                                Type = OperationType.CacheClearing,
                                Name = "Full cache repair",
                                StartedAt = UtcNow,
                                Phase = OperationRepairPhase.Repairing,
                                Outcome = OperationStatus.Cancelled,
                                Sources =
                                [
                                    new OperationRepairSource
                                    {
                                        Datasource = datasource.Name,
                                        LogRoot = datasource.LogPath,
                                        CacheRoot = datasource.CachePath,
                                        // A datasource that cannot map cache files is not scanned,
                                        // but still gets its detection and corruption refresh.
                                        KeyScheme = capabilities.CanMapLogicalObjects
                                            ? DatasourceCapabilityService.GetSchemeWireValue(capabilities)
                                            : null,
                                        NativeLaunchAuthorized = true,
                                        LogRewriteStarted = true,
                                        ResetLogPositions = true,
                                        RefreshDownloads = true,
                                        ReconcileCache = true,
                                        RefreshDetection = true,
                                        InvalidateCorruption = true
                                    }
                                ],
                                CacheClearing = new CacheClearingRepair
                                {
                                    EntityKey = datasource.Name,
                                    FullRepair = true
                                }
                            };
                        })
                        .ToList();
                }

                var replacements = new List<OperationRepair>(loaded.Count);
                var changed = false;
                foreach (var repair in loaded)
                {
                    if (repair.Phase != OperationRepairPhase.Completed)
                    {
                        _startupRepairs.TryAdd(repair.Id, 0);
                        if (!repair.Outcome.HasValue)
                        {
                            repair.Outcome = OperationStatus.Failed;
                            repair.Error = InterruptedByRestartError;
                        }
                        if (repair.Phase != OperationRepairPhase.Repairing)
                        {
                            repair.Phase = OperationRepairPhase.Repairing;
                        }
                        changed = true;
                    }

                    replacements.Add(repair);
                }

                if (changed)
                {
                    _stateService.SaveOperationRepairs(replacements);
                }

                var forgotten = replacements
                    .Where(CanForgetCompletedLogRepair)
                    .ToList();
                var retained = replacements;
                if (forgotten.Count > 0)
                {
                    var forgottenIds = forgotten.Select(repair => repair.Id).ToHashSet();
                    var trimmed = replacements
                        .Where(repair => !forgottenIds.Contains(repair.Id))
                        .ToList();
                    try
                    {
                        _stateService.SaveOperationRepairs(trimmed);
                        retained = trimmed;
                        foreach (var repair in forgotten)
                        {
                            _completedRepairs[repair.Id] = repair.CompletedAt!.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Could not remove completed log-processing repairs during recovery ownership");
                    }
                }

                foreach (var repair in retained)
                {
                    _repairs[repair.Id] = repair;
                }
                _recoveryOwnership.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _recoveryOwnership.TrySetException(ex);
                throw;
            }
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    private void StartRecovery(CancellationToken stoppingToken)
    {
        _recoveryTask ??= RecoverAfterSetupAsync(stoppingToken);
        _ = ObserveRecoveryAsync(_recoveryTask, stoppingToken);
    }

    private async Task RecoverAfterSetupAsync(CancellationToken stoppingToken)
    {
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);
        await ClearOrphanedCorruptionPresentationAsync(stoppingToken);

        var repairs = _repairs.Values
            .Where(repair => _startupRepairs.ContainsKey(repair.Id))
            .OrderBy(repair => repair.StartedAt)
            .ToList();

        var running = new List<Task>();
        foreach (var repair in repairs)
        {
            stoppingToken.ThrowIfCancellationRequested();
            if (repair.Phase == OperationRepairPhase.Completed)
            {
                CleanupCompletedRepairReceipts(repair);
                continue;
            }

            running.Add(ClaimRepairTask(repair.Id, recovery: true));
        }
        if (running.Count > 0)
        {
            await Task.WhenAll(running);
        }
    }

    protected virtual async Task ClearOrphanedCorruptionPresentationAsync(
        CancellationToken stoppingToken)
    {
        await using (var scope = _scopes.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<CorruptionDetectionService>()
                .ClearOrphanedPresentationAsync(stoppingToken);
        }
    }

    private async Task ObserveRecoveryAsync(Task recoveryTask, CancellationToken stoppingToken)
    {
        try
        {
            await recoveryTask;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Operation repair recovery stopped unexpectedly");
        }
    }

    private Task ClaimRepairTask(Guid operationId, bool recovery)
    {
        // A failed-out repair runs again only through RetryRepairAsync or a restart.
        if (HasFailedOut(operationId))
        {
            return Task.CompletedTask;
        }

        return _repairTasks.GetOrAdd(
            operationId,
            id => new Lazy<Task>(
                () => RunRepairAsync(id, recovery, _applicationLifetime.ApplicationStopping),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private async Task RunRepairAsync(
        Guid operationId,
        bool recovery,
        CancellationToken stoppingToken)
    {
        var completed = false;
        string? failure = null;
        try
        {
            if (recovery)
            {
                await _stateService.WaitForSetupCompletedAsync(stoppingToken);
                await RestoreOwnerAsync(CopyRepair(GetRequiredRepair(operationId)), stoppingToken);
                // The restored card ends at once and shows the repair while it runs.
                if (GetRequiredRepair(operationId).Phase != OperationRepairPhase.Completed)
                {
                    _operationTracker.BeginRepair(operationId);
                }
                CompleteRestoredRepair(operationId);
            }

            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                await WaitForRunningWorkAsync(operationId, stoppingToken);

                var repair = CopyRepair(GetRequiredRepair(operationId));
                if (repair.Phase == OperationRepairPhase.Completed)
                {
                    CleanupCompletedRepairReceipts(repair);
                    await ForgetCompletedLogRepairAsync(operationId, stoppingToken);
                    completed = true;
                    return;
                }

                if (repair.RetryAtUtc is { } scheduledRetryAt && scheduledRetryAt > UtcNow)
                {
                    await WaitUntilAsync(scheduledRetryAt, stoppingToken);
                    continue;
                }

                try
                {
                    await _repairGate.WaitAsync(stoppingToken);
                    try
                    {
                        repair = CopyRepair(GetRequiredRepair(operationId));
                        // Waited for only inside the gate: every child a repair starts runs under
                        // it, so this wait never sees another repair's own child.
                        await WaitForNativeProcessesAsync(repair, stoppingToken);
                        await ApplyRepairAsync(repair, stoppingToken);
                        await SaveRepairCoreAsync(
                            GetRequiredRepair(operationId),
                            next =>
                            {
                                next.Phase = OperationRepairPhase.Completed;
                                next.RetryAtUtc = null;
                                next.CompletedAt = UtcNow;
                            },
                            stoppingToken);
                        CleanupCompletedRepairReceipts(GetRequiredRepair(operationId));
                    }
                    finally
                    {
                        _repairGate.Release();
                    }

                    await ForgetCompletedLogRepairAsync(operationId, stoppingToken);
                    completed = true;
                    return;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Required repair failed for operation {OperationId}", operationId);
                    _repairFailures.AddOrUpdate(operationId, 1, (_, count) => count + 1);
                    if (HasFailedOut(operationId))
                    {
                        // The record stays Repairing on disk, so Retry or the next start runs it.
                        _logger.LogError(
                            "Repair for operation {OperationId} failed out and waits for a retry",
                            operationId);
                        failure = ex.Message;
                        return;
                    }

                    var retryAt = UtcNow.Add(_repairRetryDelay);
                    try
                    {
                        await SaveRepairAsync(
                            operationId,
                            next => next.RetryAtUtc = retryAt,
                            stoppingToken);
                    }
                    catch (Exception saveException)
                    {
                        _logger.LogError(
                            saveException,
                            "Failed to save the retry time for operation repair {OperationId}",
                            operationId);
                    }

                    await WaitUntilAsync(retryAt, stoppingToken);
                }
            }
        }
        finally
        {
            // The only place the entry leaves, so a retry can start a new run only after this one
            // has ended; a run the stopping token ended leaves its card to the next start. Retry
            // takes the same gate, so it never starts a run between the removal and the row's end.
            await _admissionGate.WaitAsync(CancellationToken.None);
            try
            {
                _repairTasks.TryRemove(operationId, out _);
                if (completed)
                {
                    _repairFailures.TryRemove(operationId, out _);
                    _operationTracker.EndRepair(operationId, null);
                }
                else if (failure is not null)
                {
                    _operationTracker.EndRepair(operationId, failure);
                }
            }
            finally
            {
                _admissionGate.Release();
            }
        }
    }

    private async Task WaitForRunningWorkAsync(Guid operationId, CancellationToken stoppingToken)
    {
        while (true)
        {
            Task workChanged;
            await _admissionGate.WaitAsync(stoppingToken);
            try
            {
                // A retried repair skips job admission, and a failed-out repair let queued jobs start,
                // so it waits here, the way a new job would, for a running job that has no repair record
                // to order it: a history upgrade, a database reset, or a corruption or cache size scan
                // (a scan that read the cache before this repair's deletes would save stale results).
                // A cache clear is ordered by its own repair record instead: while it runs, the check
                // below holds this wait, and before it starts work it waits for this repair, so also
                // waiting for its row would leave the two waiting on each other.
                var jobRunning = _operationTracker.GetActiveOperations(null)
                    .Any(operation => (OperationConflictChecker.IsGlobal(operation.Type)
                            || operation.Type is OperationType.CorruptionDetection or OperationType.CacheSizeScan)
                        && !_repairs.ContainsKey(operation.Id));
                // A force-stopped job may still be finishing its own writes, so its repair waits for
                // the job's FinishRepairAsync. A running log pass never holds a repair back: the log
                // file lock keeps the repair's log steps apart from it.
                if (!jobRunning
                    && !_forceStoppedOwners.ContainsKey(operationId)
                    && !_repairs.Values.Any(repair => repair.Id != operationId
                        && repair.Phase == OperationRepairPhase.Running
                        && repair.Type != OperationType.LogProcessing))
                {
                    return;
                }
                // A job that ends signals no repair work, so the wait looks again each second.
                workChanged = jobRunning
                    ? Task.WhenAny(_workChanged.Task, Task.Delay(TimeSpan.FromSeconds(1), stoppingToken))
                    : _workChanged.Task;
            }
            finally
            {
                _admissionGate.Release();
            }

            await workChanged.WaitAsync(stoppingToken);
        }
    }

    private async Task MaintainRepairsAsync(CancellationToken stoppingToken)
    {
        await WaitForRecoveryOwnershipAsync(stoppingToken);
        var cutoff = UtcNow - _completedRepairRetention;

        await _repairStateGate.WaitAsync(stoppingToken);
        try
        {
            var cleanupAccepted = _repairs.Values
                .Where(repair => repair.Phase == OperationRepairPhase.Completed)
                .ToDictionary(
                    repair => repair.Id,
                    CleanupCompletedRepairReceipts);
            var forgotten = _repairs.Values
                .Where(CanForgetCompletedLogRepair)
                .Select(repair => repair.Id)
                .ToHashSet();
            var expired = _repairs.Values
                .Where(repair => repair.Phase == OperationRepairPhase.Completed
                    && repair.CompletedAt < cutoff
                    && cleanupAccepted[repair.Id])
                .Select(repair => repair.Id)
                .ToHashSet();
            var removed = forgotten.Concat(expired).ToHashSet();
            if (removed.Count > 0)
            {
                PersistRepairsWithout(removed);
                foreach (var operationId in removed)
                {
                    if (forgotten.Contains(operationId)
                        && _repairs.TryGetValue(operationId, out var repair))
                    {
                        _completedRepairs[operationId] = repair.CompletedAt!.Value;
                    }
                    _repairs.TryRemove(operationId, out _);
                    _startupRepairs.TryRemove(operationId, out _);
                }
            }

            foreach (var operationId in _completedRepairs
                .Where(item => item.Value < cutoff)
                .Select(item => item.Key))
            {
                _completedRepairs.TryRemove(operationId, out _);
            }
        }
        finally
        {
            _repairStateGate.Release();
        }

        // Until its setup finishes, the database cannot be reached.
        if (!_configuration.GetValue<bool>("Runtime:DatabaseSetupPending"))
        {
            var retainedScanIds = _repairs.Values
                .Where(repair => repair.EvictionScanId.HasValue)
                .Select(repair => repair.EvictionScanId!.Value)
                .ToHashSet();
            await PruneEvictionScanCheckpointsAsync(cutoff, retainedScanIds, stoppingToken);
        }

        foreach (var repair in _repairs.Values.Where(repair => repair.Phase == OperationRepairPhase.Repairing))
        {
            _ = ClaimRepairTask(repair.Id, _startupRepairs.ContainsKey(repair.Id));
        }
    }

    private bool CleanupCompletedRepairReceipts(OperationRepair repair)
    {
        if (repair.Phase != OperationRepairPhase.Completed)
        {
            throw new InvalidOperationException(
                $"Operation repair {repair.Id} cannot remove root receipts before completion.");
        }

        var cleanupAccepted = true;
        foreach (var source in repair.Sources.Where(source => source.ReceiptPath is not null))
        {
            cleanupAccepted &= CleanupCompletedRepairReceipt(repair.Id, source);
        }
        return cleanupAccepted;
    }

    private bool CleanupCompletedRepairReceipt(Guid operationId, OperationRepairSource source)
    {
        var receiptPath = source.ReceiptPath!;
        var receiptDirectory = Path.GetDirectoryName(receiptPath);
        if (string.IsNullOrWhiteSpace(receiptDirectory) || !Directory.Exists(receiptDirectory))
        {
            _logger.LogWarning(
                "Kept root receipt cleanup pending for completed operation {OperationId} because {ReceiptPath} is unavailable",
                operationId,
                receiptPath);
            return false;
        }

        byte[] receiptBytes;
        try
        {
            receiptBytes = File.ReadAllBytes(receiptPath);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Kept root receipt cleanup pending for completed operation {OperationId} at {ReceiptPath}",
                operationId,
                receiptPath);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                exception,
                "Kept root receipt cleanup pending for completed operation {OperationId} at {ReceiptPath}",
                operationId,
                receiptPath);
            return false;
        }

        if (!ReceiptMatches(receiptBytes, operationId, source.CacheRoot))
        {
            _logger.LogWarning(
                "Kept root receipt {ReceiptPath} because it does not match completed operation {OperationId}",
                receiptPath,
                operationId);
            return false;
        }

        try
        {
            if (!File.ReadAllBytes(receiptPath).SequenceEqual(receiptBytes))
            {
                _logger.LogWarning(
                    "Kept root receipt {ReceiptPath} because it changed during cleanup for operation {OperationId}",
                    receiptPath,
                    operationId);
                return false;
            }

            File.Delete(receiptPath);
            if (File.Exists(receiptPath))
            {
                _logger.LogWarning(
                    "Kept root receipt cleanup pending for completed operation {OperationId} at {ReceiptPath}",
                    operationId,
                    receiptPath);
                return false;
            }

            _logger.LogInformation(
                "Removed root receipt {ReceiptPath} for completed operation {OperationId}",
                receiptPath,
                operationId);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (IOException exception)
        {
            _logger.LogWarning(
                exception,
                "Kept root receipt cleanup pending for completed operation {OperationId} at {ReceiptPath}",
                operationId,
                receiptPath);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            _logger.LogWarning(
                exception,
                "Kept root receipt cleanup pending for completed operation {OperationId} at {ReceiptPath}",
                operationId,
                receiptPath);
            return false;
        }
    }

    private static bool ReceiptMatches(byte[] receiptBytes, Guid operationId, string? cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(receiptBytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 4
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionNumber)
                || versionNumber != 1
                || !root.TryGetProperty("operationId", out var storedOperationId)
                || storedOperationId.ValueKind != JsonValueKind.String
                || !Guid.TryParse(storedOperationId.GetString(), out var parsedOperationId)
                || parsedOperationId != operationId
                || !root.TryGetProperty("cachePath", out var storedCacheRoot)
                || storedCacheRoot.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("hadCacheFiles", out var hadCacheFiles)
                || hadCacheFiles.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !hadCacheFiles.GetBoolean())
            {
                return false;
            }

            var storedPath = storedCacheRoot.GetString();
            if (string.IsNullOrWhiteSpace(storedPath))
            {
                return false;
            }

            var expected = ResolveCacheRoot(cacheRoot);
            var actual = ResolveCacheRoot(storedPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(expected, actual, comparison);
        }
        catch (Exception exception) when (exception is JsonException
            or ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ResolveCacheRoot(string cacheRoot)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(cacheRoot));
        return Path.TrimEndingDirectorySeparator(
            directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory.FullName);
    }

    protected virtual async Task PruneEvictionScanCheckpointsAsync(
        DateTime cutoff,
        IReadOnlySet<Guid> retainedScanIds,
        CancellationToken stoppingToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var oldFinalized = await context.EvictionScanCheckpoints
            .Where(checkpoint => checkpoint.FinalizedAtUtc < cutoff)
            .ToListAsync(stoppingToken);
        var removable = oldFinalized
            .Where(checkpoint => !retainedScanIds.Contains(checkpoint.OperationId))
            .ToList();
        if (removable.Count == 0)
        {
            return;
        }

        context.EvictionScanCheckpoints.RemoveRange(removable);
        await context.SaveChangesAsync(stoppingToken);
        _logger.LogInformation(
            "Removed {Count} finalized eviction scan checkpoints older than the repair retention window",
            removable.Count);
    }

    private async Task SaveRepairCoreAsync(
        OperationRepair current,
        Action<OperationRepair> update,
        CancellationToken cancellationToken)
    {
        await _repairStateGate.WaitAsync(cancellationToken);
        try
        {
            SaveRepairCore(current, update);
        }
        finally
        {
            _repairStateGate.Release();
        }
    }

    private void SaveRepairCore(OperationRepair current, Action<OperationRepair> update)
    {
        var latest = GetRequiredRepair(current.Id);
        var replacement = CopyRepair(latest);
        update(replacement);
        ValidateRepairChange(latest, replacement);
        PersistRepairs(replacement);
        _repairs[replacement.Id] = replacement;
    }

    private void PersistRepairs(OperationRepair replacement)
    {
        var snapshot = _repairs.Values
            .Where(repair => repair.Id != replacement.Id)
            .Append(replacement)
            .OrderBy(repair => repair.StartedAt)
            .Select(CopyRepair)
            .ToList();
        _stateService.SaveOperationRepairs(snapshot);
    }

    private void PersistRepairsWithout(HashSet<Guid> operationIds)
    {
        var snapshot = _repairs.Values
            .Where(repair => !operationIds.Contains(repair.Id))
            .OrderBy(repair => repair.StartedAt)
            .Select(CopyRepair)
            .ToList();
        _stateService.SaveOperationRepairs(snapshot);
    }

    private async Task ForgetCompletedLogRepairAsync(
        Guid operationId,
        CancellationToken stoppingToken)
    {
        await _repairStateGate.WaitAsync(stoppingToken);
        try
        {
            if (!_repairs.TryGetValue(operationId, out var repair)
                || !CanForgetCompletedLogRepair(repair))
            {
                return;
            }

            try
            {
                PersistRepairsWithout(new HashSet<Guid> { operationId });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Could not remove completed log-processing repair {OperationId}",
                    operationId);
                return;
            }

            _completedRepairs[operationId] = repair.CompletedAt!.Value;
            _repairs.TryRemove(operationId, out _);
            _startupRepairs.TryRemove(operationId, out _);
        }
        finally
        {
            _repairStateGate.Release();
        }
    }

    private static bool CanForgetCompletedLogRepair(OperationRepair repair)
    {
        return repair.Type == OperationType.LogProcessing
            && repair.Phase == OperationRepairPhase.Completed
            && !repair.EvictionScanId.HasValue
            && repair.Sources.All(source => source.ReceiptPath is null);
    }

    private OperationRepair GetRequiredRepair(Guid operationId)
    {
        return _repairs.TryGetValue(operationId, out var repair)
            ? repair
            : throw new KeyNotFoundException($"Operation repair {operationId} was not prepared.");
    }

    private void SignalWorkChanged()
    {
        var previous = _workChanged;
        _workChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult(true);
    }

    private static void RecordWorkStart(OperationRepair repair, string? datasource)
    {
        if (datasource is null)
        {
            repair.DatabaseWriteStarted = true;
            return;
        }

        var source = repair.Sources.SingleOrDefault(
            candidate => string.Equals(candidate.Datasource, datasource, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Operation repair {repair.Id} does not contain datasource '{datasource}'.");
        source.NativeLaunchAuthorized = true;
    }

    private static OperationRepair CopyRepair(OperationRepair repair)
    {
        var json = JsonSerializer.Serialize(repair);
        return JsonSerializer.Deserialize<OperationRepair>(json)
            ?? throw new InvalidDataException($"Operation repair {repair.Id} could not be copied.");
    }

    private static void ValidateNewRepair(OperationRepair repair)
    {
        ValidateStoredRepair(repair);
        if (repair.Phase != OperationRepairPhase.Prepared
            || repair.Outcome.HasValue
            || repair.Error is not null
            || repair.RetryAtUtc.HasValue
            || repair.CompletedAt.HasValue
            || repair.DatabaseWriteStarted
            || repair.Sources.Any(source => source.NativeLaunchAuthorized || source.NativeCompletionAccepted)
            || StateService.HasConfirmedRepairCounters(repair))
        {
            throw new InvalidOperationException($"Operation repair {repair.Id} is not a new prepared repair.");
        }
    }

    private static void ValidateStoredRepair(OperationRepair repair)
    {
        StateService.ValidateOperationRepair(repair);
    }

    private static void ValidateRepairChange(OperationRepair current, OperationRepair replacement)
    {
        ValidateStoredRepair(replacement);
        if (current.Version != replacement.Version
            || current.Id != replacement.Id
            || current.Type != replacement.Type
            || !string.Equals(current.Name, replacement.Name, StringComparison.Ordinal)
            || current.StartedAt != replacement.StartedAt
            || !NoticeEqual(current.Notice, replacement.Notice)
            || !TargetEqual(current.Target, replacement.Target)
            || !CorruptionEqual(current.Corruption, replacement.Corruption)
            || current.Sources.Count != replacement.Sources.Count)
        {
            throw new InvalidOperationException($"Operation repair {current.Id} changed immutable identity.");
        }
        if (!MetricIdentityEqual(current, replacement))
        {
            throw new InvalidOperationException($"Operation repair {current.Id} changed metric identity.");
        }
        if (ConfirmedCountersDecreased(current, replacement))
        {
            throw new InvalidOperationException($"Operation repair {current.Id} reduced confirmed counters.");
        }
        if (replacement.Phase < current.Phase)
        {
            throw new InvalidOperationException($"Operation repair {current.Id} moved to an earlier phase.");
        }
        if (current.Outcome.HasValue && current.Outcome != replacement.Outcome)
        {
            throw new InvalidOperationException($"Operation repair {current.Id} changed its original outcome.");
        }
        if (current.Error is not null && !string.Equals(current.Error, replacement.Error, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Operation repair {current.Id} changed its original error.");
        }
        if (current.CompletedAt.HasValue && current.CompletedAt != replacement.CompletedAt)
        {
            throw new InvalidOperationException($"Operation repair {current.Id} changed its completion time.");
        }
        if (current.DatabaseWriteStarted && !replacement.DatabaseWriteStarted)
        {
            throw new InvalidOperationException($"Operation repair {current.Id} cleared its work checkpoint.");
        }
        if (!EvictionScanPointerAllowed(current, replacement))
        {
            throw new InvalidOperationException($"Operation repair {current.Id} has an invalid scan attempt pointer.");
        }

        for (var index = 0; index < current.Sources.Count; index++)
        {
            var before = current.Sources[index];
            var after = replacement.Sources[index];
            if (!SourceScopeEqual(current.Type, before, after))
            {
                throw new InvalidOperationException($"Operation repair {current.Id} changed source scope.");
            }
            if (before.NativeLaunchAuthorized && !after.NativeLaunchAuthorized
                || before.NativeCompletionAccepted && !after.NativeCompletionAccepted
                || before.LogRewriteStarted && !after.LogRewriteStarted
                || before.LogPositionsKept && !after.LogPositionsKept)
            {
                throw new InvalidOperationException($"Operation repair {current.Id} cleared a source checkpoint.");
            }
            if (before.ReceiptPath is not null
                && !string.Equals(before.ReceiptPath, after.ReceiptPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Operation repair {current.Id} changed a root receipt.");
            }
            if (before.NativeCompletionAccepted
                && !before.CorruptionCandidateIds.SequenceEqual(after.CorruptionCandidateIds, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Operation repair {current.Id} changed accepted corruption candidates.");
            }
            // The log step writes its three counts until it keeps the log positions; a redo after a
            // reset writes them then.
            if (before.NativeCompletionAccepted
                && (!CorruptionCountsEqual(before.CorruptionCounts, after.CorruptionCounts)
                    || before.LogPositionsKept
                        && before.CorruptionCounts is { } keptCounts
                        && after.CorruptionCounts is { } nextCounts
                        && (keptCounts.LogLinesRemoved != nextCounts.LogLinesRemoved
                            || keptCounts.LogEntriesDeleted != nextCounts.LogEntriesDeleted
                            || keptCounts.DownloadsDeleted != nextCounts.DownloadsDeleted)))
            {
                throw new InvalidOperationException(
                    $"Operation repair {current.Id} changed accepted corruption counters.");
            }
        }
    }

    private static bool EvictionScanPointerAllowed(OperationRepair current, OperationRepair replacement)
    {
        if (current.EvictionScanId == replacement.EvictionScanId)
        {
            return true;
        }
        if (!replacement.EvictionScanId.HasValue || replacement.EvictionScanId == Guid.Empty)
        {
            return false;
        }
        if (current.Type == OperationType.EvictionScan)
        {
            return !current.EvictionScanId.HasValue
                && replacement.EvictionScanId == replacement.Id;
        }
        if (current.Type is OperationType.CacheClearing
            or OperationType.GameRemoval
            or OperationType.ServiceRemoval
            or OperationType.CorruptionRemoval
            or OperationType.EvictionRemoval)
        {
            return replacement.EvictionScanId != replacement.Id;
        }

        return false;
    }

    private static bool MetricIdentityEqual(OperationRepair left, OperationRepair right)
    {
        return (left.CacheClearing, right.CacheClearing) switch
            {
                (null, null) => true,
                ({ } first, { } second) =>
                    string.Equals(first.EntityKey, second.EntityKey, StringComparison.Ordinal)
                    && string.Equals(first.DatasourceName, second.DatasourceName, StringComparison.OrdinalIgnoreCase),
                _ => false
            }
            && (left.LogRemoval, right.LogRemoval) switch
            {
                (null, null) => true,
                ({ } first, { } second) =>
                    string.Equals(first.Service, second.Service, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(first.Datasource, second.Datasource, StringComparison.OrdinalIgnoreCase),
                _ => false
            }
            && (left.Removal, right.Removal) switch
            {
                (null, null) => true,
                ({ } first, { } second) =>
                    string.Equals(first.EntityKey, second.EntityKey, StringComparison.Ordinal)
                    && string.Equals(first.EntityName, second.EntityName, StringComparison.Ordinal)
                    && string.Equals(first.EntityKind, second.EntityKind, StringComparison.Ordinal)
                    && string.Equals(first.Service, second.Service, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(first.EpicAppId, second.EpicAppId, StringComparison.Ordinal)
                    && first.DetectionMethod == second.DetectionMethod
                    && first.CorruptionScanId == second.CorruptionScanId,
                _ => false
            }
            && (left.GameDetection, right.GameDetection) switch
            {
                (null, null) => true,
                ({ } first, { } second) =>
                    first.ParentOperationId == second.ParentOperationId
                    && first.ScanType == second.ScanType
                    && first.StartTime == second.StartTime,
                _ => false
            }
            && (left.EvictionRemoval, right.EvictionRemoval) switch
            {
                (null, null) => true,
                ({ } first, { } second) => EvictionSelectionEqual(first.Selection, second.Selection),
                _ => false
            };
    }

    private static bool ConfirmedCountersDecreased(OperationRepair before, OperationRepair after)
    {
        return before.CacheClearing is { } firstClearing && after.CacheClearing is { } nextClearing
                && (nextClearing.DirectoriesProcessed < firstClearing.DirectoriesProcessed
                    || nextClearing.TotalDirectories < firstClearing.TotalDirectories
                    || nextClearing.BytesDeleted < firstClearing.BytesDeleted
                    || nextClearing.FilesDeleted < firstClearing.FilesDeleted
                    || nextClearing.DatasourcesCleared < firstClearing.DatasourcesCleared)
            || before.LogProcessing is { } firstProcessing && after.LogProcessing is { } nextProcessing
                && (nextProcessing.EntriesProcessed < firstProcessing.EntriesProcessed
                    || nextProcessing.LinesProcessed < firstProcessing.LinesProcessed)
            || before.LogRemoval is { } firstLogRemoval && after.LogRemoval is { } nextLogRemoval
                && (nextLogRemoval.FilesProcessed < firstLogRemoval.FilesProcessed
                    || nextLogRemoval.LinesProcessed < firstLogRemoval.LinesProcessed
                    || nextLogRemoval.LinesRemoved < firstLogRemoval.LinesRemoved
                    || nextLogRemoval.DatabaseRecordsDeleted < firstLogRemoval.DatabaseRecordsDeleted)
            || before.Removal is { } firstRemoval && after.Removal is { } nextRemoval
                && (nextRemoval.FilesDeleted < firstRemoval.FilesDeleted
                    || nextRemoval.BytesFreed < firstRemoval.BytesFreed
                    || nextRemoval.FilesProcessed < firstRemoval.FilesProcessed
                    || nextRemoval.TotalFiles < firstRemoval.TotalFiles
                    || nextRemoval.LogEntriesRemoved < firstRemoval.LogEntriesRemoved)
            || before.GameDetection is { } firstDetection && after.GameDetection is { } nextDetection
                && (nextDetection.TotalGamesDetected < firstDetection.TotalGamesDetected
                    || nextDetection.TotalServicesDetected < firstDetection.TotalServicesDetected)
            || before.EvictionScan is { } firstScan && after.EvictionScan is { } nextScan
                && (nextScan.Processed < firstScan.Processed
                    || nextScan.Evicted < firstScan.Evicted
                    || nextScan.UnEvicted < firstScan.UnEvicted)
            || before.EvictionRemoval is { } firstEviction && after.EvictionRemoval is { } nextEviction
                && (nextEviction.DownloadsRemoved < firstEviction.DownloadsRemoved
                    || nextEviction.LogEntriesRemoved < firstEviction.LogEntriesRemoved);
    }

    private static bool SourceScopeEqual(
        OperationType operationType,
        OperationRepairSource left,
        OperationRepairSource right)
    {
        return string.Equals(left.Datasource, right.Datasource, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.LogRoot, right.LogRoot, StringComparison.Ordinal)
            && string.Equals(left.CacheRoot, right.CacheRoot, StringComparison.Ordinal)
            && string.Equals(left.KeyScheme, right.KeyScheme, StringComparison.Ordinal)
            && left.ResetLogPositions == right.ResetLogPositions
            && (left.RefreshDownloads == right.RefreshDownloads
                || operationType == OperationType.LogProcessing
                    && left.RefreshDownloads
                    && !right.RefreshDownloads
                    && right.NativeCompletionAccepted)
            && left.ReconcileCache == right.ReconcileCache
            && left.RefreshDetection == right.RefreshDetection
            && left.InvalidateCorruption == right.InvalidateCorruption
            && left.ApplyCorruptionCandidates == right.ApplyCorruptionCandidates;
    }

    private static bool TargetEqual(CacheRepairTarget? left, CacheRepairTarget? right)
    {
        return (left, right) switch
        {
            (null, null) => true,
            // The Steam removal stores its safe depot set once, after its native step reports it.
            ({ } first, { } second) => first.SteamAppId == second.SteamAppId
                && (first.SteamDepotIds.Count == 0 || first.SteamDepotIds.SequenceEqual(second.SteamDepotIds))
                && string.Equals(first.EpicGame, second.EpicGame, StringComparison.Ordinal)
                && string.Equals(first.GameName, second.GameName, StringComparison.Ordinal)
                && string.Equals(first.Service, second.Service, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool CorruptionEqual(CorruptionRepair? left, CorruptionRepair? right)
    {
        return (left, right) switch
        {
            (null, null) => true,
            ({ } first, { } second) => first.ScanId == second.ScanId
                && first.ContractVersion == second.ContractVersion
                && first.DetectionMethod == second.DetectionMethod
                && string.Equals(first.Service, second.Service, StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool CorruptionCountsEqual(CorruptionRemovalCounts? left, CorruptionRemovalCounts? right)
    {
        return (left, right) switch
        {
            (null, null) => true,
            ({ } first, { } second) => first.UrlsRemoved == second.UrlsRemoved
                && first.FilesDeleted == second.FilesDeleted
                && first.AlreadyMissing == second.AlreadyMissing
                && first.Healed == second.Healed
                && first.BytesFreed == second.BytesFreed,
            _ => false
        };
    }

    private static bool EvictionSelectionEqual(EvictionRemovalMetadata left, EvictionRemovalMetadata right)
    {
        return string.Equals(left.Scope, right.Scope, StringComparison.Ordinal)
            && string.Equals(left.Key, right.Key, StringComparison.Ordinal)
            && string.Equals(left.GameName, right.GameName, StringComparison.Ordinal);
    }

    private static bool NoticeEqual(RunNotice? left, RunNotice? right)
    {
        return (left, right) switch
        {
            (null, null) => true,
            ({ } first, { } second) => first.Mode == second.Mode
                && first.Trigger == second.Trigger
                && first.Actor == second.Actor
                && first.RestoredOrigin == second.RestoredOrigin,
            _ => false
        };
    }

    protected virtual DateTime UtcNow => DateTime.UtcNow;

    protected virtual Task WaitUntilAsync(DateTime retryAtUtc, CancellationToken stoppingToken)
    {
        var remaining = retryAtUtc - UtcNow;
        return remaining > TimeSpan.Zero
            ? Task.Delay(remaining, stoppingToken)
            : Task.CompletedTask;
    }

    private async Task WaitForNativeProcessesAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        if (!repair.Sources.Any(source => source.NativeLaunchAuthorized))
        {
            return;
        }

        // rsync is not this app's binary, and on a pid: host install the name matches host
        // processes, so only a startup repair, which cannot know what its job left, waits for it.
        var processNames = ProcessNames(repair)
            .Where(name => name != "rsync" || _startupRepairs.ContainsKey(repair.Id))
            .ToList();
        // A killed binary stuck in uninterruptible IO must not hold the queue forever.
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        bound.CancelAfter(_repairRetryDelay);
        try
        {
            await _processManager.WaitForProcessesExitAsync(processNames, bound.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Native processes {string.Join(", ", processNames)} did not exit for operation repair {repair.Id}.");
        }
    }

    internal static IReadOnlyCollection<string> ProcessNames(OperationRepair repair)
    {
        return repair.Type switch
        {
            OperationType.CacheClearing => ["cache_clear", "rsync", "cache_eviction_scan"],
            OperationType.EvictionScan => ["cache_game_detect", "cache_eviction_scan"],
            OperationType.EvictionRemoval => ["cache_purge_log_entries", "cache_eviction_scan"],
            OperationType.CorruptionDetection or OperationType.CorruptionRemoval =>
                ["cache_corruption", "cache_eviction_scan"],
            OperationType.GameDetection => ["cache_game_detect"],
            OperationType.LogProcessing => ["log_processor"],
            OperationType.LogRemoval => ["log_service_manager"],
            OperationType.ServiceRemoval =>
                ["cache_service_remove", "cache_purge_log_entries", "cache_eviction_scan"],
            OperationType.GameRemoval => GameRemovalProcessNames(repair),
            _ => []
        };
    }

    // A removal's log step runs cache_purge_log_entries after its cache step.
    private static IReadOnlyCollection<string> GameRemovalProcessNames(OperationRepair repair)
    {
        if (repair.Target?.SteamAppId.HasValue == true)
        {
            return ["cache_steam_remove", "cache_purge_log_entries", "cache_eviction_scan"];
        }
        if (!string.IsNullOrWhiteSpace(repair.Target?.EpicGame))
        {
            return ["cache_epic_remove", "cache_purge_log_entries", "cache_eviction_scan"];
        }
        return repair.Target?.Service?.ToLowerInvariant() switch
        {
            "blizzard" => ["cache_blizzard_remove", "cache_purge_log_entries", "cache_eviction_scan"],
            "riot" => ["cache_riot_remove", "cache_purge_log_entries", "cache_eviction_scan"],
            "xbox" => ["cache_xbox_remove", "cache_purge_log_entries", "cache_eviction_scan"],
            _ => throw new InvalidDataException(
                $"Game removal repair {repair.Id} has no supported native target.")
        };
    }

    protected virtual Task RestoreOwnerAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        return DispatchRestoreRepairAsync(repair, stoppingToken);
    }

    protected virtual Task ApplyRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        return DispatchRepairAsync(repair, stoppingToken);
    }

    private async Task DispatchRestoreRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        switch (repair.Type)
        {
            case OperationType.CacheClearing:
                await services.GetRequiredService<CacheClearingService>()
                    .RestoreRepairAsync(repair, stoppingToken);
                break;
            case OperationType.LogProcessing:
                await services.GetRequiredService<RustLogProcessorService>()
                    .RestoreRepairAsync(repair, stoppingToken);
                break;
            case OperationType.LogRemoval:
                await services.GetRequiredService<RustLogRemovalService>()
                    .RestoreRepairAsync(repair, stoppingToken);
                break;
            case OperationType.GameRemoval:
            case OperationType.ServiceRemoval:
                await services.GetRequiredService<CacheManagementService>()
                    .RestoreRepairAsync(repair, stoppingToken);
                break;
            case OperationType.CorruptionRemoval:
                await services.GetRequiredService<CorruptionDetectionService>()
                    .RestoreRepairAsync(repair, stoppingToken);
                break;
            case OperationType.GameDetection:
                await services.GetRequiredService<GameCacheDetectionService>()
                    .RestoreRepairAsync(repair, stoppingToken);
                break;
            case OperationType.EvictionScan:
            case OperationType.EvictionRemoval:
                await services.GetRequiredService<CacheReconciliationService>()
                    .RestoreRepairAsync(repair, stoppingToken);
                break;
            default:
                throw new InvalidDataException($"Operation type {repair.Type} has no repair owner.");
        }
    }

    private async Task DispatchRepairAsync(OperationRepair repair, CancellationToken stoppingToken)
    {
        // Only this attempt's own steps see the limit; children of other jobs keep running
        // unbounded. It is set inside this async method because the caller ends the repair, and
        // ending it starts queued jobs on tasks that would copy the value.
        RustProcessHelper.ChildSilenceLimit.Value = _repairSilenceLimit;
        await using var scope = _scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        // Steps act on this copy; it is never saved.
        var applied = CopyRepair(repair);

        if (!IsDownloadsRefreshOnly(repair))
        {
            var datasourceService = services.GetRequiredService<DatasourceService>();
            var capabilityService = services.GetRequiredService<DatasourceCapabilityService>();
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            foreach (var source in applied.Sources.Where(source => source.NativeLaunchAuthorized).ToList())
            {
                var current = datasourceService.GetDatasource(source.Datasource);
                if (current is null
                    || (source.LogRoot is not null
                        && !string.Equals(source.LogRoot, current.LogPath, pathComparison))
                    || (source.CacheRoot is not null
                        && !string.Equals(source.CacheRoot, current.CachePath, pathComparison))
                    || (source.KeyScheme is not null
                        && !string.Equals(
                            source.KeyScheme,
                            capabilityService.GetKeySchemeWireValue(current),
                            StringComparison.Ordinal)))
                {
                    // Repairing it now would touch another datasource's files or log, so it abstains
                    // and nothing else waits for it.
                    _logger.LogWarning(
                        "Skipped datasource {Datasource} in operation repair {OperationId} because it was removed or changed after the repair was prepared",
                        source.Datasource,
                        repair.Id);
                    applied.Sources.Remove(source);
                }
            }
        }

        var launchedSources = applied.Sources
            .Where(source => source.NativeLaunchAuthorized)
            .ToList();

        // A log step that started and did not keep its positions is redone by the family resume, so
        // its datasource imports from the start again. Only such a source takes the lock, so a clean
        // repair never pauses the import.
        if (launchedSources.Any(source => source.ResetLogPositions && LogStepUnfinished(source)))
        {
            await using (await LockLogFilesAsync(repair.Id, repair.Type, LogFileLockKind.Rows, stoppingToken))
            {
                // Read again under the lock: an owner marks its source kept before it releases it.
                var appliedNames = launchedSources
                    .Select(source => source.Datasource)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var logProcessor = services.GetRequiredService<RustLogProcessorService>();
                foreach (var source in GetRequiredRepair(repair.Id).Sources
                    .Where(source => source.ResetLogPositions
                        && LogStepUnfinished(source)
                        && appliedNames.Contains(source.Datasource)))
                {
                    logProcessor.ResetLogPosition(source.Datasource);
                    // A cache clear has no log step to redo, so its reset is the whole step and a
                    // retried attempt leaves the positions the import has read since.
                    if (repair.Type == OperationType.CacheClearing)
                    {
                        await MarkLogPositionsKeptAsync(repair.Id, source.Datasource);
                    }
                }
            }
        }

        // A clean in-session removal changed only what its own steps recorded, so the repair
        // refreshes that and skips the full scan; a startup repair cannot prove what happened and
        // keeps it; a structural corruption removal cannot tell which downloads lost their last
        // file, and a cache clear can tell only when no download touched its datasources while it ran.
        var skipsCacheScan = (applied.Type is OperationType.GameRemoval
                    or OperationType.ServiceRemoval
                    or OperationType.CacheClearing
                || applied.Type == OperationType.CorruptionRemoval
                    && applied.Corruption?.DetectionMethod != CorruptionDetectionMethod.Structural)
            && !_startupRepairs.ContainsKey(applied.Id)
            && applied.Outcome == OperationStatus.Completed
            && launchedSources.All(source => source.NativeCompletionAccepted
                && (!source.ResetLogPositions || source.LogPositionsKept));
        if (skipsCacheScan && applied.Type == OperationType.CacheClearing)
        {
            // A cache clear knows what it deleted only when nothing downloaded into its roots while it ran:
            // a download that ended during the clear may have lost its files after the walk passed, and one
            // that refilled a cleared directory makes older rows cached again. Zero-byte rows never change
            // eviction state, so only byte-backed finished rows count; a running row counts at any size.
            var clearedNames = launchedSources
                .Select(source => source.Datasource.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var clearSawTraffic = await services.GetRequiredService<AppDbContext>().Downloads
                .AnyAsync(download =>
                    clearedNames.Contains(download.Datasource.ToLower())
                    && (download.IsActive
                        || (download.EndTimeUtc >= applied.StartedAt
                            && (download.CacheHitBytes > 0 || download.CacheMissBytes > 0))),
                    stoppingToken);
            skipsCacheScan = !clearSawTraffic;
        }

        try
        {
            switch (applied.Type)
            {
                case OperationType.CacheClearing:
                    await services.GetRequiredService<CacheClearingService>()
                        .ResumeRepairAsync(applied, stoppingToken);
                    await services.GetRequiredService<CacheReconciliationService>()
                        .EvictClearedSourcesAsync(applied, skipsCacheScan, stoppingToken);
                    break;
                case OperationType.LogProcessing:
                    await services.GetRequiredService<RustLogProcessorService>()
                        .ResumeRepairAsync(applied, stoppingToken);
                    break;
                case OperationType.LogRemoval:
                    await services.GetRequiredService<RustLogRemovalService>()
                        .ResumeRepairAsync(applied, stoppingToken);
                    break;
                case OperationType.GameRemoval:
                case OperationType.ServiceRemoval:
                    await services.GetRequiredService<CacheManagementService>()
                        .ResumeRepairAsync(applied, stoppingToken);
                    break;
                case OperationType.CorruptionRemoval:
                    await services.GetRequiredService<CorruptionDetectionService>()
                        .ResumeRepairAsync(applied, stoppingToken);
                    break;
                case OperationType.GameDetection:
                    await services.GetRequiredService<GameCacheDetectionService>()
                        .ResumeRepairAsync(applied, stoppingToken);
                    break;
                case OperationType.EvictionScan:
                case OperationType.EvictionRemoval:
                    await services.GetRequiredService<CacheReconciliationService>()
                        .ResumeRepairAsync(applied, stoppingToken);
                    break;
                default:
                    throw new InvalidDataException($"Operation type {applied.Type} has no repair owner.");
            }
        }
        finally
        {
            // Also after a failed attempt: a redo that rewrote one datasource's log before another
            // failed changed the counts the Log Removal panel shows.
            if (launchedSources.Any(source => source.ResetLogPositions))
            {
                // The count program recounts once a log file is newer than its saved counts, so failing
                // to clear them must not replace the repair's own error.
                try
                {
                    await services.GetRequiredService<CacheManagementService>()
                        .InvalidateServiceCountsAsync();
                }
                catch (Exception countsException)
                {
                    _logger.LogError(
                        countsException,
                        "Failed to invalidate the service counts after the repair of operation {OperationId}",
                        repair.Id);
                }
            }
        }

        // Only sources with a key scheme can be scanned (ReconcileRepairAsync drops the rest).
        var scannedSources = applied.Type is (OperationType.CacheClearing
                or OperationType.GameRemoval
                or OperationType.ServiceRemoval
                or OperationType.CorruptionRemoval)
            && !skipsCacheScan
            ? launchedSources.Where(source => source.ReconcileCache && source.KeyScheme is not null).ToList()
            : [];
        if (scannedSources.Count > 0)
        {
            await services.GetRequiredService<CacheReconciliationService>()
                .ReconcileRepairAsync(applied, stoppingToken);
        }
        var unscannedSources = launchedSources.Except(scannedSources).ToList();

        var evictionTailOwned = applied.Type is OperationType.EvictionScan or OperationType.EvictionRemoval;
        var projectionsChanged = false;
        if (!evictionTailOwned && unscannedSources.Any(source => source.InvalidateCorruption))
        {
            await services.GetRequiredService<CorruptionDetectionService>()
                .InvalidateRepairAsync(applied, stoppingToken);
            projectionsChanged = true;
        }

        // Game detection owns only its own summary, which a completed run already refreshed; no
        // detection clears the cache-file scan the Cache Files card shows.
        var refreshDetection = applied.Type == OperationType.GameDetection
            ? applied.DatabaseWriteStarted && applied.Outcome != OperationStatus.Completed
            : unscannedSources.Any(source => source.RefreshDetection);
        if (!evictionTailOwned && refreshDetection)
        {
            await services.GetRequiredService<GameCacheDetectionService>()
                .RefreshDiskSummaryAndInvalidateAsync(stoppingToken);
            if (applied.Type != OperationType.GameDetection)
            {
                services.GetRequiredService<CacheManagementService>().InvalidateCachedScan();
            }
            projectionsChanged = true;
        }

        // The dashboard and download views refetch once after the repair changed what they show.
        if (projectionsChanged || NeedsDownloadsRefresh(applied))
        {
            await services.GetRequiredService<ISignalRNotificationService>()
                .NotifyAllAsync(SignalREvents.DownloadsRefresh);
        }
    }

    // A log step that started and did not keep its positions is always finished by the repair.
    internal static bool LogStepUnfinished(OperationRepairSource source) =>
        source.LogRewriteStarted && !source.LogPositionsKept;

    // A crash or failure after a cache step also rolls its log step forward; a cancel leaves the history.
    internal static bool NeedsLogStepRedo(OperationRepair repair, OperationRepairSource source) =>
        LogStepUnfinished(source)
        || source.NativeCompletionAccepted
            && !source.LogRewriteStarted
            && repair.Outcome != OperationStatus.Cancelled;

    private static bool IsDownloadsRefreshOnly(OperationRepair repair)
    {
        return repair.Type == OperationType.LogProcessing
            && repair.LogProcessing is not null
            && !repair.EvictionScanId.HasValue
            && repair.Sources
                .Where(source => source.NativeLaunchAuthorized)
                .All(source => source.ReceiptPath is null
                    && !source.ResetLogPositions
                    && !source.ReconcileCache
                    && !source.RefreshDetection
                    && !source.InvalidateCorruption
                    && !source.ApplyCorruptionCandidates);
    }

    internal static bool NeedsDownloadsRefresh(OperationRepair repair)
    {
        var refreshRequested = repair.Sources.Any(
                source => source.NativeLaunchAuthorized && source.RefreshDownloads)
            || (repair.Type is OperationType.EvictionRemoval or OperationType.CacheClearing
                && repair.Sources.Any(source => source.RefreshDownloads));
        return refreshRequested
            && repair.Type != OperationType.EvictionScan;
    }

    private void CompleteRestoredRepair(Guid operationId)
    {
        var repair = GetRequiredRepair(operationId);
        if (_operationTracker.GetOperation(operationId) is null)
        {
            return;
        }

        var success = repair.Outcome == OperationStatus.Completed;
        var cancelled = repair.Outcome == OperationStatus.Cancelled;
        _operationTracker.CompleteOperation(
            operationId,
            success,
            repair.Error,
            cancelled);
    }
}
