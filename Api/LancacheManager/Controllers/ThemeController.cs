using Microsoft.AspNetCore.Mvc;
using LancacheManager.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ThemeController : ControllerBase
{
    private readonly string _themesPath;
    private readonly ILogger<ThemeController> _logger;
    private const string DEFAULT_THEME_NAME = "dark-default";

    public ThemeController(IConfiguration configuration, ILogger<ThemeController> logger)
    {
        _logger = logger;
        
        _themesPath = Path.Combine("/data", "themes");

        // Ensure themes directory exists
        if (!Directory.Exists(_themesPath))
        {
            Directory.CreateDirectory(_themesPath);
            _logger.LogInformation($"Created themes directory: {_themesPath}");
        }

        // Initialize default themes
        InitializeDefaultThemes();
    }

    [HttpGet]
    public async Task<IActionResult> GetThemes()
    {
        try
        {
            var themes = new List<object>();

            if (!Directory.Exists(_themesPath))
            {
                InitializeDefaultThemes();
            }

            // Get both JSON and TOML files
            var jsonFiles = Directory.GetFiles(_themesPath, "*.json");
            var tomlFiles = Directory.GetFiles(_themesPath, "*.toml");
            var themeFiles = jsonFiles.Concat(tomlFiles).ToArray();

            // Define which themes are system themes (removed high-contrast)
            var systemThemes = new[] { "dark-default", "light-default" };

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

                        name = root.TryGetProperty("name", out var n) ? n.GetString() : themeId;
                        description = root.TryGetProperty("description", out var d) ? d.GetString() : "";
                        author = root.TryGetProperty("author", out var a) ? a.GetString() : "Unknown";
                        version = root.TryGetProperty("version", out var v) ? v.GetString() : "1.0.0";
                    }

                    themes.Add(new
                    {
                        id = themeId,
                        name = name,
                        description = description,
                        author = author,
                        version = version,
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

                // Ensure unique ID
                var counter = 0;
                var baseId = themeId;
                while (System.IO.File.Exists(Path.Combine(_themesPath, $"{themeId}.toml")))
                {
                    counter++;
                    themeId = $"{baseId}-{counter}";
                }

                filePath = Path.Combine(_themesPath, $"{themeId}.toml");
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
                var themeName = root.GetProperty("name").GetString();
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
        // Sanitize ID
        id = Regex.Replace(id, @"[^a-zA-Z0-9-_]", "");

        // Define system themes that cannot be deleted (removed high-contrast)
        var systemThemes = new[] { "dark-default", "light-default" };

        // Prevent deletion of system themes
        if (systemThemes.Contains(id))
        {
            return BadRequest(new { error = "Cannot delete system themes. These are built-in themes required for the application." });
        }

        try
        {
            // Check for both TOML and JSON files
            var tomlPath = Path.Combine(_themesPath, $"{id}.toml");
            var jsonPath = Path.Combine(_themesPath, $"{id}.json");
            var deleted = false;

            if (System.IO.File.Exists(tomlPath))
            {
                System.IO.File.Delete(tomlPath);
                deleted = true;
                _logger.LogInformation($"Deleted TOML theme: {id}");
            }
            
            if (System.IO.File.Exists(jsonPath))
            {
                System.IO.File.Delete(jsonPath);
                deleted = true;
                _logger.LogInformation($"Deleted JSON theme: {id}");
            }
            
            if (!deleted)
            {
                // List all files for debugging
                var files = Directory.GetFiles(_themesPath);
                _logger.LogWarning($"Theme not found: {id}. Available files: {string.Join(", ", files.Select(Path.GetFileName))}");
                return NotFound(new { error = "Theme not found" });
            }

            _logger.LogInformation($"Theme deleted: {id} by {HttpContext.Connection.RemoteIpAddress}");

            return Ok(new { success = true, message = "Theme deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete theme {id}");
            return StatusCode(500, new { error = "Failed to delete theme" });
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
            // Get all theme files
            var themeFiles = Directory.GetFiles(_themesPath, "*.toml")
                .Concat(Directory.GetFiles(_themesPath, "*.json"))
                .ToArray();

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
                    deletedThemes.Add(fileName);
                    _logger.LogInformation($"Deleted theme: {fileName}");
                }
                catch (Exception ex)
                {
                    errors.Add($"Failed to delete {fileName}: {ex.Message}");
                    _logger.LogError(ex, $"Failed to delete theme file: {file}");
                }
            }

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

    private void InitializeDefaultThemes()
    {
        try
        {
            // Remove high-contrast if it exists
            var highContrastPath = Path.Combine(_themesPath, "high-contrast.json");
            if (System.IO.File.Exists(highContrastPath))
            {
                System.IO.File.Delete(highContrastPath);
                _logger.LogInformation("Removed deprecated high-contrast theme");
            }

            var darkThemePath = Path.Combine(_themesPath, $"{DEFAULT_THEME_NAME}.json");

            if (!System.IO.File.Exists(darkThemePath))
            {
                var darkTheme = new
                {
                    name = "Dark Default",
                    id = DEFAULT_THEME_NAME,
                    description = "Default dark theme for LanCache Monitor",
                    author = "System",
                    version = "2.2.0",
                    colors = new Dictionary<string, string>
                    {
                        // Backgrounds
                        ["--bg-primary"] = "#111827",
                        ["--bg-secondary"] = "#1f2937",
                        ["--bg-tertiary"] = "#374151",
                        ["--bg-hover"] = "#4b5563",
                        ["--bg-input"] = "#374151",
                        ["--bg-dropdown"] = "#1f2937",
                        ["--bg-dropdown-hover"] = "#374151",
                        ["--bg-nav"] = "#1f2937",

                        // Borders
                        ["--border-primary"] = "#374151",
                        ["--border-secondary"] = "#4b5563",
                        ["--border-input"] = "#4b5563",
                        ["--border-nav"] = "#374151",
                        ["--border-dropdown"] = "#374151",

                        // Text colors
                        ["--text-primary"] = "#ffffff",
                        ["--text-secondary"] = "#d1d5db",
                        ["--text-muted"] = "#9ca3af",
                        ["--text-disabled"] = "#6b7280",
                        ["--text-button"] = "#ffffff",
                        ["--text-dropdown"] = "#ffffff",
                        ["--text-dropdown-item"] = "#ffffff",
                        ["--text-input"] = "#ffffff",
                        ["--text-placeholder"] = "#9ca3af",
                        ["--text-nav"] = "#d1d5db",
                        ["--text-nav-active"] = "#3b82f6",

                        // Icon colors
                        ["--icon-primary"] = "#d1d5db",
                        ["--icon-button"] = "#ffffff",
                        ["--icon-muted"] = "#9ca3af",

                        // Accent colors
                        ["--accent-blue"] = "#3b82f6",
                        ["--accent-green"] = "#10b981",
                        ["--accent-yellow"] = "#f59e0b",
                        ["--accent-red"] = "#ef4444",
                        ["--accent-purple"] = "#8b5cf6",
                        ["--accent-cyan"] = "#06b6d4",
                        ["--accent-orange"] = "#f97316",
                        ["--accent-pink"] = "#ec4899",

                        // Status colors
                        ["--success"] = "#10b981",
                        ["--warning"] = "#f59e0b",
                        ["--error"] = "#ef4444",
                        ["--info"] = "#3b82f6"
                    }
                };

                var json = JsonSerializer.Serialize(darkTheme, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                System.IO.File.WriteAllText(darkThemePath, json);
                _logger.LogInformation("Created default dark theme v2.2");
            }

            // Create a light theme as well
            var lightThemePath = Path.Combine(_themesPath, "light-default.json");

            if (!System.IO.File.Exists(lightThemePath))
            {
                var lightTheme = new
                {
                    name = "Light Default",
                    id = "light-default",
                    description = "Default light theme for LanCache Monitor",
                    author = "System",
                    version = "2.2.0",
                    colors = new Dictionary<string, string>
                    {
                        // Backgrounds
                        ["--bg-primary"] = "#ffffff",
                        ["--bg-secondary"] = "#f9fafb",
                        ["--bg-tertiary"] = "#f3f4f6",
                        ["--bg-hover"] = "#e5e7eb",
                        ["--bg-input"] = "#ffffff",
                        ["--bg-dropdown"] = "#ffffff",
                        ["--bg-dropdown-hover"] = "#e5e7eb",
                        ["--bg-nav"] = "#ffffff",

                        // Borders
                        ["--border-primary"] = "#e5e7eb",
                        ["--border-secondary"] = "#d1d5db",
                        ["--border-input"] = "#d1d5db",
                        ["--border-nav"] = "#e5e7eb",
                        ["--border-dropdown"] = "#9ca3af",

                        // Text colors
                        ["--text-primary"] = "#111827",
                        ["--text-secondary"] = "#374151",
                        ["--text-muted"] = "#6b7280",
                        ["--text-disabled"] = "#9ca3af",
                        ["--text-button"] = "#ffffff",
                        ["--text-dropdown"] = "#111827",
                        ["--text-dropdown-item"] = "#111827",
                        ["--text-input"] = "#111827",
                        ["--text-placeholder"] = "#9ca3af",
                        ["--text-nav"] = "#374151",
                        ["--text-nav-active"] = "#1d4ed8",

                        // Icon colors
                        ["--icon-primary"] = "#6b7280",
                        ["--icon-button"] = "#ffffff",
                        ["--icon-muted"] = "#9ca3af",

                        // Accent colors - more vibrant for light backgrounds
                        ["--accent-blue"] = "#1d4ed8",
                        ["--accent-green"] = "#16a34a",
                        ["--accent-yellow"] = "#ca8a04",
                        ["--accent-red"] = "#dc2626",
                        ["--accent-purple"] = "#7c3aed",
                        ["--accent-cyan"] = "#0891b2",
                        ["--accent-orange"] = "#ea580c",
                        ["--accent-pink"] = "#be185d",

                        // Status colors - vibrant versions
                        ["--success"] = "#16a34a",
                        ["--warning"] = "#ca8a04",
                        ["--error"] = "#dc2626",
                        ["--info"] = "#2563eb"
                    }
                };

                var json = JsonSerializer.Serialize(lightTheme, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                System.IO.File.WriteAllText(lightThemePath, json);
                _logger.LogInformation("Created default light theme v2.2");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize default themes");
        }
    }
}