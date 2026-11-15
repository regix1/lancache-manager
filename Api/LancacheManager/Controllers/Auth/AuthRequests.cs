using System.Text.Json.Serialization;

namespace LancacheManager.Controllers;

// Request models
public class ChangePasswordRequest
{
    /// <summary>
    /// The password the account has now. Required, so a session left open on a shared machine is not
    /// on its own permission to change what the password is.
    /// </summary>
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>
    /// The replacement, held to the same rules an account is created under.
    /// </summary>
    public string NewPassword { get; set; } = string.Empty;
}

public class GuestDurationRequest
{
    // null = clear UI override (revert to env/appsettings default).
    public int? DurationHours { get; set; }
}

public class GuestLockRequest
{
    public bool IsLocked { get; set; }
}

public class GuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
    public int? MaxThreadCount { get; set; }
    // Optional Battle.net defaults (anonymous service); omitted by Steam-only callers.
    public bool? BattleNetEnabledByDefault { get; set; }
    public int? BattleNetDurationHours { get; set; }
    // Optional Riot defaults (anonymous service); omitted by Steam-only callers.
    public bool? RiotEnabledByDefault { get; set; }
    public int? RiotDurationHours { get; set; }
}

public class GuestPrefillToggleRequest
{
    public bool Enabled { get; set; }
}

public class EpicGuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
    public int? MaxThreadCount { get; set; }
}

public class BattleNetGuestPrefillConfigRequest
{
    private int? _maxThreadCount;

    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;

    /// <summary>
    /// The guest thread limit; null clears it. A page from before Battle.net had a limit leaves the field
    /// out, and that keeps the stored limit.
    /// </summary>
    public int? MaxThreadCount
    {
        get => _maxThreadCount;
        set
        {
            _maxThreadCount = value;
            MaxThreadCountSent = true;
        }
    }

    /// <summary>
    /// Whether the body carried <see cref="MaxThreadCount"/>, null included.
    /// </summary>
    [JsonIgnore]
    public bool MaxThreadCountSent { get; private set; }
}

public class RiotGuestPrefillConfigRequest
{
    private int? _maxThreadCount;

    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;

    /// <summary>
    /// The guest thread limit; null clears it. A page from before Riot had a limit leaves the field out,
    /// and that keeps the stored limit.
    /// </summary>
    public int? MaxThreadCount
    {
        get => _maxThreadCount;
        set
        {
            _maxThreadCount = value;
            MaxThreadCountSent = true;
        }
    }

    /// <summary>
    /// Whether the body carried <see cref="MaxThreadCount"/>, null included.
    /// </summary>
    [JsonIgnore]
    public bool MaxThreadCountSent { get; private set; }
}

public class XboxGuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
    public int? MaxThreadCount { get; set; }
}
