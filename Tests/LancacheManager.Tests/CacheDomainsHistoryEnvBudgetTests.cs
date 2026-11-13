using System.Net;
using LancacheManager.Core.Services.StatusCheck;

namespace LancacheManager.Tests;

/// <summary>
/// The added-to-the-list date lookup reads the LANCache environment (NOFETCH, the list's repository and
/// branch) before it asks GitHub. When Docker stops answering those reads, the lookup still ends within
/// its own short budget, so the Status Check result is not held behind it.
/// </summary>
public sealed class CacheDomainsHistoryEnvBudgetTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "cache-domains-env-budget-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnansweredEnvironmentRead_EndsTheLookupWithinItsBudget()
    {
        // NOFETCH is the lookup's first read, so an unanswered NOFETCH holds it as every read unanswered would.
        var environment = new CacheDomainsTestEnvironment { SlowKey = "NOFETCH" };
        var service = environment.NewService(_rootDirectory, new NoGitHub(), new CapturingLogger<CacheDomainsService>());

        using var stop = new CancellationTokenSource();
        var lookup = service.GetAddedDatesAsync(["blur-updates.lanparty.fyi"], ["lancache.steamcontent.com"], stop.Token);
        try
        {
            var ended = await Task.WhenAny(lookup, Task.Delay(Bound)) == lookup;
            Assert.True(ended,
                $"The added-to-the-list lookup was still waiting on an environment read {Bound.TotalSeconds} s after it started");
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAny(lookup, Task.Delay(Bound));
        }
    }

    /// <summary>GitHub answering nothing useful; the lookup should never get this far.</summary>
    private sealed class NoGitHub : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
