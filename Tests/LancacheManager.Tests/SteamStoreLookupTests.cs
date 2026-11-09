using System.Net;
using LancacheManager.Core.Services.SteamKit2;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

// A store lookup that fails must leave the app without a name, so a later pass can still name it,
// and must not be repeated within the hour, because the store API rate-limits.
public sealed class SteamStoreLookupTests
{
    [Fact]
    public async Task AStoreErrorAnswersNullAndIsNotAskedAgainWithinTheHour()
    {
        var handler = new CountingHandler(() => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var steam = new SteamService(new HttpClient(handler), NullLogger<SteamService>.Instance);

        Assert.Null(await steam.GetGameInfoAsync(440));
        Assert.Null(await steam.GetGameInfoAsync(440));

        Assert.Equal(1, handler.Calls);
        Assert.Equal(new long[] { 440 }, steam.GetRecentlyFailedAppIds());
    }

    [Fact]
    public async Task AStoreTimeoutAnswersNull()
    {
        var handler = new CountingHandler(() => throw new TaskCanceledException("timed out", new TimeoutException()));
        using var steam = new SteamService(new HttpClient(handler), NullLogger<SteamService>.Instance);

        Assert.Null(await steam.GetGameInfoAsync(440));

        Assert.Equal(1, handler.Calls);
    }

    private sealed class CountingHandler(Func<Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send();
        }
    }
}
