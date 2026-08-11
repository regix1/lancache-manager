using System.Collections.Concurrent;
using System.Net;
using DnsClient;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Hubs;
using LancacheManager.Models.Responses;

namespace LancacheManager.Core.Services;

/// <summary>
/// Turns client addresses into the names their own network publishes for them, so a row can read
/// "gaming-pc" instead of "192.168.1.42".
///
/// Every part of this is bounded, because a network with no reverse zone is the ordinary case
/// rather than a fault: only private addresses are looked up at all, each query gets two seconds
/// and no retry, a whole batch gets a wall-clock budget, concurrent queries are capped, and misses
/// are cached exactly like answers so a dashboard refresh never re-asks a question the network has
/// already declined. Private reverse zones are not delegated on the public internet, so the query
/// goes to the lancache DNS server the LAN actually uses, and falls back to the system resolver
/// when none is detected. A reverse name is a cosmetic label, never an identity or access fact.
/// </summary>
public sealed class ClientHostnameService : IClientHostnameService
{
    private static readonly TimeSpan _queryTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Wall-clock bound on one <see cref="ResolveAsync"/> batch. Whatever has not answered by then
    /// is absent from THAT response only: the queries keep running detached from the caller and
    /// warm the cache, so the remaining names arrive with the next refresh instead of holding a
    /// page open behind a resolver that is not answering.
    /// </summary>
    private static readonly TimeSpan _batchBudget = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan _hostnameTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a query that never got an answer is remembered, both in this service's cache and in
    /// the library's own. Far shorter than a name, because a resolver that was briefly unreachable
    /// must not leave a whole client list unnamed for as long as a real answer stands.
    /// </summary>
    private static readonly TimeSpan _failedResultsCacheDuration = TimeSpan.FromMinutes(5);

    /// <summary>How long a detected resolver is reused before detection runs again.</summary>
    private static readonly TimeSpan _resolverTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Bound on finding the LAN's DNS server. Detection inspects Docker and probes several candidate
    /// addresses, so it is far slower than the query it prepares for and gets a budget of its own.
    /// </summary>
    private static readonly TimeSpan _resolverDetectionTimeout = TimeSpan.FromSeconds(5);

    private const int MaxConcurrency = 8;
    private const int MaxLookupsPerRequest = 256;

    /// <summary>
    /// Stands in for the resolver address when no lancache DNS server was detected and the lookup
    /// fell back to the container's own system resolver. It decides which reason an unnamed batch is
    /// given: silence from a fallback the network never chose is
    /// <see cref="ClientHostnamesReason.NoResolver"/>, not a statement about the addresses.
    /// </summary>
    private const string SystemResolverAddress = "system";

    private readonly ILogger<ClientHostnameService> _logger;
    private readonly IStateService _stateService;
    private readonly ILancacheServerLocator _serverLocator;
    private readonly ISignalRNotificationService _notifications;
    private readonly Func<IPAddress, CancellationToken, Task<string?>> _reverseLookup;
    private readonly ClientHostnameCache _cache;

    private readonly SemaphoreSlim _resolverLock = new(1, 1);
    private LookupClient? _lookupClient;
    private DateTime _lookupClientCreatedAtUtc;

    /// <summary>
    /// Paired with <see cref="_lookupClient"/> and refreshed at the same time: the detected lancache
    /// DNS IP, or <see cref="SystemResolverAddress"/> when detection fell through. Read together with
    /// <see cref="_lookupClient"/>, so a non-null client always has an address to go with it.
    /// </summary>
    private string? _lookupClientResolverAddress;

    public ClientHostnameService(
        ILogger<ClientHostnameService> logger,
        IStateService stateService,
        ILancacheServerLocator serverLocator,
        ISignalRNotificationService notifications)
        : this(logger, stateService, serverLocator, notifications, reverseLookup: null)
    {
    }

    /// <summary>
    /// Substitutes the DNS transport, which answers with the raw reverse name exactly as a resolver
    /// gives it, so the gating, bounding, normalising and caching around it can be exercised without
    /// a resolver on the network. Pass null to use real reverse DNS.
    /// </summary>
    internal ClientHostnameService(
        ILogger<ClientHostnameService> logger,
        IStateService stateService,
        ILancacheServerLocator serverLocator,
        ISignalRNotificationService notifications,
        Func<IPAddress, CancellationToken, Task<string?>>? reverseLookup)
    {
        _logger = logger;
        _stateService = stateService;
        _serverLocator = serverLocator;
        _notifications = notifications;
        _reverseLookup = reverseLookup ?? QueryReverseAsync;
        _cache = new ClientHostnameCache(LookupAsync, _hostnameTtl, _failedResultsCacheDuration, MaxConcurrency);
    }

