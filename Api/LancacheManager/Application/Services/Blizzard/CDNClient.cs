using LancacheManager.Application.Services.Blizzard.Extensions;

namespace LancacheManager.Application.Services.Blizzard;

/// <summary>
/// HTTP client for downloading files from Blizzard's CDN.
/// </summary>
public class CDNClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private string? _cdnHost;
    private string _cdnPath;
    private readonly string _product;
    private readonly ILogger? _logger;

    private const string DEFAULT_CDN = "us.cdn.blizzard.com";

    public CDNClient(string product = "wow", string region = "us", ILogger? logger = null)
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _product = product;
        _cdnPath = $"/{product}";  // Default, will be updated from CDNs file
        _logger = logger;
        // CDN host and path will be resolved on first use
    }

    /// <summary>
    /// Ensures CDN host and path are resolved from the CDNs file
    /// </summary>
    private async Task EnsureCdnHostAsync()
    {
        if (_cdnHost != null)
        {
            return;
        }

        try
        {
            // Get the CDNs list from Blizzard
            var cdnsContent = await GetCDNsAsync(_product);
            var (cdnHost, cdnPath) = ParseCdnHostAndPath(cdnsContent);
            _cdnHost = cdnHost ?? DEFAULT_CDN;

            // Use the path from CDNs file if available
            if (!string.IsNullOrEmpty(cdnPath))
            {
                _cdnPath = cdnPath;
                _logger?.LogDebug("Using CDN path from CDNs file: {Path}", _cdnPath);
            }

            _logger?.LogDebug("Using CDN host: {Host}", _cdnHost);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to get CDN host from CDNs file, using default: {Default}", DEFAULT_CDN);
            _cdnHost = DEFAULT_CDN;
        }
    }

    /// <summary>
    /// Parses the CDNs file to extract available CDN hosts and path
    /// </summary>
    private (string? host, string? path) ParseCdnHostAndPath(string content)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            return (null, null);
        }

        // Find the header line - headers may include type info after !
        var headers = lines[0].Split('|').Select(h => h.Split('!')[0].Trim()).ToArray();
        var hostsIdx = Array.IndexOf(headers, "Hosts");
        var pathIdx = Array.IndexOf(headers, "Path");

        if (hostsIdx == -1)
        {
            _logger?.LogWarning("Missing Hosts header in CDNs file. Headers: {Headers}", string.Join(", ", headers));
            return (null, null);
        }

        // Parse the last (most recent) entry
        var lastLine = lines[lines.Length - 1];
        var values = lastLine.Split('|');

        if (values.Length <= hostsIdx)
        {
            _logger?.LogWarning("Not enough values in CDNs file");
            return (null, null);
        }

        // Get the path if available
        string? cdnPath = null;
        if (pathIdx != -1 && values.Length > pathIdx)
        {
            cdnPath = values[pathIdx].Trim();
            if (!string.IsNullOrEmpty(cdnPath) && !cdnPath.StartsWith('/'))
            {
                cdnPath = "/" + cdnPath;
            }
        }

        // Hosts can be space-separated - try to find a working one
        var hosts = values[hostsIdx].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Log all available hosts for debugging
        _logger?.LogDebug("Available CDN hosts: {Hosts}", string.Join(", ", hosts));

        // Prefer hosts that are not level3 (they often give 403)
        // Priority: us.cdn.blizzard.com, eu.cdn.blizzard.com, kr.cdn.blizzard.com, then others
        var preferredHosts = new[] { "us.cdn.blizzard.com", "eu.cdn.blizzard.com", "kr.cdn.blizzard.com" };

        foreach (var preferred in preferredHosts)
        {
            if (hosts.Contains(preferred))
            {
                _logger?.LogDebug("Using preferred CDN host: {Host}", preferred);
                return (preferred, cdnPath);
            }
        }

        // If no preferred host, use the first non-level3 host
        var nonLevel3Host = hosts.FirstOrDefault(h => !h.Contains("level3"));
        if (nonLevel3Host != null)
        {
            _logger?.LogDebug("Using first non-level3 host: {Host}", nonLevel3Host);
            return (nonLevel3Host, cdnPath);
        }

        // Fallback to first host if all else fails
        var fallbackHost = hosts.Length > 0 ? hosts[0] : null;
        return (fallbackHost, cdnPath);
    }

    public async Task<byte[]> DownloadFileAsync(string folder, MD5Hash hash, bool isIndex = false)
    {
        await EnsureCdnHostAsync();

        var hashStr = hash.ToHexString();
        var path = $"{_cdnPath}/{folder}/{hashStr.Substring(0, 2)}/{hashStr.Substring(2, 2)}/{hashStr}";

        if (isIndex)
        {
            path += ".index";
        }

        var url = $"http://{_cdnHost}{path}";

        _logger?.LogDebug("Downloading: {Url}", url);

        try
        {
            return await _httpClient.GetByteArrayAsync(url);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error downloading {Url}", url);
            throw;
        }
    }

    public async Task<byte[]> DownloadFileAsync(string folder, string hash, bool isIndex = false)
    {
        return await DownloadFileAsync(folder, hash.ToMD5(), isIndex);
    }

    public async Task<byte[]> DownloadConfigAsync(MD5Hash hash)
    {
        return await DownloadFileAsync("config", hash);
    }

    public async Task<byte[]> DownloadDataAsync(MD5Hash hash, bool isIndex = false)
    {
        return await DownloadFileAsync("data", hash, isIndex);
    }

    public async Task<string> GetVersionsAsync(string product)
    {
        var url = $"http://us.patch.battle.net:1119/{product}/versions";
        _logger?.LogDebug("Downloading versions: {Url}", url);
        return await _httpClient.GetStringAsync(url);
    }

    public async Task<string> GetCDNsAsync(string product)
    {
        var url = $"http://us.patch.battle.net:1119/{product}/cdns";
        _logger?.LogDebug("Downloading CDNs: {Url}", url);
        return await _httpClient.GetStringAsync(url);
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
