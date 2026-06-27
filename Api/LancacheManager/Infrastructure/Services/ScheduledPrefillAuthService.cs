using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Services.Xbox;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Determines the authentication plan for a scheduled prefill run per service.
/// </summary>
public sealed class ScheduledPrefillAuthService : IScheduledPrefillAuthService
{
    private readonly ISteamAuthStorageService _steamAuthStorage;
    private readonly EpicAuthStorageService _epicAuthStorage;
    private readonly XboxAuthStorageService _xboxAuthStorage;
    private readonly ILogger<ScheduledPrefillAuthService> _logger;

    public ScheduledPrefillAuthService(
        ISteamAuthStorageService steamAuthStorage,
        EpicAuthStorageService epicAuthStorage,
        XboxAuthStorageService xboxAuthStorage,
        ILogger<ScheduledPrefillAuthService> logger)
    {
        _steamAuthStorage = steamAuthStorage;
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
        SteamAuthData authData = _steamAuthStorage.GetAuthData();

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

                bool success = await session.Client.ProvideAutoLoginAsync(
                    session.Id, resolvedUsername, resolvedRefreshToken, sessionCt);

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

        string? displayName = string.IsNullOrEmpty(authData.RefreshToken)
            ? null
            : authData.DisplayName;

        return new ScheduledPrefillAuthPlan
        {
            Service = service,
            State = ScheduledPrefillAuthState.NeedsLogin,
            NeedsLoginReason = "DaemonHeadlessLoginUnsupported",
            DisplayName = displayName
        };
    }

    private ScheduledPrefillAuthPlan BuildXboxPlan(PrefillPlatform service)
    {
        XboxAuthData authData = _xboxAuthStorage.GetAuthData();

        bool authenticated = !string.IsNullOrEmpty(authData.RefreshToken);
        string? displayName = authenticated ? authData.DisplayName : null;

        // Reuse the SAME expiry the Integrations card surfaces: MSA refresh tokens carry no
        // returned expiry, so it's the last-auth time + the documented ~90-day inactivity window.
        DateTimeOffset? expiresAtUtc = authenticated && authData.LastAuthenticated.HasValue
            ? new DateTimeOffset(DateTime.SpecifyKind(authData.LastAuthenticated.Value, DateTimeKind.Utc))
                .Add(XboxCatalogMappingService.XboxLoginValidity)
            : null;

        return new ScheduledPrefillAuthPlan
        {
            Service = service,
            State = ScheduledPrefillAuthState.NeedsLogin,
            NeedsLoginReason = "DaemonHeadlessLoginUnsupported",
            DisplayName = displayName,
            ExpiresAtUtc = expiresAtUtc
        };
    }
}
