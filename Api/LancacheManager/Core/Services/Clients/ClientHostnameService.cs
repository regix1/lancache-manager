using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DnsClient;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Turns client addresses into the names their own network publishes for them, so a row can read
/// "gaming-pc" instead of "192.168.1.42".
///
/// Every part of this is bounded, because a network with no reverse zone is the ordinary case
/// rather than a fault: only private addresses are looked up at all, each query gets two seconds
/// and no retry, a whole batch gets a wall-clock budget, concurrent queries are capped, and misses
/// are cached exactly like answers so a dashboard refresh never re-asks a question the network has
/// already declined. Candidates come only from the host's configured IPv4 resolvers and Docker
/// containers that expose DNS port 53. Public, guessed-router, and subnet-scan destinations are
/// never added. NXDOMAIN continues to the next candidate; a resolver that returns a usable name
/// is asked first afterwards. A reverse name is a cosmetic label, never an identity or access fact.
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

    private const int MaxConcurrency = 8;
    private const int MaxLookupsPerRequest = 256;
    internal const int MaxHostnameResolvers = 8;
    private const int MaxHostnameLength = 253;

    private readonly ILogger<ClientHostnameService> _logger;
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILancacheServerLocator? _locator;
    private readonly Func<IPAddress, CancellationToken, Task<string?>> _reverseLookup;
    private readonly Func<string, IPAddress, CancellationToken, Task<string?>>? _queryOnResolver;
    private readonly Func<IReadOnlyList<string>> _nameservers;
    private readonly Func<IReadOnlyList<string>> _gateways;
    private readonly ClientHostnameCache _cache;

    private readonly SemaphoreSlim _resolverLock = new(1, 1);
    private CachedResolver? _cachedResolver;
    private string? _promotedResolverIp;

    public ClientHostnameService(
        ILogger<ClientHostnameService> logger,
        IStateService stateService,
        ISignalRNotificationService notifications,
        ILancacheServerLocator locator)
        : this(logger, stateService, notifications, reverseLookup: null, nameservers: null, gateways: null, locator)
    {
    }

    /// <summary>
    /// Substitutes the DNS transport, which answers with the raw reverse name exactly as a resolver
    /// gives it, and the host's own network configuration, so the gating, bounding, normalising and
    /// caching around them can be exercised without a resolver on the network and without the
    /// answers changing with the machine the tests run on. Pass null to use the real ones.
    /// </summary>
    internal ClientHostnameService(
        ILogger<ClientHostnameService> logger,
        IStateService stateService,
        ISignalRNotificationService notifications,
        Func<IPAddress, CancellationToken, Task<string?>>? reverseLookup,
        Func<IReadOnlyList<string>>? nameservers,
        Func<IReadOnlyList<string>>? gateways = null,
        ILancacheServerLocator? locator = null,
        Func<string, IPAddress, CancellationToken, Task<string?>>? queryOnResolver = null)
    {
        _logger = logger;
        _stateService = stateService;
        _notifications = notifications;
        _locator = locator;
        _queryOnResolver = queryOnResolver;
        _reverseLookup = reverseLookup ?? QueryReverseAsync;
        _nameservers = nameservers ?? ReadLocalNameservers;
        _gateways = gateways ?? ReadLocalGatewayIps;
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
        Forget();
    }

    public ClientHostnameSettings GetSettings()
    {
        return new ClientHostnameSettings
        {
            Resolver = _stateService.GetClientHostnameResolver(),
            GuestAccess = _stateService.GetClientHostnameGuestAccess(),
            RouterLookup = _stateService.GetClientHostnameRouterLookup(),
            DockerLookup = _stateService.GetClientHostnameDockerLookup()
        };
    }

    /// <summary>
    /// Records which DNS servers the lookup may ask. The named address is refused unless it is a
    /// private or loopback IPv4 literal: a name would have to be resolved by the very lookup being
    /// configured, and a public address would send a list of the network's own machines off-site.
    /// Nothing is stored when the address is refused, so a mistyped one cannot silently clear a
    /// setting that was working.
    /// </summary>
    public bool SetSettings(ClientHostnameSettings settings)
    {
        var trimmed = settings.Resolver?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && !LancacheServerLocator.IsSafeLanResolverIp(trimmed))
        {
            return false;
        }

        _stateService.SetClientHostnameResolver(string.IsNullOrEmpty(trimmed) ? null : trimmed);
        _stateService.SetClientHostnameGuestAccess(settings.GuestAccess);
        _stateService.SetClientHostnameRouterLookup(settings.RouterLookup);
        _stateService.SetClientHostnameDockerLookup(settings.DockerLookup);

        // Same reasoning as the toggle: someone who has just changed which servers are asked is
        // watching the screen, not waiting out the chain's ten-minute window. Switching a source
        // off has to drop the names it already found, or they stand for another half hour.
        Forget();
        return true;
    }

    public bool IsVisibleToGuests()
    {
        return _stateService.GetClientHostnameGuestAccess();
    }

    /// <summary>Drops every remembered name and the resolver chain, so the next request rebuilds both.</summary>
    private void Forget()
    {
        _cache.Clear();
        Volatile.Write(ref _cachedResolver, null);
        Volatile.Write(ref _promotedResolverIp, null);
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

        // Build the chain before starting any address work, and build it for one of the addresses
        // in hand so the routers for this batch's subnet are in it from the first query rather than
        // arriving on a rebuild partway through. An empty chain is an expected, explainable outcome;
        // failures while reading the host's network configuration are not swallowed here and flow to
        // the application's global exception handler.
        var resolvers = await GetLookupClientsAsync(candidates[0], cancellationToken);
        if (resolvers.Count == 0)
        {
            return new ClientHostnameLookupOutcome(resolved, ClientHostnamesReason.NoResolver);
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

        var resolver = await GetLookupClientAsync(cancellationToken);
        if (resolver == null)
        {
            return new ClientAddressLookupOutcome(Array.Empty<string>(), ClientAddressLookupReason.NoResolver);
        }

        var queries = await Task.WhenAll(
            QueryAddressesAsync(resolver.Client, name, QueryType.A, cancellationToken),
            QueryAddressesAsync(resolver.Client, name, QueryType.AAAA, cancellationToken));

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
            ClientAddressLookupReason.NoRecords);
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
            var hostname = SanitizeReverseName(await _reverseLookup(address, CancellationToken.None));
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
    }

    private async Task<string?> QueryReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var resolvers = await GetLookupClientsAsync(address.ToString(), cancellationToken);
        if (resolvers.Count == 0)
        {
            throw new InvalidOperationException("No LAN DNS resolver is configured for reverse lookups.");
        }

        Exception? lastFailure = null;
        var anyAnswered = false;

        foreach (var resolver in resolvers)
        {
            try
            {
                var raw = _queryOnResolver != null
                    ? await _queryOnResolver(resolver.Address, address, cancellationToken)
                    : await QueryReverseOnAsync(resolver.Client, address, cancellationToken);
                anyAnswered = true;
                var name = SanitizeReverseName(raw);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    Promote(resolver.Address);
                    return name;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                lastFailure = ex;
            }
            catch (DnsResponseException ex)
            {
                lastFailure = ex;
            }
        }

        // One resolver saying the name does not exist settles nothing while others are still to be
        // asked, so silence only counts as unanswered when every resolver failed. Every failing hop
        // records why, and that is raised so the address lands on the short failure window instead
        // of being remembered as a machine the network has no name for.
        if (!anyAnswered)
        {
            throw lastFailure!;
        }

        return null;
    }

    private async Task<string?> QueryReverseOnAsync(
        LookupClient client,
        IPAddress address,
        CancellationToken cancellationToken)
    {
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
    /// The first resolver in the trusted chain, after any promotion. Used as the "is anyone there"
    /// gate and for forward lookups.
    /// </summary>
    internal async Task<HostnameResolver?> GetLookupClientAsync(CancellationToken cancellationToken)
    {
        var resolvers = await GetLookupClientsAsync(null, cancellationToken);
        return resolvers.Count == 0 ? null : resolvers[0];
    }

    /// <summary>
    /// Every DNS server one reverse lookup will ask, cached briefly because network configuration
    /// can change while the app is running. <paramref name="clientIp"/> is the address about to be
    /// looked up: its subnet decides which router addresses belong in the chain, so a chain built
    /// for one subnet is rebuilt when a client from another turns up. Pass null when the caller has
    /// no particular address in mind. Public and non-IPv4 addresses are dropped rather than queried.
    /// </summary>
    internal async Task<IReadOnlyList<HostnameResolver>> GetLookupClientsAsync(
        string? clientIp,
        CancellationToken cancellationToken)
    {
        var prefix = SubnetPrefix(clientIp);

        var cached = Volatile.Read(ref _cachedResolver);
        if (IsUsable(cached, prefix))
        {
            return OrderResolvers(cached!.Resolvers);
        }

        await _resolverLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cachedResolver;
            if (IsUsable(cached, prefix))
            {
                return OrderResolvers(cached!.Resolvers);
            }

            // Subnets carry over from the chain being replaced. A client list that arrives one
            // address at a time would otherwise rebuild on every new subnet and drop the routers
            // it found for the previous one.
            var prefixes = new HashSet<string>(
                cached?.SubnetPrefixes ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (prefix != null)
            {
                prefixes.Add(prefix);
            }

            // Both switches are read here rather than filtered afterwards, so a source that is off
            // costs no Docker call and contributes no address that a later step has to remove.
            IReadOnlyList<string> dockerResolvers = Array.Empty<string>();
            IReadOnlyList<string> dockerHost = Array.Empty<string>();
            if (_locator != null && _stateService.GetClientHostnameDockerLookup())
            {
                dockerResolvers = await _locator.DetectLanResolverIpsAsync(cancellationToken);
                dockerHost = await _locator.DetectHostDockerInternalIpsAsync(cancellationToken);
            }

            IReadOnlyList<string> subnetRouters = _stateService.GetClientHostnameRouterLookup()
                ? SubnetRouters(prefixes)
                : Array.Empty<string>();

            var resolverIps = CollectHostnameResolverIps(
                _stateService.GetClientHostnameResolver(),
                _nameservers(),
                _gateways(),
                dockerHost,
                dockerResolvers,
                subnetRouters);

            var resolvers = new List<HostnameResolver>(resolverIps.Count);
            foreach (var resolverIp in resolverIps)
            {
                if (IPAddress.TryParse(resolverIp, out var address))
                {
                    resolvers.Add(new HostnameResolver(
                        new LookupClient(BoundedOptions(new LookupClientOptions(address))), resolverIp));
                }
            }

            if (resolvers.Count > 0)
            {
                // A rebuild triggered by a new subnet often lands on the same chain, because the
                // router it went looking for was already in the list as a gateway. An unchanged
                // chain has nothing to announce.
                var replaced = new HashSet<string>(
                    cached?.Resolvers.Select(resolver => resolver.Address) ?? Enumerable.Empty<string>());
                if (!replaced.SetEquals(resolvers.Select(resolver => resolver.Address)))
                {
                    // A new subnet always appends its own router guesses, so a changed chain is the
                    // ordinary case rather than news. Only the first chain is announced; the rest
                    // stay available to anyone who turns Debug on.
                    _logger.Log(
                        cached == null ? LogLevel.Information : LogLevel.Debug,
                        "Reverse DNS lookups will ask {ResolverIps}",
                        string.Join(", ", resolvers.Select(resolver => resolver.Address)));
                }
            }
            else
            {
                _logger.LogInformation("No safe IPv4 DNS server is configured for reverse lookups");
            }

            Volatile.Write(ref _cachedResolver, new CachedResolver(resolvers, DateTime.UtcNow, prefixes));
            return OrderResolvers(resolvers);
        }
        finally
        {
            _resolverLock.Release();
        }
    }

    /// <summary>
    /// Whether a cached chain still stands for the address about to be looked up: it must be inside
    /// its window, and it must already cover that address's subnet, because the routers for a
    /// subnet nobody had seen yet are missing from it.
    /// </summary>
    private static bool IsUsable(CachedResolver? cached, string? prefix)
    {
        return cached != null
            && DateTime.UtcNow - cached.CreatedAtUtc < _resolverTtl
            && (prefix == null || cached.SubnetPrefixes.Contains(prefix));
    }

    /// <summary>
    /// The chain in the order it is asked: the address an admin set first, because an explicit
    /// choice outranks anything discovered; then the host's own DNS configuration; then the
    /// gateways it routes through, which on a host-networked or bare-metal deployment is the LAN
    /// router itself; then the Docker host, which is how a bridge-networked container reaches a
    /// resolver running beside it; then containers publishing DNS; and last the router addresses
    /// derived from the client subnets, which are the only entries the app infers rather than
    /// reads. Inferred addresses go last so the common case never waits on one to time out.
    /// Public, link-local, multicast, malformed, and duplicate addresses are dropped, and the list
    /// is capped so the chain cannot grow into a scan.
    /// </summary>
    internal static List<string> CollectHostnameResolverIps(
        string? adminResolverIp,
        IReadOnlyList<string>? nameservers,
        IReadOnlyList<string>? gateways,
        IReadOnlyList<string>? hostDockerInternalIps,
        IReadOnlyList<string>? dockerResolverIps,
        IReadOnlyList<string>? subnetRouters)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IReadOnlyList<string>? ips)
        {
            if (ips == null || ordered.Count == MaxHostnameResolvers)
            {
                return;
            }

            foreach (var ip in ips)
            {
                if (!LancacheServerLocator.IsSafeLanResolverIp(ip) || !seen.Add(ip))
                {
                    continue;
                }

                ordered.Add(ip);
                if (ordered.Count == MaxHostnameResolvers)
                {
                    return;
                }
            }
        }

        Add(adminResolverIp == null ? null : new[] { adminResolverIp });
        Add(nameservers);
        Add(gateways);
        Add(hostDockerInternalIps);
        Add(dockerResolverIps);
        Add(subnetRouters);
        return ordered;
    }

    /// <summary>
    /// The addresses a router conventionally answers on for each subnet a client was seen in. Every
    /// private IPv4 network uses the same two last-octet conventions, so this hardcodes no network:
    /// the subnets come from the clients themselves and the addresses are derived from them. This is
    /// what reaches a home router's DHCP names from a bridge-networked container, where the routing
    /// table only ever shows the Docker bridge.
    /// </summary>
    internal static List<string> SubnetRouters(IEnumerable<string> subnetPrefixes)
    {
        var routers = new List<string>();
        foreach (var prefix in subnetPrefixes)
        {
            routers.Add($"{prefix}.1");
            routers.Add($"{prefix}.254");
        }

        return routers;
    }

    /// <summary>
    /// The first three octets of a private IPv4 address, or null for anything else. Null is the
    /// "no router can be derived from this" answer, and every caller treats it as such.
    /// </summary>
    internal static string? SubnetPrefix(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) ||
            !IPAddress.TryParse(ip, out var address) ||
            address.AddressFamily != AddressFamily.InterNetwork ||
            !LancacheServerLocator.IsPrivateIp(ip))
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        return $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
    }

    /// <summary>IPv4 DNS servers this host is configured to use.</summary>
    private static List<string> ReadLocalNameservers()
        => ReadInterfaceAddresses(properties => properties.DnsAddresses);

    /// <summary>
    /// IPv4 gateways this host routes through. On a bare-metal or host-networked deployment that is
    /// the LAN router, which is where DHCP reverse records usually live; inside a bridge-networked
    /// container it is the Docker bridge, which is harmless to ask and answers nothing.
    /// </summary>
    private static List<string> ReadLocalGatewayIps()
        => ReadInterfaceAddresses(properties =>
            properties.GatewayAddresses.Select(gateway => gateway.Address));

    private static List<string> ReadInterfaceAddresses(
        Func<IPInterfaceProperties, IEnumerable<IPAddress>> select)
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => select(network.GetIPProperties()))
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => address.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private IReadOnlyList<HostnameResolver> OrderResolvers(IReadOnlyList<HostnameResolver> resolvers)
    {
        var promoted = Volatile.Read(ref _promotedResolverIp);
        if (string.IsNullOrEmpty(promoted) || resolvers.Count < 2)
        {
            return resolvers;
        }

        var promotedIndex = -1;
        for (var index = 0; index < resolvers.Count; index++)
        {
            if (string.Equals(resolvers[index].Address, promoted, StringComparison.OrdinalIgnoreCase))
            {
                promotedIndex = index;
                break;
            }
        }

        if (promotedIndex <= 0)
        {
            return resolvers;
        }

        var ordered = new List<HostnameResolver>(resolvers.Count) { resolvers[promotedIndex] };
        for (var index = 0; index < resolvers.Count; index++)
        {
            if (index != promotedIndex)
            {
                ordered.Add(resolvers[index]);
            }
        }

        return ordered;
    }

    private void Promote(string resolverIp)
    {
        Volatile.Write(ref _promotedResolverIp, resolverIp);
    }

    /// <summary>
    /// Drops reverse names that are not a machine's LAN hostname. Docker Desktop answers PTR for
    /// RFC1918 addresses with host.docker.internal, which would otherwise label every client.
    /// Oversized names and control characters are rejected so a resolver cannot push a label into
    /// the UI that is not a DNS name.
    /// </summary>
    internal static string? SanitizeReverseName(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }

        var name = hostname.Trim().TrimEnd('.');
        if (name.Length == 0 || name.Length > MaxHostnameLength)
        {
            return null;
        }

        if (name.EndsWith(".docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (name.Any(char.IsControl) || Uri.CheckHostName(name) != UriHostNameType.Dns)
        {
            return null;
        }

        return name;
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
/// One DNS server in the trusted reverse-lookup chain.
/// </summary>
internal sealed record HostnameResolver(LookupClient Client, string Address);

/// <summary>
/// A built resolver chain, when it was built, and the client subnets it was built for. The subnets
/// belong to it because the router addresses in the chain are derived from them, so a chain is only
/// reusable for a client whose subnet it already covers.
/// </summary>
internal sealed record CachedResolver(
    IReadOnlyList<HostnameResolver> Resolvers,
    DateTime CreatedAtUtc,
    IReadOnlySet<string> SubnetPrefixes);

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

            try
            {
                return await entry.Lookup.Value.WaitAsync(cancellationToken);
            }
            catch when (entry.Lookup.Value.IsFaulted)
            {
                // Unexpected failures flow to the global handler and must not poison this address
                // for the normal hostname TTL. The next request gets a fresh attempt.
                _entries.TryRemove(new KeyValuePair<string, CacheEntry>(clientIp, entry));
                throw;
            }
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
