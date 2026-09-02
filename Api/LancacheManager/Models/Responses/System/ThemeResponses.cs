namespace LancacheManager.Models;

/// <summary>
/// Response for theme upload
/// </summary>
public class ThemeUploadResponse
{
    public bool Success { get; set; }
    public string ThemeId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for theme deletion
/// </summary>
public class ThemeDeleteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> FilesDeleted { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Response for theme not found with available themes
/// </summary>
public class ThemeNotFoundResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }

    /// <summary>i18n key naming the refusal, so the browser shows it in the reader's language.</summary>
    public string? StageKey { get; set; }

    /// <summary>Substitution values for the localized <see cref="StageKey"/> template.</summary>
    public Dictionary<string, object?>? Context { get; set; }

    public string[] AvailableThemes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Response for theme cleanup
/// </summary>
public class ThemeCleanupResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Response for theme preference get/set
/// </summary>
public class ThemePreferenceResponse
{
    public string ThemeId { get; set; } = string.Empty;
    public bool Success { get; set; }

    /// <summary>
    /// Confirmation text for a successful update. Null on a plain read of the current guest theme.
    /// </summary>
    public string? Message { get; set; }
}