    public bool IsEnabled()
    {
        return _stateService.GetClientHostnameLookup();
    }

    public void SetEnabled(bool enabled)
    {
        _stateService.SetClientHostnameLookup(enabled);

        // The toggle doubles as the recovery lever: an admin who publishes the missing reverse
        // records and flips it sees names straight away instead of waiting out answers that were
        // remembered from before the network was fixed.
        _cache.Clear();
    }

    public async Task<ClientHostnameLookupOutcome> ResolveAsync(
        IReadOnlyCollection<string> clientIps,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The gate comes first so a turned-off lookup costs nothing at all, not even a parse. There
        // is nothing to explain: the feature simply was not asked to do anything.
        if (!IsEnabled() || clientIps.Count == 0)
        {
            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.None);
        }

        // Public addresses are never reverse-resolved: their reverse zone belongs to someone else,
        // and this also keeps admin session addresses out of the feature without a special case.
        var privateIps = clientIps
            .Where(LancacheServerLocator.IsPrivateIp)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Counted before the cap is applied, because an address the cap dropped never becomes a
        // candidate and so cannot show up as a missing name later. Only comparing the two counts
        // tells the caller its list was cut instead of reporting a complete-looking answer.
        var truncated = privateIps.Count > MaxLookupsPerRequest;
        var candidates = privateIps.Take(MaxLookupsPerRequest).ToList();

