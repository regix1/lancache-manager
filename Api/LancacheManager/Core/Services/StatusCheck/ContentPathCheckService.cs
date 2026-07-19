using System.Net;
using System.Net.Sockets;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>Explicit-run, status-only empirical content checker. It has no background monitor.</summary>
public sealed class ContentPathCheckService : IContentPathCheckService
{
    private const int MaxConcurrentSamples = 4;
    private const int MaxProbedEdges = 3;

    private readonly Func<IReadOnlyList<ResolvedDatasource>> _datasourceProvider;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ContentPathRawScan>> _sampleProvider;
    private readonly Func<string, CancellationToken, Task<DohResolutionResult>> _resolveEdges;
    private readonly Func<ContentPathSample, IPAddress, CancellationToken, Task<ContentPathEdgeResult>> _probeEdge;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContentPathCheckService> _logger;

    public ContentPathCheckService(
        DatasourceService datasourceService,
        IHttpClientFactory httpClientFactory,
        RustProcessHelper rustProcessHelper,
        ILogger<ContentPathCheckService> logger)
        : this(
            datasourceService.GetDatasources,
            new RustContentPathScanner(
                (directory, token) => rustProcessHelper.ScanContentSamplesAsync(directory, cancellationToken: token)).ScanAsync,
            new PublicDohResolver(httpClientFactory.CreateClient()).ResolveAsync,
            new DirectContentProbe().ProbeEdgeAsync,
            TimeProvider.System,
            logger)
    {
    }

