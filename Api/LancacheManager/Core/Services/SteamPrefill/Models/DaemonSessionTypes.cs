using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services.SteamPrefill;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DaemonSessionStatus
{
    Active,
    Terminated,
    Error
}

[JsonConverter(typeof(JsonStringEnumConverter))]
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
