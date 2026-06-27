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
    public const string UserSessionDeleted = "UserSessionDeleted";
    public const string UserSessionsCleared = "UserSessionsCleared";
    public const string SessionLastSeenUpdated = "SessionLastSeenUpdated";
    public const string GuestRefreshRateUpdated = "GuestRefreshRateUpdated";

    // User Preferences
    public const string UserPreferencesUpdated = "UserPreferencesUpdated";
    public const string UserPreferencesReset = "UserPreferencesReset";

    // Cache Operations
    public const string CorruptionDetectionStarted = "CorruptionDetectionStarted";
    public const string CorruptionDetectionProgress = "CorruptionDetectionProgress";
    public const string CorruptionDetectionComplete = "CorruptionDetectionComplete";
    public const string CorruptionRemovalStarted = "CorruptionRemovalStarted";
    public const string CorruptionRemovalProgress = "CorruptionRemovalProgress";
    public const string CorruptionRemovalComplete = "CorruptionRemovalComplete";
    public const string CacheClearingStarted = "CacheClearingStarted";
    public const string CacheClearingProgress = "CacheClearingProgress";
    public const string CacheClearingComplete = "CacheClearingComplete";
    public const string CacheScanComplete = "CacheScanComplete";
    public const string CacheSizeScanStarted = "CacheSizeScanStarted";
    public const string CacheSizeScanProgress = "CacheSizeScanProgress";
    public const string CacheSizeScanComplete = "CacheSizeScanComplete";
    public const string OperationWaiting = "OperationWaiting";
    public const string OperationWaitingComplete = "OperationWaitingComplete";
    public const string ServiceRemovalStarted = "ServiceRemovalStarted";
    public const string ServiceRemovalProgress = "ServiceRemovalProgress";
    public const string ServiceRemovalComplete = "ServiceRemovalComplete";
    public const string EvictionScanStarted = "EvictionScanStarted";
    public const string EvictionScanProgress = "EvictionScanProgress";
    public const string EvictionScanComplete = "EvictionScanComplete";
    public const string EvictionRemovalStarted = "EvictionRemovalStarted";
    public const string EvictionRemovalProgress = "EvictionRemovalProgress";
    public const string EvictionRemovalComplete = "EvictionRemovalComplete";

    // Games
    public const string GameDetectionStarted = "GameDetectionStarted";
    public const string GameDetectionProgress = "GameDetectionProgress";
    public const string GameDetectionComplete = "GameDetectionComplete";
    public const string GameRemovalStarted = "GameRemovalStarted";
    public const string GameRemovalProgress = "GameRemovalProgress";
    public const string GameRemovalComplete = "GameRemovalComplete";
    public const string GameImagesUpdated = "GameImagesUpdated";

    // Log Processing
    public const string LogProcessingStarted = "LogProcessingStarted";
    public const string LogProcessingProgress = "LogProcessingProgress";
    public const string LogProcessingComplete = "LogProcessingComplete";
    public const string LogRemovalStarted = "LogRemovalStarted";
    public const string LogRemovalProgress = "LogRemovalProgress";
    public const string LogRemovalComplete = "LogRemovalComplete";

    // Database Operations
    public const string DatabaseResetStarted = "DatabaseResetStarted";
    public const string DatabaseResetProgress = "DatabaseResetProgress";
    public const string DatabaseResetComplete = "DatabaseResetComplete";

    // Data Import
    public const string DataImportStarted = "DataImportStarted";
    public const string DataImportProgress = "DataImportProgress";
    public const string DataImportComplete = "DataImportComplete";

    // Metrics Security
    public const string MetricsSecurityUpdated = "MetricsSecurityUpdated";

    // Schedules
    public const string SchedulesUpdated = "SchedulesUpdated";

    // System / Config
    public const string DefaultGuestRefreshRateChanged = "DefaultGuestRefreshRateChanged";
    public const string AllowedTimeFormatsChanged = "AllowedTimeFormatsChanged";
    public const string DefaultGuestPreferencesChanged = "DefaultGuestPreferencesChanged";
    public const string DefaultGuestThemeChanged = "DefaultGuestThemeChanged";

    // Auth / Guest Mode
    public const string GuestModeLockChanged = "GuestModeLockChanged";
    public const string GuestRefreshRateLockChanged = "GuestRefreshRateLockChanged";
    public const string GuestDurationUpdated = "GuestDurationUpdated";
    /// <summary>
    /// Payload: { sessionId, service ("steam"|"epic"), enabled, prefillExpiresAt }
    /// </summary>
    public const string GuestPrefillPermissionChanged = "GuestPrefillPermissionChanged";
    public const string GuestPrefillConfigChanged = "GuestPrefillConfigChanged";
    public const string PrefillDefaultsChanged = "PrefillDefaultsChanged";

    // Prefill Daemon
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

    // Epic Prefill Daemon Events
    public const string EpicDaemonSessionCreated = "EpicDaemonSessionCreated";
    public const string EpicDaemonSessionUpdated = "EpicDaemonSessionUpdated";
    public const string EpicDaemonSessionTerminated = "EpicDaemonSessionTerminated";
    public const string EpicAuthStateChanged = "EpicAuthStateChanged";
    public const string EpicCredentialChallenge = "EpicCredentialChallenge";
    public const string EpicStatusChanged = "EpicStatusChanged";
    public const string EpicPrefillStateChanged = "EpicPrefillStateChanged";
    public const string EpicPrefillProgress = "EpicPrefillProgress";
    public const string EpicPrefillHistoryUpdated = "EpicPrefillHistoryUpdated";
    public const string EpicSessionEnded = "EpicSessionEnded";

    // Battle.net Prefill Daemon Events (anonymous - no account login)
    public const string BattleNetDaemonSessionCreated = "BattleNetDaemonSessionCreated";
    public const string BattleNetDaemonSessionUpdated = "BattleNetDaemonSessionUpdated";
    public const string BattleNetDaemonSessionTerminated = "BattleNetDaemonSessionTerminated";
    public const string BattleNetAuthStateChanged = "BattleNetAuthStateChanged";
    public const string BattleNetCredentialChallenge = "BattleNetCredentialChallenge";
    public const string BattleNetStatusChanged = "BattleNetStatusChanged";
    public const string BattleNetPrefillStateChanged = "BattleNetPrefillStateChanged";
    public const string BattleNetPrefillProgress = "BattleNetPrefillProgress";
    public const string BattleNetPrefillHistoryUpdated = "BattleNetPrefillHistoryUpdated";
    public const string BattleNetSessionEnded = "BattleNetSessionEnded";

    // Riot Prefill Daemon Events (anonymous - no account login)
    public const string RiotDaemonSessionCreated = "RiotDaemonSessionCreated";
    public const string RiotDaemonSessionUpdated = "RiotDaemonSessionUpdated";
    public const string RiotDaemonSessionTerminated = "RiotDaemonSessionTerminated";
    public const string RiotAuthStateChanged = "RiotAuthStateChanged";
    public const string RiotCredentialChallenge = "RiotCredentialChallenge";
    public const string RiotStatusChanged = "RiotStatusChanged";
    public const string RiotPrefillStateChanged = "RiotPrefillStateChanged";
    public const string RiotPrefillProgress = "RiotPrefillProgress";
    public const string RiotPrefillHistoryUpdated = "RiotPrefillHistoryUpdated";
    public const string RiotSessionEnded = "RiotSessionEnded";

    // Xbox / Microsoft Store Prefill Daemon Events (login-required - Microsoft OAuth device-code)
    public const string XboxDaemonSessionCreated = "XboxDaemonSessionCreated";
    public const string XboxDaemonSessionUpdated = "XboxDaemonSessionUpdated";
    public const string XboxDaemonSessionTerminated = "XboxDaemonSessionTerminated";
    public const string XboxAuthStateChanged = "XboxAuthStateChanged";
    public const string XboxCredentialChallenge = "XboxCredentialChallenge";
    public const string XboxStatusChanged = "XboxStatusChanged";
    public const string XboxPrefillStateChanged = "XboxPrefillStateChanged";
    public const string XboxPrefillProgress = "XboxPrefillProgress";
    public const string XboxPrefillHistoryUpdated = "XboxPrefillHistoryUpdated";
    public const string XboxSessionEnded = "XboxSessionEnded";

    // Epic Guest Prefill Config
    public const string EpicGuestPrefillConfigChanged = "EpicGuestPrefillConfigChanged";

    // Battle.net Guest Prefill Config
    public const string BattleNetGuestPrefillConfigChanged = "BattleNetGuestPrefillConfigChanged";

    // Riot Guest Prefill Config
    public const string RiotGuestPrefillConfigChanged = "RiotGuestPrefillConfigChanged";

    // Xbox Guest Prefill Config
    public const string XboxGuestPrefillConfigChanged = "XboxGuestPrefillConfigChanged";

    // Epic Game Mapping
    public const string EpicGameMappingsUpdated = "EpicGameMappingsUpdated";
    public const string EpicMappingProgress = "EpicMappingProgress";

    // Battle.net Game Mapping
    public const string BlizzardGameMappingsUpdated = "BlizzardGameMappingsUpdated";

    // Xbox Game Mapping
    public const string XboxGameMappingsUpdated = "XboxGameMappingsUpdated";
    public const string XboxMappingProgress = "XboxMappingProgress";

    // Directory Permissions
    public const string DirectoryPermissionsChanged = "DirectoryPermissionsChanged";

    // SteamKit2 / Depot Mapping
    public const string DepotMappingStarted = "DepotMappingStarted";
    public const string DepotMappingProgress = "DepotMappingProgress";
    public const string DepotMappingComplete = "DepotMappingComplete";
    public const string SteamSessionError = "SteamSessionError";
    public const string SteamAutoLogout = "SteamAutoLogout";
    public const string AutomaticScanSkipped = "AutomaticScanSkipped";

    // Scheduled Prefill
    public const string ScheduledPrefillStarted = "ScheduledPrefillStarted";
    public const string ScheduledPrefillProgress = "ScheduledPrefillProgress";
    public const string ScheduledPrefillCompleted = "ScheduledPrefillCompleted";
}
