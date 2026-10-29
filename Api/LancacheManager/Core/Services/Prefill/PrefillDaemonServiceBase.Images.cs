using Docker.DotNet;
using Docker.DotNet.Models;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Middleware;

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
                    // A canceled pull, like one that leaves no image to start from below, is due again on
                    // the next Start rather than after the check interval.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        lock (_imageStateLock) _nextImageCheckAtUtc = nowUtc;
                    }
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
                        lock (_imageStateLock) _nextImageCheckAtUtc = nowUtc;
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
            && !session.Recovering
            && session.Runs.Values.All(run => run.TerminalCompletedFlag == 2 && !run.AdmissionPending)
            && session.PendingLoginChallenge is null
            && session.LoginOperationId is null
            && session.AuthState != DaemonAuthState.LoggingIn
            && session.LoginSettled
            && !session.SuppressLoginChallengePublication
            // A sign-in cancel still waiting for the daemon decides whether the sign-in ended; the container stays until it does.
            && !session.LoginCanceling;
    }

    private async Task TryReplacePersistentImageAsync(
        DaemonSession session,
        string configuredImage,
        string desiredImageId,
        CancellationToken cancellationToken)
    {
        if (!PersistentChangeGate.TryEnterChange(out var gateLease) || gateLease is null)
        {
            return;
        }

        var loginLease = false;
        var recoveryLease = false;
        var admissionClaimed = false;
        var client = session.Client;
        try
        {
            loginLease = session.LoginLock.Wait(0);
            if (!loginLease) return;
            recoveryLease = session.RecoveryWork.Wait(0);
            if (!recoveryLease) return;

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
                    || session.Runs.Values.Any(run => run.TerminalCompletedFlag != 2 || run.AdmissionPending)
                    || session.PendingLoginChallenge is not null
                    || session.LoginOperationId is not null
                    || session.AuthState == DaemonAuthState.LoggingIn
                    || !session.LoginSettled
                    || session.LoginCanceling
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
                LastLoginAt = session.LastLoginAt,
                Authenticated = session.AuthState == DaemonAuthState.Authenticated,
                SigningInAgain = session.SignInAgainAfterDrop is { IsCompleted: false },
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
            session.RecoveryWork.Release();
            recoveryLease = false;
            session.LoginLock.Release();
            loginLease = false;
            await gateLease.DisposeAsync();
            gateLease = null;

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
            if (recoveryLease) session.RecoveryWork.Release();
            if (loginLease) session.LoginLock.Release();
            if (gateLease is not null) await gateLease.DisposeAsync();
        }
    }

    private async Task ResumeImageChangeAsync(PersistentImageChange change, CancellationToken cancellationToken)
    {
        if (change.Cancelled)
        {
            // A person's Stop that Docker could not finish is finished here; the change stores no person.
            await CancelPendingImageChangeAsync(change, terminatedBy: "system", cancellationToken);
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
            try
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
                    persistentContainerCreated: async (sessionId, containerId, containerName) =>
                    {
                        var reusedAttempt = string.Equals(sessionId, change.CreatedSessionId, StringComparison.Ordinal);
                        change.CreatedSessionId = sessionId;
                        change.CreatedContainerId = containerId;
                        // The row is written with the stored expiry and sign-in time before the new daemon connects, so a restart
                        // before it registers re-adopts the container as a reconnect, and outside KillOnRestart
                        // that re-adopt signs the new daemon in from the saved login while the date is ahead. An attempt
                        // that reuses the earlier attempt's id finds that row, whose sign-in time came from the same change.
                        if (reusedAttempt)
                        {
                            await _sessionService.ReactivateSessionAsync(
                                sessionId,
                                ScheduledPrefillConstants.DeriveSystemUserId(),
                                containerId,
                                containerName,
                                change.ExpiresAt,
                                ServiceName);
                        }
                        else
                        {
                            await _sessionService.CreateSessionAsync(
                                sessionId,
                                ScheduledPrefillConstants.DeriveSystemUserId(),
                                containerId,
                                containerName,
                                change.ExpiresAt,
                                change.LastLoginAt,
                                ServiceName);
                        }
                        // A person's Cancel of the old session's sign-in, and a log-off of its container, are stored on
                        // the new row too, so a restart before the new daemon registers keeps the new container signed out.
                        bool canceled;
                        bool loggedInWhileCanceled;
                        bool signedOut;
                        lock (change.Session.PrefillLock)
                        {
                            canceled = change.Session.LoginCanceledByPerson;
                            loggedInWhileCanceled = change.Session.LoggedInWhilePersonCanceled;
                            signedOut = change.Session.SignedOut;
                        }
                        if (canceled || signedOut)
                        {
                            await _sessionService.SetLoginStateAsync(sessionId, canceled, loggedInWhileCanceled, signedOut,
                                change.LastLoginAt, change.ExpiresAt);
                        }
                    },
                    imageChange: change,
                    resolvedImageId: change.DesiredImageId,
                    persistentLastLoginAtUtc: change.LastLoginAt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The old container is gone, so its row no longer reads Active while the update waits for the next
                // pass. Orphaned keeps the login for that pass, and a person's Stop still ends it.
                if (!change.OldSessionRetired)
                {
                    await _sessionService.MarkOrphanedAsync(change.Session.Id);
                }
                throw;
            }
        }

        change.CreatedSessionId = successor.Id;
        change.CreatedContainerId = successor.ContainerId;
        if (!change.OldSessionRetired)
        {
            await _sessionService.TerminateSessionAsync(
                change.Session.Id,
                "Persistent container image updated",
                terminatedBy: "system");
            // The owner's tabs get SessionEnded just below, so only the account holders need this copy.
            await _notifications.NotifyAdminAsync(
                EventSessionTerminated,
                new { sessionId = change.Session.Id, reason = "Persistent container image updated" });
            string[] subscribers;
            lock (change.Session.PrefillLock) subscribers = change.Session.SubscribedConnections.ToArray();
            foreach (var connection in subscribers)
            {
                await SendToClientAsync(
                    connection,
                    EventSessionEnded,
                    new { sessionId = change.Session.Id, reason = "Persistent container image updated", reasonKey = ImageUpdatedReasonKey });
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
        // The person's Stop is recorded before Docker is asked, so a failed removal followed by a restart
        // does not bring the container back.
        if (!change.OldSessionRetired)
        {
            // A canceled pass left the old row Orphaned.
            await _sessionService.TerminateSessionAsync(
                change.Session.Id,
                "Persistent session stopped",
                terminatedBy,
                replaceOrphaned: true);
        }
        var successor = string.IsNullOrEmpty(change.CreatedSessionId) ? null : GetSession(change.CreatedSessionId);
        if (successor is null)
        {
            // The new container's row ends with this Stop too, so neither a restart nor a FullPersistence
            // recreate brings it back with the login. It reads Orphaned when the attempt it names was
            // replaced and the next create failed before writing its own row.
            if (!string.IsNullOrEmpty(change.CreatedSessionId))
            {
                await _sessionService.TerminateSessionAsync(
                    change.CreatedSessionId,
                    "Persistent session stopped",
                    terminatedBy,
                    replaceOrphaned: true);
            }

            if (!change.ContainerRemoved)
            {
                await TryBestEffortLogoutAsync(change.Session, "pending image update cancelled by explicit stop");
            }

            // The image teardown removes the old container and, on success, releases the old client and its
            // teardown entry.
            if (!change.ContainerRemoved)
            {
                await TryStartImageTermination(change).Work;
            }

            if (!string.IsNullOrEmpty(change.CreatedContainerId))
            {
                var removal = await RemoveContainerForceAsync(change.CreatedContainerId, cancellationToken);
                if (removal == ContainerRemovalOutcome.RemovalInProgress)
                {
                    throw ContainerRemovalFailure();
                }
            }

            if (!change.ContainerRemoved)
            {
                throw ContainerRemovalFailure();
            }

            var volumeResult = await ClearPersistentAuthVolumeAsync(cancellationToken);
            if (volumeResult == PersistentVolumeClearResult.DockerUnavailable)
            {
                throw new ServiceUnavailableException("Docker did not answer while clearing the persistent login volume.")
                {
                    StageKey = "errors.prefill.dockerUnavailable"
                };
            }
            if (volumeResult == PersistentVolumeClearResult.InUse)
            {
                throw new InvalidOperationException("The persistent login volume could not be cleared while cancelling the pending image update.");
            }
        }

        if (!change.OldSessionRetired)
        {
            await _notifications.NotifyAdminAsync(
                EventSessionTerminated,
                new { sessionId = change.Session.Id, reason = "Persistent session stopped" });
            change.OldSessionRetired = true;
        }
        ClearPendingImageChange(change);
    }
}
