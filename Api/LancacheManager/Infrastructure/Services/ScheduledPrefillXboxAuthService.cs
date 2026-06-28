using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;
using LancacheManager.Services.Xbox;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Xbox MSA device-code login for scheduled prefill only. Polls Microsoft for approval,
/// resolves account identity, and stores credentials in the isolated scheduled prefill Xbox store
/// (no catalog harvest or mapping merge).
/// </summary>
public sealed class ScheduledPrefillXboxAuthService
{
    private readonly XboxAuthClient _authClient;
    private readonly IScheduledPrefillXboxAuthStorageService _storage;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<ScheduledPrefillXboxAuthService> _logger;
    private readonly SemaphoreSlim _loginStartLock = new(1, 1);
    private CancellationTokenSource? _loginPollCts;

    public ScheduledPrefillXboxAuthService(
        XboxAuthClient authClient,
        IScheduledPrefillXboxAuthStorageService storage,
        ISignalRNotificationService notifications,
        ILogger<ScheduledPrefillXboxAuthService> logger)
    {
        _authClient = authClient;
        _storage = storage;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<XboxDeviceCodeChallenge> StartLoginAsync(CancellationToken ct = default)
    {
        await _loginStartLock.WaitAsync(ct);
        try
        {
            try
            {
                _loginPollCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Old poll already finished.
            }

            var pollCts = new CancellationTokenSource();
            _loginPollCts = pollCts;

            try
            {
                XboxDeviceCodeResponse deviceCode;
                using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(pollCts.Token, ct))
                {
                    deviceCode = await _authClient.RequestDeviceCodeAsync(requestCts.Token);
                }

                var authData = _storage.GetAuthData();
                var signer = !string.IsNullOrEmpty(authData.DeviceKeyPkcs8)
                    ? XblRequestSigner.FromPkcs8Base64(authData.DeviceKeyPkcs8)
                    : XblRequestSigner.CreateNew();

                var operationId = Guid.NewGuid();
                _ = Task.Run(() => RunLoginPollAsync(deviceCode, signer, operationId, pollCts), CancellationToken.None);

                return new XboxDeviceCodeChallenge
                {
                    UserCode = deviceCode.UserCode ?? string.Empty,
                    VerificationUri = deviceCode.VerificationUri ?? string.Empty,
                    ExpiresIn = deviceCode.ExpiresIn,
                    Interval = deviceCode.Interval,
                    OperationId = operationId
                };
            }
            catch
            {
                if (ReferenceEquals(_loginPollCts, pollCts))
                {
                    _loginPollCts = null;
                }
                pollCts.Dispose();
                throw;
            }
        }
        finally
        {
            _loginStartLock.Release();
        }
    }

    public void CancelLogin()
    {
        try
        {
            _loginPollCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Poll loop already finished.
        }
    }

    private async Task RunLoginPollAsync(
        XboxDeviceCodeResponse deviceCode,
        XblRequestSigner signer,
        Guid operationId,
        CancellationTokenSource pollCts)
    {
        var ct = pollCts.Token;
        try
        {
            await EmitProgressAsync(operationId, "signalr.scheduledPrefill.xbox.authenticating", 10,
                "Waiting for Microsoft sign-in...");

            var msaToken = await _authClient.PollForTokenAsync(deviceCode, ct);

            await EmitProgressAsync(operationId, "signalr.scheduledPrefill.xbox.collecting", 60,
                "Confirming Xbox account...");

            var identity = await _authClient.GetAccountIdentityAsync(msaToken.AccessToken!, signer, ct);

            ct.ThrowIfCancellationRequested();

            _storage.SaveAuthData(new XboxAuthData
            {
                RefreshToken = msaToken.RefreshToken,
                DeviceKeyPkcs8 = signer.ExportPkcs8Base64(),
                DisplayName = identity.DisplayName,
                Xuid = identity.Xuid,
                LastAuthenticated = DateTime.UtcNow,
                GamesDiscovered = 0
            });

            await EmitTerminalAsync(
                operationId,
                success: true,
                cancelled: false,
                stageKey: "signalr.scheduledPrefill.xbox.completed",
                message: "Scheduled prefill Xbox login complete",
                error: null);

            _logger.LogInformation(
                "Scheduled prefill Xbox authentication saved for user: {DisplayName}",
                identity.DisplayName);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Scheduled prefill Xbox login cancelled");
            await EmitTerminalAsync(
                operationId,
                success: false,
                cancelled: true,
                stageKey: "signalr.scheduledPrefill.xbox.cancelled",
                message: "Xbox login cancelled",
                error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled prefill Xbox login failed");
            await EmitTerminalAsync(
                operationId,
                success: false,
                cancelled: false,
                stageKey: "signalr.scheduledPrefill.xbox.failed",
                message: "Xbox login failed",
                error: ex.Message);
        }
        finally
        {
            signer.Dispose();
            if (ReferenceEquals(_loginPollCts, pollCts))
            {
                _loginPollCts = null;
            }
            pollCts.Dispose();
        }
    }

    private async Task EmitProgressAsync(Guid operationId, string stageKey, double percentComplete, string message)
    {
        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillXboxAuthProgress, new
            {
                operationId,
                success = false,
                status = OperationStatus.Running,
                stageKey,
                percentComplete,
                gamesDiscovered = 0,
                cancelled = false,
                error = (string?)null,
                message,
                isTerminal = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit scheduled prefill Xbox auth progress ({StageKey})", stageKey);
        }
    }

    private async Task EmitTerminalAsync(
        Guid operationId,
        bool success,
        bool cancelled,
        string stageKey,
        string message,
        string? error)
    {
        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.ScheduledPrefillXboxAuthProgress, new
            {
                operationId,
                success,
                status = success || cancelled
                    ? OperationStatus.Completed
                    : OperationStatus.Failed,
                stageKey,
                percentComplete = success || cancelled ? 100.0 : 0.0,
                gamesDiscovered = 0,
                cancelled,
                error,
                message,
                isTerminal = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit terminal scheduled prefill Xbox auth progress ({StageKey})", stageKey);
        }
    }
}
