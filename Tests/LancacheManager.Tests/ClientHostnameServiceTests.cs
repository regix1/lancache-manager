using System.Net;
using System.Reflection;
using System.Text.Json;
using DnsClient;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the bounds on client hostname lookups: the feature costs nothing while it is off, public
/// addresses are never reverse-resolved, a network that has no name for a machine is asked once
/// rather than on every refresh, concurrent askers share one query, a resolver failure is a missing
/// name rather than a fault, and the name that reaches a row carries no trailing dot.
/// </summary>
public sealed class ClientHostnameServiceTests
{
    [Fact]
    public async Task ATurnedOffLookupReturnsNoNamesAndAsksNothingAsync()
    {
        var queries = 0;
        var service = CreateService(enabled: false, _ =>
        {
            queries++;
            return "gaming-pc.lan.";
        });

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        Assert.Equal(ClientHostnamesReason.None, outcome.Reason);
        Assert.Equal(0, queries);
        Assert.False(service.IsEnabled());
    }

    [Fact]
    public async Task PublicAddressesAreNeverReverseResolvedAsync()
    {
        var queries = 0;
        var service = CreateService(enabled: true, _ =>
        {
            queries++;
            return "dns.google.";
        });

        var outcome = await service.ResolveAsync(
            new[] { "8.8.8.8", "203.0.113.7" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        // Every candidate was filtered out by the private-IP gate before any lookup ran, so there is
        // nothing left to ask - a distinct reason from "the DNS server answered with nothing".
        Assert.Equal(ClientHostnamesReason.NoClients, outcome.Reason);
        Assert.Equal(0, queries);
    }

    [Fact]
    public async Task AResolvedNameLosesItsTrailingDotAsync()
    {
        var service = CreateService(enabled: true, _ => "gaming-pc.lan.");

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
        Assert.Equal("10.0.0.9", Assert.Single(outcome.Hostnames).Key);
        // A name was found, so there is nothing to explain, whichever resolver answered.
        Assert.Equal(ClientHostnamesReason.None, outcome.Reason);
    }

    [Fact]
    public async Task AnAddressWithNoNameIsRememberedSoTheNetworkIsAskedOnceAsync()
    {
        var queries = 0;
        var service = CreateService(enabled: true, _ =>
        {
            queries++;
            return null;
        });

        var first = (await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None)).Hostnames;
        var second = (await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None)).Hostnames;

        Assert.Empty(first);
        Assert.Empty(second);
        // A network with no reverse zone is the common case, so the miss must be cached exactly
        // like an answer or every dashboard refresh re-asks the same failing question.
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task AFailedQueryIsForgottenSoonerThanAnAddressWithNoNameAsync()
    {
        // "The network has no name for this machine" is settled and stands for the full name
        // window. "The query never got through" says nothing about the address at all, so it has to
        // be asked again soon instead of reading as a definitive blank for as long as an answer.
        var answeredQueries = 0;
        var answered = new ClientHostnameCache(
            _ =>
            {
                answeredQueries++;
                return Task.FromResult(new HostnameLookupResult(null, Answered: true));
            },
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMilliseconds(30),
            maxConcurrency: 1);

        var failedQueries = 0;
        var failed = new ClientHostnameCache(
            _ =>
            {
                failedQueries++;
                return Task.FromResult(new HostnameLookupResult(null, Answered: false));
            },
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMilliseconds(30),
            maxConcurrency: 1);

        await answered.GetAsync("10.0.0.9", CancellationToken.None);
        await failed.GetAsync("10.0.0.9", CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(150));

        await answered.GetAsync("10.0.0.9", CancellationToken.None);
        await failed.GetAsync("10.0.0.9", CancellationToken.None);

        Assert.Equal(1, answeredQueries);
        Assert.Equal(2, failedQueries);
    }

    [Fact]
    public async Task TurningTheLookupOffAndOnAsksTheNetworkAgainAsync()
    {
        var queries = 0;
        var service = CreateService(enabled: true, _ =>
        {
            queries++;
            return null;
        });

        await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);
        service.SetEnabled(false);
        service.SetEnabled(true);
        await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        // The toggle is the only recovery lever an admin has: publishing the reverse records that
        // were missing and flipping it must re-ask the network rather than replay what it said
        // before the zone was fixed.
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task AnAddressThatWaitsOutASlowResolverSetupStillGetsTheResolverAsync()
    {
        // Finding the resolver happens once for a whole batch: one lookup detects the LAN's DNS
        // server while the rest queue behind it. The queue must not carry a deadline of its own,
        // because the moment detection takes as long as the budget sized for it, every queued
        // address fails at SETTING UP a resolver, and that is not an answer about the address. The
        // hold below outlasts that budget on purpose, since that is the case that used to leave a
        // whole batch unnamed after every resolver rebuild.
        var detections = 0;
        var locator = CreateProxy<ILancacheServerLocator>((method, _) =>
        {
            if (method.Name != nameof(ILancacheServerLocator.DetectDnsServerIpAsync))
            {
                return DefaultReturn(method.ReturnType);
            }

            Interlocked.Increment(ref detections);
            return DetectAfterAsync(TimeSpan.FromSeconds(5.2), "10.0.0.53");
        });

        var service = CreateService(
            enabled: true, (_, _) => Task.FromResult<string?>(null), locator);

        var winner = service.GetLookupClientAsync(CancellationToken.None);
        var waiter = service.GetLookupClientAsync(CancellationToken.None);

        var winnerClient = await winner;
        var waiterClient = await waiter;

        Assert.NotNull(waiterClient.Client);
        Assert.Same(winnerClient.Client, waiterClient.Client);
        Assert.Equal(1, Volatile.Read(ref detections));
    }

    [Fact]
    public async Task HiddenAndExcludedClientsAreNeverReverseResolvedAsync()
    {
        // A client an admin hid or left out of stats appears on no screen, so a name for it is a
        // query that buys nothing and a slot taken from a machine that is on screen.
        var queried = new List<string>();
        var service = CreateService(enabled: true, address =>
        {
            lock (queried)
            {
                queried.Add(address.ToString());
            }

            return "machine.lan.";
        });

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"client-hostnames-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        context.Downloads.Add(new Download { Service = "steam", ClientIp = "10.0.0.9" });
        context.Downloads.Add(new Download { Service = "steam", ClientIp = "10.0.0.10" });
        context.Downloads.Add(new Download { Service = "steam", ClientIp = "10.0.0.11" });
        await context.SaveChangesAsync();

        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string> { "10.0.0.10" },
            nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string> { "10.0.0.11" },
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => DefaultReturn(method.ReturnType)
        });

