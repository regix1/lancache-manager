using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Services.Xbox;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Determines the authentication plan for a scheduled prefill run per service.
/// </summary>
public sealed class ScheduledPrefillAuthService : IScheduledPrefillAuthService
{
    private static readonly TimeSpan[] _autoLoginRetryDelays = [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(6)
    ];

    private readonly IScheduledPrefillSteamAuthStorageService _scheduledPrefillSteamAuthStorage;
    private readonly EpicAuthStorageService _epicAuthStorage;
    private readonly XboxAuthStorageService _xboxAuthStorage;
    private readonly ILogger<ScheduledPrefillAuthService> _logger;

    public ScheduledPrefillAuthService(
        IScheduledPrefillSteamAuthStorageService scheduledPrefillSteamAuthStorage,
        EpicAuthStorageService epicAuthStorage,
        XboxAuthStorageService xboxAuthStorage,
        ILogger<ScheduledPrefillAuthService> logger)
    {
        _scheduledPrefillSteamAuthStorage = scheduledPrefillSteamAuthStorage;
        _epicAuthStorage = epicAuthStorage;
        _xboxAuthStorage = xboxAuthStorage;
        _logger = logger;
    }

    public Task<ScheduledPrefillAuthPlan> EnsureAuthenticatedAsync(
        PrefillPlatform service,
        ScheduledPrefillAuthContext context,
        CancellationToken ct)
    {
        switch (service)
        {
            case PrefillPlatform.BattleNet:
            case PrefillPlatform.Riot:
                return Task.FromResult(new ScheduledPrefillAuthPlan
                {
                    Service = service,
                    State = ScheduledPrefillAuthState.Ready
                });

            case PrefillPlatform.Steam:
                return Task.FromResult(BuildSteamPlan(service));

            case PrefillPlatform.Epic:
                return Task.FromResult(BuildEpicPlan(service));

            case PrefillPlatform.Xbox:
                return Task.FromResult(BuildXboxPlan(service));

            default:
                return Task.FromResult(new ScheduledPrefillAuthPlan
                {
                    Service = service,
                    State = ScheduledPrefillAuthState.NeedsLogin,
                    NeedsLoginReason = "DaemonHeadlessLoginUnsupported"
                });
        }
    }

    private ScheduledPrefillAuthPlan BuildSteamPlan(PrefillPlatform service)
    {
        SteamAuthData authData = _scheduledPrefillSteamAuthStorage.GetAuthData();

        string? refreshToken = authData.RefreshToken;
        string? username = authData.Username;

        if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(username))
        {
            return new ScheduledPrefillAuthPlan
            {
                Service = service,
                State = ScheduledPrefillAuthState.NeedsLogin,
                NeedsLoginReason = "SteamLoginRequired"
            };
        }

        string resolvedUsername = username;
        string resolvedRefreshToken = refreshToken;

        // Steam stores only a refresh token + login timestamp; there is no real credential
        // expiry to surface, so leave it null rather than mislabeling the login time as expiry.
        DateTimeOffset? expiresAtUtc = null;

