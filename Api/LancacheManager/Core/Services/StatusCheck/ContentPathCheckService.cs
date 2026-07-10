using System.Net;
using System.Net.Sockets;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>Explicit-run, status-only empirical content checker. It has no background monitor.</summary>
public sealed class ContentPathCheckService : IContentPathCheckService
{
    private const int MaxConcurrentSamples = 4;
    private const int MaxProbedEdges = 3;

    private readonly Func<IReadOnlyList<ResolvedDatasource>> _datasourceProvider;
    private readonly ContentPathLogScanner _scanner;
    private readonly Func<string, CancellationToken, Task<DohResolutionResult>> _resolveEdges;
    private readonly Func<ContentPathSample, IPAddress, CancellationToken, Task<ContentPathEdgeResult>> _probeEdge;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContentPathCheckService> _logger;

    public ContentPathCheckService(
        DatasourceService datasourceService,
        IHttpClientFactory httpClientFactory,
        ILogger<ContentPathCheckService> logger)
        : this(
            datasourceService.GetDatasources,
            new ContentPathLogScanner(),
            new PublicDohResolver(httpClientFactory.CreateClient()).ResolveAsync,
            new DirectContentProbe().ProbeEdgeAsync,
            TimeProvider.System,
            logger)
    {
    }

    internal ContentPathCheckService(
        Func<IReadOnlyList<ResolvedDatasource>> datasourceProvider,
        ContentPathLogScanner scanner,
        Func<string, CancellationToken, Task<DohResolutionResult>> resolveEdges,
        Func<ContentPathSample, IPAddress, CancellationToken, Task<ContentPathEdgeResult>> probeEdge,
        TimeProvider timeProvider,
        ILogger<ContentPathCheckService> logger)
    {
        _datasourceProvider = datasourceProvider;
        _scanner = scanner;
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

        ContentPathScanResult scan;
        try
        {
            var logPaths = _datasourceProvider()
                .Where(datasource => datasource.Enabled)
                .Select(datasource => datasource.LogFilePath)
                .ToList();
            scan = await _scanner.ScanAsync(logPaths, knownServices, now, cancellationToken);
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

        var report = new StatusCheckContentReport
        {
            Availability = scan.Availability,
            ScanTruncated = scan.ScanTruncated,
            ScannedBytes = scan.ScannedBytes
        };
        if (scan.Availability != "available" || scan.Samples.Count == 0)
        {
            report.CheckedAtUtc = _timeProvider.GetUtcNow();
            return report;
        }

        using var semaphore = new SemaphoreSlim(MaxConcurrentSamples);
        var tasks = scan.Samples.Select(async (sample, index) =>
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
