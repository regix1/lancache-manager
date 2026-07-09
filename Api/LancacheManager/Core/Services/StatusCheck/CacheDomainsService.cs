using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// Acquires the uklans/cache-domains service/domain list: downloads the branch archive tarball
/// from GitHub in a SINGLE request (a raw.githubusercontent.com request per file gets the burst
/// 429-rate-limited - observed live as 15 of 26 services "none resolving" because their domain
/// files never arrived), tolerantly parses the 4 manifest shapes the ecosystem uses (design ported
/// from the monolithic fork's Go parser, extended to keep the actual domain strings - the fork
/// only kept counts), and persists the extracted files under
/// <see cref="IPathResolver.GetDataDirectory"/>/cache-domains/ so a restart or a NOFETCH deployment
/// can still serve a domain list.
/// </summary>
public sealed class CacheDomainsService : ICacheDomainsService
{
    // THE hardcoded-default site: CACHE_DOMAINS_REPO / CACHE_DOMAINS_BRANCH / NOFETCH each fall back
    // to these independently when absent from the lancache .env file.
    private const string DefaultRepoUrl = "https://github.com/uklans/cache-domains.git";
    private const string DefaultBranch = "master";
    private const bool DefaultNoFetch = false;

    private static readonly TimeSpan _staleAfter = TimeSpan.FromHours(24);

    private readonly ILogger<CacheDomainsService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ILancacheEnvFileReader _envReader;
    private readonly ILancacheEnvironmentSource _environmentSource;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISignalRNotificationService _notifications;
    private readonly string _domainsDirectory;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CacheDomainsList? _cachedList;
    private DomainsSource? _cachedSource;

