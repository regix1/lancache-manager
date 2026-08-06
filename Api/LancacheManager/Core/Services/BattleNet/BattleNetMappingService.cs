using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.BattleNet;

/// <summary>
/// Re-maps EXISTING Blizzard/Battle.net downloads to game names by parsing the TACT
/// CDN-path / product segment out of <c>Downloads.LastUrl</c> (<c>/tpr/&lt;seg&gt;/</c>).
///
/// Mirrors Epic's <c>ResolveEpicDownloadsAsync</c> (string-match on <c>LastUrl</c>, no
/// app-id column dependency). Unlike Steam PICS / Epic OAuth this needs no account, no
/// API, and no login - the catalog is the static, single-sourced
/// <c>tact_products.json</c> embedded from the rust-processor (the SAME file the Rust
/// <c>log_processor</c> compiles in via <c>include_str!</c>), so the inline-ingest naming
/// and this re-map can never drift.
///
/// Resolution for a <c>/tpr/&lt;seg&gt;/</c> segment (lowercased): products[seg] -> game;
/// else aliases[seg] -> game; else if seg in shared -> sharedLabel; else unresolved
/// (GameName left NULL). GameAppId is always left NULL (Blizzard has no integer app id).
/// </summary>
public class BattleNetMappingService
{
    private const string CatalogResourceName = "LancacheManager.tact_products.json";

    /// <summary>Terminal stage key for a pass that matched none of its candidates to a game.</summary>
    private const string NothingResolvedSkipStageKey = "signalr.battleNetMapping.skippedNothingResolved";

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ILogger<BattleNetMappingService> _logger;
    private readonly Lazy<TactCatalog> _catalog;
    private readonly SemaphoreSlim _resolveGate = new(1, 1);

