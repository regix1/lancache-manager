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
            
            var themeFiles = Directory.GetFiles(_themesPath, "*.json");
            
            // Define which themes are system themes
            var systemThemes = new[] { "dark-default", "light-default", "high-contrast" };
            
            foreach (var file in themeFiles)
            {
                try
                {
                    var content = await System.IO.File.ReadAllTextAsync(file);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;
                    
                    var themeId = Path.GetFileNameWithoutExtension(file);
                    
                    themes.Add(new
                    {
                        id = themeId,
                        name = root.TryGetProperty("name", out var n) ? n.GetString() : themeId,
                        description = root.TryGetProperty("description", out var d) ? d.GetString() : "",
                        author = root.TryGetProperty("author", out var a) ? a.GetString() : "Unknown",
                        version = root.TryGetProperty("version", out var v) ? v.GetString() : "1.0.0",
                        isDefault = systemThemes.Contains(themeId) // Mark system themes properly
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
            
            var filePath = Path.Combine(_themesPath, $"{id}.json");
            
            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning($"Theme not found: {id}");
                return NotFound(new { error = "Theme not found" });
            }
            
            var content = await System.IO.File.ReadAllTextAsync(filePath);
            var theme = JsonSerializer.Deserialize<JsonElement>(content);
            
            return Ok(theme);
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
        
        if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Only JSON theme files are allowed" });
        }
        
        if (file.Length > 1024 * 1024) // 1MB max
        {
            return BadRequest(new { error = "Theme file too large (max 1MB)" });
        }
        
        try
        {
            // Read and validate JSON
            using var stream = file.OpenReadStream();
            using var doc = await JsonDocument.ParseAsync(stream);
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
            
            // Validate color format
            foreach (var color in colors.EnumerateObject())
            {
                if (!color.Name.StartsWith("--"))
                {
                    return BadRequest(new { error = $"Color property '{color.Name}' must start with '--'" });
                }
                
                var value = color.Value.GetString();
                if (string.IsNullOrEmpty(value) || (!value.StartsWith("#") && !value.StartsWith("rgb")))
                {
                    return BadRequest(new { error = $"Invalid color value for '{color.Name}'" });
                }
            }
            
            // Generate safe filename
            var themeName = root.GetProperty("name").GetString();
            var themeId = Regex.Replace(themeName, @"[^a-zA-Z0-9-_]", "-").ToLower();
            themeId = themeId.Substring(0, Math.Min(themeId.Length, 50)); // Limit length
            
            // Ensure unique ID
            var counter = 0;
            var baseId = themeId;
            while (System.IO.File.Exists(Path.Combine(_themesPath, $"{themeId}.json")))
            {
                counter++;
                themeId = $"{baseId}-{counter}";
            }
            
            var filePath = Path.Combine(_themesPath, $"{themeId}.json");
            
            // Reset stream and save
            stream.Position = 0;
            using var fileStream = new FileStream(filePath, FileMode.Create);
            await stream.CopyToAsync(fileStream);
            
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
        
        // Define system themes that cannot be deleted
        var systemThemes = new[] { "dark-default", "light-default", "high-contrast" };
        
        // Prevent deletion of system themes
        if (systemThemes.Contains(id))
        {
            return BadRequest(new { error = "Cannot delete system themes. These are built-in themes required for the application." });
        }
        
        try
        {
            var filePath = Path.Combine(_themesPath, $"{id}.json");
            
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound(new { error = "Theme not found" });
            }
            
            System.IO.File.Delete(filePath);
            
            _logger.LogInformation($"Theme deleted: {id} by {HttpContext.Connection.RemoteIpAddress}");
            
            return Ok(new { success = true, message = "Theme deleted successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to delete theme {id}");
            return StatusCode(500, new { error = "Failed to delete theme" });
        }
    }

    private void InitializeDefaultThemes()
    {
        try
        {
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
                        ["--bg-dropdown"] = "#1f2937",  // Dark dropdown background for dark theme
                        ["--bg-dropdown-hover"] = "#374151",  // Darker gray hover for dark theme
                        ["--bg-nav"] = "#1f2937",
                        
                        // Borders
                        ["--border-primary"] = "#374151",
                        ["--border-secondary"] = "#4b5563",
                        ["--border-input"] = "#4b5563",
                        ["--border-nav"] = "#374151",
                        ["--border-dropdown"] = "#374151",  // Dark gray dropdown border
                        
                        // Text colors
                        ["--text-primary"] = "#ffffff",
                        ["--text-secondary"] = "#d1d5db",
                        ["--text-muted"] = "#9ca3af",
                        ["--text-disabled"] = "#6b7280",
                        ["--text-button"] = "#ffffff",
                        ["--text-dropdown"] = "#ffffff",  // White text for dark dropdown
                        ["--text-dropdown-item"] = "#ffffff",  // White text for dark dropdown items
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
                        ["--bg-dropdown"] = "#ffffff",  // White dropdown background
                        ["--bg-dropdown-hover"] = "#e5e7eb",  // Light gray hover for light theme
                        ["--bg-nav"] = "#ffffff",
                        
                        // Borders
                        ["--border-primary"] = "#e5e7eb",
                        ["--border-secondary"] = "#d1d5db",
                        ["--border-input"] = "#d1d5db",
                        ["--border-nav"] = "#e5e7eb",
                        ["--border-dropdown"] = "#9ca3af",  // Gray border for light theme
                        
                        // Text colors
                        ["--text-primary"] = "#111827",
                        ["--text-secondary"] = "#374151",
                        ["--text-muted"] = "#6b7280",
                        ["--text-disabled"] = "#9ca3af",
                        ["--text-button"] = "#ffffff",
                        ["--text-dropdown"] = "#111827",  // Black text for dropdown
                        ["--text-dropdown-item"] = "#111827",  // Black text for dropdown items
                        ["--text-input"] = "#111827",
                        ["--text-placeholder"] = "#9ca3af",
                        ["--text-nav"] = "#374151",
                        ["--text-nav-active"] = "#1d4ed8",
                        
                        // Icon colors
                        ["--icon-primary"] = "#6b7280",
                        ["--icon-button"] = "#ffffff",
                        ["--icon-muted"] = "#9ca3af",
                        
                        // Accent colors
                        ["--accent-blue"] = "#1d4ed8",
                        ["--accent-green"] = "#15803d",
                        ["--accent-yellow"] = "#a16207",
                        ["--accent-red"] = "#b91c1c",
                        ["--accent-purple"] = "#7c3aed",
                        ["--accent-cyan"] = "#0891b2",
                        ["--accent-orange"] = "#ea580c",
                        ["--accent-pink"] = "#be185d",
                        
                        // Status colors
                        ["--success"] = "#15803d",
                        ["--warning"] = "#a16207",
                        ["--error"] = "#b91c1c",
                        ["--info"] = "#1d4ed8"
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