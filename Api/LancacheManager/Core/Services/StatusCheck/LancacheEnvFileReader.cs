using LancacheManager.Core.Interfaces;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// Generic lancache <c>.env</c> discovery + parse, extracted from the private discovery chain that
/// used to live only inside <see cref="LancacheManager.Core.Services.CacheManagementService.ReadCacheSizeFromEnvFile"/>.
/// That method now delegates to this reader (see the refactor there) so there is exactly one copy
/// of the path list. Fails soft everywhere - a missing/unreadable .env file is logged at Debug and
/// callers simply get <c>null</c> back, never an exception.
/// </summary>
public sealed class LancacheEnvFileReader : ILancacheEnvFileReader
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<LancacheEnvFileReader> _logger;
    private readonly string _cachePath;

    private readonly object _lock = new();
    private string? _resolvedPath;
    private DateTime _resolvedPathWriteTimeUtc;
    private Dictionary<string, string>? _cachedValues;

    public LancacheEnvFileReader(
        IConfiguration configuration,
        ILogger<LancacheEnvFileReader> logger,
        DatasourceService datasourceService,
        IPathResolver pathResolver)
    {
        _configuration = configuration;
        _logger = logger;

        // Same cache-path resolution CacheManagementService uses, so the ".env near the cache path"
        // fallback entries agree between the two services.
        var defaultDatasource = datasourceService.GetDefaultDatasource();
        if (defaultDatasource != null)
        {
            _cachePath = defaultDatasource.CachePath;
        }
        else
        {
            var configCachePath = configuration["LanCache:CachePath"];
            _cachePath = !string.IsNullOrEmpty(configCachePath)
                ? pathResolver.ResolvePath(configCachePath)
                : pathResolver.GetCacheDirectory();
        }
    }

    public string? ResolvedPath
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _resolvedPath;
            }
        }
    }

    public string? TryGetValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        lock (_lock)
        {
            EnsureLoaded();
            return _cachedValues != null && _cachedValues.TryGetValue(key, out var value) ? value : null;
        }
    }

    /// <summary>Must be called under <see cref="_lock"/>.</summary>
    private void EnsureLoaded()
    {
        var candidatePath = DiscoverEnvFilePath();
        if (candidatePath == null)
        {
            _resolvedPath = null;
            _cachedValues = null;
            return;
        }

        DateTime writeTimeUtc;
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(candidatePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stat lancache .env file at {Path}", candidatePath);
            _resolvedPath = null;
            _cachedValues = null;
            return;
        }

        if (_cachedValues != null && _resolvedPath == candidatePath && _resolvedPathWriteTimeUtc == writeTimeUtc)
        {
            return; // cache still fresh
        }

        try
        {
            _cachedValues = ParseEnvFile(candidatePath);
            _resolvedPath = candidatePath;
            _resolvedPathWriteTimeUtc = writeTimeUtc;
            _logger.LogDebug("Loaded lancache .env file from {Path} ({Count} keys)", candidatePath, _cachedValues.Count);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read lancache .env file at {Path}", candidatePath);
            _resolvedPath = null;
            _cachedValues = null;
        }
    }

    /// <summary>
    /// Discovery order copied EXACTLY from the original <c>ReadCacheSizeFromEnvFile</c>: config
    /// override first, then the 3 absolute paths, then 2 cache-path-relative paths.
    /// </summary>
    private string? DiscoverEnvFilePath()
    {
        var configuredPath = _configuration["LanCache:EnvFilePath"];
        if (!string.IsNullOrEmpty(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        var possiblePaths = new[]
        {
            "/srv/lancache/.env",
            "/opt/lancache/.env",
            "/lancache/.env",
            Path.Combine(Path.GetDirectoryName(_cachePath) ?? "", ".env"),
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(_cachePath) ?? "") ?? "", ".env")
        };

        foreach (var path in possiblePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Case-insensitive key match; trims whitespace and a single pair of wrapping quotes
    /// from each value. Internal + <c>InternalsVisibleTo("LancacheManager.Tests")</c> for direct
    /// unit coverage of the parse rules without touching disk.</summary>
    internal static Dictionary<string, string> ParseEnvFile(string path)
    {
        return ParseEnvLines(File.ReadAllLines(path));
    }

    internal static Dictionary<string, string> ParseEnvLines(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            {
                value = value[1..^1];
            }
            else
            {
                // Unquoted values can carry an inline comment (DISABLE_STEAM=true # note) - drop
                // it when the # is preceded by whitespace, matching docker-compose's .env rules.
                for (var i = 1; i < value.Length; i++)
                {
                    if (value[i] == '#' && char.IsWhiteSpace(value[i - 1]))
                    {
                        value = value[..i].TrimEnd();
                        break;
                    }
                }
            }

            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }
}
