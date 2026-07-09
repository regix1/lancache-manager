using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DnsClient;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;
using LancacheManager.Models.Responses;
using Microsoft.Extensions.Options;

namespace LancacheManager.Core.Services.StatusCheck;

/// <summary>
/// Runs the Status Check DNS sweep: resolves every cache-domains entry (via the configured/detected
/// lancache-dns server, or the system resolver as a last resort) and compares against the lancache
/// server's expected IP(s); heartbeat-verifies the cache once per sweep. Also backs the ad hoc
/// "test a domain" flow. One sweep at a time (Interlocked guard), tracked via
/// <see cref="IUnifiedOperationTracker"/> for cancellation/progress, persisted via
/// <see cref="IStateService"/> so the last result survives a restart.
/// </summary>
public sealed class StatusCheckService : IStatusCheckService
{
    private const int MaxConcurrency = 16;
    private static readonly TimeSpan _perDomainTimeout = TimeSpan.FromSeconds(3);
    private const string WildcardProbeLabel = "status-check";
    private const int ProgressEveryNDomains = 10;
    // Full-detail failure lines are capped so a total outage (every domain failing) explains
    // itself in the log without printing hundreds of near-identical warnings.
    private const int MaxLoggedFailureSamples = 10;

    private readonly ILogger<StatusCheckService> _logger;
    private readonly ICacheDomainsService _domainsService;
    private readonly ILancacheServerLocator _serverLocator;
    private readonly ILancacheEnvironmentSource _environmentSource;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ISignalRNotificationService _notifications;
    private readonly IStateService _stateService;
    private readonly IOptionsMonitor<PrefillNetworkOptions> _networkOptions;

    private int _running;
    private Guid? _currentOperationId;
    private readonly object _lastResultLock = new();
    private StatusCheckResult? _cachedLastResult;
    private bool _lastResultLoaded;

    // Contract amendment v1.4: active verification. Each unique private resolved IP is
    // heartbeat-probed at most once per TTL window - shared by the sweep (dedup across the
    // hundreds of domains that resolve to the same few cache IPs) and by test-domain requests.
    private readonly HeartbeatVerdictCache _heartbeatCache;

    public StatusCheckService(
        ILogger<StatusCheckService> logger,
        ICacheDomainsService domainsService,
        ILancacheServerLocator serverLocator,
        ILancacheEnvironmentSource environmentSource,
        IUnifiedOperationTracker operationTracker,
        ISignalRNotificationService notifications,
        IStateService stateService,
        IOptionsMonitor<PrefillNetworkOptions> networkOptions)
    {
        _logger = logger;
        _domainsService = domainsService;
        _serverLocator = serverLocator;
        _environmentSource = environmentSource;
        _operationTracker = operationTracker;
        _notifications = notifications;
        _stateService = stateService;
        _networkOptions = networkOptions;
        _heartbeatCache = new HeartbeatVerdictCache(
            ip => serverLocator.ProbeHeartbeatAsync(ip, CancellationToken.None),
            ttl: TimeSpan.FromSeconds(60),
            maxConcurrency: 8);
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public Guid? CurrentOperationId => _currentOperationId;

    public StatusCheckResult? GetLastResult()
    {
        lock (_lastResultLock)
        {
            if (!_lastResultLoaded)
            {
                _cachedLastResult = _stateService.GetStatusCheckResult();
                _lastResultLoaded = true;
            }
            return _cachedLastResult;
        }
    }

    public string GetResolverMode()
    {
        // Normalize on read so a corrupt/absent persisted value can never break the sweep.
        return StatusCheckResolverModes.Normalize(_stateService.GetStatusCheckResolverMode());
    }

    public void SetResolverMode(string mode)
    {
        if (!StatusCheckResolverModes.IsValid(mode))
        {
            throw new ArgumentException(
                $"Invalid resolver mode '{mode}'. Expected one of: {string.Join(", ", StatusCheckResolverModes.All)}.",
                nameof(mode));
        }

        _stateService.SetStatusCheckResolverMode(mode);
    }

    public Guid? StartSweep()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return null;
        }

        var cts = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(OperationType.StatusCheck, "Status Check Sweep", cts);
        _currentOperationId = operationId;

        // Sweep runs detached from the request that started it - the controller returns 202 with
        // the operationId immediately and progress/completion arrive over SignalR. Only the
        // operation tracker's CTS may cancel it; a client disconnecting after the 202 must not.
        _ = Task.Run(() => RunSweepAsync(operationId, cts), CancellationToken.None);