        var controller = new ClientHostnamesController(
            context, service, CreateDefaultProxy<ISignalRNotificationService>(), stateService);

        var response = Assert.IsType<ClientHostnamesResponse>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetHostnamesAsync(CancellationToken.None)).Result).Value);

        Assert.Equal(new[] { "10.0.0.9" }, queried);
        Assert.Equal(new[] { "10.0.0.9" }, response.Hostnames.Keys);
        // A name was found for the one address left after filtering, so there is nothing to explain.
        Assert.Equal(ClientHostnamesReason.None, response.Reason);
    }

    [Fact]
    public async Task ConcurrentAskersForOneAddressShareASingleQueryAsync()
    {
        var queries = 0;
        var released = new TaskCompletionSource();
        var service = CreateService(enabled: true, async (_, _) =>
        {
            Interlocked.Increment(ref queries);
            await released.Task;
            return "gaming-pc.lan.";
        });

        var first = service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);
        var second = service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);
        released.SetResult();

        Assert.Equal("gaming-pc.lan", Assert.Single((await first).Hostnames).Value);
        Assert.Equal("gaming-pc.lan", Assert.Single((await second).Hostnames).Value);
        Assert.Equal(1, Volatile.Read(ref queries));
    }

    [Fact]
    public async Task AResolverFailureLeavesTheAddressUnnamedInsteadOfThrowingAsync()
    {
        // A transport failure reaches the caller as ConnectionTimeout and throws even when DNS
        // errors are configured to come back as a result instead.
        var service = CreateService(enabled: true, _ =>
            throw new DnsResponseException(DnsResponseCode.ConnectionTimeout));

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
    }

    [Fact]
    public void AResolverErrorIsNotTakenAsAnAnswerAboutTheAddress()
    {
        // Response codes come back as a response rather than an exception, so a refusal or a server
        // failure otherwise takes the same branch as an empty answer section and reads as "the
        // network has no name for this machine" — blanking the client for as long as a real answer
        // stands. Only the resolver answering, or stating the name does not exist, settles anything
        // about the address. Reaching the branch needs a resolver on the network, so it is pinned
        // to the source here instead.
        var source = ReadSource("Core", "Services", "Clients", "ClientHostnameService.cs");

        Assert.Contains(
            "var responseCode = response.Header.ResponseCode;", source, StringComparison.Ordinal);
        Assert.Contains(
            "responseCode != DnsHeaderResponseCode.NoError", source, StringComparison.Ordinal);
        Assert.Contains(
            "responseCode != DnsHeaderResponseCode.NotExistentDomain", source, StringComparison.Ordinal);
        Assert.Contains(
            "throw new DnsResponseException((DnsResponseCode)responseCode);", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedQueryIsRetriedAfterTheFailureWindowAsync()
    {
        // A resolver whose query ACLs do not cover this container refuses these lookups, which says
        // nothing about the address at all. It has to be asked again within minutes rather than
        // standing as a definitive blank for as long as a name does. The windows below stand in for
        // the five and thirty minutes the service uses, so the one actually chosen is observed
        // rather than inferred.
        var queries = 0;
        var service = CreateService(enabled: true, _ =>
        {
            queries++;
            throw new DnsResponseException(DnsResponseCode.Refused);
        });

        var cache = new ClientHostnameCache(
            service.LookupAsync,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMilliseconds(30),
            maxConcurrency: 1);

        await cache.GetAsync("10.0.0.9", CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await cache.GetAsync("10.0.0.9", CancellationToken.None);

        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task AnAddressTheNetworkSaysHasNoNameKeepsTheFullNameWindowAsync()
    {
        // The counterpart to the refusal above, and the case the whole feature is built around: a
        // resolver that states the name does not exist has settled the matter, so it must keep the
        // long window and not be re-asked on every refresh.
        var queries = 0;
        var service = CreateService(enabled: true, _ =>
        {
            queries++;
            return null;
        });

        var cache = new ClientHostnameCache(
            service.LookupAsync,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMilliseconds(30),
            maxConcurrency: 1);

        await cache.GetAsync("10.0.0.9", CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await cache.GetAsync("10.0.0.9", CancellationToken.None);

        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task AnUnexpectedFailureLeavesTheAddressUnnamedInsteadOfThrowingAsync()
    {
        var service = CreateService(enabled: true, _ => throw new InvalidOperationException("no resolver"));

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
    }

    [Fact]
    public async Task OnlyTheAddressesWithNamesAppearInTheResultAsync()
    {
        var service = CreateService(enabled: true, address =>
            address.ToString() == "10.0.0.1" ? "gaming-pc.lan." : null);

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
        Assert.False(outcome.Hostnames.ContainsKey("10.0.0.2"));
    }

    [Fact]
    public async Task EveryPrivateAddressAnsweredButNamelessReportsNoRecordsAsync()
    {
        // The user's actual case: a lancache DNS server was found and it answered every query, and
        // has no reverse record for any of the addresses. The old Dictionary-only shape could not
        // tell this apart from the lookup being off or never having asked anyone, so this is the
        // reproducing test for that gap - it cannot even be expressed before ResolveAsync returns a
        // reason.
        var locator = CreateDetectedResolverLocator("10.0.0.53");
        var service = CreateService(enabled: true, (_, _) => Task.FromResult<string?>(null), locator);

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        Assert.Equal(ClientHostnamesReason.NoRecords, outcome.Reason);
    }

    [Fact]
    public async Task NoLancacheDnsServerDetectedFallsBackAndReportsNoResolverAsync()
    {
        // Detection found nothing (the default locator stub reports no candidate), so the lookup
        // fell back to the container's own system resolver - not the network's DNS server, so its
        // silence is a different reason from one the network's own server gave.
        var service = CreateService(enabled: true, (_, _) => Task.FromResult<string?>(null));

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        Assert.Equal(ClientHostnamesReason.NoResolver, outcome.Reason);
    }

    [Fact]
    public async Task EveryQueryTimingOutReportsResolverTimeoutAsync()
    {
        // A lancache DNS server was found, but every query it was asked timed out - a different
        // reason from the same server actually answering with nothing.
        var locator = CreateDetectedResolverLocator("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (_, _) => throw new OperationCanceledException(),
            locator);

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        Assert.Equal(ClientHostnamesReason.ResolverTimeout, outcome.Reason);
    }

    [Fact]
    public async Task SomeAddressesNamedAndSomeNotReportsSomeUnnamedAsync()
    {
        // A hand-filled reverse zone names the machines someone got around to and nothing else, so
        // a batch where one address comes back named and another does not is the ordinary result.
        // Saying nothing because a name was found leaves the bare address sitting beside a named
        // one with no explanation at all.
        var locator = CreateDetectedResolverLocator("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>(
                address.ToString() == "10.0.0.1" ? "gaming-pc.lan." : null),
            locator);

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
        Assert.Equal(ClientHostnamesReason.SomeUnnamed, outcome.Reason);
    }

    [Fact]
    public async Task MorePrivateAddressesThanTheCapReportsTooManyClientsAsync()
    {
        // A network with more clients than one lookup will ask about. Every address that was asked
        // about came back named, so from inside the batch nothing looks wrong at all, while the
        // addresses past the cap still render as bare numbers with nothing to account for them.
        var locator = CreateDetectedResolverLocator("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>($"client-{address}.lan."),
            locator);

        var outcome = await service.ResolveAsync(
            PrivateAddresses(LookupCap + 44), CancellationToken.None);

        Assert.Equal(LookupCap, outcome.Hostnames.Count);
        Assert.Equal(ClientHostnamesReason.TooManyClients, outcome.Reason);
    }

    [Fact]
    public async Task AddressesDroppedByTheCapOutrankAddressesWithNoNameAsync()
    {
        // Both are true at once: the list was cut, and some of the addresses that were asked about
        // have no reverse record. The cut is the one worth saying, because adding reverse records
        // would not bring back the addresses that were never asked about.
        var locator = CreateDetectedResolverLocator("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>(
                address.GetAddressBytes()[3] % 2 == 0 ? $"client-{address}.lan." : null),
            locator);

        var outcome = await service.ResolveAsync(
            PrivateAddresses(LookupCap + 44), CancellationToken.None);

        Assert.NotEmpty(outcome.Hostnames);
        Assert.True(outcome.Hostnames.Count < LookupCap);
        Assert.Equal(ClientHostnamesReason.TooManyClients, outcome.Reason);
    }

    [Fact]
    public async Task ExactlyTheCapManyAddressesLeavesNothingToReportAsync()
    {
        // Sitting exactly on the cap, nothing was dropped and there is nothing to warn about, which
        // holds the boundary at more-than rather than at-least.
        var locator = CreateDetectedResolverLocator("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>($"client-{address}.lan."),
            locator);

        var outcome = await service.ResolveAsync(PrivateAddresses(LookupCap), CancellationToken.None);

        Assert.Equal(LookupCap, outcome.Hostnames.Count);
        Assert.NotEqual(ClientHostnamesReason.TooManyClients, outcome.Reason);
        Assert.Equal(ClientHostnamesReason.None, outcome.Reason);
    }

    [Fact]
    public void ClientHostnamesResponse_Reason_UsesCamelCaseAndRejectsIntegers()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        var json = JsonSerializer.Serialize(
            new ClientHostnamesResponse { Enabled = true, Reason = ClientHostnamesReason.NoRecords },
            options);

        Assert.Contains("\"reason\":\"noRecords\"", json, StringComparison.Ordinal);

        const string integerReason = "{\"enabled\":true,\"reason\":3}";
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<ClientHostnamesResponse>(integerReason, options));
    }

    [Fact]
    public async Task TheEndpointReportsTheLookupOffAndAsksNothingWhileItIsOffAsync()
    {
        var queries = 0;
        var service = CreateService(enabled: false, _ =>
        {
            queries++;
            return "gaming-pc.lan.";
        });

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"client-hostnames-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        context.Downloads.Add(new Download { Service = "steam", ClientIp = "10.0.0.9" });
        await context.SaveChangesAsync();

        var controller = new ClientHostnamesController(
            context,
            service,
            CreateDefaultProxy<ISignalRNotificationService>(),
            CreateDefaultProxy<IStateService>());

        var response = Assert.IsType<ClientHostnamesResponse>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetHostnamesAsync(CancellationToken.None)).Result).Value);

        Assert.False(response.Enabled);
        Assert.Empty(response.Hostnames);
        Assert.Equal(ClientHostnamesReason.None, response.Reason);
        Assert.Equal(0, queries);
    }

    [Fact]
    public void ReadingNamesIsOrdinaryUseAndFlippingTheToggleIsAdminOnly()
    {
        var controllerType = typeof(ClientHostnamesController);

        var classPolicies = controllerType
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToList();
        Assert.Equal(new string?[] { null }, classPolicies);

        var togglePolicies = controllerType
            .GetMethod(nameof(ClientHostnamesController.SetEnabledAsync))!
            .GetCustomAttributes<AuthorizeAttribute>()
            .Select(a => a.Policy)
            .ToList();
        Assert.Equal(new[] { "AccountHolder" }, togglePolicies);
    }

    [Fact]
    public void TheResolverIsBoundedToTwoSecondsWithoutRetries()
    {
        // The bound is what keeps a network that never answers from holding a page open, so it is
        // locked here rather than left to a reviewer's eye.
        var source = ReadSource("Core", "Services", "Clients", "ClientHostnameService.cs");

        Assert.Contains("_queryTimeout = TimeSpan.FromSeconds(2)", source, StringComparison.Ordinal);
        Assert.Contains("options.Timeout = _queryTimeout;", source, StringComparison.Ordinal);
        Assert.Contains("options.Retries = 0;", source, StringComparison.Ordinal);
        Assert.Contains("options.CacheFailedResults = true;", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallerThatGoesAwayGetsACancellationRatherThanAShortListOfNamesAsync()
    {
        // A caller that abandoned the request asked for nothing, so it must not receive a partial
        // map that reads like a complete answer.
        using var caller = new CancellationTokenSource();
        var service = CreateService(enabled: true, (_, _) =>
        {
            caller.Cancel();
            return Task.FromResult<string?>("gaming-pc.lan.");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ResolveAsync(new[] { "10.0.0.9" }, caller.Token));
    }

    [Fact]
    public async Task NamesThatOutranTheBatchBudgetAreAnnouncedOnceWhenTheyArriveAsync()
    {
        // The first fetch after the lookup is turned on is often empty: the cache was just cleared,
        // and finding the resolver is allowed longer than a whole batch is. The queries carry on and
        // warm the cache, so the only thing between the viewer and the names is being told they
        // arrived. One announcement covers the whole batch however many addresses were waiting,
        // because a client list of any size must not become a stream of events.
        var announced = new List<string>();
        var released = new TaskCompletionSource();
        var service = CreateService(
            enabled: true,
            async (address, _) =>
            {
                await released.Task;
                return $"{address.ToString().Replace('.', '-')}.lan.";
            },
            notifications: CreateRecordingNotifications(announced));

        var whileTheQueriesRan = await service.ResolveAsync(
            new[] { "10.0.0.9", "10.0.0.10" }, CancellationToken.None);

        Assert.Empty(whileTheQueriesRan.Hostnames);
        // The batch budget elapsed with both lookups still running, which is a different reason
        // from a resolver that answered and simply had nothing to say.
        Assert.Equal(ClientHostnamesReason.StillLooking, whileTheQueriesRan.Reason);
        released.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (announced)
            {
                if (announced.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        // Long enough that a second address announcing itself separately would have landed too.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        lock (announced)
        {
            Assert.Equal(new[] { SignalREvents.ClientHostnamesChanged }, announced);
        }

        var afterTheNamesArrived = await service.ResolveAsync(
            new[] { "10.0.0.9", "10.0.0.10" }, CancellationToken.None);

        Assert.Equal(
            new[] { "10-0-0-9.lan", "10-0-0-10.lan" }, afterTheNamesArrived.Hostnames.Values);
    }

    [Fact]
    public async Task ABatchThatOutranTheBudgetAndFoundNothingStillAnnouncesThatItFinishedAsync()
    {
        // The viewer has already been told the lookup is still running. Nothing else refreshes that
        // notice, so a batch that settles without a single name has to announce anyway - otherwise a
        // network with no reverse records sits on "still looking" and never reaches the explanation
        // that would tell the admin what to fix.
        var announced = new List<string>();
        var released = new TaskCompletionSource();
        var service = CreateService(
            enabled: true,
            async (_, _) =>
            {
                await released.Task;
                return null;
            },
            CreateDetectedResolverLocator("10.0.0.53"),
            CreateRecordingNotifications(announced));

        var whileTheQueriesRan = await service.ResolveAsync(
            new[] { "10.0.0.9", "10.0.0.10" }, CancellationToken.None);

        Assert.Equal(ClientHostnamesReason.StillLooking, whileTheQueriesRan.Reason);
        released.SetResult();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (announced)
            {
                if (announced.Count > 0)
                {
                    break;
                }
            }

            await Task.Delay(20);
        }

        lock (announced)
        {
            Assert.Equal(new[] { SignalREvents.ClientHostnamesChanged }, announced);
        }

        var afterTheBatchSettled = await service.ResolveAsync(
            new[] { "10.0.0.9", "10.0.0.10" }, CancellationToken.None);

        Assert.Empty(afterTheBatchSettled.Hostnames);
        Assert.Equal(ClientHostnamesReason.NoRecords, afterTheBatchSettled.Reason);
    }

    [Fact]
    public async Task OneAddressAnsweredWithNoNameOutweighsAnotherThatNeverAnsweredAsync()
    {
        // A server that answered even one query is reachable, so the missing reverse record is the
        // useful thing to say. Reporting a timeout instead would send an admin looking at the
        // network when the record is what is absent.
        var locator = CreateDetectedResolverLocator("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => address.ToString() == "10.0.0.1"
                ? Task.FromResult<string?>(null)
                : throw new OperationCanceledException(),
            locator);

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        Assert.Equal(ClientHostnamesReason.NoRecords, outcome.Reason);
    }

    [Fact]
    public async Task ANameFoundThroughTheSystemResolverIsNotReportedAsHavingNoResolverAsync()
    {
        // Detection found no lancache DNS server, but the container's own resolver did name an
        // address. A name coming back settles that there was a server to ask, so the batch must
        // never be explained as having found none.
        ClientHostnameService? service = null;
        string? resolverTheNameCameFrom = null;
        service = CreateService(
            enabled: true,
            async (address, token) =>
            {
                // A real query asks for the resolver first and then talks to it, so the stand-in
                // does the same and keeps whichever one it was handed. Reading it here is what
                // proves the name arrived through the system fallback rather than a detected
                // lancache DNS server.
                var (_, resolver) = await service!.GetLookupClientAsync(token);
                if (address.ToString() != "10.0.0.1")
                {
                    return null;
                }

                resolverTheNameCameFrom = resolver;
                return "gaming-pc.lan.";
            });

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
        Assert.Equal("system", resolverTheNameCameFrom);
        Assert.NotEqual(ClientHostnamesReason.NoResolver, outcome.Reason);
    }

    [Fact]
    public async Task ADetectionThatFailedOnceStillReportsTheServerTheQueriesReachedAsync()
    {
        // The first attempt to find the DNS server threw, and the attempt each query makes for
        // itself found the server. One unlucky first attempt must not stop the batch reaching the
        // real server, nor leave the names it found needing an explanation.
        var detections = 0;
        var locator = CreateProxy<ILancacheServerLocator>((method, _) =>
        {
            if (method.Name != nameof(ILancacheServerLocator.DetectDnsServerIpAsync))
            {
                return DefaultReturn(method.ReturnType);
            }

            return Interlocked.Increment(ref detections) == 1
                ? Task.FromException<string?>(
                    new InvalidOperationException("the container's resolver configuration could not be read"))
                : Task.FromResult<string?>("10.0.0.53");
        });

        ClientHostnameService? service = null;
        service = CreateService(
            enabled: true,
            async (address, token) =>
            {
                await service!.GetLookupClientAsync(token);
                return address.ToString() == "10.0.0.1" ? "gaming-pc.lan." : null;
            },
            locator);

        var outcome = await service.ResolveAsync(new[] { "10.0.0.1" }, CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
        Assert.Equal(ClientHostnamesReason.None, outcome.Reason);
    }

    [Fact]
    public void FindingTheResolverDoesNotSpendTheQueryBudget()
    {
        // Detection inspects Docker and probes several candidate addresses, so it runs far slower
        // than the query it prepares for. Charging it to the two-second query timeout would time
        // every lookup out on a cold start and cache the whole client list as unnamed for the full
        // name TTL, which is the one failure this feature can never recover from on its own.
        var source = ReadSource("Core", "Services", "Clients", "ClientHostnameService.cs");

        Assert.Contains(
            "_resolverDetectionTimeout = TimeSpan.FromSeconds(5)", source, StringComparison.Ordinal);
        Assert.Contains("detection.CancelAfter(_resolverDetectionTimeout);", source, StringComparison.Ordinal);

        var resolverReady = source.IndexOf(
            "await GetLookupClientAsync(cancellationToken);", StringComparison.Ordinal);
        var queryClockStarts = source.IndexOf(
            "queryTimeout.CancelAfter(_queryTimeout);", StringComparison.Ordinal);

        Assert.True(resolverReady > 0, "the resolver is obtained before the query runs");
        Assert.True(queryClockStarts > resolverReady, "the query clock starts only once the resolver is ready");
    }

    /// <summary>
    /// How many addresses one lookup asks about, mirroring the service's own cap so the tests can
    /// sit on either side of it.
    /// </summary>
    private const int LookupCap = 256;

    /// <summary>As many distinct private addresses as asked for.</summary>
    private static string[] PrivateAddresses(int count)
        => Enumerable.Range(0, count)
            .Select(index => $"10.1.{index / 256}.{index % 256}")
            .ToArray();

    private static ClientHostnameService CreateService(bool enabled, Func<IPAddress, string?> reverseLookup)
        => CreateService(enabled, (address, _) => Task.FromResult(reverseLookup(address)));

    /// <summary>Records the event names broadcast to every viewer, so a test can tell one
    /// announcement for a whole batch from one per address.</summary>
    private static ISignalRNotificationService CreateRecordingNotifications(List<string> announced)
        => CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name != nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                return DefaultReturn(method.ReturnType);
            }

            lock (announced)
            {
                announced.Add((string)args![0]!);
            }

            return Task.CompletedTask;
        });

    /// <summary>Answers with a resolver address only after the given hold, standing in for the
    /// Docker inspection and candidate probing that finding the LAN's DNS server really does.</summary>
    private static async Task<string?> DetectAfterAsync(TimeSpan hold, string resolverIp)
    {
        await Task.Delay(hold);
        return resolverIp;
    }

    /// <summary>Locator stub that reports the given address as the detected lancache DNS server, so
    /// a test can tell "that server answered with nothing" apart from "no server was found".</summary>
    private static ILancacheServerLocator CreateDetectedResolverLocator(string resolverIp)
        => CreateProxy<ILancacheServerLocator>((method, _) => method.Name switch
        {
            nameof(ILancacheServerLocator.DetectDnsServerIpAsync) => Task.FromResult<string?>(resolverIp),
            _ => DefaultReturn(method.ReturnType)
        });

    private static ClientHostnameService CreateService(
        bool enabled,
        Func<IPAddress, CancellationToken, Task<string?>> reverseLookup,
        ILancacheServerLocator? serverLocator = null,
        ISignalRNotificationService? notifications = null)
    {
        var toggle = new LookupToggle { Enabled = enabled };
        var stateService = CreateProxy<IStateService>((method, args) => method.Name switch
        {
            nameof(IStateService.GetClientHostnameLookup) => toggle.Enabled,
            nameof(IStateService.SetClientHostnameLookup) => toggle.Set((bool)args![0]!),
            nameof(IStateService.GetStatusCheckResolverMode) => "auto",
            _ => DefaultReturn(method.ReturnType)
        });

        return new ClientHostnameService(
            NullLogger<ClientHostnameService>.Instance,
            stateService,
            serverLocator ?? CreateDefaultProxy<ILancacheServerLocator>(),
            notifications ?? CreateDefaultProxy<ISignalRNotificationService>(),
            reverseLookup);
    }

    /// <summary>Stands in for the persisted global toggle so a test can read what a set wrote.</summary>
    private sealed class LookupToggle
    {
        public bool Enabled { get; set; }

        public object? Set(bool enabled)
        {
            Enabled = enabled;
            return null;
        }
    }

    private static T CreateDefaultProxy<T>() where T : class
        => CreateProxy<T>((method, _) => DefaultReturn(method.ReturnType));

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [DefaultValue(resultType)]);
        }

        return DefaultValue(returnType);
    }

    private static object? DefaultValue(Type type)
        => type.IsValueType ? Activator.CreateInstance(type) : null;

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, "Api", "LancacheManager", .. pathSegments]));
    }

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod == null ? null : Handler?.Invoke(targetMethod, args);
    }
}
