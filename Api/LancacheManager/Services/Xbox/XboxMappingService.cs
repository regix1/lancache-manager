using System.Text.RegularExpressions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Re-maps EXISTING, already-ingested Xbox / Microsoft Store downloads to their game identity by
/// matching <c>Downloads.LastUrl</c> against the per-file CDN path fragments the authenticated
/// daemon contributed (stored as <see cref="XboxCdnPattern"/>).
///
/// Xbox content is delivered through <c>*.dl.delivery.mp.microsoft.com</c>, which lancache tags
/// <c>wsus</c> (SHARED with Windows Update / Office / Defender) as opaque
/// <c>/filestreamingservice/files/&lt;GUID&gt;</c> objects with no product id in the URL. Xbox rides
/// the NAMED-game model (like Blizzard/Riot): a matched row canonicalizes to
/// <c>Service='xbox'</c> + <c>GameName=&lt;title&gt;</c> + <c>XboxProductId=&lt;id&gt;</c> with
/// <c>GameAppId</c>/<c>EpicAppId</c> NULL. <c>XboxProductId</c> is metadata only (art + GUID→name).
///
/// IMPORTANT - this is BACKFILL of INACTIVE rows ONLY. The Rust ingest path is the primary,
/// active-session-safe canonicalizer; the active-session lookup keys on the raw <c>Downloads.Service</c>,
/// so re-tagging an ACTIVE row here would split the download. Unmatched <c>wsus</c> stays generic
/// Windows Update and is NEVER relabeled (the fragment-shape guard below is what prevents a
/// <c>Contains("")</c> from mislabeling ALL Windows Update traffic).
/// </summary>
public class XboxMappingService
{
    // Two distinct Xbox content-URL shapes reach the cache and must BOTH validate:
    //   1. Delivery-Optimization CLIENT traffic (dl.delivery.mp, tagged `wsus`): the opaque, stable
    //      /filestreamingservice/files/<36-char GUID> object (marker + exactly one GUID).
    //   2. Prefill-daemon traffic pulled direct from assets1.xboxlive.com (tagged `xboxlive`):
    //      /<digit>/<guid>/<guid>/<version>.<guid>/<packageName> (no filestreamingservice marker,
    //      but >=2 well-formed GUIDs). The daemon emits exactly this path.
    // A valid fragment MUST be one of these shapes, or matching it against generic wsus/xboxlive
    // URLs would mislabel unrelated Windows Update / Xbox Live traffic.
    private static readonly Regex _filestreamingFragmentRegex = new(
        @"/filestreamingservice/files/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A single canonical 8-4-4-4-12 hex GUID. The assets1 package path carries >=2 of these (a
    // content GUID + a version GUID), which generic wsus / Windows-Update / other-service paths do
    // not, so requiring two keeps the validator specific. Mirrors the Rust `is_guid_at` shape.
    private static readonly Regex _guidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ISignalRNotificationService _notifications;
    private readonly XboxApiDirectClient _apiClient;
    private readonly ILogger<XboxMappingService> _logger;

    // Serializes catalog merges. Two Xbox sessions authenticating concurrently each fire a detached
    // MergeDaemonCatalogAsync on its own DbContext; both would read empty dedup dicts, both INSERT the
    // same UrlFragment/ProductId, and the second SaveChangesAsync would hit the UNIQUE index
    // (IX_XboxCdnPatterns_UrlFragment / IX_XboxGameMappings_ProductId) and roll back the ENTIRE second
    // batch, dropping that session's whole catalog. The merge is infrequent and best-effort, so a
    // process-wide gate is the cheapest correct fix (the service is a singleton).
    private static readonly SemaphoreSlim _mergeGate = new(1, 1);

    public XboxMappingService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ISignalRNotificationService notifications,
        XboxApiDirectClient apiClient,
        ILogger<XboxMappingService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _notifications = notifications;
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <summary>
    /// Resolves still-<c>wsus</c>/<c>xboxlive</c>, INACTIVE downloads against stored Xbox CDN patterns. On match,
    /// canonicalizes the row to <c>Service='xbox'</c>, sets <c>GameName</c> + <c>XboxProductId</c>,
    /// best-effort fetches/stores the DisplayCatalog banner, persists, and emits
    /// <see cref="SignalREvents.XboxGameMappingsUpdated"/> + <see cref="SignalREvents.DownloadsRefresh"/>.
    /// Returns the number of downloads that were re-tagged.
    /// </summary>
    public async Task<int> ResolveDownloadsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        // Load the contributed CDN patterns and keep only well-formed filestreaming fragments,
        // longest-first so the most specific pattern wins.
        var patterns = await db.XboxCdnPatterns
            .AsNoTracking()
            .ToListAsync(ct);

        var validPatterns = patterns
            .Where(p => IsValidFragment(p.UrlFragment))
            .OrderByDescending(p => p.UrlFragment.Length)
            .ToList();

        if (validPatterns.Count == 0)
        {
            _logger.LogInformation("No usable Xbox CDN patterns to resolve downloads against");
            return 0;
        }

        // Candidate rows: still tagged wsus (DO-client traffic) OR xboxlive (prefill-daemon traffic
        // direct from assets1.xboxlive.com), no game name yet, have a LastUrl, and INACTIVE.
        // Re-tagging an active row would split the in-flight download (the Rust ingest path owns
        // active rows), so we never touch them here.
        const string wsusServicePattern = "%wsus%";
        const string xboxliveServicePattern = "%xboxlive%";
        var candidates = await db.Downloads
            .Where(d => (EF.Functions.Like(d.Service, wsusServicePattern)
                            || EF.Functions.Like(d.Service, xboxliveServicePattern))
                        && d.GameName == null
                        && d.LastUrl != null
                        && !d.IsActive)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var resolvedCount = 0;
        var resolvedProductIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unmatchedSampleLogged = false;

        foreach (var download in candidates)
        {
            if (string.IsNullOrEmpty(download.LastUrl)) continue;

            var match = validPatterns.FirstOrDefault(p =>
                download.LastUrl.Contains(p.UrlFragment, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                if (!unmatchedSampleLogged)
                {
                    _logger.LogDebug("Unmatched wsus/xboxlive download stays generic (sample url: '{Url}')", download.LastUrl);
                    unmatchedSampleLogged = true;
                }
                continue;
            }

            // Canonicalize to the Xbox named-game identity. GameAppId/EpicAppId stay NULL; the
            // detection-side sentinel of 0 is applied where detection rows are created, not here.
            download.Service = "xbox";
            download.GameName = match.Title;
            download.XboxProductId = match.ProductId;
            resolvedCount++;
            resolvedProductIds.Add(match.ProductId);
        }

        if (resolvedCount == 0)
        {
            return 0;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Re-tagged {Count}/{Total} wsus/xboxlive downloads to Xbox titles",
            resolvedCount, candidates.Count);

        // Best-effort: fetch banner art for the newly-resolved products via DisplayCatalog and
        // store it on the XboxGameMapping (keyed by ProductId). A failure here never blocks the
        // re-tag - the row already has its GameName.
        await EnsureBannerArtAsync(db, resolvedProductIds, ct);

        await _notifications.NotifyAllAsync(SignalREvents.XboxGameMappingsUpdated, new
        {
            source = "xbox-download-resolution",
            resolvedCount
        });

        // DownloadsRefresh so the dashboard re-pulls the re-tagged rows.
        await _notifications.NotifyAllAsync(SignalREvents.DownloadsRefresh, new
        {
            source = "xbox-download-resolution",
            resolvedCount
        });

        return resolvedCount;
    }

    /// <summary>
    /// Persists the Xbox catalog an authenticated daemon session contributed via <c>get-cdn-info</c>:
    /// upserts an <see cref="XboxGameMapping"/> per product (ProductId-&gt;Title) and an
    /// <see cref="XboxCdnPattern"/> per well-formed <c>/filestreamingservice/files/&lt;GUID&gt;</c>
    /// fragment (de-duplicated on UrlFragment). This is the PRODUCER side that fills the tables
    /// <see cref="ResolveDownloadsAsync"/> reads; without it the resolver never matches and Xbox
    /// downloads never auto-name. Mirrors Epic's <c>MergeOwnedGamesAsync</c> + <c>MergeCdnPatternsAsync</c>
    /// (same contract, Xbox-correct body). Malformed fragments are rejected by the SAME shape guard
    /// the resolver uses, so a bad fragment can never relabel generic Windows Update traffic.
    /// Returns the number of CDN patterns newly persisted.
    /// </summary>
    public async Task<int> MergeDaemonCatalogAsync(List<CdnInfo> cdnInfos, CancellationToken ct = default)
    {
        if (cdnInfos.Count == 0) return 0;

        // Serialize against any concurrent merge so the read-then-insert dedup below cannot race a
        // second caller into a UNIQUE-index violation that discards a whole catalog (see _mergeGate).
        await _mergeGate.WaitAsync(ct);
        try
        {
            return await MergeDaemonCatalogCoreAsync(cdnInfos, ct);
        }
        finally
        {
            _mergeGate.Release();
        }
    }

    private async Task<int> MergeDaemonCatalogCoreAsync(List<CdnInfo> cdnInfos, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;

        var existingMappings = await db.XboxGameMappings
            .ToDictionaryAsync(m => m.ProductId, StringComparer.OrdinalIgnoreCase, ct);
        var existingPatterns = await db.XboxCdnPatterns
            .ToDictionaryAsync(p => p.UrlFragment, StringComparer.OrdinalIgnoreCase, ct);

        var newMappings = 0;
        var newPatterns = 0;

        foreach (var info in cdnInfos)
        {
            if (string.IsNullOrWhiteSpace(info.AppId)) continue;

            // Upsert the product->title mapping (the catalog row; art is fetched lazily on resolve).
            if (existingMappings.TryGetValue(info.AppId, out var mapping))
            {
                mapping.LastSeenAtUtc = now;
                if (!string.IsNullOrWhiteSpace(info.Name))
                    mapping.Title = info.Name;
            }
            else
            {
                mapping = new XboxGameMapping
                {
                    ProductId = info.AppId,
                    Title = info.Name,
                    DiscoveredAtUtc = now,
                    LastSeenAtUtc = now
                };
                db.XboxGameMappings.Add(mapping);
                existingMappings[info.AppId] = mapping;
                newMappings++;
            }

            // Persist one CDN pattern per valid per-file fragment, rejecting any fragment that is not
            // a /filestreamingservice/files/<GUID> path (the resolver's Contains() would otherwise
            // mislabel unrelated wsus traffic).
            foreach (var fragment in info.FilePathFragments)
            {
                if (!IsValidFragment(fragment)) continue;

                if (existingPatterns.TryGetValue(fragment, out var pattern))
                {
                    pattern.LastSeenAtUtc = now;
                    if (!string.IsNullOrWhiteSpace(info.CdnHost))
                        pattern.CdnHost = info.CdnHost;

                    // First-writer-wins on the product binding: if a DIFFERENT product already owns
                    // this fragment (two Store products legitimately sharing a delivery GUID), do NOT
                    // flip it - that would make the same cached object resolve to a different title on
                    // every login (nondeterministic naming). Keep the original mapping and log once.
                    if (!string.Equals(pattern.ProductId, info.AppId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "Xbox CDN fragment {Fragment} already mapped to product {ExistingProduct} ('{ExistingTitle}'); " +
                            "ignoring conflicting product {NewProduct} ('{NewTitle}') to keep naming deterministic",
                            fragment, pattern.ProductId, pattern.Title, info.AppId, info.Name);
                    }
                    else if (!string.IsNullOrWhiteSpace(info.Name))
                    {
                        // Same product: a title refresh is safe.
                        pattern.Title = info.Name;
                    }
                }
                else
                {
                    pattern = new XboxCdnPattern
                    {
                        ProductId = info.AppId,
                        Title = info.Name,
                        UrlFragment = fragment,
                        CdnHost = info.CdnHost,
                        DiscoveredAtUtc = now,
                        LastSeenAtUtc = now
                    };
                    db.XboxCdnPatterns.Add(pattern);
                    existingPatterns[fragment] = pattern;
                    newPatterns++;
                }
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Xbox daemon catalog merge: {NewMappings} new games, {NewPatterns} new CDN patterns ({Apps} apps reported)",
            newMappings, newPatterns, cdnInfos.Count);

        if (newMappings > 0 || newPatterns > 0)
        {
            await _notifications.NotifyAllAsync(SignalREvents.XboxGameMappingsUpdated, new
            {
                source = "xbox-daemon-catalog",
                newMappings,
                newPatterns
            });
        }

        return newPatterns;
    }

    /// <summary>
    /// For each newly-resolved ProductId with an <see cref="XboxGameMapping"/> that has no
    /// <c>ImageUrl</c>, fetches the DisplayCatalog banner and stores it. Empty/failed lookups are
    /// skipped (the UI falls back to the service-icon placeholder, never a wrong image).
    /// </summary>
    private async Task EnsureBannerArtAsync(AppDbContext db, HashSet<string> productIds, CancellationToken ct)
    {
        if (productIds.Count == 0) return;

        var mappings = await db.XboxGameMappings
            .Where(m => productIds.Contains(m.ProductId) && (m.ImageUrl == null || m.ImageUrl == ""))
            .ToListAsync(ct);

        await FetchAndStoreBannerArtAsync(db, mappings, ct);
    }

    /// <summary>
    /// Backfills banner art for EVERY <see cref="XboxGameMapping"/> that still has no <c>ImageUrl</c>,
    /// not just the ones resolved in a given pass. Self-heals titles whose first banner fetch hiccupped
    /// transiently: <see cref="EnsureBannerArtAsync"/> only ever fetches for the products resolved in
    /// that single pass, so a transient miss would otherwise leave <c>ImageUrl</c> empty forever. Runs
    /// on the catalog-refresh path ONLY (12h schedule / on-login / startup), never per log ingest.
    /// Best-effort and idempotent: a mapping that already has art is skipped, so once a banner is stored
    /// it stops retrying; a title DisplayCatalog genuinely has no art for is retried each refresh, which
    /// is acceptable at the bounded refresh cadence.
    /// </summary>
    public async Task BackfillMissingBannerArtAsync(CancellationToken ct = default)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct);

        var mappings = await db.XboxGameMappings
            .Where(m => m.ImageUrl == null || m.ImageUrl == "")
            .ToListAsync(ct);

        if (mappings.Count == 0) return;

        _logger.LogInformation("Backfilling Xbox banner art for {Count} mapping(s) missing art", mappings.Count);
        await FetchAndStoreBannerArtAsync(db, mappings, ct);
    }