        return new ScheduledPrefillAuthPlan
        {
            Service = service,
            State = ScheduledPrefillAuthState.Ready,
            DisplayName = resolvedUsername,
            ExpiresAtUtc = expiresAtUtc,
            AfterSessionCreatedAsync = async (session, sessionCt) =>
            {
                _logger.LogInformation(
                    "Providing Steam auto-login credentials to scheduled prefill session {SessionId} for {Username}",
                    session.Id, resolvedUsername);

                bool success = await TryAutoLoginWithRetriesAsync(
                    session.Id,
                    sessionCt,
                    ct => session.Client.ProvideAutoLoginAsync(session.Id, resolvedUsername, resolvedRefreshToken, ct));

                if (!success)
                {
                    throw new InvalidOperationException(
                        $"Steam auto-login failed for scheduled prefill session {session.Id}.");
                }
            }
        };
    }

    private ScheduledPrefillAuthPlan BuildEpicPlan(PrefillPlatform service)
    {
        EpicAuthData authData = _epicAuthStorage.GetAuthData();

        string? refreshToken = authData.RefreshToken;

        if (string.IsNullOrEmpty(refreshToken))
        {
            return new ScheduledPrefillAuthPlan
            {
                Service = service,
                State = ScheduledPrefillAuthState.NeedsLogin,
                NeedsLoginReason = "EpicLoginRequired"
            };
        }

        string resolvedRefreshToken = refreshToken;

        // Epic stores only a refresh token + login timestamp; there is no real credential
        // expiry to surface, so leave it null rather than mislabeling the login time as expiry.
        return new ScheduledPrefillAuthPlan
        {
            Service = service,
            State = ScheduledPrefillAuthState.Ready,
            DisplayName = authData.DisplayName,
            ExpiresAtUtc = null,
            AfterSessionCreatedAsync = async (session, sessionCt) =>
            {
                _logger.LogInformation(
                    "Providing Epic auto-login credentials to scheduled prefill session {SessionId}",
                    session.Id);

                bool success = await TryAutoLoginWithRetriesAsync(
                    session.Id,
                    sessionCt,
                    ct => session.Client.ProvideEpicAutoLoginAsync(session.Id, resolvedRefreshToken, ct));

                if (!success)
                {
                    throw new InvalidOperationException(
                        $"Epic auto-login failed for scheduled prefill session {session.Id}.");
                }
            }
        };
    }

    private ScheduledPrefillAuthPlan BuildXboxPlan(PrefillPlatform service)
    {
        XboxAuthData authData = _xboxAuthStorage.GetAuthData();

        string? refreshToken = authData.RefreshToken;
        string? deviceKeyPkcs8 = authData.DeviceKeyPkcs8;

        if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(deviceKeyPkcs8))
        {
            return new ScheduledPrefillAuthPlan
            {
                Service = service,
                State = ScheduledPrefillAuthState.NeedsLogin,
                NeedsLoginReason = "XboxLoginRequired"
            };
        }

        string resolvedRefreshToken = refreshToken;
        string resolvedDeviceKeyPkcs8 = deviceKeyPkcs8;

        // Reuse the SAME expiry the Integrations card surfaces: MSA refresh tokens carry no
        // returned expiry, so it's the last-auth time + the documented ~90-day inactivity window.
        DateTimeOffset? expiresAtUtc = authData.LastAuthenticated.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(authData.LastAuthenticated.Value, DateTimeKind.Utc))
                .Add(XboxCatalogMappingService.XboxLoginValidity)
            : null;

        return new ScheduledPrefillAuthPlan
        {
            Service = service,
            State = ScheduledPrefillAuthState.Ready,
            DisplayName = authData.DisplayName,
            ExpiresAtUtc = expiresAtUtc,
            AfterSessionCreatedAsync = async (session, sessionCt) =>
            {
                _logger.LogInformation(
                    "Providing Xbox auto-login credentials to scheduled prefill session {SessionId}",
                    session.Id);

                bool success = await TryAutoLoginWithRetriesAsync(
                    session.Id,
                    sessionCt,
                    ct => session.Client.ProvideXboxAutoLoginAsync(
                        session.Id, resolvedRefreshToken, resolvedDeviceKeyPkcs8, ct));

                if (!success)
                {
                    throw new InvalidOperationException(
                        $"Xbox auto-login failed for scheduled prefill session {session.Id}.");
                }
            }
        };
    }

    private async Task<bool> TryAutoLoginWithRetriesAsync(
        string sessionId,
        CancellationToken ct,
        Func<CancellationToken, Task<bool>> attemptAutoLogin)
    {
        for (var attempt = 0; attempt <= _autoLoginRetryDelays.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (await attemptAutoLogin(ct))
            {
                return true;
            }

            if (attempt >= _autoLoginRetryDelays.Length)
            {
                break;
            }

            var delay = _autoLoginRetryDelays[attempt];
            _logger.LogWarning(
                "Auto-login attempt {Attempt} failed for session {SessionId}; retrying in {DelaySeconds}s",
                attempt + 1,
                sessionId,
                delay.TotalSeconds);

            await Task.Delay(delay, ct);
        }

        return false;
    }
}
