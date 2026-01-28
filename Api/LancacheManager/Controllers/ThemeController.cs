using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Models;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Security;
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
    private static readonly string[] SYSTEM_THEMES = { "dark-default", "light-default" };

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

        // Ensure themes directory exists
        if (!Directory.Exists(_themesPath))
        {
            Directory.CreateDirectory(_themesPath);
            _logger.LogInformation($"Created themes directory: {_themesPath}");
        }

        // Frontend theme service handles built-in themes, backend only manages custom uploaded themes
    }

    private bool ThemeExists(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        if (SYSTEM_THEMES.Contains(themeId))
        {
            return true;
        }

        var tomlPath = Path.Combine(_themesPath, $"{themeId}.toml");
        var jsonPath = Path.Combine(_themesPath, $"{themeId}.json");
        return System.IO.File.Exists(tomlPath) || System.IO.File.Exists(jsonPath);
    }

    [HttpGet]
    public async Task<IActionResult> GetThemes()
    {
        var themes = new List<ThemeInfo>();

        if (!Directory.Exists(_themesPath))
        {
            Directory.CreateDirectory(_themesPath);
            _logger.LogInformation($"Created themes directory: {_themesPath}");
        }

        // Get both JSON and TOML files
        var jsonFiles = Directory.GetFiles(_themesPath, "*.json");
        var tomlFiles = Directory.GetFiles(_themesPath, "*.toml");
        var themeFiles = jsonFiles.Concat(tomlFiles).ToArray();

        // System themes are provided by frontend but marked as protected
        var systemThemes = SYSTEM_THEMES;

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
                    IsDefault = systemThemes.Contains(themeId),
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

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTheme(string id)
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
            return NotFound(new ErrorResponse { Error = "Theme not found" });
        }

        var content = await System.IO.File.ReadAllTextAsync(jsonPath);
        var jsonTheme = JsonSerializer.Deserialize<JsonElement>(content);

        return Ok(jsonTheme);
    }

    [HttpPost("upload")]
    [RequireAuth]
    public async Task<IActionResult> UploadTheme(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new ErrorResponse { Error = "No file provided" });
        }

        var isToml = file.FileName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);
        var isJson = file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        if (!isToml && !isJson)
        {
            return BadRequest(new ErrorResponse { Error = "Only TOML and JSON theme files are allowed" });
        }

        if (file.Length > 1024 * 1024) // 1MB max
        {
            return BadRequest(new ErrorResponse { Error = "Theme file too large (max 1MB)" });
        }

        try
        {
            string themeId;
            string filePath;

            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();

            if (isToml)
            {
                // For TOML, just save it directly
                // Generate safe filename from the original filename
                var baseName = Path.GetFileNameWithoutExtension(file.FileName);
                themeId = Regex.Replace(baseName, @"[^a-zA-Z0-9-_]", "-").ToLower();
                themeId = themeId.Substring(0, Math.Min(themeId.Length, 50));

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
                    return BadRequest(new ErrorResponse { Error = "Theme must have a 'name' property" });
                }

                if (!root.TryGetProperty("colors", out var colors) || colors.ValueKind != JsonValueKind.Object)
                {
                    return BadRequest(new ErrorResponse { Error = "Theme must have a 'colors' object" });
                }

                // Generate safe filename
                var themeName = root.GetProperty("name").GetString() ?? "custom-theme";
                themeId = Regex.Replace(themeName, @"[^a-zA-Z0-9-_]", "-").ToLower();
                themeId = themeId.Substring(0, Math.Min(themeId.Length, 50));

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
            return BadRequest(new ErrorResponse { Error = "Invalid JSON format" });
        }
    }

    [HttpDelete("{id}")]
    [RequireAuth]
    public IActionResult DeleteTheme(string id)
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

        // Prevent deletion of system themes
        if (SYSTEM_THEMES.Contains(id))
        {
            _logger.LogWarning($"Attempted to delete system theme: {id}");
            return BadRequest(new ErrorResponse
            {
                Error = "Cannot delete system theme",
                Details = $"'{id}' is a protected system theme and cannot be deleted"
            });
        }


        try
        {
            // Check for both TOML and JSON files
            var tomlPath = Path.Combine(_themesPath, $"{id}.toml");
            var jsonPath = Path.Combine(_themesPath, $"{id}.json");

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
                // Neither file existed - this is an error
                var availableFiles = Directory.GetFiles(_themesPath)
                    .Select(Path.GetFileName)
                    .ToArray();

                _logger.LogWarning($"Theme not found: {id}. Available files: {string.Join(", ", availableFiles)}");

                return NotFound(new ThemeNotFoundResponse
                {
                    Error = $"Theme '{id}' not found on server",
                    Details = $"No files matching '{id}.toml' or '{id}.json' were found",
                    AvailableThemes = availableFiles.Select(f => Path.GetFileNameWithoutExtension(f) ?? "").Where(s => !string.IsNullOrEmpty(s)).Distinct().ToArray()
                });
            }

            if (errors.Count > 0 && filesDeleted.Count == 0)
            {
                // Files existed but couldn't be deleted
                return StatusCode(500, new ErrorResponse
                {
                    Error = "Failed to delete theme",
                    Details = string.Join("; ", errors)
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
            return StatusCode(500, new ErrorResponse { Error = "Permission denied - cannot delete theme file" });
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, $"IO error when deleting theme {id}");
            return StatusCode(500, new ErrorResponse { Error = $"IO error: {ex.Message}" });
        }
    }

    [HttpPost("cleanup")]
    [RequireAuth]
    public IActionResult CleanupThemes()
    {
        var deletedThemes = new List<string>();
        var errors = new List<string>();

        _logger.LogInformation("Starting theme cleanup operation");

        // Get all theme files
        var themeFiles = Directory.GetFiles(_themesPath, "*.toml")
            .Concat(Directory.GetFiles(_themesPath, "*.json"))
            .ToArray();

        _logger.LogInformation($"Found {themeFiles.Length} theme files to process");

        foreach (var file in themeFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(file);

            // Skip system themes
            if (SYSTEM_THEMES.Contains(fileName))
            {
                _logger.LogInformation($"Skipping system theme: {fileName}");
                continue;
            }

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
            RemainingThemes = SYSTEM_THEMES
        });
    }

    // Theme Preference Endpoints
    [HttpGet("preference")]
    public IActionResult GetThemePreference()
    {
        var themeId = _stateRepository.GetSelectedTheme() ?? "dark-default";
        _logger.LogInformation($"Retrieved theme preference: {themeId}");

        return Ok(new ThemePreferenceResponse
        {
            ThemeId = themeId
        });
    }

    [HttpPut("preference")]
    [RequireAuth]
    public IActionResult SetThemePreference([FromBody] ThemePreferenceRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ThemeId))
        {
            return BadRequest(new ErrorResponse { Error = "Theme ID is required" });
        }

        // Sanitize theme ID
        var themeId = Regex.Replace(request.ThemeId, @"[^a-zA-Z0-9-_]", "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return BadRequest(new ErrorResponse { Error = "Theme ID is required" });
        }

        if (!ThemeExists(themeId))
        {
            return NotFound(new ErrorResponse { Error = "Theme not found" });
        }

        _stateRepository.SetSelectedTheme(themeId);
        _logger.LogInformation($"Updated theme preference to: {themeId}");

        return Ok(new ThemePreferenceResponse
        {
            Success = true,
            ThemeId = themeId,
            Message = "Theme preference saved successfully"
        });
    }

    // Default Guest Theme Endpoints
    [HttpGet("preferences/guest")]
    public IActionResult GetDefaultGuestTheme()
    {
        var themeId = _stateRepository.GetDefaultGuestTheme() ?? "dark-default";
        _logger.LogInformation($"Retrieved default guest theme: {themeId}");

        return Ok(new ThemePreferenceResponse
        {
            ThemeId = themeId
        });
    }

    [HttpPut("preferences/guest")]
    [RequireAuth]
    public async Task<IActionResult> SetDefaultGuestTheme([FromBody] ThemePreferenceRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.ThemeId))
        {
            return BadRequest(new ErrorResponse { Error = "Theme ID is required" });
        }

        // Sanitize theme ID
        var themeId = Regex.Replace(request.ThemeId, @"[^a-zA-Z0-9-_]", "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return BadRequest(new ErrorResponse { Error = "Theme ID is required" });
        }

        if (!ThemeExists(themeId))
        {
            return NotFound(new ErrorResponse { Error = "Theme not found" });
        }

        _stateRepository.SetDefaultGuestTheme(themeId);
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
            ThemeId = themeId,
            Message = "Default guest theme saved successfully"
        });
    }
}
