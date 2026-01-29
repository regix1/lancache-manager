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
    public string[] AvailableThemes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Response for theme cleanup
/// </summary>
public class ThemeCleanupResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> DeletedThemes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string[] RemainingThemes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Response for theme preference get/set
/// </summary>
public class ThemePreferenceResponse
{
    public string ThemeId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Response for preferences update
/// </summary>
public class PreferencesUpdateResponse
{
    public string Message { get; set; } = string.Empty;
}