    public CacheDomainsService(
        ILogger<CacheDomainsService> logger,
        IPathResolver pathResolver,
        ILancacheEnvFileReader envReader,
        ILancacheEnvironmentSource environmentSource,
        IHttpClientFactory httpClientFactory,
        ISignalRNotificationService notifications)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _envReader = envReader;
        _environmentSource = environmentSource;
        _httpClientFactory = httpClientFactory;
        _notifications = notifications;
        _domainsDirectory = Path.Combine(_pathResolver.GetDataDirectory(), "cache-domains");
    }

    public async Task<CacheDomainsList> GetDomainsAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var (list, _) = await LoadAsync(forceRefresh, cancellationToken);
        return list;
    }

    public async Task<CacheDomainsRefreshOutcome> RefreshDomainsAsync(CancellationToken cancellationToken)
    {
        var noFetchResult = await _environmentSource.GetValueAsync("NOFETCH", cancellationToken);
        var noFetch = EnvValueParsing.ParseBool(noFetchResult.Value) ?? DefaultNoFetch;
        if (noFetch)
        {
            // Never touch the network while NOFETCH is set - serve whatever is currently cached
            // (disk copy or empty) and let the controller turn this into a 409.
            var (list, source) = await LoadAsync(forceRefresh: false, cancellationToken);
            return new CacheDomainsRefreshOutcome
            {
                Success = false,
                BlockedReason = "NOFETCH is enabled in the lancache .env file; refresh is disabled. Set NOFETCH=false (or remove it) to allow fetching an updated domain list.",
                Domains = list,
                Source = source
            };
        }

        var (refreshedList, refreshedSource) = await LoadAsync(forceRefresh: true, cancellationToken);
        return new CacheDomainsRefreshOutcome { Success = true, Domains = refreshedList, Source = refreshedSource };
    }

    public DomainsSource GetCurrentSource()
    {
        // Sync convenience path for a cold-start GET before anything has ever triggered LoadAsync -
        // deliberately file-tier-only (no Docker round trip on a plain status read); once LoadAsync
        // has run at least once (sweep, dropdown fetch, or refresh), _cachedSource (tiered, via
        // LoadAsync) is returned instead and this branch is never hit again.
        if (_cachedSource != null)
        {
            return _cachedSource;
        }

        var repoFromFile = _envReader.TryGetValue("CACHE_DOMAINS_REPO");
        var branchFromFile = _envReader.TryGetValue("CACHE_DOMAINS_BRANCH");
        var noFetchFromFile = _envReader.TryGetValue("NOFETCH");

        return new DomainsSource
        {
            RepoUrl = FirstNonEmpty(repoFromFile, DefaultRepoUrl),
            Branch = FirstNonEmpty(branchFromFile, DefaultBranch),
            EnvFilePath = _envReader.ResolvedPath,
            NoFetch = EnvValueParsing.ParseBool(noFetchFromFile) ?? DefaultNoFetch,
            EnvSource = repoFromFile != null || branchFromFile != null || noFetchFromFile != null ? "envFile" : "defaults",
            FromCache = false
        };
    }

    private async Task<(CacheDomainsList List, DomainsSource Source)> LoadAsync(bool forceRefresh, CancellationToken ct)
    {
        var repoResult = await _environmentSource.GetValueAsync("CACHE_DOMAINS_REPO", ct);
        var branchResult = await _environmentSource.GetValueAsync("CACHE_DOMAINS_BRANCH", ct);
        var noFetchResult = await _environmentSource.GetValueAsync("NOFETCH", ct);

        var repoUrl = FirstNonEmpty(repoResult.Value, DefaultRepoUrl);
        var branch = FirstNonEmpty(branchResult.Value, DefaultBranch);
        var noFetch = EnvValueParsing.ParseBool(noFetchResult.Value) ?? DefaultNoFetch;
        var envSource = DetermineEnvSource(repoResult, branchResult, noFetchResult);
        // Contract v1.2: envFilePath is null when Docker inspect supplied any of the 3 variables.
        var envFilePath = envSource == "dockerInspect" ? null : _envReader.ResolvedPath;

        await _refreshLock.WaitAsync(ct);
        try
        {
            var isStale = _cachedSource?.FetchedAtUtc is null ||
                          DateTime.UtcNow - _cachedSource.FetchedAtUtc.Value > _staleAfter;

            var attemptFetch = !noFetch && (forceRefresh || _cachedList == null || isStale);

            if (attemptFetch)
            {
                var fetched = await TryFetchFromGitHubAsync(repoUrl, branch, ct);
                if (fetched != null)
                {
                    _cachedList = fetched;
                    _cachedSource = new DomainsSource
                    {
                        RepoUrl = repoUrl,
                        Branch = branch,
                        EnvFilePath = envFilePath,
                        EnvSource = envSource,
                        NoFetch = noFetch,
                        FetchedAtUtc = DateTime.UtcNow,
                        FromCache = false
                    };

                    _notifications.NotifyAllFireAndForget(SignalREvents.CacheDomainsRefreshed, new { domainsSource = _cachedSource });
                    return (_cachedList, _cachedSource);
                }
            }

            if (_cachedList == null)
            {
                var disk = LoadFromDisk();
                if (disk != null)
                {
                    _cachedList = disk;
                    _cachedSource = new DomainsSource
                    {
                        RepoUrl = repoUrl,
                        Branch = branch,
                        EnvFilePath = envFilePath,
                        EnvSource = envSource,
                        NoFetch = noFetch,
                        FetchedAtUtc = GetDiskManifestWriteTimeUtc(),
                        FromCache = true
                    };
                }
                else
                {
                    _cachedList = new CacheDomainsList();
                    _cachedSource = new DomainsSource
                    {
                        RepoUrl = repoUrl,
                        Branch = branch,
                        EnvFilePath = envFilePath,
                        EnvSource = envSource,
                        NoFetch = noFetch,
                        FetchedAtUtc = null,
                        FromCache = false,
                        Error = noFetch
                            ? "NOFETCH is enabled and no cached cache-domains list exists on disk yet."
                            : "Unable to fetch the cache-domains list and no cached copy exists on disk."
                    };
                }
            }
            else
            {
                // Serving the in-memory cache (fetch skipped by NOFETCH/freshness, or fetch failed).
                _cachedSource = new DomainsSource
                {
                    RepoUrl = repoUrl,
                    Branch = branch,
                    EnvFilePath = envFilePath,
                    EnvSource = envSource,
                    NoFetch = noFetch,
                    FetchedAtUtc = _cachedSource?.FetchedAtUtc,
                    FromCache = true,
                    Error = attemptFetch ? "Fetch failed; serving the last known domain list." : null
                };
            }

            return (_cachedList, _cachedSource);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Reports the highest-priority tier that actually supplied a value among the 3
    /// domains-source variables (contract amendment v1.2): "dockerInspect" if any came from Docker,
    /// else "envFile" if any came from the .env file, else "defaults" (all 3 fell through to
    /// hardcoded consts).</summary>
    internal static string DetermineEnvSource(params EnvValueResult[] results)
    {
        if (results.Any(r => r.Value != null && r.Source == EnvValueSource.DockerInspect))
        {
            return "dockerInspect";
        }
        if (results.Any(r => r.Value != null && r.Source == EnvValueSource.EnvFile))
        {
            return "envFile";
        }
        return "defaults";
    }

    private async Task<CacheDomainsList?> TryFetchFromGitHubAsync(string repoUrl, string branch, CancellationToken ct)
    {
        var archiveUrl = TryConvertToArchiveUrl(repoUrl, branch);
        if (archiveUrl == null)
        {
            _logger.LogWarning("Status Check: CACHE_DOMAINS_REPO '{RepoUrl}' is not a recognizable GitHub URL; skipping fetch", repoUrl);
            return null;
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LancacheManager/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            httpClient.MaxResponseContentBufferSize = MaxArchiveBytes;

            var archiveBytes = await httpClient.GetByteArrayAsync(archiveUrl, ct);
            var files = ExtractArchiveFiles(new MemoryStream(archiveBytes));

            if (!files.TryGetValue("cache_domains.json", out var manifestJson))
            {
                _logger.LogWarning("Status Check: archive from {RepoUrl}@{Branch} contained no cache_domains.json", repoUrl, branch);
                return null;
            }

            var services = ParseCacheDomainsJson(manifestJson);
            if (services.Count == 0)
            {
                _logger.LogWarning("Status Check: cache_domains.json from {RepoUrl}@{Branch} had no recognizable service entries", repoUrl, branch);
                return null;
            }

            var result = new CacheDomainsList();
            var referencedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, description, fileNames, mixedContent) in services)
            {
                var domains = new List<string>();
                foreach (var file in fileNames)
                {
                    var fileName = SanitizeFileName(file);
                    if (files.TryGetValue(fileName, out var fileContent))
                    {
                        domains.AddRange(ParseDomainFile(fileContent));
                        referencedFiles[fileName] = fileContent;
                    }
                    else
                    {
                        _logger.LogWarning("Status Check: domain file {File} for service {Service} is missing from the {RepoUrl}@{Branch} archive", file, name, repoUrl, branch);
                    }
                }

                // De-dup case-insensitively (within a file and across a service's multiple domain
                // files), preserving first-seen order - duplicate hostnames otherwise inflate
                // TotalCount and collide as React keys.
                result.Services.Add(new CacheDomainService
                {
                    Name = name,
                    Description = description,
                    Domains = DedupeDomainsPreservingOrder(domains),
                    MixedContent = mixedContent
                });
            }

            if (result.Services.All(s => s.Domains.Count == 0))
            {
                // A manifest whose files all failed to materialize is a broken fetch, not a valid
                // list - serving it would cache an all-"none resolving" sweep for 24 hours. Fall
                // through to the disk copy instead.
                _logger.LogWarning("Status Check: archive from {RepoUrl}@{Branch} yielded no domains for any service; ignoring it", repoUrl, branch);
                return null;
            }

            // Persist only after the archive proved usable, so a bad fetch can never clobber a
            // good disk copy.
            EnsureDomainsDirectory();
            await File.WriteAllTextAsync(Path.Combine(_domainsDirectory, "cache_domains.json"), manifestJson, ct);
            foreach (var (fileName, fileContent) in referencedFiles)
            {
                await File.WriteAllTextAsync(Path.Combine(_domainsDirectory, fileName), fileContent, ct);
            }

            _logger.LogInformation(
                "Status Check: fetched cache-domains archive from {RepoUrl}@{Branch} ({ServiceCount} services, {DomainCount} domains)",
                repoUrl, branch, result.Services.Count, result.Services.Sum(s => s.Domains.Count));
            return result;
        }
        // InvalidData/EndOfStream/Format cover a corrupt or truncated tarball (GZipStream and
        // TarReader throw all three) - a bad archive must degrade to the disk copy, never fail
        // the sweep.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException
            or InvalidDataException or EndOfStreamException or FormatException)
        {
            _logger.LogWarning(ex, "Status Check: failed to fetch cache-domains archive from {RepoUrl}@{Branch}", repoUrl, branch);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Status Check: cache_domains.json from {RepoUrl}@{Branch} was not valid JSON", repoUrl, branch);
            return null;
        }
    }

    // The uklans archive is ~12 KB; these bounds only exist so a misconfigured CACHE_DOMAINS_REPO
    // pointing at a huge repository can't balloon memory.
    private const long MaxArchiveBytes = 16 * 1024 * 1024;
    private const long MaxArchiveEntryBytes = 4 * 1024 * 1024;

    /// <summary>Extracts the manifest/domain files from a gzipped GitHub branch tarball into a
    /// base-filename -&gt; content map (archive paths carry a <c>{repo}-{branch}/</c> prefix, and
    /// <see cref="SanitizeFileName"/> already keys the disk cache by base name). Only .json/.txt
    /// entries are kept - the repo's scripts are irrelevant here.</summary>
    internal static Dictionary<string, string> ExtractArchiveFiles(Stream archiveStream)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream == null ||
                entry.Length > MaxArchiveEntryBytes)
            {
                continue;
            }

            var name = Path.GetFileName(entry.Name);
            if (name.Length == 0 ||
                (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                 !name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            using var streamReader = new StreamReader(entry.DataStream);
            files[name] = streamReader.ReadToEnd();
        }
        return files;
    }

    private CacheDomainsList? LoadFromDisk()
    {
        var manifestPath = Path.Combine(_domainsDirectory, "cache_domains.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var services = ParseCacheDomainsJson(manifestJson);
            var result = new CacheDomainsList();

            foreach (var (name, description, files, mixedContent) in services)
            {
                var domains = new List<string>();
                foreach (var file in files)
                {
                    var filePath = Path.Combine(_domainsDirectory, SanitizeFileName(file));
                    if (File.Exists(filePath))
                    {
                        domains.AddRange(ParseDomainFile(File.ReadAllText(filePath)));
                    }
                }
                result.Services.Add(new CacheDomainService
                {
                    Name = name,
                    Description = description,
                    Domains = DedupeDomainsPreservingOrder(domains),
                    MixedContent = mixedContent
                });
            }

            if (result.Services.All(s => s.Domains.Count == 0))
            {
                // Same guard as the fetch path: a manifest with no readable domain files would
                // drive an all-"none resolving" sweep. Better to report no list at all.
                _logger.LogWarning("Status Check: cached cache-domains list at {Path} has no domains for any service; ignoring it", _domainsDirectory);
                return null;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status Check: failed to load cached cache-domains list from disk at {Path}", manifestPath);
            return null;
        }
    }

    private DateTime? GetDiskManifestWriteTimeUtc()
    {
        var manifestPath = Path.Combine(_domainsDirectory, "cache_domains.json");
        return File.Exists(manifestPath) ? File.GetLastWriteTimeUtc(manifestPath) : (DateTime?)null;
    }

    private void EnsureDomainsDirectory()
    {
        try
        {
            if (!Directory.Exists(_domainsDirectory))
            {
                Directory.CreateDirectory(_domainsDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status Check: failed to create cache-domains directory at {Path}", _domainsDirectory);
        }
    }

    private static string SanitizeFileName(string file) => Path.GetFileName(file);

    private static string FirstNonEmpty(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// Converts a GitHub repo URL (https://github.com/owner/repo.git, https://github.com/owner/repo,
    /// or git@github.com:owner/repo.git) into the branch archive tarball URL - ONE request for the
    /// whole repo, where per-file raw.githubusercontent.com requests get burst-rate-limited (429).
    /// Returns <c>null</c> for anything that isn't a recognizable GitHub URL.
    /// </summary>
    internal static string? TryConvertToArchiveUrl(string repoUrl, string branch)
    {
        if (string.IsNullOrWhiteSpace(repoUrl) || string.IsNullOrWhiteSpace(branch))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            repoUrl.Trim(),
            @"github\.com[:/]+(?<owner>[^/]+)/(?<repo>[^/]+?)(\.git)?/?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return null;
        }

        var owner = match.Groups["owner"].Value;
        var repo = match.Groups["repo"].Value;
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(repo))
        {
            return null;
        }

        // The bare-ref archive form (not refs/heads/) so CACHE_DOMAINS_BRANCH may also name a tag,
        // a commit SHA, or a slashed branch, matching what the raw-file URLs this replaced accepted.
        return $"https://github.com/{owner}/{repo}/archive/{branch}.tar.gz";
    }

    /// <summary>
    /// Tolerantly parses the 4 shapes the cache-domains ecosystem uses (design ported from the
    /// monolithic fork's Go parser at <c>admin/backend/services/domains.go</c>, format 1 is
    /// canonical/upstream):
    /// <list type="number">
    /// <item>{ "cache_domains": [ { "name", "description", "domain_files": [...] }, ... ] }</item>
    /// <item>{ "cache_domains": { "service": { "domain_files": [...] } } }</item>
    /// <item>{ "service": { "domain_files": [...] } } (no wrapper key)</item>
    /// <item>{ "service": ["file1.txt", "file2.txt"] } (bare array)</item>
    /// </list>
    /// Unlike the Go original, this keeps the individual domain_files list per service (the caller
    /// then reads each file for the actual domain strings - this parser never opens .txt files).
    /// </summary>
    internal static List<(string Name, string Description, List<string> DomainFiles, bool MixedContent)> ParseCacheDomainsJson(string json)
    {
        var results = new List<(string, string, List<string>, bool)>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        if (root.TryGetProperty("cache_domains", out var cacheDomainsEl))
        {
            if (cacheDomainsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in cacheDomainsEl.EnumerateArray())
                {
                    var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    var description = entry.TryGetProperty("description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                    var files = ExtractDomainFiles(entry);
                    results.Add((name, description, files, ExtractMixedContent(entry)));
                }
                return results;
            }

            if (cacheDomainsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cacheDomainsEl.EnumerateObject())
                {
                    results.Add(ParseServiceObject(prop.Name, prop.Value));
                }
                return results;
            }
        }

        foreach (var prop in root.EnumerateObject())
        {
            results.Add(ParseServiceObject(prop.Name, prop.Value));
        }

        return results;
    }

    private static (string Name, string Description, List<string> DomainFiles, bool MixedContent) ParseServiceObject(string name, JsonElement value)
    {
        var description = string.Empty;
        var mixedContent = false;
        List<string> files;

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("description", out var d))
            {
                description = d.GetString() ?? string.Empty;
            }
            files = ExtractDomainFiles(value);
            mixedContent = ExtractMixedContent(value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            // Format 4: bare array of filenames.
            files = new List<string>();
            foreach (var f in value.EnumerateArray())
            {
                var s = f.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    files.Add(s);
                }
            }
        }
        else
        {
            files = new List<string>();
        }

        return (name, description, files, mixedContent);
    }

    /// <summary>Reads the optional <c>mixed_content</c> flag, tolerating both the JSON boolean the
    /// upstream manifest uses and a "true" string (same leniency as the rest of this parser).</summary>
    private static bool ExtractMixedContent(JsonElement serviceElement)
    {
        if (!serviceElement.TryGetProperty("mixed_content", out var mc))
        {
            return false;
        }
        return mc.ValueKind == JsonValueKind.True ||
               (mc.ValueKind == JsonValueKind.String && EnvValueParsing.ParseBool(mc.GetString()) == true);
    }

    private static List<string> ExtractDomainFiles(JsonElement serviceElement)
    {
        var files = new List<string>();
        if (serviceElement.TryGetProperty("domain_files", out var df) && df.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in df.EnumerateArray())
            {
                var s = f.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    files.Add(s);
                }
            }
        }
        return files;
    }

    /// <summary>
    /// Parses a cache-domains <c>.txt</c> file: one hostname per line, blank lines and lines
    /// starting with <c>#</c> skipped, wildcard entries (leading <c>*</c>) kept verbatim - the
    /// sweep does the <c>*.x.y -&gt; status-check.x.y</c> substitution, not this parser. A trailing
    /// inline comment is also stripped, mirroring <see cref="LancacheEnvFileReader.ParseEnvLines"/>'s
    /// rule for unquoted values: a <c>#</c> preceded by whitespace starts a comment.
    /// </summary>
    internal static List<string> ParseDomainFile(string content)
    {
        var domains = new List<string>();
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            for (var i = 1; i < line.Length; i++)
            {
                if (line[i] == '#' && char.IsWhiteSpace(line[i - 1]))
                {
                    line = line[..i].TrimEnd();
                    break;
                }
            }

            if (line.Length == 0)
            {
                continue;
            }

            domains.Add(line);
        }
        return domains;
    }

    /// <summary>De-dups a service's accumulated domain list (already merged across all of its
    /// domain_files) case-insensitively, preserving first-seen order.</summary>
    internal static List<string> DedupeDomainsPreservingOrder(IEnumerable<string> domains) =>
        domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