    public BattleNetMappingService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        ILogger<BattleNetMappingService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _logger = logger;
        _catalog = new Lazy<TactCatalog>(LoadCatalog);
    }

    /// <summary>
    /// Re-maps existing Blizzard downloads with no GameName by resolving the TACT segment
    /// from LastUrl. Sets GameName (game display name or the shared label), leaves
    /// GameAppId NULL, persists, and emits <see cref="SignalREvents.DownloadsRefresh"/> so the
    /// renamed rows are re-pulled. Returns the number of downloads that were resolved.
    /// </summary>
    public async Task<int> ResolveDownloadsAsync(CancellationToken ct = default)
    {
        await _resolveGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            const string blizzardServicePattern = "%blizzard%";
            var unresolvedDownloads = await db.Downloads
                .Where(d => EF.Functions.Like(d.Service, blizzardServicePattern)
                            && d.GameName == null
                            && d.LastUrl != null)
                .ToListAsync(ct);

            if (unresolvedDownloads.Count == 0)
            {
                _logger.LogInformation("No unnamed Blizzard downloads with a LastUrl to resolve");
                return 0;
            }

            var processed = 0;
            var resolvedCount = 0;
            var lastReportedBucket = -1;
            Dictionary<string, object?> Context(string? errorDetail = null) =>
                new()
                {
                    ["processed"] = processed,
                    ["total"] = unresolvedDownloads.Count,
                    ["mapped"] = resolvedCount,
                    ["errorDetail"] = errorDetail,
                };

            await using var reporter = new MappingOperationReporter(
                _notifications,
                _operationTracker,
                MappingOperations.BattleNet,
                showNotification: false,
                ct,
                _logger);
            await reporter.StartAsync(Context());

            try
            {
                var unmatchedSampleLogged = false;
                foreach (var download in unresolvedDownloads)
                {
                    reporter.Token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(download.LastUrl))
                    {
                        var segment = ExtractTactSegment(download.LastUrl);
                        if (segment == null)
                        {
                            var rootSegment = ExtractCdnPathRootSegment(download.LastUrl);
                            if (rootSegment != null
                                && _catalog.Value.Resolve(rootSegment).Kind != TactResolutionKind.Unknown)
                            {
                                segment = rootSegment;
                            }
                        }

                        if (segment != null)
                        {
                            var resolution = _catalog.Value.Resolve(segment);
                            if (resolution.Kind != TactResolutionKind.Unknown)
                            {
                                download.GameName = resolution.Name;
                                resolvedCount++;
                            }
                            else if (!unmatchedSampleLogged)
                            {
                                _logger.LogWarning(
                                    "Unmapped Blizzard CDN path '{Segment}' (sample url: '{Url}')",
                                    segment,
                                    download.LastUrl);
                                unmatchedSampleLogged = true;
                            }
                        }
                    }

                    processed++;
                    var bucket = processed * 20 / unresolvedDownloads.Count;
                    if (bucket > lastReportedBucket || processed == unresolvedDownloads.Count)
                    {
                        lastReportedBucket = bucket;
                        await reporter.ReportAsync(
                            processed * 90.0 / unresolvedDownloads.Count,
                            "signalr.battleNetMapping.resolving",
                            Context());
                    }
                }

                if (resolvedCount > 0)
                {
                    await reporter.ReportAsync(
                        95,
                        "signalr.battleNetMapping.saving",
                        Context());
                    await db.SaveChangesAsync(reporter.Token);
                    await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
                    {
                        source = "blizzard-download-resolution",
                        resolvedCount
                    });
                }

                // Nothing resolved means nothing was saved and no refresh was emitted, so the pass
                // left the database exactly as it found it and says so instead of claiming a
                // completed re-map. Progress above is not suppressed the way a signed-out Epic or
                // Xbox pass suppresses its own: those know before they start that they may do
                // nothing, while this only learns it after walking every candidate, and the walk
                // itself is real work worth showing.
                if (resolvedCount == 0)
                {
                    await reporter.CompleteSkippedAsync(NothingResolvedSkipStageKey, Context());
                }
                else
                {
                    await reporter.CompleteAsync(success: true, context: Context());
                }

                return resolvedCount;
            }
            catch (OperationCanceledException)
            {
                await reporter.CompleteAsync(
                    success: false,
                    error: "Cancelled by user",
                    cancelled: true,
                    context: Context());
                throw;
            }
            catch (Exception ex)
            {
                await reporter.CompleteAsync(
                    success: false,
                    error: ex.Message,
                    context: Context(ex.Message));
                throw;
            }
        }
        finally
        {
            _resolveGate.Release();
        }
    }

    /// <summary>
    /// Parse the TACT CDN-path / product segment from a Blizzard CDN URL
    /// (<c>/tpr/&lt;seg&gt;/...</c>). C# port of the Rust <c>extract_tact_product</c>.
    /// Returns the lowercased segment, or null if the URL has no <c>/tpr/&lt;segment&gt;</c>.
    /// </summary>
    internal static string? ExtractTactSegment(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i] == "tpr")
            {
                var seg = segments[i + 1];
                return string.IsNullOrEmpty(seg) ? null : seg.ToLowerInvariant();
            }
        }
        return null;
    }

    /// <summary>
    /// First path segment of a Blizzard CDN URL, lowercased: the CDN path root for
    /// products that publish a non-tpr path (verified against the live cdns configs,
    /// btlr uses <c>cortez/Cerberus-B-Live</c>). Callers must catalog-gate the result;
    /// an arbitrary path also has a first segment.
    /// </summary>
    internal static string? ExtractCdnPathRootSegment(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : segments[0].ToLowerInvariant();
    }

    private TactCatalog LoadCatalog()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(CatalogResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded TACT catalog resource '{CatalogResourceName}' not found. " +
                "Verify the EmbeddedResource link in LancacheManager.csproj.");

        var raw = JsonSerializer.Deserialize<RawCatalog>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Embedded tact_products.json is malformed");

        // Aliases first, then products, so a product slug always wins on collision.
        var games = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw.Aliases != null)
        {
            foreach (var kvp in raw.Aliases)
                games[kvp.Key] = kvp.Value;
        }
        if (raw.Products != null)
        {
            foreach (var kvp in raw.Products)
                games[kvp.Key] = kvp.Value;
        }

        var shared = new HashSet<string>(raw.Shared ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        return new TactCatalog(
            raw.SharedLabel ?? "Battle.net (shared)",
            games,
            shared,
            raw.Products?.Count ?? 0);
    }

    /// <summary>Raw deserialization shape matching tact_products.json.</summary>
    private sealed class RawCatalog
    {
        [JsonPropertyName("sharedLabel")]
        public string? SharedLabel { get; set; }

        [JsonPropertyName("products")]
        public Dictionary<string, string>? Products { get; set; }

        [JsonPropertyName("aliases")]
        public Dictionary<string, string>? Aliases { get; set; }

        [JsonPropertyName("shared")]
        public List<string>? Shared { get; set; }
    }

    /// <summary>Parsed, case-insensitive catalog used for resolution (mirrors the Rust Catalog).</summary>
    private sealed class TactCatalog
    {
        private readonly string _sharedLabel;
        private readonly Dictionary<string, string> _games;
        private readonly HashSet<string> _shared;

        public int ProductCount { get; }

        public TactCatalog(string sharedLabel, Dictionary<string, string> games, HashSet<string> shared, int productCount)
        {
            _sharedLabel = sharedLabel;
            _games = games;
            _shared = shared;
            ProductCount = productCount;
        }

        public TactResolution Resolve(string segment)
        {
            if (_games.TryGetValue(segment, out var name))
                return new TactResolution(TactResolutionKind.Game, name);
            if (_shared.Contains(segment))
                return new TactResolution(TactResolutionKind.Shared, _sharedLabel);
            return new TactResolution(TactResolutionKind.Unknown, null);
        }
    }

    private enum TactResolutionKind
    {
        Game,
        Shared,
        Unknown
    }

    private readonly struct TactResolution
    {
        public TactResolutionKind Kind { get; }
        public string? Name { get; }

        public TactResolution(TactResolutionKind kind, string? name)
        {
            Kind = kind;
            Name = name;
        }
    }
}

