using System.Net;
using System.Reflection;
using System.Text.Json;
using DnsClient;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.StatusCheck;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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

        var controller = SignedInAs(SessionType.Admin, new ClientHostnamesController(
            context, service, CreateDefaultProxy<ISignalRNotificationService>(), stateService));

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
    public async Task AnUnexpectedFailureReachesTheGlobalErrorHandlerAsync()
    {
        var service = CreateService(enabled: true, _ => throw new InvalidOperationException("no resolver"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None));
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
        var nameservers = ConfiguredNameservers("10.0.0.53");
        var service = CreateService(enabled: true, (_, _) => Task.FromResult<string?>(null), nameservers);

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        Assert.Equal(ClientHostnamesReason.NoRecords, outcome.Reason);
    }

    [Fact]
    public async Task AHostWithNoConfiguredResolverStillAsksTheClientsOwnSubnetRoutersAsync()
    {
        // The case this covers is a container that can see nothing but Docker's own resolver. The
        // routers of the subnet the clients are sitting in are the last thing left to ask, and they
        // are where a home network's DHCP names actually live.
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>("gaming-pc.lan."),
            nameservers: () => Array.Empty<string>());

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);
        var resolvers = await service.GetLookupClientsAsync("10.0.0.9", CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.254" }, resolvers.Select(resolver => resolver.Address));
    }

    [Fact]
    public async Task AForwardLookupWithNothingToAskReportsNoResolverAsync()
    {
        // A name typed into the nickname editor belongs to no subnet, so there are no routers to
        // fall back on and an empty chain is still a real outcome on this path.
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => Array.Empty<string>());

        var outcome = await service.ResolveAddressesAsync("gaming-pc.lan", CancellationToken.None);

        Assert.Empty(outcome.Addresses);
        Assert.Equal(ClientAddressLookupReason.NoResolver, outcome.Reason);
    }

    [Fact]
    public async Task EveryQueryTimingOutReportsResolverTimeoutAsync()
    {
        // A lancache DNS server was found, but every query it was asked timed out - a different
        // reason from the same server actually answering with nothing.
        var nameservers = ConfiguredNameservers("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (_, _) => throw new OperationCanceledException(),
            nameservers);

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
        var nameservers = ConfiguredNameservers("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>(
                address.ToString() == "10.0.0.1" ? "gaming-pc.lan." : null),
            nameservers);

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
        var nameservers = ConfiguredNameservers("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>($"client-{address}.lan."),
            nameservers);

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
        var nameservers = ConfiguredNameservers("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>(
                address.GetAddressBytes()[3] % 2 == 0 ? $"client-{address}.lan." : null),
            nameservers);

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
        var nameservers = ConfiguredNameservers("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => Task.FromResult<string?>($"client-{address}.lan."),
            nameservers);

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

        var controller = SignedInAs(SessionType.Admin, new ClientHostnamesController(
            context,
            service,
            CreateDefaultProxy<ISignalRNotificationService>(),
            CreateDefaultProxy<IStateService>()));

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
            ConfiguredNameservers("10.0.0.53"),
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
        var nameservers = ConfiguredNameservers("10.0.0.53");
        var service = CreateService(
            enabled: true,
            (address, _) => address.ToString() == "10.0.0.1"
                ? Task.FromResult<string?>(null)
                : throw new OperationCanceledException(),
            nameservers);

        var outcome = await service.ResolveAsync(
            new[] { "10.0.0.1", "10.0.0.2" }, CancellationToken.None);

        Assert.Empty(outcome.Hostnames);
        Assert.Equal(ClientHostnamesReason.NoRecords, outcome.Reason);
    }

    [Fact]
    public void CollectHostnameResolverIps_KeepsEveryPrivateHostResolverThenDocker()
    {
        var selected = Collect(
            nameservers: new[] { "172.16.2.98", "10.0.0.53", "172.16.1.1" },
            dockerResolvers: new[] { "172.18.0.5" });

        Assert.Equal(new[] { "172.16.2.98", "10.0.0.53", "172.16.1.1", "172.18.0.5" }, selected);
    }

    [Fact]
    public void CollectHostnameResolverIps_SkipsPublicAndLinkLocalAndKeepsLaterPrivate()
    {
        var selected = Collect(
            nameservers: new[] { "1.1.1.1", "169.254.169.254", "10.0.0.53" },
            dockerResolvers: new[] { "8.8.8.8", "172.16.1.222" });

        Assert.Equal(new[] { "10.0.0.53", "172.16.1.222" }, selected);
    }

    [Fact]
    public void CollectHostnameResolverIps_AcceptsLoopbackAndDedupesAndCaps()
    {
        var host = Enumerable.Range(1, 6).Select(index => $"10.0.0.{index}").ToArray();
        host[0] = "127.0.0.11";
        var docker = new[] { "10.0.0.2", "10.8.0.1", "10.8.0.2", "10.8.0.3" };

        var selected = Collect(nameservers: host, dockerResolvers: docker);

        Assert.Equal(ClientHostnameService.MaxHostnameResolvers, selected.Count);
        Assert.Equal("127.0.0.11", selected[0]);
        Assert.Equal(1, selected.Count(ip => ip == "10.0.0.2"));
        Assert.Equal(new[] { "10.8.0.1", "10.8.0.2" }, selected.Skip(6));
    }

    [Fact]
    public void CollectHostnameResolverIps_AsksTheAddressAnAdminNamedFirst()
    {
        var selected = Collect(
            adminResolverIp: "172.16.1.222",
            nameservers: new[] { "10.0.0.53" },
            dockerResolvers: new[] { "172.18.0.5" });

        Assert.Equal(new[] { "172.16.1.222", "10.0.0.53", "172.18.0.5" }, selected);
    }

    [Fact]
    public void CollectHostnameResolverIps_OrdersDiscoveredSourcesAheadOfDerivedRouters()
    {
        var selected = Collect(
            nameservers: new[] { "127.0.0.11" },
            gateways: new[] { "172.17.0.1" },
            hostDockerInternalIps: new[] { "192.168.65.2" },
            dockerResolvers: new[] { "172.18.0.5" },
            subnetRouters: new[] { "172.16.1.1", "172.16.1.254" });

        Assert.Equal(
            new[] { "127.0.0.11", "172.17.0.1", "192.168.65.2", "172.18.0.5", "172.16.1.1", "172.16.1.254" },
            selected);
    }

    [Fact]
    public void SubnetRouters_DerivesBothConventionsFromEverySubnetSeen()
    {
        Assert.Equal(
            new[] { "172.16.1.1", "172.16.1.254", "10.8.0.1", "10.8.0.254" },
            ClientHostnameService.SubnetRouters(new[] { "172.16.1", "10.8.0" }));
    }

    [Theory]
    [InlineData("172.16.1.146", "172.16.1")]
    [InlineData("10.8.0.9", "10.8.0")]
    [InlineData("127.0.0.11", null)]
    [InlineData("1.1.1.1", null)]
    [InlineData("not-an-ip", null)]
    [InlineData(null, null)]
    public void SubnetPrefix_OnlyDerivesFromPrivateIPv4(string? ip, string? expected)
    {
        Assert.Equal(expected, ClientHostnameService.SubnetPrefix(ip));
    }

    /// <summary>Names the chain's sources so a test only spells out the ones it is about.</summary>
    private static List<string> Collect(
        string? adminResolverIp = null,
        IReadOnlyList<string>? nameservers = null,
        IReadOnlyList<string>? gateways = null,
        IReadOnlyList<string>? hostDockerInternalIps = null,
        IReadOnlyList<string>? dockerResolvers = null,
        IReadOnlyList<string>? subnetRouters = null)
        => ClientHostnameService.CollectHostnameResolverIps(
            adminResolverIp, nameservers, gateways, hostDockerInternalIps, dockerResolvers, subnetRouters);

    [Fact]
    public void SanitizeReverseName_DropsDockerDesktopPlaceholdersAndInvalidNames()
    {
        Assert.Equal("adguard.lan", ClientHostnameService.SanitizeReverseName("adguard.lan."));
        Assert.Null(ClientHostnameService.SanitizeReverseName("host.docker.internal."));
        Assert.Null(ClientHostnameService.SanitizeReverseName("gateway.docker.internal"));
        Assert.Null(ClientHostnameService.SanitizeReverseName("   "));
        Assert.Null(ClientHostnameService.SanitizeReverseName("bad\nname.lan"));
        Assert.Null(ClientHostnameService.SanitizeReverseName(new string('a', 254)));
    }

    [Fact]
    public async Task GetLookupClientAsync_CachesTheConfiguredResolverChainAsync()
    {
        var reads = 0;
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () =>
            {
                reads++;
                return new[] { "172.16.2.98", "10.0.0.53" };
            });

        var first = await service.GetLookupClientsAsync(null, CancellationToken.None);
        var second = await service.GetLookupClientsAsync(null, CancellationToken.None);

        Assert.Equal(new[] { "172.16.2.98", "10.0.0.53" }, first.Select(resolver => resolver.Address));
        Assert.Same(first[0].Client, second[0].Client);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task GetLookupClientsAsync_IncludesDockerResolversFromTheLocatorAsync()
    {
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "172.16.2.98" },
            locator: new TestLancacheServerLocator(new[] { "172.16.1.222", "1.1.1.1" }));

        var resolvers = await service.GetLookupClientsAsync(null, CancellationToken.None);

        Assert.Equal(new[] { "172.16.2.98", "172.16.1.222" }, resolvers.Select(resolver => resolver.Address));
    }

    [Theory]
    [InlineData(SessionType.Guest, false, false)]
    [InlineData(SessionType.Guest, true, true)]
    [InlineData(SessionType.Admin, false, true)]
    [InlineData(SessionType.User, false, true)]
    public async Task TheEndpointNamesClientsForAGuestOnlyWhenAnAdminHasAllowedItAsync(
        SessionType sessionType,
        bool guestAccess,
        bool expectsNames)
    {
        var service = CreateService(enabled: true, _ => "gaming-pc.lan.");
        Assert.True(service.SetSettings(new ClientHostnameSettings { GuestAccess = guestAccess }));

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"client-hostnames-guest-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        context.Downloads.Add(new Download { Service = "steam", ClientIp = "10.0.0.9" });
        await context.SaveChangesAsync();

        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => DefaultReturn(method.ReturnType)
        });

        var controller = SignedInAs(sessionType, new ClientHostnamesController(
            context, service, CreateDefaultProxy<ISignalRNotificationService>(), stateService));

        var response = Assert.IsType<ClientHostnamesResponse>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetHostnamesAsync(CancellationToken.None)).Result).Value);

        Assert.Equal(expectsNames, response.Enabled);
        Assert.Equal(expectsNames, response.Hostnames.Count > 0);
    }

    [Fact]
    public async Task AGuestIsNeverToldWhichDnsServerTheAppWasConfiguredToAskAsync()
    {
        var service = CreateService(enabled: true, _ => "gaming-pc.lan.");
        Assert.True(service.SetSettings(new ClientHostnameSettings
        {
            Resolver = "172.16.1.222",
            GuestAccess = true
        }));

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"client-hostnames-settings-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);

        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => DefaultReturn(method.ReturnType)
        });

        ClientHostnamesResponse Read(SessionType sessionType)
        {
            var controller = SignedInAs(sessionType, new ClientHostnamesController(
                context, service, CreateDefaultProxy<ISignalRNotificationService>(), stateService));
            return Assert.IsType<ClientHostnamesResponse>(
                Assert.IsType<OkObjectResult>(
                    controller.GetHostnamesAsync(CancellationToken.None).Result.Result).Value);
        }

        // Names are one thing; which server on the network holds them is the admin's own
        // configuration and travels no further than an account holder.
        Assert.Null(Read(SessionType.Guest).Settings.Resolver);
        Assert.Equal("172.16.1.222", Read(SessionType.Admin).Settings.Resolver);
    }

    [Fact]
    public void GuestsAreNotShownClientNamesUntilAnAdminSaysSo()
    {
        var service = CreateService(enabled: true, (_, _) => Task.FromResult<string?>(null));

        // The default has to be the closed one: a guest is given a view of the cache, not a list
        // of the machines on the network.
        Assert.False(service.IsVisibleToGuests());
        Assert.False(service.GetSettings().GuestAccess);

        Assert.True(service.SetSettings(new ClientHostnameSettings { GuestAccess = true }));
        Assert.True(service.IsVisibleToGuests());
    }

    [Fact]
    public async Task SwitchingOffTheRouterLookupLeavesOnlyConfiguredServersAsync()
    {
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "127.0.0.11" });

        Assert.True(service.SetSettings(new ClientHostnameSettings { RouterLookup = false }));

        var resolvers = await service.GetLookupClientsAsync("172.16.1.146", CancellationToken.None);

        // The derived addresses are the only ones the app works out for itself, so switching them
        // off has to leave nothing behind that was not read from the host's own configuration.
        Assert.Equal(new[] { "127.0.0.11" }, resolvers.Select(resolver => resolver.Address));
    }

    [Fact]
    public async Task SwitchingOffTheDockerLookupAsksDockerNothingAsync()
    {
        var asked = 0;
        var locator = new TestLancacheServerLocator(
            new[] { "172.16.1.222" }, onDetect: () => asked++);
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "127.0.0.11" },
            locator: locator);

        Assert.True(service.SetSettings(new ClientHostnameSettings { DockerLookup = false, RouterLookup = false }));

        var resolvers = await service.GetLookupClientsAsync(null, CancellationToken.None);

        Assert.Equal(new[] { "127.0.0.11" }, resolvers.Select(resolver => resolver.Address));
        // Off means the socket is never touched, not that its answer is discarded afterwards.
        Assert.Equal(0, asked);
    }

    [Fact]
    public void ARefusedResolverLeavesEverySettingAsItWas()
    {
        var service = CreateService(enabled: true, (_, _) => Task.FromResult<string?>(null));

        Assert.True(service.SetSettings(new ClientHostnameSettings
        {
            Resolver = "172.16.1.222",
            GuestAccess = true,
            RouterLookup = false,
            DockerLookup = false
        }));

        Assert.False(service.SetSettings(new ClientHostnameSettings { Resolver = "8.8.8.8" }));

        // A refused address must not write the switches that travelled with it, or one mistyped
        // address quietly turns discovery back on and re-opens names to guests.
        var settings = service.GetSettings();
        Assert.Equal("172.16.1.222", settings.Resolver);
        Assert.True(settings.GuestAccess);
        Assert.False(settings.RouterLookup);
        Assert.False(settings.DockerLookup);
    }

    [Fact]
    public async Task AnAddressAnAdminNamedIsAskedAheadOfTheHostsOwnResolverAsync()
    {
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "127.0.0.11" });

        Assert.True(service.SetSettings(new ClientHostnameSettings { Resolver = "172.16.1.222" }));

        var resolvers = await service.GetLookupClientsAsync(null, CancellationToken.None);

        Assert.Equal(new[] { "172.16.1.222", "127.0.0.11" }, resolvers.Select(resolver => resolver.Address));
        Assert.Equal("172.16.1.222", service.GetSettings().Resolver);
    }

    [Fact]
    public async Task AnAddressOutsideThePrivateRangesIsRefusedAndChangesNothingAsync()
    {
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "127.0.0.11" });

        Assert.True(service.SetSettings(new ClientHostnameSettings { Resolver = "172.16.1.222" }));
        Assert.False(service.SetSettings(new ClientHostnameSettings { Resolver = "8.8.8.8" }));
        Assert.False(service.SetSettings(new ClientHostnameSettings { Resolver = "gateway.lan" }));

        // A refused address must not quietly unset the one that was working, or an admin who
        // mistypes loses the setting that was naming their clients.
        Assert.Equal("172.16.1.222", service.GetSettings().Resolver);
        var resolvers = await service.GetLookupClientsAsync(null, CancellationToken.None);
        Assert.Equal("172.16.1.222", resolvers[0].Address);
    }

    [Fact]
    public async Task ClearingTheNamedAddressHandsTheChoiceBackToDiscoveryAsync()
    {
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "127.0.0.11" });

        Assert.True(service.SetSettings(new ClientHostnameSettings { Resolver = "172.16.1.222" }));
        Assert.True(service.SetSettings(new ClientHostnameSettings { Resolver = string.Empty }));

        Assert.Null(service.GetSettings().Resolver);
        var resolvers = await service.GetLookupClientsAsync(null, CancellationToken.None);
        Assert.Equal(new[] { "127.0.0.11" }, resolvers.Select(resolver => resolver.Address));
    }

    [Fact]
    public async Task TheRoutersOfAClientsOwnSubnetAreAskedAfterEverythingDiscoveredAsync()
    {
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "127.0.0.11" });

        var resolvers = await service.GetLookupClientsAsync("172.16.1.146", CancellationToken.None);

        Assert.Equal(
            new[] { "127.0.0.11", "172.16.1.1", "172.16.1.254" },
            resolvers.Select(resolver => resolver.Address));
    }

    [Fact]
    public async Task AClientOnANewSubnetRebuildsTheChainWithoutLosingTheOldSubnetAsync()
    {
        var service = CreateService(
            enabled: true,
            (_, _) => Task.FromResult<string?>(null),
            nameservers: () => new[] { "127.0.0.11" });

        await service.GetLookupClientsAsync("172.16.1.146", CancellationToken.None);
        var resolvers = await service.GetLookupClientsAsync("10.8.0.9", CancellationToken.None);

        // Both subnets' routers stand: a client list that arrives one address at a time would
        // otherwise drop the routers found for the address before it.
        Assert.Equal(
            new[] { "127.0.0.11", "172.16.1.1", "172.16.1.254", "10.8.0.1", "10.8.0.254" },
            resolvers.Select(resolver => resolver.Address));
    }

    [Fact]
    public async Task NxDomainOnTheFirstResolverContinuesToTheNextAsync()
    {
        var asked = new List<string>();
        var service = CreateQueryService(
            enabled: true,
            nameservers: () => new[] { "10.0.0.53", "172.16.1.222" },
            queryOnResolver: (resolverIp, _, _) =>
            {
                asked.Add(resolverIp);
                return Task.FromResult<string?>(resolverIp == "172.16.1.222" ? "gaming-pc.lan." : null);
            });

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
        Assert.Equal(new[] { "10.0.0.53", "172.16.1.222" }, asked);
        Assert.Equal(ClientHostnamesReason.None, outcome.Reason);
    }

    [Fact]
    public async Task AResolverThatReturnsANameIsAskedFirstAfterwardsAsync()
    {
        var asked = new List<string>();
        var service = CreateQueryService(
            enabled: true,
            nameservers: () => new[] { "10.0.0.53", "172.16.1.222" },
            queryOnResolver: (resolverIp, _, _) =>
            {
                asked.Add(resolverIp);
                return Task.FromResult<string?>(resolverIp == "172.16.1.222" ? "gaming-pc.lan." : null);
            });

        await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);
        asked.Clear();
        await service.ResolveAsync(new[] { "10.0.0.10" }, CancellationToken.None);

        Assert.Equal(new[] { "172.16.1.222" }, asked);
    }

    [Fact]
    public async Task AnInvalidNameOnTheFirstResolverContinuesToTheNextAsync()
    {
        var service = CreateQueryService(
            enabled: true,
            nameservers: () => new[] { "10.0.0.53", "172.16.1.222" },
            queryOnResolver: (resolverIp, _, _) => Task.FromResult<string?>(
                resolverIp == "10.0.0.53" ? "host.docker.internal." : "gaming-pc.lan."));

        var outcome = await service.ResolveAsync(new[] { "10.0.0.9" }, CancellationToken.None);

        Assert.Equal("gaming-pc.lan", Assert.Single(outcome.Hostnames).Value);
    }

    [Fact]
    public async Task LiveResolverFromEnvironmentIsAskedAsync()
    {
        var configured = Environment.GetEnvironmentVariable("LANCACHE_HOSTNAME_LIVE_DNS");
        if (string.IsNullOrWhiteSpace(configured) ||
            !IPAddress.TryParse(configured, out var liveAddress) ||
            !LancacheServerLocator.IsProbeableCandidateIp(configured))
        {
            return;
        }

        var service = CreateLiveService(ConfiguredNameservers(configured));
        var outcome = await service.ResolveAsync(new[] { configured }, CancellationToken.None);

        Assert.True(
            outcome.Hostnames.TryGetValue(configured, out var name),
            $"the live resolver {configured} answered {outcome.Reason} with no name");
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.DoesNotContain("docker.internal", name, StringComparison.OrdinalIgnoreCase);
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

    private static ClientHostnameService CreateLiveService(Func<IReadOnlyList<string>> nameservers)
    {
        var toggle = new LookupToggle { Enabled = true };
        var stateService = CreateProxy<IStateService>((method, args) => method.Name switch
        {
            nameof(IStateService.GetClientHostnameLookup) => toggle.Enabled,
            nameof(IStateService.SetClientHostnameLookup) => toggle.Set((bool)args![0]!),
            _ => DefaultReturn(method.ReturnType)
        });

        return new ClientHostnameService(
            NullLogger<ClientHostnameService>.Instance,
            stateService,
            CreateDefaultProxy<ISignalRNotificationService>(),
            reverseLookup: null,
            nameservers,
            NoGateways);
    }

    /// <summary>
    /// Stands in for the host's routing table. The real one differs from machine to machine, so a
    /// test that did not supply this would assert against whatever gateway the build agent happens
    /// to route through.
    /// </summary>
    private static readonly Func<IReadOnlyList<string>> NoGateways = Array.Empty<string>;

    private static ClientHostnameService CreateService(bool enabled, Func<IPAddress, string?> reverseLookup)
        => CreateService(enabled, (address, _) => Task.FromResult(reverseLookup(address)));

    /// <summary>
    /// Attaches the session the middleware would have attached, so the controller sees the caller
    /// it would see behind the real pipeline.
    /// </summary>
    private static ClientHostnamesController SignedInAs(
        SessionType sessionType,
        ClientHostnamesController controller)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = new UserSession { SessionType = sessionType };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

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

    private static Func<IReadOnlyList<string>> ConfiguredNameservers(string resolverIp)
        => () => new[] { resolverIp };

    private static ClientHostnameService CreateService(
        bool enabled,
        Func<IPAddress, CancellationToken, Task<string?>> reverseLookup,
        Func<IReadOnlyList<string>>? nameservers = null,
        ISignalRNotificationService? notifications = null,
        ILancacheServerLocator? locator = null)
    {
        var toggle = new LookupToggle { Enabled = enabled };
        string? namedResolver = null;
        var guestAccess = false;
        var routerLookup = true;
        var dockerLookup = true;
        var stateService = CreateProxy<IStateService>((method, args) => method.Name switch
        {
            nameof(IStateService.GetClientHostnameLookup) => toggle.Enabled,
            nameof(IStateService.SetClientHostnameLookup) => toggle.Set((bool)args![0]!),
            nameof(IStateService.GetClientHostnameResolver) => namedResolver,
            nameof(IStateService.SetClientHostnameResolver) => namedResolver = (string?)args![0],
            nameof(IStateService.GetClientHostnameGuestAccess) => guestAccess,
            nameof(IStateService.SetClientHostnameGuestAccess) => guestAccess = (bool)args![0]!,
            nameof(IStateService.GetClientHostnameRouterLookup) => routerLookup,
            nameof(IStateService.SetClientHostnameRouterLookup) => routerLookup = (bool)args![0]!,
            nameof(IStateService.GetClientHostnameDockerLookup) => dockerLookup,
            nameof(IStateService.SetClientHostnameDockerLookup) => dockerLookup = (bool)args![0]!,
            _ => DefaultReturn(method.ReturnType)
        });

        return new ClientHostnameService(
            NullLogger<ClientHostnameService>.Instance,
            stateService,
            notifications ?? CreateDefaultProxy<ISignalRNotificationService>(),
            reverseLookup,
            nameservers ?? ConfiguredNameservers("10.0.0.53"),
            NoGateways,
            locator);
    }

    private static ClientHostnameService CreateQueryService(
        bool enabled,
        Func<IReadOnlyList<string>> nameservers,
        Func<string, IPAddress, CancellationToken, Task<string?>> queryOnResolver)
    {
        var toggle = new LookupToggle { Enabled = enabled };
        string? namedResolver = null;
        var guestAccess = false;
        var routerLookup = true;
        var dockerLookup = true;
        var stateService = CreateProxy<IStateService>((method, args) => method.Name switch
        {
            nameof(IStateService.GetClientHostnameLookup) => toggle.Enabled,
            nameof(IStateService.SetClientHostnameLookup) => toggle.Set((bool)args![0]!),
            nameof(IStateService.GetClientHostnameResolver) => namedResolver,
            nameof(IStateService.SetClientHostnameResolver) => namedResolver = (string?)args![0],
            nameof(IStateService.GetClientHostnameGuestAccess) => guestAccess,
            nameof(IStateService.SetClientHostnameGuestAccess) => guestAccess = (bool)args![0]!,
            nameof(IStateService.GetClientHostnameRouterLookup) => routerLookup,
            nameof(IStateService.SetClientHostnameRouterLookup) => routerLookup = (bool)args![0]!,
            nameof(IStateService.GetClientHostnameDockerLookup) => dockerLookup,
            nameof(IStateService.SetClientHostnameDockerLookup) => dockerLookup = (bool)args![0]!,
            _ => DefaultReturn(method.ReturnType)
        });

        return new ClientHostnameService(
            NullLogger<ClientHostnameService>.Instance,
            stateService,
            CreateDefaultProxy<ISignalRNotificationService>(),
            reverseLookup: null,
            nameservers,
            NoGateways,
            locator: null,
            queryOnResolver: queryOnResolver);
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

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod == null ? null : Handler?.Invoke(targetMethod, args);
    }
}
