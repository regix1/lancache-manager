namespace LancacheManager.Core.Services.SteamPrefill;

public enum DaemonSessionStatus
{
    Active,
    Terminated,
    Error
}

public enum DaemonAuthState
{
    NotAuthenticated,
    LoggingIn,
    UsernameRequired,
    PasswordRequired,
    TwoFactorRequired,
    SteamGuardRequired,
    DeviceConfirmationRequired,
    AuthorizationUrlRequired,
    Authenticated
}
