using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Models;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for theme management
/// Handles theme upload, deletion, retrieval, and user preferences
/// </summary>
[ApiController]
[Route("api/themes")]
public class ThemeController : ControllerBase
{
    private readonly string _themesPath;
    private readonly ILogger<ThemeController> _logger;
    private readonly IStateService _stateRepository;
    private readonly ISignalRNotificationService _notifications;

    // System theme IDs that cannot be deleted
    private static readonly string[] _systemThemes = { "dark-default", "light-default", "graphite" };

    public ThemeController(
        IConfiguration configuration,
        ILogger<ThemeController> logger,
        IPathResolver pathResolver,
        IStateService stateRepository,
        ISignalRNotificationService notifications)
    {
        _logger = logger;
        _stateRepository = stateRepository;
        _notifications = notifications;

        _themesPath = pathResolver.GetThemesDirectory();

        // Frontend theme service handles built-in themes, backend only manages custom uploaded themes.
        // The themes directory itself is created lazily, only immediately before the upload handler
        // actually writes a custom theme file (see UploadThemeAsync) - not here - so a fresh install
        // that never uploads a custom theme doesn't end up with an empty /data/themes directory.
    }

    /// <summary>
    /// Validates and sanitizes a theme ID from a request.
    /// Returns null with the sanitized themeId if valid, or an IActionResult error if invalid.
    /// </summary>
    private (string? themeId, ActionResult? error) SanitizeThemeId(ThemePreferenceRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ThemeId))
        {
            return (null, BadRequest(ApiResponse.Error("Theme ID is required")));
        }

        var themeId = Regex.Replace(request.ThemeId, @"[^a-zA-Z0-9-_]", "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return (null, BadRequest(ApiResponse.Error("Theme ID is required")));
        }

        if (!ThemeExists(themeId))
        {
            return (null, NotFound(ApiResponse.Error("Theme not found")));
        }

        return (themeId, null);
    }

    private bool ThemeExists(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        if (_systemThemes.Contains(themeId))
        {
            return true;
        }

        var tomlPath = Path.Combine(_themesPath, $"{themeId}.toml");
        var jsonPath = Path.Combine(_themesPath, $"{themeId}.json");
        return System.IO.File.Exists(tomlPath) || System.IO.File.Exists(jsonPath);
    }

    /// <summary>
    /// Returns an error result when a theme ID belongs to a system theme, or null when it is free.
    /// System themes are supplied by the frontend and resolved before it asks the server, so a
    /// custom file claiming one of their IDs would be stored but never applied.
    /// </summary>
    private BadRequestObjectResult? RejectSystemThemeId(string themeId)
    {
        if (!_systemThemes.Contains(themeId))
        {
            return null;
        }

        _logger.LogWarning($"Rejected theme upload claiming system theme ID: {themeId}");

        return BadRequest(ApiResponse.Error(
            "Cannot use a system theme ID",
            $"'{themeId}' is a built-in theme. Rename the theme so it produces a different ID."));
    }

    /// <summary>
    /// Lists custom uploaded themes.
    /// </summary>
    /// <remarks>
    /// Built-in themes are handled entirely by the frontend. Returns an empty list without creating
    /// the themes directory when none has ever been uploaded, since this fires on nearly every page load.
    /// </remarks>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(List<ThemeInfo>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ThemeInfo>>> GetThemesAsync()
    {
        var themes = new List<ThemeInfo>();

        if (!Directory.Exists(_themesPath))
        {
            // No custom theme has ever been uploaded - return the empty list rather than
            // creating the directory just because the frontend asked for it (this endpoint
            // fires on nearly every page load).
            return Ok(themes);
        }

        // Get both JSON and TOML files
        var jsonFiles = Directory.GetFiles(_themesPath, "*.json");
        var tomlFiles = Directory.GetFiles(_themesPath, "*.toml");
        var themeFiles = jsonFiles.Concat(tomlFiles).ToArray();

        foreach (var file in themeFiles)
        {
            try
            {
                var themeId = Path.GetFileNameWithoutExtension(file);

                // Skip high-contrast if it exists
                if (themeId == "high-contrast")
                {
                    // Delete the file if it exists
                    System.IO.File.Delete(file);
                    continue;
                }

                // A file claiming a system theme ID can never be applied, because the frontend
                // resolves system themes before it asks the server for one. Listing it would
                // replace the working built-in with an entry that does nothing. Uploads are
                // rejected now; this covers files written before that check existed. The file is
                // left on disk - deleting it is the operator's call, via delete or cleanup.
                if (_systemThemes.Contains(themeId))
                {
                    _logger.LogDebug($"Skipping theme file that claims a system theme ID: {file}");
                    continue;
                }

                string name, description, author, version;

                if (file.EndsWith(".toml"))
                {
                    // For TOML files, we'll let the frontend parse them
                    // Just get basic metadata
                    name = themeId;
                    description = "TOML Theme";
                    author = "Custom";
                    version = "1.0.0";
                }
                else
                {
                    // Parse JSON file
                    var content = await System.IO.File.ReadAllTextAsync(file);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    name = root.TryGetProperty("name", out var n) ? n.GetString() ?? themeId : themeId;
                    description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    author = root.TryGetProperty("author", out var a) ? a.GetString() ?? "Unknown" : "Unknown";
                    version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0.0" : "1.0.0";
                }

                themes.Add(new ThemeInfo
                {
                    Id = themeId,
                    Name = name,
                    Description = description,
                    Author = author,
                    Version = version,
                    Format = file.EndsWith(".toml") ? "toml" : "json"
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to parse theme file: {file}");
            }
        }

        return Ok(themes);
    }

    /// <summary>
    /// Gets one theme's raw file contents.
    /// </summary>
    /// <remarks>
    /// Returns TOML as <c>application/toml</c> text, or JSON as-is. No ProducesResponseType is
    /// declared here because the body shape is the arbitrary theme file itself, not a fixed schema.
    /// </remarks>
    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetThemeAsync(string id)
    {
        // Sanitize ID to prevent path traversal
        id = Regex.Replace(id, @"[^a-zA-Z0-9-_]", "");

        // Check for TOML file first
        var tomlPath = Path.Combine(_themesPath, $"{id}.toml");
        if (System.IO.File.Exists(tomlPath))
        {
            var tomlContent = await System.IO.File.ReadAllTextAsync(tomlPath);
            // Return TOML content directly with proper content type
            return Content(tomlContent, "application/toml");
        }

        // Fallback to JSON file
        var jsonPath = Path.Combine(_themesPath, $"{id}.json");
        if (!System.IO.File.Exists(jsonPath))
        {
            _logger.LogWarning($"Theme not found: {id}");
            return NotFound(ApiResponse.Error("Theme not found"));
        }

        var content = await System.IO.File.ReadAllTextAsync(jsonPath);
        var jsonTheme = JsonSerializer.Deserialize<JsonElement>(content);

        return Ok(jsonTheme);
    }

    /// <summary>
    /// Uploads a custom TOML or JSON theme (max 1MB).
    /// </summary>
    /// <remarks>
    /// Rejects filenames claiming a system theme ID, since the frontend resolves system themes
    /// before asking the server and a same-ID file would be stored but never applied.
    /// </remarks>
    [HttpPost("upload")]
    [Authorize(Policy = "AccountHolder")]
    [RequestSizeLimit(1_048_576)]
    [ProducesResponseType(typeof(ThemeUploadResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ThemeUploadResponse>> UploadThemeAsync(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(ApiResponse.Error("No file provided"));
        }

        var isToml = file.FileName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);
        var isJson = file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        if (!isToml && !isJson)
        {
            return BadRequest(ApiResponse.Error("Only TOML and JSON theme files are allowed"));
        }

        if (file.Length > 1024 * 1024) // 1MB max
        {
            return BadRequest(ApiResponse.Error("Theme file too large (max 1MB)"));
        }

        try
        {
            string themeId;
            string filePath;

            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            // Ensure the themes directory exists before writing (idempotent). This is the
            // one place that actually needs the directory to exist, since it's the only
            // handler that ever writes a custom theme file.
            if (!Directory.Exists(_themesPath))
            {
                Directory.CreateDirectory(_themesPath);
                _logger.LogInformation($"Created themes directory: {_themesPath}");
            }

            if (isToml)
            {
                // For TOML, just save it directly
                // Generate safe filename from the original filename
                var baseName = Path.GetFileNameWithoutExtension(file.FileName);
                themeId = Regex.Replace(baseName, @"[^a-zA-Z0-9-_]", "-").ToLower();
                themeId = themeId.Substring(0, Math.Min(themeId.Length, 50));

                var systemIdError = RejectSystemThemeId(themeId);
                if (systemIdError != null) return systemIdError;

                // Simply use the theme ID from the filename - overwrite if exists
                filePath = Path.Combine(_themesPath, $"{themeId}.toml");

                _logger.LogInformation($"Saving theme to: {filePath} (will overwrite if exists)");
                await System.IO.File.WriteAllTextAsync(filePath, content);
            }
            else
            {
                // Parse JSON and validate
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                // Validate required fields
                if (!root.TryGetProperty("name", out _))
                {
                    return BadRequest(ApiResponse.Error("Theme must have a 'name' property"));
                }

                if (!root.TryGetProperty("colors", out var colors) || colors.ValueKind != JsonValueKind.Object)
                {
                    return BadRequest(ApiResponse.Error("Theme must have a 'colors' object"));
                }

                // Generate safe filename
                var themeName = root.GetProperty("name").GetString() ?? "custom-theme";
                themeId = Regex.Replace(themeName, @"[^a-zA-Z0-9-_]", "-").ToLower();
                themeId = themeId.Substring(0, Math.Min(themeId.Length, 50));

                var systemIdError = RejectSystemThemeId(themeId);
                if (systemIdError != null) return systemIdError;

                // Ensure unique ID
                var counter = 0;
                var baseId = themeId;
                while (System.IO.File.Exists(Path.Combine(_themesPath, $"{themeId}.json")))
                {
                    counter++;
                    themeId = $"{baseId}-{counter}";
                }

                filePath = Path.Combine(_themesPath, $"{themeId}.json");
                await System.IO.File.WriteAllTextAsync(filePath, content);
            }

            _logger.LogInformation($"Theme uploaded: {themeId} by {HttpContext.Connection.RemoteIpAddress}");

            return Ok(new ThemeUploadResponse
            {
                Success = true,
                ThemeId = themeId,
                Message = "Theme uploaded successfully"
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in theme upload");
            return BadRequest(ApiResponse.Error("Invalid JSON format"));
        }
    }

    /// <summary>
    /// Deletes a custom theme's TOML and/or JSON file.
    /// </summary>
    /// <remarks>
    /// System theme IDs are rejected unless a custom file happens to claim one, in which case that
    /// file is removed like any other.
    /// </remarks>
    [HttpDelete("{id}")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(ThemeDeleteResponse), StatusCodes.Status200OK)]
    public ActionResult<ThemeDeleteResponse> DeleteTheme(string id)
    {
        // Log the incoming request
        _logger.LogInformation($"Delete theme request received for ID: '{id}' from {HttpContext.Connection.RemoteIpAddress}");

        // Sanitize ID
        var originalId = id;
        id = Regex.Replace(id, @"[^a-zA-Z0-9-_]", "");

        if (originalId != id)
        {
            _logger.LogWarning($"Theme ID was sanitized from '{originalId}' to '{id}'");
        }

        // Check for both TOML and JSON files
        var tomlPath = Path.Combine(_themesPath, $"{id}.toml");
        var jsonPath = Path.Combine(_themesPath, $"{id}.json");

        // Prevent deletion of system themes. They are supplied by the frontend and have no file
        // here, so there is nothing to remove. A file carrying a system theme ID is a custom file
        // and removing it cannot take the built-in with it, so that case falls through to the
        // delete below - it is how an operator clears a file left by an older upload.
        if (_systemThemes.Contains(id)
            && !System.IO.File.Exists(tomlPath)
            && !System.IO.File.Exists(jsonPath))
        {
            _logger.LogWarning($"Attempted to delete system theme: {id}");
            return BadRequest(ApiResponse.Error(
                "Cannot delete system theme",
                $"'{id}' is a protected system theme and cannot be deleted"));
        }


        try
        {
            _logger.LogInformation($"Looking for theme files:");
            _logger.LogInformation($"  TOML path: {tomlPath} - Exists: {System.IO.File.Exists(tomlPath)}");
            _logger.LogInformation($"  JSON path: {jsonPath} - Exists: {System.IO.File.Exists(jsonPath)}");

            var filesDeleted = new List<string>();
            var errors = new List<string>();

            // Try to delete TOML file
            if (System.IO.File.Exists(tomlPath))
            {
                try
                {
                    System.IO.File.Delete(tomlPath);
                    filesDeleted.Add($"{id}.toml");
                    _logger.LogInformation($"Successfully deleted TOML theme: {id}");

                    // Verify deletion
                    if (System.IO.File.Exists(tomlPath))
                    {
                        _logger.LogError($"File still exists after deletion attempt: {tomlPath}");
                        errors.Add($"Failed to delete {id}.toml - file still exists");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to delete {id}.toml: {ex.Message}");
                    _logger.LogError(ex, $"Failed to delete TOML file: {tomlPath}");
                }
            }

            // Try to delete JSON file
            if (System.IO.File.Exists(jsonPath))
            {
                try
                {
                    System.IO.File.Delete(jsonPath);
                    filesDeleted.Add($"{id}.json");
                    _logger.LogInformation($"Successfully deleted JSON theme: {id}");

                    // Verify deletion
                    if (System.IO.File.Exists(jsonPath))
                    {
                        _logger.LogError($"File still exists after deletion attempt: {jsonPath}");
                        errors.Add($"Failed to delete {id}.json - file still exists");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to delete {id}.json: {ex.Message}");
                    _logger.LogError(ex, $"Failed to delete JSON file: {jsonPath}");
                }
            }

            // Check results
            if (filesDeleted.Count == 0 && errors.Count == 0)
            {
                // Neither file existed - this is an error. The themes directory may not
                // exist at all if no custom theme has ever been uploaded.
                string?[] availableFiles = Directory.Exists(_themesPath)
                    ? Directory.GetFiles(_themesPath).Select(Path.GetFileName).ToArray()
                    : Array.Empty<string?>();

                _logger.LogWarning($"Theme not found: {id}. Available files: {string.Join(", ", availableFiles)}");

                return NotFound(new ThemeNotFoundResponse
                {
                    Error = $"Theme '{id}' not found on server",
                    Details = $"No files matching '{id}.toml' or '{id}.json' were found",
                    StageKey = "errors.themes.notFound",
                    Context = new Dictionary<string, object?> { ["id"] = id },
                    AvailableThemes = availableFiles.Select(f => Path.GetFileNameWithoutExtension(f) ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray()
                });
            }

            if (errors.Count > 0 && filesDeleted.Count == 0)
            {
                // Files existed but couldn't be deleted
                return StatusCode(500, new ErrorResponse
                {
                    Error = "Failed to delete theme",
                    Details = string.Join("; ", errors),
                    StageKey = "errors.themes.deleteFailed"
                });
            }

            // At least one file was deleted successfully
            _logger.LogInformation($"Theme deletion completed for '{id}'. Deleted: {string.Join(", ", filesDeleted)}. Errors: {string.Join(", ", errors)}");

            return Ok(new ThemeDeleteResponse
            {
                Success = true,
                Message = $"Theme '{id}' deleted successfully",
                FilesDeleted = filesDeleted,
                Errors = errors
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, $"Permission denied when deleting theme {id}");
            return StatusCode(500, new ErrorResponse
            {
                Error = "Permission denied - cannot delete theme file",
                StageKey = "errors.themes.deletePermission"
            });
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error when deleting theme {ThemeId}", id);
            throw; // -> GlobalExceptionMiddleware -> 500 safe { error, details?, statusCode, traceId }
        }
    }

    /// <summary>
    /// Deletes every custom theme file.
    /// </summary>
    /// <remarks>
    /// Includes one whose name happens to match a system theme ID (system themes have no file of
    /// their own; a custom file claiming that name is treated like any other custom file). This is
    /// a manual admin operation with no UI button.
    /// </remarks>
    [HttpPost("cleanup")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(ThemeCleanupResponse), StatusCodes.Status200OK)]
    public ActionResult<ThemeCleanupResponse> CleanupThemes()
    {
        var deletedThemes = new List<string>();
        var errors = new List<string>();

        _logger.LogInformation("Starting theme cleanup operation");

        // Get all theme files. The themes directory may not exist at all if no custom
        // theme has ever been uploaded - treat that as zero files to clean up.
        var themeFiles = Directory.Exists(_themesPath)
            ? Directory.GetFiles(_themesPath, "*.toml").Concat(Directory.GetFiles(_themesPath, "*.json")).ToArray()
            : Array.Empty<string>();

        _logger.LogInformation($"Found {themeFiles.Length} theme files to process");

        foreach (var file in themeFiles)
        {
            // Every file here is a custom theme, including one whose name matches a system theme
            // ID - system themes come from the frontend and never have a file - so cleanup removes
            // it like any other. That is the operator's route to clearing a stale file, since it
            // no longer appears in the theme list.
            var fileName = Path.GetFileNameWithoutExtension(file);

            try
            {
                System.IO.File.Delete(file);
                deletedThemes.Add($"{fileName}{Path.GetExtension(file)}");
                _logger.LogInformation($"Deleted theme file: {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                errors.Add($"Failed to delete {Path.GetFileName(file)}: {ex.Message}");
                _logger.LogError(ex, $"Failed to delete theme file: {file}");
            }
        }

        _logger.LogInformation($"Cleanup complete. Deleted {deletedThemes.Count} themes, {errors.Count} errors");

        return Ok(new ThemeCleanupResponse
        {
            Success = true,
            Message = $"Cleanup complete. Deleted {deletedThemes.Count} theme(s)",
            DeletedThemes = deletedThemes,
            Errors = errors,
            RemainingThemes = _systemThemes
        });
    }

    // Default Guest Theme Endpoints

    /// <summary>
    /// Gets the theme new guest sessions start with by default.
    /// </summary>
    [HttpGet("preferences/guest")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ThemePreferenceResponse), StatusCodes.Status200OK)]
    public ActionResult<ThemePreferenceResponse> GetDefaultGuestTheme()
    {
        var themeId = _stateRepository.GetDefaultGuestTheme() ?? "dark-default";
        _logger.LogInformation($"Retrieved default guest theme: {themeId}");

        return Ok(new ThemePreferenceResponse
        {
            ThemeId = themeId
        });
    }

    /// <summary>
    /// Sets the theme new guest sessions start with by default.
    /// </summary>
    /// <remarks>
    /// Broadcasts the change so guests with no explicit theme selection of their own pick it up live.
    /// </remarks>
    [HttpPut("preferences/guest")]
    [Authorize(Policy = "AccountHolder")]
    [ProducesResponseType(typeof(ThemePreferenceResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<ThemePreferenceResponse>> SetDefaultGuestThemeAsync([FromBody] ThemePreferenceRequest request)
    {
        var (themeId, error) = SanitizeThemeId(request);
        if (error != null) return error;

        _stateRepository.SetDefaultGuestTheme(themeId!);
        _logger.LogInformation($"Updated default guest theme to: {themeId}");

        // Broadcast theme change to all connected clients
        // Only guest users with selectedTheme=null will apply this change
        await _notifications.NotifyAllAsync(SignalREvents.DefaultGuestThemeChanged, new
        {
            newThemeId = themeId
        });

        _logger.LogInformation($"Broadcasted DefaultGuestThemeChanged event for theme: {themeId}");

        return Ok(new ThemePreferenceResponse
        {
            Success = true,
            ThemeId = themeId!,
            Message = "Default guest theme saved successfully"
        });
    }
}
