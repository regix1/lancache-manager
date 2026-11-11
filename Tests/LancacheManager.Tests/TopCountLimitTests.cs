using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The Xbox daemon refuses a Top count above 100. A prefill request asking for more, from the panel or a
/// scheduled run, is refused before any command reaches the daemon, with a key the browser translates.
/// </summary>
public sealed class TopCountLimitTests
{
    [Fact]
    public async Task XboxPrefillAboveItsTopLimitIsRefusedBeforeTheDaemonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var xbox = new XboxPrefillDaemonService(NullLogger<XboxPrefillDaemonService>.Instance,
            (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new ConfigurationBuilder().Build(), null!,
            (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>(),
            fixture.History,
            new PrefillCacheService(new TestDbContextFactory(fixture.Options), NullLogger<PrefillCacheService>.Instance),
            null!, null!, new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory());
        var sessions = (ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(xbox)!;
        sessions[fixture.Session.Id] = fixture.Session;

        var refused = await Assert.ThrowsAsync<ValidationException>(
            () => xbox.PrefillAsync(fixture.Session.Id, top: 150));

        Assert.Equal("errors.prefill.topCountAboveLimit", refused.StageKey);
        Assert.Equal(100, refused.Context!["max"]);
        Assert.Equal(0, fixture.Client.StartCount);
        Assert.Empty(xbox.GetRuns(fixture.Session.Id));
    }
}