    /// <summary>
    /// Shared per-mapping banner fetch/store loop used by both <see cref="EnsureBannerArtAsync"/> (the
    /// newly-resolved set) and <see cref="BackfillMissingBannerArtAsync"/> (every art-less mapping).
    /// Each mapping is fetched best-effort: an empty/failed lookup is skipped and logged, never thrown,
    /// so one bad product cannot abort the batch. Persists once, only if at least one banner was stored.
    /// </summary>
    private async Task FetchAndStoreBannerArtAsync(AppDbContext db, List<XboxGameMapping> mappings, CancellationToken ct)
    {
        if (mappings.Count == 0) return;

        var updated = false;
        foreach (var mapping in mappings)
        {
            try
            {
                var imageUrl = await _apiClient.GetBannerImageUrlAsync(mapping.ProductId, ct);
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    mapping.ImageUrl = imageUrl;
                    mapping.LastSeenAtUtc = DateTime.UtcNow;
                    updated = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Xbox banner art for ProductId {ProductId}", mapping.ProductId);
            }
        }

        if (updated)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Fragment-shape guard: a fragment is usable ONLY if it is a well-formed Xbox content path,
    /// i.e. either the Delivery-Optimization <c>/filestreamingservice/files/&lt;GUID&gt;</c> marker
    /// (marker + 1 GUID), OR an assets1.xboxlive.com package path carrying &gt;=2 canonical
    /// 8-4-4-4-12 GUIDs. This rejects empty, bare "/", and generic single-/zero-GUID paths so a
    /// <c>Contains</c> can never match unrelated Windows Update / Xbox Live traffic. Kept
    /// behaviorally byte-for-byte equivalent to Rust <c>cache_utils::is_valid_xbox_fragment</c>.
    /// </summary>
    internal static bool IsValidFragment(string? fragment)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return false;
        if (fragment == "/") return false;
        if (_filestreamingFragmentRegex.IsMatch(fragment)) return true; // DO-client path (marker + 1 GUID)
        return _guidRegex.Count(fragment) >= 2;                         // assets1 package path (>=2 GUIDs)
    }
}
