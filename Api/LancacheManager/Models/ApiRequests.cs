using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Models;

#region Auth Requests

public class SetGuestDurationRequest
{
    public int DurationHours { get; set; }
}

public class SetGuestLockRequest
{
    public bool IsLocked { get; set; }
}

public class SetGuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
}

public class ToggleGuestPrefillRequest
{
    public bool Enabled { get; set; }
    public int? DurationHours { get; set; }
}

#endregion

#region Database Requests

public class ResetTablesRequest
{
    public List<string> Tables { get; set; } = new();
}

#endregion

#region Data Migration Requests

public class DataMigrationImportRequest
{
    public string ConnectionString { get; set; } = string.Empty;
    public int? BatchSize { get; set; } = 1000;
    public bool OverwriteExisting { get; set; } = false;
}

#endregion

#region Download Requests

public class BatchDownloadEventsRequest
{
    public List<int> DownloadIds { get; set; } = new();
}

#endregion

#region Event Requests

public class CreateEventRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public DateTime? StartTimeLocal { get; set; }
    public DateTime? EndTimeLocal { get; set; }
    public int? ColorIndex { get; set; }
}

public class UpdateEventRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public DateTime? StartTimeLocal { get; set; }
    public DateTime? EndTimeLocal { get; set; }
    public int? ColorIndex { get; set; }
}

#endregion

#region GC Requests

public class UpdateGcSettingsRequest
{
    public string Aggressiveness { get; set; } = "disabled";
    public long MemoryThresholdMB { get; set; } = 4096;
}

#endregion

#region Logs Requests

public class UpdateLogPositionRequest
{
    public long? Position { get; set; }
    public bool? Reset { get; set; }
}

#endregion

#region Metrics Requests

public class SetIntervalRequest
{
    public int Interval { get; set; }
}

public class SetSecurityRequest
{
    public bool Enabled { get; set; }
}

#endregion

#region Operation State Requests

public class SaveStateRequest
{
    public string Key { get; set; } = string.Empty;
    public string? Type { get; set; }
    public JsonElement? Data { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public int? ExpirationMinutes { get; set; }
}

public class UpdateStateRequest
{
    public Dictionary<string, object>? Updates { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
}

#endregion

#region Prefill Daemon Requests

public class ProvideCredentialRequest
{
    public CredentialChallenge? Challenge { get; set; }
    public string? Credential { get; set; }
}

public class SetSelectedAppsRequest
{
    public List<string>? AppIds { get; set; }
}

public class PrefillCacheStatusRequest
{
    public List<string>? AppIds { get; set; }
}

public class StartPrefillRequest
{
    public bool All { get; set; }
    public bool Recent { get; set; }
    public bool RecentlyPurchased { get; set; }
    public int? Top { get; set; }
    public bool Force { get; set; }
    public List<string>? OperatingSystems { get; set; }
    public int? MaxConcurrency { get; set; }
}

#endregion

#region Steam API Key Requests

public class SaveApiKeyRequest
{
    public string ApiKey { get; set; } = string.Empty;
}

public class TestApiKeyRequest
{
    public string ApiKey { get; set; } = string.Empty;
}

#endregion

#region Steam Auth Requests

public class SetSteamModeRequest
{
    public string? Mode { get; set; }
}

public class SteamLoginRequest
{
    [StringLength(64, ErrorMessage = "Username cannot exceed 64 characters")]
    public string? Username { get; set; }

    [StringLength(256, ErrorMessage = "Password cannot exceed 256 characters")]
    public string? Password { get; set; }

    [StringLength(10, ErrorMessage = "TwoFactorCode cannot exceed 10 characters")]
    [RegularExpression(@"^[A-Z0-9]*$", ErrorMessage = "TwoFactorCode contains invalid characters")]
    public string? TwoFactorCode { get; set; }

    [StringLength(10, ErrorMessage = "EmailCode cannot exceed 10 characters")]
    [RegularExpression(@"^[A-Z0-9]*$", ErrorMessage = "EmailCode contains invalid characters")]
    public string? EmailCode { get; set; }

    public bool AllowMobileConfirmation { get; set; } = true;
    public bool AutoStartPicsRebuild { get; set; } = false;
}

#endregion

#region Prefill Defaults Requests

public class SetPrefillDefaultsRequest
{
    public List<string>? OperatingSystems { get; set; }
    public string? MaxConcurrency { get; set; }
    public string? EpicDefaultPrefillMaxConcurrency { get; set; }
}

#endregion

#region System Requests

public class UpdateLogRotationScheduleRequest
{
    public int ScheduleHours { get; set; }
}

public class SetAllowedTimeFormatsRequest
{
    public List<string> Formats { get; set; } = new();
}

public class SetBoolPreferenceRequest
{
    public bool Value { get; set; }
}

public class UpdateSetupRequest
{
    public bool? Completed { get; set; }
}

public class SetCacheDeleteModeRequest
{
    public string DeleteMode { get; set; } = string.Empty;
}

public class SetCrawlIntervalRequest
{
    public int IntervalHours { get; set; }
}

public class SetScanModeRequest
{
    public string Mode { get; set; } = string.Empty;
}

public class SetRefreshRateRequest
{
    public string RefreshRate { get; set; } = string.Empty;
}

public class GuestRefreshRateLockRequest
{
    public bool Locked { get; set; }
}

#endregion

#region Prefill Admin Requests

public class TerminateSessionRequest
{
    public string? Reason { get; set; }
    public bool Force { get; set; }
}

public class BanRequest
{
    public string? Reason { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

public class BanByUsernameRequest
{
    public string Username { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? SessionId { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

#endregion

#region Stats Requests

public class UpdateStatsExclusionsRequest
{
    public List<string> Ips { get; set; } = new();
}

#endregion

#region Theme Requests

public class ThemePreferenceRequest
{
    public string ThemeId { get; set; } = string.Empty;
}

#endregion