    internal ContentPathCheckService(
        Func<IReadOnlyList<ResolvedDatasource>> datasourceProvider,
        Func<IReadOnlyList<string>, CancellationToken, Task<ContentPathRawScan>> sampleProvider,
        Func<string, CancellationToken, Task<DohResolutionResult>> resolveEdges,
        Func<ContentPathSample, IPAddress, CancellationToken, Task<ContentPathEdgeResult>> probeEdge,
        TimeProvider timeProvider,
        ILogger<ContentPathCheckService> logger)
    {
        _datasourceProvider = datasourceProvider;
        _sampleProvider = sampleProvider;
        _resolveEdges = resolveEdges;
        _probeEdge = probeEdge;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<StatusCheckContentReport> CheckAsync(
        IReadOnlyList<CacheDomainService> services,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var knownServices = services
            .Select(service => service.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        ContentPathRawScan scan;
        var datasources = _datasourceProvider()
            .Where(datasource => datasource.Enabled)
            .ToList();
        try
        {
            // Refresh each datasource's sources so the bare-metal logs/ -> logs/http descent is
            // resolved, then scan the RESOLVED log directory (not the legacy access.log path): the
            // Rust scan reuses the shared discovery to read both the monolithic and per-service
            // sources under it.
            var logDirectories = datasources
                .Select(datasource =>
                {
                    datasource.RefreshLogSources();
                    return datasource.LogPath;
                })
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            scan = await _sampleProvider(logDirectories, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Status Check content scan returned fail-soft state {FailureCategory}",
                ex.GetType().Name);
            return new StatusCheckContentReport
            {
                Availability = "unreadable",
                CheckedAtUtc = _timeProvider.GetUtcNow()
            };
        }

        // The Rust scan reused the canonical grammar; C# owns the security boundary. Map each
        // candidate through the path/SSRF safety, host DNS normalization, not-future timestamp and
        // known-service filters, then apply the bounded, deterministic sample selection.
        var samples = new List<ContentPathSample>();
        foreach (var record in scan.Records)
        {
            if (ContentPathRecordFilter.TryMap(record, knownServices, now, out var sample))
            {
                samples.Add(sample!);
            }
        }
        var selectedSamples = ContentPathSampleSelector.Select(samples, now);

        // Typed states instead of truthful-looking empty success: a readable log with zero
        // recognizable samples is "noSamples", never "available" with nothing behind it. Both the
        // monolithic and per-service formats are supported now, so there is no unsupported state.
        var availability = scan.Availability;
        if (availability == "available" && selectedSamples.Count == 0)
        {
            availability = "noSamples";
        }

        var report = new StatusCheckContentReport
        {
            Availability = availability,
            ScanTruncated = scan.ScanTruncated,
            ScannedBytes = scan.ScannedBytes
        };
        if (availability != "available" || selectedSamples.Count == 0)
        {
            report.CheckedAtUtc = _timeProvider.GetUtcNow();
            return report;
        }

        using var semaphore = new SemaphoreSlim(MaxConcurrentSamples);
        var tasks = selectedSamples.Select(async (sample, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return (Index: index, Result: await CheckSampleAsync(sample, cancellationToken));
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        var checkedPaths = await Task.WhenAll(tasks);
        report.Paths = checkedPaths
            .OrderBy(item => item.Index)
            .Select(item => item.Result)
            .ToList();
        report.CheckedAtUtc = _timeProvider.GetUtcNow();

        _logger.LogInformation(
            "Status Check content lane checked {PathCount} real path(s) from {ScannedBytes} bounded log bytes",
            report.Paths.Count,
            report.ScannedBytes);
        return report;
    }

    /// <summary>The one shared resolve-and-probe pipeline: DoH edges, bounded selection, pinned
    /// probes with fail-soft typed placeholders. Both the sweep lane and the ad hoc host probe
    /// run through here so their behaviour can never drift apart.</summary>
    private async Task<(DohResolutionResult Resolution, List<ContentPathEdgeResult> Edges)> ResolveAndProbeEdgesAsync(
        ContentPathSample sample,
        CancellationToken cancellationToken)
    {
        DohResolutionResult resolution;
        try
        {
            resolution = await _resolveEdges(sample.Host, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Status Check content DNS control returned typed failure {FailureCategory}",
                ex.GetType().Name);
            resolution = new DohResolutionResult(Array.Empty<IPAddress>(), 0, false, "dohUnavailable");
        }

        var edgeResults = new List<ContentPathEdgeResult>();
        foreach (var address in SelectProbeAddresses(resolution.Addresses))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                edgeResults.Add(await _probeEdge(sample, address, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Status Check content edge probe returned typed failure {FailureCategory}",
                    ex.GetType().Name);
                edgeResults.Add(new ContentPathEdgeResult
                {
                    Address = address.ToString(),
                    AddressFamily = address.AddressFamily == AddressFamily.InterNetwork ? "ipv4" : "ipv6",
                    Http = new ProtocolProbeResult { Outcome = "invalidResponse" },
                    Https = new ProtocolProbeResult { Outcome = "invalidResponse" }
                });
            }
        }

        return (resolution, edgeResults);
    }

    public async Task<HostProtocolProbeResult> ProbeHostAsync(string host, CancellationToken cancellationToken)
    {
        var sample = new ContentPathSample(
            Service: string.Empty,
            Host: host,
            Target: "/",
            ObservedAtUtc: _timeProvider.GetUtcNow(),
            CacheOutcome: string.Empty,
            StatusCode: 0,
            Bytes: 0);
        var (resolution, edgeResults) = await ResolveAndProbeEdgesAsync(sample, cancellationToken);
        var consensus = ContentPathConsensus.Classify(edgeResults, resolution.FailureReason);
        return new HostProtocolProbeResult
        {
            ProtocolStatus = consensus.Status,
            ProtocolReason = consensus.Reason,
            ConsensusEdges = consensus.ConsensusEdges,
            TotalPublicEdges = resolution.TotalAddresses,
            Edges = edgeResults
        };
    }

    private async Task<ContentPathCheckResult> CheckSampleAsync(
        ContentPathSample sample,
        CancellationToken cancellationToken)
    {
        var (resolution, edgeResults) = await ResolveAndProbeEdgesAsync(sample, cancellationToken);
        var consensus = ContentPathConsensus.Classify(edgeResults, resolution.FailureReason);
        return new ContentPathCheckResult
        {
            Service = sample.Service,
            Host = sample.Host,
            PathDisplay = ContentPathTargetSafety.ToDisplayPath(sample.Target),
            SampleObservedAtUtc = sample.ObservedAtUtc,
            CacheEvidence = new CacheTraversalEvidence
            {
                Outcome = sample.CacheOutcome,
                StatusCode = sample.StatusCode,
                Bytes = sample.Bytes
            },
            ProtocolStatus = consensus.Status,
            ProtocolReason = consensus.Reason,
            ConsensusEdges = consensus.ConsensusEdges,
            TotalPublicEdges = resolution.TotalAddresses,
            Edges = edgeResults
        };
    }

    private static List<IPAddress> SelectProbeAddresses(IReadOnlyList<IPAddress> addresses)
    {
        var ordered = addresses
            .Where(PublicAddressSafety.IsPublic)
            .Distinct()
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .ToList();
        var selected = new List<IPAddress>(MaxProbedEdges);

        var firstIpv4 = ordered.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        var firstIpv6 = ordered.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetworkV6);
        if (firstIpv4 != null)
        {
            selected.Add(firstIpv4);
        }
        if (firstIpv6 != null)
        {
            selected.Add(firstIpv6);
        }

        selected.AddRange(ordered.Where(address => !selected.Contains(address)).Take(MaxProbedEdges - selected.Count));
        return selected;
    }
}