        if (candidates.Count == 0)
        {
            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.NoClients);
        }

        // Detached from the batch budget below on purpose, the same way a per-address query already
        // reaches the resolver (see QueryReverseAsync): detection is one-time setup that can run
        // longer than the whole batch is given, and working out which server would answer must not
        // itself be cut short by that budget. Any failure here is not an answer about an address, so
        // it degrades to the system resolver rather than failing the whole request.
        string? resolverAddress = null;
        var detection = GetLookupClientAsync(CancellationToken.None);
        try
        {
            (_, resolverAddress) = await detection.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Only this wait is abandoned. Detection was started with no token of ours, so it runs
            // on for whoever else is queued behind it rather than being cancelled by one caller
            // hanging up. Nothing awaits it once this wait is gone, so its failure is reported from
            // the task itself: a resolver that broke for a real reason must still say so at the
            // moment someone is working out why names are missing.
            _ = detection.ContinueWith(
                finished => _logger.LogWarning(
                    finished.Exception, "Could not determine which DNS server to use for reverse lookups"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine which DNS server to use for reverse lookups");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_batchBudget);

        var lookups = new List<(string ClientIp, Task<HostnameLookupResult> Result)>(candidates.Count);
        foreach (var clientIp in candidates)
        {
            lookups.Add((clientIp, _cache.GetAsync(clientIp, budget.Token)));
        }

        var budgetElapsed = false;
        var timedOutCount = 0;
        foreach (var (clientIp, resultTask) in lookups)
        {
            HostnameLookupResult result;
            try
            {
                result = await resultTask;
            }
            catch (OperationCanceledException)
            {
                // The budget elapsed (or the caller went away). Every remaining wait is already
                // cancelled, so the loop still awaits them rather than leaving their faults
                // unobserved; the queries themselves carry on detached and warm the cache, so the
                // rest of the names land on the next refresh.
                budgetElapsed = true;
                continue;
            }

            if (!result.Answered)
            {
                timedOutCount++;
            }

            if (!string.IsNullOrWhiteSpace(result.Hostname))
            {
                resolved[clientIp] = result.Hostname;
            }
        }

        // Every wait above is observed before this point. A caller that went away asked for nothing,
        // so it gets a cancellation rather than a short map that reads like a complete answer.
        cancellationToken.ThrowIfCancellationRequested();

        // A setup attempt that failed above does not settle which server was asked: every address
        // query prepares the resolver the same way, so once the batch is done the address the
        // service is holding is the one the queries actually went to. Without this, one unlucky
        // first attempt would pick the reason belonging to the fallback for a batch that reached
        // the real DNS server.
        resolverAddress ??= Volatile.Read(ref _lookupClient) != null
            ? _lookupClientResolverAddress!
            : SystemResolverAddress;

        if (budgetElapsed)
        {
            _logger.LogDebug(
                "Reverse DNS named {Resolved} of {Requested} client addresses before the batch budget elapsed",
                resolved.Count, candidates.Count);

            AnnounceRemainingNames(candidates.Where(clientIp => !resolved.ContainsKey(clientIp)).ToList());

            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.StillLooking);
        }

        if (truncated)
        {
            // Ranked above a partial answer because it is the more actionable message: the addresses
            // past the cap were never asked about, so no reverse record on the network would have
            // named them and telling someone to go add one would send them after the wrong thing.
            _logger.LogDebug(
                "Reverse DNS looked up {Requested} of {Total} private client addresses; the rest were left for a later request",
                candidates.Count, privateIps.Count);

            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.TooManyClients);
        }

        if (resolved.Count == candidates.Count)
        {
            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.None);
        }

        if (resolved.Count > 0)
        {
            // The network names some of its machines and not others, which is the ordinary result of
            // a reverse zone that was filled in by hand. Saying nothing would leave the addresses
            // that stayed bare sitting beside named ones with no explanation.
            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.SomeUnnamed);
        }

        if (resolverAddress == SystemResolverAddress)
        {
            // No lancache DNS server was detected, so the system resolver answered instead - and
            // its silence carries no weight the way the network's own DNS server's would.
            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.NoResolver);
        }

        var reason = timedOutCount == candidates.Count
            ? ClientHostnamesReason.ResolverTimeout
            : ClientHostnamesReason.NoRecords;
        return new ClientHostnameLookupOutcome(resolved, reason);
    }

    /// <summary>
    /// Watches the queries that outran the batch budget, off the request that started them, and
    /// tells every viewer once if any of them produced a name. Without this the names sit in the
    /// cache until something else asks for them, which on the first fetch after the lookup is turned
    /// on means the addresses stay bare until the page is reloaded by hand. Re-asking the cache
    /// attaches to the same in-flight query rather than starting another, and the one broadcast
    /// covers the whole batch, so a long client list cannot turn into a stream of events.
    /// </summary>
    private void AnnounceRemainingNames(List<string> pendingIps)
    {
        if (pendingIps.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(
                    pendingIps.Select(clientIp => _cache.GetAsync(clientIp, CancellationToken.None)));

                // The caller was already told the lookup is still running, so the settle is announced
                // whether or not a name came back. Staying quiet when nothing was found leaves that
                // "still looking" notice on screen with nothing left to arrive and nothing else to
                // clear it.
                await _notifications.NotifyAllAsync(SignalREvents.ClientHostnamesChanged);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Reverse DNS names that arrived after the batch budget could not be announced");
            }
        });
    }

    /// <summary>
    /// The addresses one name resolves to. Both address families are asked for at once so a
    /// dual-stacked machine answers in a single round trip, and each query is bounded exactly like
    /// a reverse one. Nothing here is cached: a name is typed in by hand and asked about once, and
    /// the person typing it has usually just changed the record they are checking for.
    /// </summary>
    public async Task<ClientAddressLookupOutcome> ResolveAddressesAsync(
        string hostname,
        CancellationToken cancellationToken)
    {
        var name = hostname?.Trim().TrimEnd('.') ?? string.Empty;
        if (name.Length == 0)
        {
            return new ClientAddressLookupOutcome(Array.Empty<string>(), ClientAddressLookupReason.NoRecords);
        }

        LookupClient client;
        string resolverAddress;
        try
        {
            // Prepared before the query clock starts, for the same reason the reverse path does it:
            // detection runs orders of magnitude slower than the query it sets up, and charging it
            // to the query timeout would fail every lookup on a cold start.
            (client, resolverAddress) = await GetLookupClientAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Finding a resolver to look up {Hostname} took too long", name);
            return new ClientAddressLookupOutcome(Array.Empty<string>(), ClientAddressLookupReason.ResolverTimeout);
        }

        var queries = await Task.WhenAll(
            QueryAddressesAsync(client, name, QueryType.A, cancellationToken),
            QueryAddressesAsync(client, name, QueryType.AAAA, cancellationToken));

        // De-duplicated across the two families, in the order the resolver gave them, so a machine
        // that answers on both stacks is not offered the same address twice.
        var addresses = queries
            .SelectMany(query => query.Addresses)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (addresses.Count > 0)
        {
            return new ClientAddressLookupOutcome(addresses, ClientAddressLookupReason.None);
        }

        // Which of the three silences this was decides whether the person typing should fix their
        // DNS, their spelling, or simply try again, so an empty list is never left to speak for
        // itself.
        if (!queries.Any(query => query.Answered))
        {
            return new ClientAddressLookupOutcome(Array.Empty<string>(), ClientAddressLookupReason.ResolverTimeout);
        }

        return new ClientAddressLookupOutcome(
            Array.Empty<string>(),
            resolverAddress == SystemResolverAddress
                ? ClientAddressLookupReason.NoResolver
                : ClientAddressLookupReason.NoRecords);
    }

    /// <summary>
    /// One address family's answer for a name, and whether the question was answered at all. A
    /// resolver that refused or never replied says nothing about the name, and is kept apart from
    /// the flat "this name has no address" that a real answer carries.
    /// </summary>
    private async Task<(IReadOnlyList<string> Addresses, bool Answered)> QueryAddressesAsync(
        LookupClient client,
        string hostname,
        QueryType queryType,
        CancellationToken cancellationToken)
    {
        try
        {
            using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queryTimeout.CancelAfter(_queryTimeout);

            var response = await client.QueryAsync(hostname, queryType, cancellationToken: queryTimeout.Token);

            // Sorted exactly as the reverse path sorts them: only an answer or a stated
            // non-existence says anything about the name, and anything else is the resolver
            // reporting on itself.
            var responseCode = response.Header.ResponseCode;
            if (responseCode != DnsHeaderResponseCode.NoError &&
                responseCode != DnsHeaderResponseCode.NotExistentDomain)
            {
                _logger.LogDebug(
                    "The {QueryType} lookup for {Hostname} was answered with {ResponseCode}",
                    queryType, hostname, responseCode);
                return (Array.Empty<string>(), false);
            }

            var addresses = queryType == QueryType.AAAA
                ? response.Answers.AaaaRecords().Select(record => record.Address.ToString()).ToList()
                : response.Answers.ARecords().Select(record => record.Address.ToString()).ToList();

            return (addresses, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("The {QueryType} lookup for {Hostname} did not answer in time", queryType, hostname);
            return (Array.Empty<string>(), false);
        }
        catch (DnsResponseException ex)
        {
            _logger.LogDebug(ex, "The {QueryType} lookup for {Hostname} failed", queryType, hostname);
            return (Array.Empty<string>(), false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The {QueryType} lookup for {Hostname} failed unexpectedly", queryType, hostname);
            return (Array.Empty<string>(), false);
        }
    }

    /// <summary>
    /// One address's reverse name, or nothing when the network has no name for it, the query
    /// failed, or it timed out. Every caller shows the raw address in all three cases, but the
    /// outcome also records whether the question was answered, because a network that has no name
    /// for a machine has settled the matter while a query that never got through has not.
    /// Internal so the window each outcome is remembered for can be observed in a cache a test can
    /// outlast, rather than inferred from the classification alone.
    /// </summary>
    internal async Task<HostnameLookupResult> LookupAsync(string clientIp)
    {
        if (!IPAddress.TryParse(clientIp, out var address))
        {
            // A string that is not an address will never become one, so this stands as long as a
            // name does rather than being asked again.
            return new HostnameLookupResult(null, Answered: true);
        }

        try
        {
            // Detached from every caller's token on purpose: this task is shared by everyone waiting
            // on the same address, so one abandoned request must not poison the cached answer. The
            // time bounds sit with the work they bound, inside QueryReverseAsync.
            //
            // Reverse answers arrive fully qualified and dot-terminated. The trailing dot is
            // stripped here, outside the transport, so every path through the service returns the
            // same shape whatever answered.
            var hostname = (await _reverseLookup(address, CancellationToken.None))?.TrimEnd('.');
            return new HostnameLookupResult(
                string.IsNullOrWhiteSpace(hostname) ? null : hostname,
                Answered: true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Reverse DNS lookup for {ClientIp} did not answer in time", clientIp);
            return new HostnameLookupResult(null, Answered: false);
        }
        catch (DnsResponseException ex)
        {
            // A transport failure surfaces as ConnectionTimeout and throws even with DNS errors
            // turned into results, so this catch is required on top of the response checks below.
            _logger.LogDebug(ex, "Reverse DNS lookup for {ClientIp} failed", clientIp);
            return new HostnameLookupResult(null, Answered: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reverse DNS lookup for {ClientIp} failed unexpectedly", clientIp);
            return new HostnameLookupResult(null, Answered: false);
        }
    }

    private async Task<string?> QueryReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        // The resolver is prepared before the query clock starts. Detection is one-time setup that
        // runs orders of magnitude slower than a PTR query, and charging it to the query timeout
        // would time out every lookup on a cold start and cache the whole client list as unnamed.
        var (client, _) = await GetLookupClientAsync(cancellationToken);

        using var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queryTimeout.CancelAfter(_queryTimeout);

        var response = await client.QueryReverseAsync(address, queryTimeout.Token);

        // Response codes arrive as a response rather than an exception, so they are sorted here.
        // Only two of them say anything about the address: the resolver answered, or it stated the
        // name does not exist. A refusal, a server failure or a malformed exchange is the resolver
        // reporting on itself, and reading that as "this machine has no name" would blank the client
        // for as long as a real answer stands. Raising it puts it on the failed-query path, which is
        // forgotten in minutes and asked again.
        var responseCode = response.Header.ResponseCode;
        if (responseCode != DnsHeaderResponseCode.NoError &&
            responseCode != DnsHeaderResponseCode.NotExistentDomain)
        {
            throw new DnsResponseException((DnsResponseCode)responseCode);
        }

        // An answered query does not mean a name came back: an empty answer section is what a
        // resolver returns for the many LAN machines that have no reverse record at all.
        if (response.Answers.Count == 0)
        {
            return null;
        }

        return response.Answers.PtrRecords().FirstOrDefault()?.PtrDomainName.Value;
    }

    /// <summary>
    /// The shared resolver, detected once per <see cref="_resolverTtl"/> window and reused by every
    /// lookup in between, alongside the address that decides which reason an unnamed batch is given:
    /// the detected lancache DNS IP, or <see cref="SystemResolverAddress"/> when detection fell
    /// through. Internal so the queueing behaviour of a batch that all arrives while one detection
    /// is running can be exercised without a resolver on the network.
    /// </summary>
    internal async Task<(LookupClient Client, string ResolverAddress)> GetLookupClientAsync(CancellationToken cancellationToken)
    {
        var cachedClient = Volatile.Read(ref _lookupClient);
        if (cachedClient != null && DateTime.UtcNow - _lookupClientCreatedAtUtc < _resolverTtl)
        {
            return (cachedClient, _lookupClientResolverAddress!);
        }

        // The wait carries no deadline of its own. A whole batch arrives here within microseconds
        // of each other, and charging each waiter a detection budget it is not spending would fail
        // everyone who lost the race the moment detection took as long as the budget sized for it.
        // Failing to set up a resolver is not an answer about the address. Whoever holds the lock
        // is already bounded below, so the wait is bounded too.
        await _resolverLock.WaitAsync(cancellationToken);
        try
        {
            cachedClient = _lookupClient;
            if (cachedClient != null && DateTime.UtcNow - _lookupClientCreatedAtUtc < _resolverTtl)
            {
                return (cachedClient, _lookupClientResolverAddress!);
            }

            using var detection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            detection.CancelAfter(_resolverDetectionTimeout);

            var (client, resolverAddress) = await BuildLookupClientAsync(detection.Token);
            _lookupClientCreatedAtUtc = DateTime.UtcNow;
            _lookupClientResolverAddress = resolverAddress;
            Volatile.Write(ref _lookupClient, client);
            return (client, resolverAddress);
        }
        finally
        {
            _resolverLock.Release();
        }
    }

    private async Task<(LookupClient Client, string ResolverAddress)> BuildLookupClientAsync(CancellationToken cancellationToken)
    {
        var mode = StatusCheckResolverModes.Normalize(_stateService.GetStatusCheckResolverMode());

        string? resolverIp;
        try
        {
            resolverIp = await _serverLocator.DetectDnsServerIpAsync(mode, knownCacheIps: null, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Outlasting the detection budget is the same outcome as finding nothing, and the
            // fallback below still resolves names. Failing here instead would leave every address
            // unnamed for the whole name TTL because a Docker inspect was slow once.
            _logger.LogInformation("Detecting the lancache DNS server took too long; using the system resolver");
            resolverIp = null;
        }

        // The address comes from detection, never from a request, and is re-checked against the
        // private/loopback gate before a resolver is ever pointed at it.
        if (resolverIp != null &&
            LancacheServerLocator.IsProbeableCandidateIp(resolverIp) &&
            IPAddress.TryParse(resolverIp, out var resolverAddress))
        {
            _logger.LogInformation("Reverse DNS lookups will use the lancache DNS server {ResolverIp}", resolverIp);
            return (new LookupClient(BoundedOptions(new LookupClientOptions(resolverAddress))), resolverIp);
        }

        // No lancache DNS server was detected. The system-configured resolver is the next best
        // source of a reverse name, and unlike the cache-bypass probes a name carries no trust.
        _logger.LogInformation("No lancache DNS server detected; reverse DNS lookups will use the system resolver");
        return (new LookupClient(BoundedOptions(new LookupClientOptions())), SystemResolverAddress);
    }

    private static LookupClientOptions BoundedOptions(LookupClientOptions options)
    {
        options.Timeout = _queryTimeout;
        options.Retries = 0;
        options.UseCache = true;
        // The library's own negative cache, on top of this service's cache: a machine with no
        // reverse record is the common case and must not cost a query on every refresh.
        options.CacheFailedResults = true;
        options.FailedResultsCacheDuration = _failedResultsCacheDuration;
        return options;
    }
}

/// <summary>
/// What one reverse lookup established: the name the network published, if any, and whether the
/// question was answered at all. "This machine has no reverse record" is a settled fact about the
/// network and stands for the full name window; "the query never got through" says nothing about
/// the address and is forgotten quickly so the next refresh asks again.
/// </summary>
internal readonly record struct HostnameLookupResult(string? Hostname, bool Answered);

/// <summary>
/// Per-address reverse-name cache: one query per address per TTL window however many rows ask for
/// it, concurrent askers share a single in-flight query, and fan-out is capped so a large client
/// list cannot flood the network's DNS server. A "no name" answer is cached exactly like a name,
/// while a query that failed is held only briefly.
///
/// Queries run detached from caller cancellation (the query's own timeout bounds them) so one
/// abandoned caller can never poison the shared cached task; callers still honour their own token
/// through <see cref="Task.WaitAsync(CancellationToken)"/>.
/// </summary>
internal sealed class ClientHostnameCache
{
    private readonly Func<string, Task<HostnameLookupResult>> _lookup;
    private readonly TimeSpan _ttl;
    private readonly TimeSpan _failureTtl;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class CacheEntry
    {
        public required Lazy<Task<HostnameLookupResult>> Lookup { get; init; }
        public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    }

    internal ClientHostnameCache(
        Func<string, Task<HostnameLookupResult>> lookup,
        TimeSpan ttl,
        TimeSpan failureTtl,
        int maxConcurrency)
    {
        _lookup = lookup;
        _ttl = ttl;
        _failureTtl = failureTtl;
        _concurrency = new SemaphoreSlim(maxConcurrency);
    }

    internal async Task<HostnameLookupResult> GetAsync(string clientIp, CancellationToken cancellationToken)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(clientIp, key => new CacheEntry
            {
                Lookup = new Lazy<Task<HostnameLookupResult>>(() => LookupBoundedAsync(key))
            });

            if (DateTime.UtcNow - entry.CreatedAtUtc > LifetimeOf(entry))
            {
                // Expired: retire this entry and loop so exactly one caller seeds the replacement.
                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(clientIp, entry));
                continue;
            }

            return await entry.Lookup.Value.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Forgets every address, so a network that has just been given the reverse records it was
    /// missing is asked again rather than answered from before the fix.
    /// </summary>
    internal void Clear()
    {
        _entries.Clear();
    }

    /// <summary>
    /// How long an entry may stand: the full window for an answer, whether or not it carried a
    /// name, and only the failure window for a query that never got one. An entry still in flight
    /// keeps the full window until it settles.
    /// </summary>
    private TimeSpan LifetimeOf(CacheEntry entry)
    {
        var lookup = entry.Lookup;
        if (!lookup.IsValueCreated || !lookup.Value.IsCompletedSuccessfully)
        {
            return _ttl;
        }

        return lookup.Value.Result.Answered ? _ttl : _failureTtl;
    }

    private async Task<HostnameLookupResult> LookupBoundedAsync(string clientIp)
    {
        await _concurrency.WaitAsync();
        try
        {
            return await _lookup(clientIp);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
