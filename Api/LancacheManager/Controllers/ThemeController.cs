using Microsoft.AspNetCore.Mvc;
using LancacheManager.Security;
using LancacheManager.Constants;
using LancacheManager.Services;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ThemeController : ControllerBase
{
    private readonly string _themesPath;
    private readonly ILogger<ThemeController> _logger;

    public ThemeController(IConfiguration configuration, ILogger<ThemeController> logger, IPathResolver pathResolver)
    {
        _logger = logger;

        _themesPath = pathResolver.GetThemesDirectory();

        // Ensure themes directory exists
        if (!Directory.Exists(_themesPath))
        {
            Directory.CreateDirectory(_themesPath);
            _logger.LogInformation($"Created themes directory: {_themesPath}");
        }

        // Frontend theme service handles built-in themes, backend only manages custom uploaded themes
    }

    [HttpGet]
    public async Task<IActionResult> GetThemes()
    {
        try
        {
            var themes = new List<object>();

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
            var systemThemes = LancacheConstants.SYSTEM_THEMES;

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

                    themes.Add(new
                    {
                        id = themeId,
                        name,
                        description,
                        author,
                        version,
                        isDefault = systemThemes.Contains(themeId),
                        format = file.EndsWith(".toml") ? "toml" : "json"
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to parse theme file: {file}");
                }
            }

            return Ok(themes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get themes");
            return StatusCode(500, new { error = "Failed to load themes" });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetTheme(string id)
    {
        try
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
                return NotFound(new { error = "Theme not found" });
            }

            var content = await System.IO.File.ReadAllTextAsync(jsonPath);
            var jsonTheme = JsonSerializer.Deserialize<JsonElement>(content);

            return Ok(jsonTheme);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get theme {id}");
            return StatusCode(500, new { error = "Failed to load theme" });
        }
    }

    [HttpPost("upload")]
    [RequireAuth]
    public async Task<IActionResult> UploadTheme(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { error = "No file provided" });
        }

        var isToml = file.FileName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);
        var isJson = file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        if (!isToml && !isJson)
        {
            return BadRequest(new { error = "Only TOML and JSON theme files are allowed" });
        }

        if (file.Length > 1024 * 1024) // 1MB max
        {
            return BadRequest(new { error = "Theme file too large (max 1MB)" });
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
                    return BadRequest(new { error = "Theme must have a 'name' property" });
                }

                if (!root.TryGetProperty("colors", out var colors) || colors.ValueKind != JsonValueKind.Object)
                {
                    return BadRequest(new { error = "Theme must have a 'colors' object" });
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

            return Ok(new
            {
                success = true,
                themeId = themeId,
                message = "Theme uploaded successfully"
            });
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in theme upload");
            return BadRequest(new { error = "Invalid JSON format" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload theme");
            return StatusCode(500, new { error = "Failed to upload theme" });
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
        if (LancacheConstants.SYSTEM_THEMES.Contains(id))
        {
            _logger.LogWarning($"Attempted to delete system theme: {id}");
            return BadRequest(new {
                error = "Cannot delete system theme",
                details = $"'{id}' is a protected system theme and cannot be deleted"
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
                
                return NotFound(new 
                { 
                    error = $"Theme '{id}' not found on server",
                    details = $"No files matching '{id}.toml' or '{id}.json' were found",
                    availableThemes = availableFiles.Select(f => Path.GetFileNameWithoutExtension(f)).Distinct().ToArray()
                });
            }
            
            if (errors.Count > 0 && filesDeleted.Count == 0)
            {
                // Files existed but couldn't be deleted
                return StatusCode(500, new 
                { 
                    error = "Failed to delete theme",
                    details = string.Join("; ", errors)
                });
            }
            
            // At least one file was deleted successfully
            _logger.LogInformation($"Theme deletion completed for '{id}'. Deleted: {string.Join(", ", filesDeleted)}. Errors: {string.Join(", ", errors)}");
            
            return Ok(new 
            { 
                success = true, 
                message = $"Theme '{id}' deleted successfully",
                filesDeleted = filesDeleted,
                errors = errors
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, $"Permission denied when deleting theme {id}");
            return StatusCode(500, new { error = "Permission denied - cannot delete theme file" });
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, $"IO error when deleting theme {id}");
            return StatusCode(500, new { error = $"IO error: {ex.Message}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Unexpected error while deleting theme {id}");
            return StatusCode(500, new { error = $"Unexpected error while deleting theme: {ex.Message}" });
        }
    }

    [HttpPost("cleanup")]
    [RequireAuth]
    public IActionResult CleanupThemes()
    {
        var systemThemes = new[] { "dark-default", "light-default" };
        var deletedThemes = new List<string>();
        var errors = new List<string>();

        try
        {
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
                if (systemThemes.Contains(fileName))
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
            
            return Ok(new 
            { 
                success = true, 
                message = $"Cleanup complete. Deleted {deletedThemes.Count} theme(s)",
                deletedThemes,
                errors,
                remainingThemes = systemThemes
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup themes");
            return StatusCode(500, new { error = "Failed to cleanup themes", details = ex.Message });
        }
    }
}