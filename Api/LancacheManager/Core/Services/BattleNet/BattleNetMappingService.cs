using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
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

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<BattleNetMappingService> _logger;
    private readonly Lazy<TactCatalog> _catalog;

    public BattleNetMappingService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ISignalRNotificationService notifications,
        ILogger<BattleNetMappingService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _notifications = notifications;
        _logger = logger;
        _catalog = new Lazy<TactCatalog>(LoadCatalog);
    }

    /// <summary>
    /// Re-maps existing Blizzard downloads with no GameName by resolving the TACT segment
    /// from LastUrl. Sets GameName (game display name or the shared label), leaves
    /// GameAppId NULL, persists, and emits <see cref="SignalREvents.BlizzardGameMappingsUpdated"/>.
    /// Returns the number of downloads that were resolved.
    /// </summary>
    public async Task<int> ResolveDownloadsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        const string blizzardServicePattern = "%blizzard%";

        // Service names are normalized to lowercase during log processing, so a lowercase
        // LIKE pattern preserves the intended matching while staying SQL-translatable.
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

        var resolvedCount = 0;
        var unmatchedSampleLogged = false;
        foreach (var download in unresolvedDownloads)
        {
            if (string.IsNullOrEmpty(download.LastUrl)) continue;

            var segment = ExtractTactSegment(download.LastUrl);
            if (segment == null)
            {
                continue;
            }

            var resolution = _catalog.Value.Resolve(segment);
            if (resolution.Kind == TactResolutionKind.Unknown)
            {
                if (!unmatchedSampleLogged)
                {
                    _logger.LogWarning(
                        "Unmapped Blizzard CDN path '{Segment}' (sample url: '{Url}')",
                        segment, download.LastUrl);
                    unmatchedSampleLogged = true;
                }
                continue;
            }

            // GameAppId stays NULL for Blizzard (no integer app id), matching ingest behavior.
            download.GameName = resolution.Name;
            resolvedCount++;
        }

        if (resolvedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Resolved {Count}/{Total} Blizzard downloads to game names",
                resolvedCount, unresolvedDownloads.Count);

            await _notifications.NotifyAllAsync(SignalREvents.BlizzardGameMappingsUpdated, new
            {
                source = "blizzard-download-resolution",
                resolvedCount
            });

            // DownloadsRefresh so the dashboard re-pulls the renamed rows.
            await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
            {
                source = "blizzard-download-resolution",
                resolvedCount
            });
        }
        else
        {
            _logger.LogInformation(
                "0 of {Count} unnamed Blizzard downloads matched the TACT catalog",
                unresolvedDownloads.Count);
        }

        return resolvedCount;
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

