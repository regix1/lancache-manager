using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    private static readonly TimeSpan _imageCheckInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _imagePullLock = new(1, 1);
    private readonly object _imageStateLock = new();
    private DateTime _nextImageCheckAtUtc = DateTime.MinValue;
    private string? _desiredImageName;
    private string? _desiredImageId;
    private string? _startupImageId;
    private PersistentImageChange? _pendingImageChange;

    internal bool HasPersistentImageWork
    {
        get
        {
            lock (_imageStateLock)
            {
                return _pendingImageChange is not null || GetActivePersistentSession() is not null;
            }
        }
    }

    private string ResolveImageName()
    {
        var configured = GetImageName();
        if (configured.Contains('@', StringComparison.Ordinal))
        {
            return configured;
        }

        var lastSlash = configured.LastIndexOf('/');
        var lastColon = configured.LastIndexOf(':');
        return lastColon > lastSlash ? configured : configured + ":latest";
    }

    private async Task<string?> EnsureImageExistsAsync(CancellationToken cancellationToken)
    {
        if (!_containerGateway.IsAvailable)
        {
            return null;
        }

        await _imagePullLock.WaitAsync(cancellationToken);
        try
        {
            var imageName = ResolveImageName();
            var nowUtc = DateTime.UtcNow;
            bool pullDue;
            lock (_imageStateLock)
            {
                pullDue = !string.Equals(_desiredImageName, imageName, StringComparison.Ordinal)
                    || nowUtc >= _nextImageCheckAtUtc;
                if (pullDue)
                {
                    _nextImageCheckAtUtc = nowUtc.Add(_imageCheckInterval);
                }
            }

            if (pullDue)
            {
                _logger.LogInformation("Pulling prefill daemon image: {ImageName}", imageName);
                try
                {
                    const int maxAttempts = 3;
                    for (var attempt = 1; ; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            await _containerGateway.CreateImageAsync(
                                new ImagesCreateParameters { FromImage = imageName },
                                null,
                                new Progress<JSONMessage>(message =>
                                {
                                    if (!string.IsNullOrEmpty(message.Status)
                                        && (message.Status.Contains("Pulling", StringComparison.Ordinal)
                                            || message.Status.Contains("Downloaded", StringComparison.Ordinal)
                                            || message.Status.Contains("up to date", StringComparison.Ordinal)))
                                    {
                                        _logger.LogInformation("Pull: {Status}", message.Status);
                                    }
                                    if (!string.IsNullOrEmpty(message.ErrorMessage))
                                    {
                                        _logger.LogError("Pull error: {Error}", message.ErrorMessage);
                                    }
                                }),
                                cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            break;
                        }
                        catch (Exception ex) when (attempt < maxAttempts && IsImagePullTransient(ex))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            _logger.LogWarning(ex,
                                "Prefill image pull failed for {ImageName}; retrying in {DelaySeconds}s (attempt {Attempt}/{MaxAttempts})",
                                imageName, attempt, attempt, maxAttempts);
                            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var cached = await _containerGateway.InspectImageAsync(imageName, cancellationToken);
                        if (string.IsNullOrWhiteSpace(cached.ID))
                        {
                            throw new InvalidOperationException($"Docker returned no immutable image ID for {imageName}.");
                        }
                        _logger.LogWarning(ex,
                            "Failed to pull latest image, using cached version: {ImageId}",
                            ShortContainerId(cached.ID));
                        StoreDesiredImage(imageName, cached.ID);
                        return cached.ID;
                    }
                    catch (DockerImageNotFoundException)
                    {
                        _logger.LogError(ex,
                            "Failed to pull image {ImageName} and no cached version is available. The {ServiceName} Prefill feature requires this image.",
                            imageName, ServiceName);
                        throw;
                    }
                }
            }

            var image = await _containerGateway.InspectImageAsync(imageName, cancellationToken);
            if (string.IsNullOrWhiteSpace(image.ID))
            {
                throw new InvalidOperationException($"Docker returned no immutable image ID for {imageName}.");
            }

            StoreDesiredImage(imageName, image.ID);
            _logger.LogInformation("Image ready: {ImageName} (ID: {ImageId})", imageName, ShortContainerId(image.ID));
            return image.ID;
        }
        finally
        {
            _imagePullLock.Release();
        }
    }

    private void StoreDesiredImage(string imageName, string imageId)
    {
        lock (_imageStateLock)
        {
            _desiredImageName = imageName;
            _desiredImageId = imageId;
        }
    }

    internal async Task ReconcilePersistentImageAsync(CancellationToken cancellationToken)
    {
        if (!HasPersistentImageWork || !_containerGateway.IsAvailable)
        {
            return;
        }

        string? desiredImageId;
        try
        {
            desiredImageId = await EnsureImageExistsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Persistent {ServiceName} image check failed; the current container remains available",
                ServiceName);
            return;
        }

        if (string.IsNullOrWhiteSpace(desiredImageId))
        {
            return;
        }

        lock (_imageStateLock)
        {
            if (!string.Equals(_desiredImageId, desiredImageId, StringComparison.Ordinal))
            {
                return;
            }
        }

        await ReconcilePersistentImageAsync(ResolveImageName(), desiredImageId, cancellationToken);
    }

    private async Task ReconcilePersistentImageAsync(
        string configuredImage,
        string desiredImageId,
        CancellationToken cancellationToken)
    {
        await _persistentStartLock.WaitAsync(cancellationToken);
        try
        {
            await ReconcilePersistentImageUnderStartLockAsync(configuredImage, desiredImageId, cancellationToken);
        }
        finally
        {
            _persistentStartLock.Release();
        }
    }

    private async Task ReconcilePersistentImageUnderStartLockAsync(
        string configuredImage,
        string desiredImageId,
        CancellationToken cancellationToken)
    {
        var pending = GetPendingImageChange();
        if (pending is not null)
        {
            await ResumeImageChangeAsync(pending, cancellationToken);
            return;
        }

        var session = GetActivePersistentSession();
        if (session is null)
        {
            return;
        }

        if (string.Equals(session.ImageId, desiredImageId, StringComparison.Ordinal))
        {
            lock (session.PrefillLock)
            {
                if (IsSessionLive(session)
                    && string.Equals(session.ImageId, desiredImageId, StringComparison.Ordinal)
                    && string.Equals(ResolveImageName(), configuredImage, StringComparison.Ordinal))
                {
                    session.ConfiguredImage = configuredImage;
                }
            }
            return;
        }

        await TryReplacePersistentImageAsync(session, configuredImage, desiredImageId, cancellationToken);
    }

    private PersistentImageChange? GetPendingImageChange()
    {
        lock (_imageStateLock)
        {
            return _pendingImageChange;
        }
    }

    private void SetPendingImageChange(PersistentImageChange change)
    {
        lock (_imageStateLock)
        {
            _pendingImageChange = change;
        }
    }

    private void ClearPendingImageChange(PersistentImageChange change)
    {
        lock (_imageStateLock)
        {
            if (ReferenceEquals(_pendingImageChange, change))
            {
                _pendingImageChange = null;
            }
        }
    }

    private bool CanReplacePersistentImage(DaemonSession session, IDaemonClient client)
    {
        return IsSessionLive(session)
            && ReferenceEquals(session.Client, client)
            && session.IsPersistent
            && session.Status == DaemonSessionStatus.Active
            && !_stopping
            && !session.CancellationTokenSource.IsCancellationRequested
            && !session.AdmissionClosed
            && !PersistentEditSessionGate.HasPendingStart()
            && !session.Recovering
            && !session.IsPrefilling
            && session.TerminalCompletedFlag != 1
            && session.Runs.Values.All(run => run.TerminalCompletedFlag == 2 && !run.AdmissionPending)
            && session.PendingLoginChallenge is null
            && session.LoginOperationId is null
            && session.AuthState != DaemonAuthState.LoggingIn
            && session.LoginSettled
            && !session.SuppressLoginChallengePublication;
    }

    private async Task TryReplacePersistentImageAsync(
        DaemonSession session,
        string configuredImage,
        string desiredImageId,
        CancellationToken cancellationToken)
    {
        if (!PersistentEditSessionGate.TryEnterMutation(out var editLease) || editLease is null)
        {
            return;
        }

        var loginLease = false;
        var recoveryLease = false;
        var prefillLease = false;
        var admissionClaimed = false;
        var client = session.Client;
        try
        {
            loginLease = session.LoginLock.Wait(0);
            if (!loginLease) return;
            recoveryLease = session.RecoveryWork.Wait(0);
            if (!recoveryLease) return;
            prefillLease = session.PrefillWork.Wait(0);
            if (!prefillLease) return;

            lock (session.PrefillLock)
            {
                if (!CanReplacePersistentImage(session, client))
                {
                    return;
                }
                session.AdmissionClosed = true;
                admissionClaimed = true;
            }

            var status = await client.GetStatusAsync(cancellationToken);
            var inspect = await _containerGateway.InspectContainerAsync(session.ContainerId, cancellationToken);
            lock (session.PrefillLock)
            {
                var sameGeneration = status?.SupportsConcurrentPrefill == true
                    && session.Capabilities?.SupportsConcurrentPrefill == true
                    && string.Equals(status.DaemonInstanceId, session.Capabilities.DaemonInstanceId, StringComparison.Ordinal)
                    && status.ActiveOperations is { Count: 0 };
                var sameImageDecision = string.Equals(ResolveImageName(), configuredImage, StringComparison.Ordinal)
                    && string.Equals(session.ConfiguredImage, configuredImage, StringComparison.Ordinal)
                    && string.Equals(inspect.Image, session.ImageId, StringComparison.Ordinal)
                    && !string.Equals(inspect.Image, desiredImageId, StringComparison.Ordinal);
                if (!admissionClaimed
                    || !IsSessionLive(session)
                    || !ReferenceEquals(session.Client, client)
                    || session.Status != DaemonSessionStatus.Active
                    || _stopping
                    || session.CancellationTokenSource.IsCancellationRequested
                    || session.Recovering
                    || session.IsPrefilling
                    || session.TerminalCompletedFlag == 1
                    || session.Runs.Values.Any(run => run.TerminalCompletedFlag != 2 || run.AdmissionPending)
                    || session.PendingLoginChallenge is not null
                    || session.LoginOperationId is not null
                    || session.AuthState == DaemonAuthState.LoggingIn
                    || !session.LoginSettled
                    || !sameGeneration
                    || !sameImageDecision)
                {
                    session.AdmissionClosed = false;
                    admissionClaimed = false;
                    return;
                }
            }

            var change = new PersistentImageChange
            {
                Session = session,
                ContainerId = session.ContainerId,
                ConfiguredImage = configuredImage,
                DesiredImageId = desiredImageId,
                ExpiresAt = session.ExpiresAt,
                Authenticated = session.AuthState == DaemonAuthState.Authenticated,
                NeedsRelogin = session.NeedsRelogin
            };

            var termination = TryStartImageTermination(change);
            if (!termination.Claimed)
            {
                lock (session.PrefillLock)
                {
                    if (IsSessionLive(session) && ReferenceEquals(session.Client, client) && session.AdmissionClosed)
                    {
                        session.AdmissionClosed = false;
                    }
                }
                admissionClaimed = false;
                return;
            }

            SetPendingImageChange(change);
            admissionClaimed = false;
            session.PrefillWork.Release();
            prefillLease = false;
            session.RecoveryWork.Release();
            recoveryLease = false;
            session.LoginLock.Release();
            loginLease = false;
            await editLease.DisposeAsync();
            editLease = null;

            await termination.Work;
            await ResumeImageChangeAsync(change, cancellationToken);
        }
        finally
        {
            if (admissionClaimed)
            {
                lock (session.PrefillLock)
                {
                    if (IsSessionLive(session) && ReferenceEquals(session.Client, client) && session.AdmissionClosed)
                    {
                        session.AdmissionClosed = false;
                    }
                }
            }
            if (prefillLease) session.PrefillWork.Release();
            if (recoveryLease) session.RecoveryWork.Release();
            if (loginLease) session.LoginLock.Release();
            if (editLease is not null) await editLease.DisposeAsync();
        }
    }

    private async Task ResumeImageChangeAsync(PersistentImageChange change, CancellationToken cancellationToken)
    {
        if (change.Cancelled)
        {
            return;
        }

        if (!change.ContainerRemoved)
        {
            var termination = TryStartImageTermination(change);
            if (!termination.Claimed)
            {
                return;
            }
            await termination.Work;
        }

        DaemonSession? successor = null;
        if (!string.IsNullOrEmpty(change.CreatedSessionId))
        {
            successor = GetSession(change.CreatedSessionId);
        }

        if (successor is null)
        {
            successor = await CreateSessionCoreAsync(
                ScheduledPrefillConstants.DeriveSystemUserId(),
                ipAddress: null,
                userAgent: null,
                SessionType.Admin,
                isPersistent: true,
                reuseExistingSession: true,
                persistentExpiresAtOverrideUtc: change.ExpiresAt,
                involuntaryRecreate: false,
                cancellationToken,
                persistentContainerCreated: (sessionId, containerId) =>
                {
                    change.CreatedSessionId = sessionId;
                    change.CreatedContainerId = containerId;
                },
                imageChange: change,
                resolvedImageId: change.DesiredImageId);
        }

        change.CreatedSessionId = successor.Id;
        change.CreatedContainerId = successor.ContainerId;
        if (!change.OldSessionRetired)
        {
            await _sessionService.TerminateSessionAsync(
                change.Session.Id,
                "Persistent container image updated",
                terminatedBy: "system");
            await NotifyHubAsync(
                EventSessionTerminated,
                new { sessionId = change.Session.Id, reason = "Persistent container image updated" });
            foreach (var connection in change.Session.SubscribedConnections.ToArray())
            {
                await SendToClientAsync(
                    connection,
                    EventSessionEnded,
                    new { sessionId = change.Session.Id, reason = "Persistent container image updated" });
            }
            change.OldSessionRetired = true;
        }
        ClearPendingImageChange(change);
    }

    private async Task CancelPendingImageChangeAsync(
        PersistentImageChange change,
        string? terminatedBy,
        CancellationToken cancellationToken)
    {
        change.Cancelled = true;
        var successor = string.IsNullOrEmpty(change.CreatedSessionId) ? null : GetSession(change.CreatedSessionId);
        if (successor is null)
        {
            if (!change.ContainerRemoved)
            {
                await TryBestEffortLogoutAsync(change.Session, "pending image update cancelled by explicit stop");
            }

            if (!change.ContainerRemoved && !string.IsNullOrEmpty(change.ContainerId))
            {
                var removal = await RemoveContainerForceAsync(change.ContainerId, cancellationToken, removeVolumes: false);
                change.ContainerRemoved = removal is ContainerRemovalOutcome.Removed or ContainerRemovalOutcome.AlreadyAbsent;
            }

            if (!string.IsNullOrEmpty(change.CreatedContainerId))
            {
                var removal = await RemoveContainerForceAsync(change.CreatedContainerId, cancellationToken, removeVolumes: false);
                if (removal == ContainerRemovalOutcome.RemovalInProgress)
                {
                    throw new InvalidOperationException("The pending replacement container is still being removed.");
                }
            }

            if (!change.ContainerRemoved)
            {
                throw new InvalidOperationException("The previous persistent container is still being removed.");
            }

            var volumeResult = await ClearPersistentAuthVolumeAsync(cancellationToken);
            if (volumeResult is PersistentVolumeClearResult.InUse or PersistentVolumeClearResult.DockerUnavailable)
            {
                throw new InvalidOperationException("The persistent login volume could not be cleared while cancelling the pending image update.");
            }
        }

        if (!change.OldSessionRetired)
        {
            await _sessionService.TerminateSessionAsync(
                change.Session.Id,
                "Persistent session stopped",
                terminatedBy);
            change.OldSessionRetired = true;
        }
        ClearPendingImageChange(change);
    }
}