        return operationId;
    }

    public async Task<(DomainCheckResult Result, HeartbeatResult? Heartbeat)> TestDomainAsync(string domain, CancellationToken cancellationToken)
    {
        var location = await _serverLocator.LocateAsync(cancellationToken);
        var (resolverSource, dnsServer, dnsClient) = await ResolveDnsTargetAsync(location, cancellationToken);

        // Ad hoc tests aren't tied to a known service; the frontend already knows which service the
        // user picked from the dropdown (if any) and can render that context itself.
        var result = await ResolveDomainAsync(domain, string.Empty, location.CacheIps, dnsClient, cancellationToken);

        // Heartbeat only makes sense against a LAN cache; probing whatever public IP an arbitrary
        // hostname resolves to would make this endpoint a server-side request proxy (SSRF surface),
        // so the probe is restricted to private/LAN addresses. Null heartbeat = not attempted.
        // The verdict cache makes this free when the sweep already probed the same IP.
        HeartbeatResult? heartbeat = null;
        var probeIp = result.ResolvedIps.FirstOrDefault(LancacheServerLocator.IsPrivateIp);
        if (probeIp != null)
        {
            heartbeat = await _heartbeatCache.GetAsync(probeIp, cancellationToken);
        }

        _logger.LogDebug("Status Check: tested domain {Domain} via {ResolverSource} resolver ({DnsServer}) -> {Status}",
            domain, resolverSource, dnsServer ?? "system", result.Status);

        return (result, heartbeat);
    }

    private async Task RunSweepAsync(Guid operationId, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var startedAt = DateTime.UtcNow;

        try
        {
            _notifications.NotifyAllFireAndForget(SignalREvents.StatusCheckProgress, new
            {
                operationId,
                completedDomains = 0,
                totalDomains = 0,
                currentService = (string?)null
            });

            var domains = await _domainsService.GetDomainsAsync(forceRefresh: false, token);
            var location = await _serverLocator.LocateAsync(token);
            var (resolverSource, dnsServer, dnsClient) = await ResolveDnsTargetAsync(location, token);

            // Contract amendment v1.1: DISABLE_<SERVICE>=true (uppercased cache_domains name) in
            // lancache-dns means the service is intentionally not cached - never query its domains,
            // and exclude it from the progress denominator so the ribbon still reaches 100%.
            var disabledServices = await DetermineDisabledServicesAsync(domains.Services, token);

            var totalDomains = domains.Services
                .Where(s => !disabledServices.Contains(s.Name))
                .Sum(s => s.Domains.Count);
            var completedDomains = 0;
            var serviceResults = new List<ServiceCheckResult>();

            _logger.LogInformation(
                "Status Check: sweep {OperationId} starting - {TotalDomains} domains across {ServiceCount} services via {ResolverSource} resolver {DnsServer}",
                operationId, totalDomains, domains.Services.Count, resolverSource, dnsServer ?? "system");

            // Cache-node aggregation: exact IP -> servedBy pairs captured while resolving this
            // sweep's domains (ResolveDomainAsync populates it below), not re-derived from the
            // heartbeat cache, so a leftover test-domain probe can never bleed into it.
            var verifiedIps = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Sweep-failure diagnostics: per-reason counts plus a capped number of full-detail
            // samples, so a mass failure (rate-limited resolver, resolver dying mid-sweep) is
            // explained in the server log instead of surfacing only as "unresolved" rows in the UI.
            var failureReasons = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
            var failureSamplesLogged = 0;

            foreach (var service in domains.Services)
            {
                token.ThrowIfCancellationRequested();

                if (disabledServices.Contains(service.Name))
                {
                    serviceResults.Add(new ServiceCheckResult
                    {
                        Service = service.Name,
                        Description = service.Description,
                        Status = "disabled",
                        ResolvedCount = 0,
                        TotalCount = service.Domains.Count,
                        Domains = new List<DomainCheckResult>()
                    });
                    continue;
                }

                using var semaphore = new SemaphoreSlim(MaxConcurrency);
                var tasks = service.Domains.Select(async originalEntry =>
                {
                    await semaphore.WaitAsync(token);
                    try
                    {
                        var result = await ResolveDomainAsync(
                            originalEntry, service.Name, location.CacheIps, dnsClient, token, verifiedIps);
                        if (result.ResolvedIps.Count == 0)
                        {
                            var reason = result.Error ?? "No A records returned";
                            failureReasons.AddOrUpdate(reason, 1, static (_, count) => count + 1);
                            if (Interlocked.Increment(ref failureSamplesLogged) <= MaxLoggedFailureSamples)
                            {
                                _logger.LogWarning(
                                    "Status Check: {Domain} ({Service}) did not resolve via {DnsServer}: {Reason} ({LatencyMs} ms)",
                                    result.Domain, service.Name, dnsServer ?? "system resolver", reason, result.LatencyMs);
                            }
                        }
                        return result;
                    }
                    finally
                    {
                        semaphore.Release();
                        var completed = Interlocked.Increment(ref completedDomains);
                        if (completed % ProgressEveryNDomains == 0)
                        {
                            _notifications.NotifyAllFireAndForget(SignalREvents.StatusCheckProgress, new
                            {
                                operationId,
                                completedDomains = completed,
                                totalDomains,
                                currentService = service.Name
                            });
                        }
                    }
                }).ToList();

                var domainResults = (await Task.WhenAll(tasks)).ToList();

                // Progress at the service boundary too, per contract ("per service boundary OR every
                // 10 domains, whichever first").
                _notifications.NotifyAllFireAndForget(SignalREvents.StatusCheckProgress, new
                {
                    operationId,
                    completedDomains,
                    totalDomains,
                    currentService = service.Name
                });

                serviceResults.Add(BuildServiceResultCore(service.Name, service.Description, domainResults));
            }

            var failedDomains = failureReasons.Values.Sum();
            if (failedDomains > 0)
            {
                var breakdown = string.Join("; ", failureReasons
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(5)
                    .Select(kvp => $"{kvp.Key} x{kvp.Value}"));
                _logger.LogWarning(
                    "Status Check: sweep {OperationId} finished with {FailedDomains}/{TotalDomains} domains unresolved. Reasons: {Breakdown}",
                    operationId, failedDomains, totalDomains, breakdown);

                // A resolver that passed the detection probe moments ago but failed most of the
                // sweep is rate limiting the burst or died mid-sweep - name that outright, it is
                // the actionable signal (the UI otherwise shows only a wall of "unresolved").
                if (resolverSource != "system" && failedDomains * 2 > totalDomains)
                {
                    _logger.LogWarning(
                        "Status Check: resolver {DnsServer} answered the detection probe but {FailedDomains} sweep queries failed - it is likely rate limiting the sweep or stopped answering mid-sweep",
                        dnsServer, failedDomains);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Status Check: sweep {OperationId} finished - all {TotalDomains} domains resolved",
                    operationId, totalDomains);
            }

            var heartbeat = await BuildHeartbeatResultAsync(location, token);

            var result = new StatusCheckResult
            {
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTime.UtcNow,
                ResolverSource = resolverSource,
                DnsServer = dnsServer,
                ExpectedCacheIps = location.CacheIps,
                ExpectedIpSource = location.Source,
                Heartbeat = heartbeat,
                Services = serviceResults,
                Summary = BuildSummaryCore(serviceResults),
                AvgLatencyMs = BuildAvgLatencyMs(serviceResults),
                CacheNodes = BuildCacheNodes(verifiedIps)
            };

            lock (_lastResultLock)
            {
                _cachedLastResult = result;
                _lastResultLoaded = true;
            }
            _stateService.SetStatusCheckResult(result);

            _operationTracker.CompleteOperation(operationId, true);
            try
            {
                await _notifications.NotifyAllAsync(SignalREvents.StatusCheckComplete, new
                {
                    operationId,
                    success = true,
                    error = (string?)null,
                    result
                });
            }
            catch (Exception notifyEx)
            {
                // The sweep succeeded and the result is already persisted - a failed broadcast
                // must not fall into the generic catch and be re-announced as a failed sweep.
                _logger.LogWarning(notifyEx,
                    "Status Check sweep {OperationId} completed but the completion broadcast failed", operationId);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Status Check sweep {OperationId} cancelled", operationId);
            _operationTracker.CompleteOperation(operationId, false, "Cancelled");
            await _notifications.NotifyAllAsync(SignalREvents.StatusCheckComplete, new
            {
                operationId,
                success = false,
                error = "Cancelled",
                result = (StatusCheckResult?)null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Status Check sweep {OperationId} failed", operationId);
            _operationTracker.CompleteOperation(operationId, false, ex.Message);
            await _notifications.NotifyAllAsync(SignalREvents.StatusCheckComplete, new
            {
                operationId,
                success = false,
                error = ex.Message,
                result = (StatusCheckResult?)null
            });
        }
        finally
        {
            // _running must clear before _currentOperationId: a reader between the two writes then
            // sees either {running:true, opId:set} or {running:false, opId:stale}, never the
            // {running:true, opId:null} window the old ordering allowed.
            cts.Dispose();
            Interlocked.Exchange(ref _running, 0);
            _currentOperationId = null;
        }
    }

    /// <summary>Looks up <c>DISABLE_&lt;SERVICE&gt;</c> (uppercased cache_domains service name) for
    /// every listed service via the tiered env source. Per-service IP overrides
    /// (<c>&lt;SERVICE&gt;CACHE_IP</c>-style) are explicitly out of scope (v1.1) - not implemented,
    /// not guessed.</summary>
    private async Task<HashSet<string>> DetermineDisabledServicesAsync(List<CacheDomainService> services, CancellationToken ct)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var service in services)
        {
            var envKey = $"DISABLE_{service.Name.ToUpperInvariant()}";
            var envResult = await _environmentSource.GetValueAsync(envKey, ct);
            if (EnvValueParsing.ParseBool(envResult.Value) == true)
            {
                disabled.Add(service.Name);
            }
        }
        return disabled;
    }

    private async Task<(string ResolverSource, string? DnsServer, LookupClient? Client)> ResolveDnsTargetAsync(
        LancacheServerLocation location, CancellationToken ct)
    {
        var configuredDnsIp = _networkOptions.CurrentValue.LancacheDnsIp;
        if (!string.IsNullOrWhiteSpace(configuredDnsIp) &&
            !string.Equals(configuredDnsIp, "auto", StringComparison.OrdinalIgnoreCase) &&
            IPAddress.TryParse(configuredDnsIp, out var configuredAddress))
        {
            return ("configured", configuredDnsIp, new LookupClient(configuredAddress, 53));
        }

        // Auto-detect the on-host lancache DNS. The user-selected resolver mode ("auto" | "bridge" |
        // "host") scopes which candidate groups are probed. Pass the located cache IP(s) as candidates
        // - a monolithic image co-locates DNS + cache on the same host, so cacheIp:53 is often the DNS.
        var detectedIp = await _serverLocator.DetectDnsServerIpAsync(GetResolverMode(), location.CacheIps, ct);
        if (string.IsNullOrWhiteSpace(detectedIp))
        {
            // Detection is a handful of 2s UDP probes - one dropped packet must not silently demote
            // the whole sweep to the system resolver, which answers public IPs for every cache-only
            // name and reads as "nothing is cached". Retry once before conceding.
            _logger.LogInformation(
                "Status Check: DNS auto-detection found no resolver on the first pass; retrying once before falling back to the system resolver");
            detectedIp = await _serverLocator.DetectDnsServerIpAsync(GetResolverMode(), location.CacheIps, ct);
        }
        if (!string.IsNullOrWhiteSpace(detectedIp) && IPAddress.TryParse(detectedIp, out var detectedAddress))
        {
            return ("detected", detectedIp, new LookupClient(detectedAddress, 53));
        }

        return ("system", null, null);
    }

    private async Task<DomainCheckResult> ResolveDomainAsync(
        string originalEntry,
        string serviceName,
        List<string> expectedIps,
        LookupClient? dnsClient,
        CancellationToken ct,
        ConcurrentDictionary<string, string>? verifiedIpSink = null)
    {
        var queryDomain = originalEntry.StartsWith('*')
            ? WildcardProbeLabel + originalEntry[1..]
            : originalEntry;

        var stopwatch = Stopwatch.StartNew();
        List<string> resolvedIps = new();
        string? error = null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_perDomainTimeout);

        try
        {
            if (dnsClient != null)
            {
                var response = await dnsClient.QueryAsync(queryDomain, QueryType.A, cancellationToken: timeoutCts.Token);
                resolvedIps = response.Answers.ARecords().Select(r => r.Address.ToString()).ToList();
                if (resolvedIps.Count == 0 && response.HasError)
                {
                    error = string.IsNullOrWhiteSpace(response.ErrorMessage) ? "NXDOMAIN" : response.ErrorMessage;
                }
            }
            else
            {
                var addresses = await Dns.GetHostAddressesAsync(queryDomain, timeoutCts.Token);
                resolvedIps = addresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToList();
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            error = "DNS query timed out";
        }
        catch (SocketException ex)
        {
            error = ex.SocketErrorCode == SocketError.HostNotFound ? "NXDOMAIN" : $"{ex.SocketErrorCode}: {ex.Message}";
        }
        catch (DnsResponseException ex)
        {
            error = ex.Code == DnsResponseCode.NotExistentDomain
                ? "NXDOMAIN"
                : string.IsNullOrWhiteSpace(ex.DnsError) ? ex.Code.ToString() : $"{ex.DnsError} ({ex.Code})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        stopwatch.Stop();

        // An empty answer with no resolver error (possible via DnsClient when the response has no
        // A records but no error flag) must still explain itself - never an unresolved row with
        // a null reason.
        if (resolvedIps.Count == 0 && error == null)
        {
            error = "No A records returned";
        }

        // Contract amendment v1.4: actively verify by probing /lancache-heartbeat on the resolved
        // IPs (deduped + cached per sweep). Only private/LAN answers are probed - a public answer
        // can never be the user's cache, and probing arbitrary public IPs server-side is pure SSRF
        // surface; public answers classify directly via the verdict table instead.
        var heartbeatVerified = false;
        string? servedBy = null;
        foreach (var ip in resolvedIps.Where(LancacheServerLocator.IsPrivateIp).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var probe = await _heartbeatCache.GetAsync(ip, ct);
            if (probe.Reachable)
            {
                heartbeatVerified = true;
                servedBy = probe.ServedBy;
                if (verifiedIpSink != null && !string.IsNullOrWhiteSpace(probe.ServedBy))
                {
                    verifiedIpSink.TryAdd(ip, probe.ServedBy);
                }
                break;
            }
        }

        return new DomainCheckResult
        {
            Domain = queryDomain,
            OriginalEntry = originalEntry,
            Service = serviceName,
            Status = BuildDomainStatus(resolvedIps, expectedIps, heartbeatVerified),
            ResolvedIps = resolvedIps,
            ExpectedIps = expectedIps,
            HeartbeatVerified = heartbeatVerified,
            ServedBy = servedBy,
            Error = error,
            LatencyMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1)
        };
    }


    private async Task<HeartbeatResult> BuildHeartbeatResultAsync(LancacheServerLocation location, CancellationToken ct)
    {
        var candidateIp = location.CacheIps.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidateIp))
        {
            return new HeartbeatResult
            {
                Reachable = false,
                ServedBy = null,
                CacheIp = null,
                Error = "No cache server IP could be determined (set Prefill__LancacheIp or ensure the lancache container is discoverable)."
            };
        }

        return await _serverLocator.ProbeHeartbeatAsync(candidateIp, ct);
    }

    /// <summary>
    /// Verdict table (contract amendment v1.4 - active verification): a heartbeat-verified answer
    /// proves the cache is live regardless of the expected list; without a heartbeat the
    /// expected-IP overlap decides (resolved with heartbeatVerified=false = DNS right, cache not
    /// answering); with neither, a private answer is "unverified" (cache down or wrong host -
    /// can't tell) while an all-public answer is "mismatched" (traffic is going to the internet,
    /// the failure this tool exists to catch). No A records = "unresolved".
    /// </summary>
    internal static string BuildDomainStatus(List<string> resolvedIps, List<string> expectedIps, bool heartbeatVerified)
    {
        if (resolvedIps.Count == 0)
        {
            return "unresolved";
        }

        if (heartbeatVerified)
        {
            return "resolved";
        }

        if (expectedIps.Count > 0 && resolvedIps.Any(ip => expectedIps.Contains(ip, StringComparer.OrdinalIgnoreCase)))
        {
            return "resolved";
        }

        // Any private answer keeps the benefit of the doubt (DNS points into the LAN); only an
        // all-public answer set is definitively bypassing the cache.
        return resolvedIps.Any(LancacheServerLocator.IsPrivateIp) ? "unverified" : "mismatched";
    }

    internal static ServiceCheckResult BuildServiceResultCore(string name, string description, List<DomainCheckResult> domains)
    {
        var resolvedCount = domains.Count(d => d.Status == "resolved");
        string status;
        if (domains.Count == 0)
        {
            status = "unresolved";
        }
        else if (resolvedCount == domains.Count)
        {
            status = "resolved";
        }
        else if (resolvedCount > 0)
        {
            // Under v1.4 a service can mix resolved (heartbeat-verified) domains with unverified
            // ones - any confirmed domain makes it "partial", not "unverified".
            status = "partial";
        }
        else if (domains.Any(d => d.Status == "unverified"))
        {
            status = "unverified";
        }
        else
        {
            status = "unresolved";
        }

        return new ServiceCheckResult
        {
            Service = name,
            Description = description,
            Status = status,
            ResolvedCount = resolvedCount,
            TotalCount = domains.Count,
            Domains = domains
        };
    }

    internal static StatusCheckSummary BuildSummaryCore(List<ServiceCheckResult> services)
    {
        return new StatusCheckSummary
        {
            TotalServices = services.Count,
            ResolvedServices = services.Count(s => s.Status == "resolved"),
            PartialServices = services.Count(s => s.Status == "partial"),
            UnresolvedServices = services.Count(s => s.Status == "unresolved"),
            // Contract amendments v1.1/v1.3: disabled and unverified services are excluded from
            // the resolved/partial/unresolved counts above (none of those strings match) and from
            // verdict sentence math; TotalServices still counts them so
            // Resolved+Partial+Unresolved+Disabled+Unverified == TotalServices always holds.
            DisabledServices = services.Count(s => s.Status == "disabled"),
            UnverifiedServices = services.Count(s => s.Status == "unverified"),
            TotalDomains = services.Sum(s => s.TotalCount),
            ResolvedDomains = services.Sum(s => s.ResolvedCount),
            UnverifiedDomains = services.Sum(s => s.Domains.Count(d => d.Status == "unverified"))
        };
    }

    /// <summary>Mean <see cref="DomainCheckResult.LatencyMs"/> across every domain result in the
    /// sweep that has one, regardless of status - unresolved/mismatched/unverified rows still
    /// measured a real DNS round trip. Null when nothing in the sweep carries a latency (e.g.
    /// every service was disabled).</summary>
    internal static double? BuildAvgLatencyMs(IEnumerable<ServiceCheckResult> services)
    {
        var latencies = services
            .SelectMany(s => s.Domains)
            .Where(d => d.LatencyMs.HasValue)
            .Select(d => d.LatencyMs!.Value)
            .ToList();

        return latencies.Count > 0 ? Math.Round(latencies.Average(), 1) : null;
    }

    /// <summary>Groups verified cache IPs by the hostname that answered their heartbeat, so the UI
    /// can report e.g. "2 cache nodes · 16 IPs" when a fleet of cache boxes sits behind DNS round
    /// robin. Built from the exact IP -&gt; servedBy pairs captured while resolving this sweep's
    /// domains (see the <c>verifiedIps</c> sink threaded through <see cref="ResolveDomainAsync"/>),
    /// never from arbitrary heartbeat-cache state. Empty when nothing verified.</summary>
    internal static List<CacheNodeInfo> BuildCacheNodes(IReadOnlyDictionary<string, string> verifiedIps)
    {
        return verifiedIps
            .GroupBy(kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CacheNodeInfo
            {
                ServedBy = g.Key,
                Ips = g.Select(kvp => kvp.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(ip => ip, Comparer<string>.Create(CompareIpAddresses))
                    .ToList()
            })
            .OrderBy(node => node.ServedBy, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Numeric (not lexicographic) IP ordering so e.g. "172.16.2.99" sorts before
    /// "172.16.2.100". Falls back to unparsed input trailing last if somehow not an IP literal.</summary>
    private static int CompareIpAddresses(string a, string b)
    {
        var bytesA = IPAddress.TryParse(a, out var ipA) ? ipA.GetAddressBytes() : Array.Empty<byte>();
        var bytesB = IPAddress.TryParse(b, out var ipB) ? ipB.GetAddressBytes() : Array.Empty<byte>();
        var length = Math.Min(bytesA.Length, bytesB.Length);
        for (var i = 0; i < length; i++)
        {
            var cmp = bytesA[i].CompareTo(bytesB[i]);
            if (cmp != 0) return cmp;
        }
        return bytesA.Length.CompareTo(bytesB.Length);
    }
}
