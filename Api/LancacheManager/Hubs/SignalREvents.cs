namespace LancacheManager.Hubs;

/// <summary>
/// Centralized SignalR event name constants.
/// Keep in sync with Web/src/contexts/SignalRContext/types.ts SIGNALR_EVENTS array.
/// </summary>
public static class SignalREvents
{
    // Client Groups
    public const string ClientGroupCreated = "ClientGroupCreated";
    public const string ClientGroupUpdated = "ClientGroupUpdated";
    public const string ClientGroupDeleted = "ClientGroupDeleted";
    public const string ClientGroupMemberAdded = "ClientGroupMemberAdded";
    public const string ClientGroupMemberRemoved = "ClientGroupMemberRemoved";
    public const string ClientGroupsCleared = "ClientGroupsCleared";

    // Events
    public const string EventCreated = "EventCreated";
    public const string EventUpdated = "EventUpdated";
    public const string EventDeleted = "EventDeleted";
    public const string DownloadTagged = "DownloadTagged";
    public const string EventsCleared = "EventsCleared";

    // Downloads
    public const string DownloadsRefresh = "DownloadsRefresh";
    public const string DownloadSpeedUpdate = "DownloadSpeedUpdate";

    // Sessions
    public const string UserSessionCreated = "UserSessionCreated";
    public const string UserSessionRevoked = "UserSessionRevoked";
    public const string UserSessionsCleared = "UserSessionsCleared";
    public const string SessionLastSeenUpdated = "SessionLastSeenUpdated";
    public const string GuestRefreshRateUpdated = "GuestRefreshRateUpdated";

    // User Preferences
    public const string UserPreferencesUpdated = "UserPreferencesUpdated";
    public const string UserPreferencesReset = "UserPreferencesReset";

    // Cache Operations
    public const string CorruptionDetectionStarted = "CorruptionDetectionStarted";
    public const string CorruptionDetectionComplete = "CorruptionDetectionComplete";
    public const string CorruptionRemovalStarted = "CorruptionRemovalStarted";
    public const string CorruptionRemovalComplete = "CorruptionRemovalComplete";
    public const string CacheClearProgress = "CacheClearProgress";
    public const string CacheClearComplete = "CacheClearComplete";
    public const string ServiceRemovalProgress = "ServiceRemovalProgress";
    public const string ServiceRemovalComplete = "ServiceRemovalComplete";

    // Games
    public const string GameDetectionStarted = "GameDetectionStarted";
    public const string GameDetectionComplete = "GameDetectionComplete";
    public const string GameRemovalProgress = "GameRemovalProgress";
    public const string GameRemovalComplete = "GameRemovalComplete";

    // Log Processing
    public const string LogRemovalProgress = "LogRemovalProgress";
    public const string LogRemovalComplete = "LogRemovalComplete";
    public const string ProcessingProgress = "ProcessingProgress";
    public const string FastProcessingComplete = "FastProcessingComplete";

    // Database Operations
    public const string DatabaseResetProgress = "DatabaseResetProgress";
    public const string PrefillSessionsCleared = "PrefillSessionsCleared";
    public const string BannedSteamUsersCleared = "BannedSteamUsersCleared";

    // System / Config
    public const string DefaultGuestRefreshRateChanged = "DefaultGuestRefreshRateChanged";
    public const string AllowedTimeFormatsChanged = "AllowedTimeFormatsChanged";
    public const string DefaultGuestPreferencesChanged = "DefaultGuestPreferencesChanged";
    public const string DefaultGuestThemeChanged = "DefaultGuestThemeChanged";

    // Auth / Guest Mode
    public const string GuestModeLockChanged = "GuestModeLockChanged";
    public const string GuestDurationUpdated = "GuestDurationUpdated";
    public const string GuestPrefillPermissionChanged = "GuestPrefillPermissionChanged";

    // Prefill Daemon
    public const string SteamUserBanned = "SteamUserBanned";
    public const string SteamUserUnbanned = "SteamUserUnbanned";
    public const string DaemonSessionCreated = "DaemonSessionCreated";
    public const string DaemonSessionUpdated = "DaemonSessionUpdated";
    public const string DaemonSessionTerminated = "DaemonSessionTerminated";
    public const string SessionSubscribed = "SessionSubscribed";
    public const string AuthStateChanged = "AuthStateChanged";
    public const string CredentialChallenge = "CredentialChallenge";
    public const string StatusChanged = "StatusChanged";
    public const string PrefillStateChanged = "PrefillStateChanged";
    public const string PrefillProgress = "PrefillProgress";
    public const string PrefillHistoryUpdated = "PrefillHistoryUpdated";
    public const string SessionEnded = "SessionEnded";

    // SteamKit2 / Depot Mapping
    public const string DepotMappingStarted = "DepotMappingStarted";
    public const string DepotMappingProgress = "DepotMappingProgress";
    public const string DepotMappingComplete = "DepotMappingComplete";
    public const string SteamSessionError = "SteamSessionError";
    public const string SteamAutoLogout = "SteamAutoLogout";
    public const string AutomaticScanSkipped = "AutomaticScanSkipped";
}
