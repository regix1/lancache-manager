using System.Net;
using System.Text.Json;
using LancacheManager.Core.Services.EpicMapping;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class EpicApiDirectClientTests
{
    [Theory]
    [InlineData("2026-09-01T12:30:00Z")]
    [InlineData("2026-09-01T14:30:00+02:00")]
    [InlineData("2026-09-01T12:30:00.0000000Z")]
    public async Task TokenExchanges_PreserveAbsoluteExpiry(string expiry)
    {
        using var handler = new TokenHandler(expiry, 3600);
        using var http = new HttpClient(handler);
        var client = new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance);
        var before = DateTime.UtcNow;
        EpicOAuthTokens[] tokens = [
            await client.ExchangeAuthCodeAsync("code"),
            await client.RefreshTokenAsync("refresh"),
            await client.ExchangeCodeAsync("exchange")
        ];
        var after = DateTime.UtcNow;
        foreach (var token in tokens)
        {
            Assert.Equal(new DateTime(2026, 9, 1, 12, 30, 0, DateTimeKind.Utc), token.ExpiresAt);
            Assert.Equal(DateTimeKind.Utc, token.ExpiresAt.Kind);
            Assert.InRange(token.RefreshExpiresAt, before.AddHours(1), after.AddHours(1));
        }
    }

    [Theory]
    [InlineData(null, 3600)]
    [InlineData("", 3600)]
    [InlineData("invalid", 3600)]
    [InlineData("2026-09-01T12:30:00", 3600)]
    [InlineData("99999-09-01T12:30:00Z", 3600)]
    [InlineData("2026-09-01T12:30:00Z", 0)]
    [InlineData("2026-09-01T12:30:00Z", -1)]
    public async Task TokenExchanges_RejectInvalidExpiry(string? expiry, int refreshSeconds)
    {
        using var handler = new TokenHandler(expiry, refreshSeconds);
        using var http = new HttpClient(handler);
        var client = new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExchangeAuthCodeAsync("code"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.RefreshTokenAsync("refresh"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ExchangeCodeAsync("exchange"));
    }

    [Fact]
    public async Task GetOwnedGames_ACatalogRequestThatTimesOutLeavesTheNameEmpty()
    {
        using var handler = new LibraryHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("id=cb"))
            {
                throw new TaskCanceledException("timed out", new TimeoutException());
            }
            return CatalogResponse(request);
        });
        using var http = new HttpClient(handler);
        var client = new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance);
        var reports = new List<(int Done, int Total)>();

        var games = await client.GetOwnedGamesAsync(
            "token",
            (done, total) =>
            {
                reports.Add((done, total));
                return Task.CompletedTask;
            });

        Assert.Equal(["A", "B", "C"], games.Select(g => g.AppId));
        Assert.Equal(["Title ca", "", "Title cc"], games.Select(g => g.Name));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], reports);
    }

    [Fact]
    public async Task GetOwnedGames_ACancelDuringACatalogRequestReturnsTheGamesReadAndStoresNoNameOnlyGame()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new LibraryHandler(request =>
        {
            if (request.RequestUri!.Query.Contains("id=cb"))
            {
                cts.Cancel();
                throw new TaskCanceledException("canceled", null, cts.Token);
            }
            return CatalogResponse(request);
        });
        using var http = new HttpClient(handler);
        var client = new EpicApiDirectClient(http, NullLogger<EpicApiDirectClient>.Instance);
        var reports = new List<(int Done, int Total)>();

        var games = await client.GetOwnedGamesAsync(
            "token",
            (done, total) =>
            {
                reports.Add((done, total));
                return Task.CompletedTask;
            },
            cts.Token);

        Assert.Equal(["A"], games.Select(g => g.AppId));
        Assert.Equal([(1, 3)], reports);
    }

    [Fact]
    public async Task GetCdnPaths_SkipsGamesWhoseAppIdIsInTheSkipSet()
    {
        using var skipHandler = new LibraryHandler(ManifestResponse);
        using var skipHttp = new HttpClient(skipHandler);
        var client = new EpicApiDirectClient(skipHttp, NullLogger<EpicApiDirectClient>.Instance);
        var reports = new List<(int Done, int Total)>();

        var skipped = await client.GetCdnInfoAsync(
            "token",
            new HashSet<string> { "A" },
            (done, total) =>
            {
                reports.Add((done, total));
                return Task.CompletedTask;
            });

        Assert.Equal(["B"], skipHandler.ManifestRequestedApps);
        Assert.Equal(["B"], skipped.Select(c => c.AppId));
        Assert.Equal([(1, 3), (2, 3), (3, 3)], reports);

        using var allHandler = new LibraryHandler(ManifestResponse);
        using var allHttp = new HttpClient(allHandler);
        var allClient = new EpicApiDirectClient(allHttp, NullLogger<EpicApiDirectClient>.Instance);

        await allClient.GetCdnInfoAsync("token", new HashSet<string>(), static (_, _) => Task.CompletedTask);

        Assert.Equal(["A", "B"], allHandler.ManifestRequestedApps);
    }

    private static HttpResponseMessage CatalogResponse(HttpRequestMessage request)
    {
        var itemId = request.RequestUri!.Query.Split("id=")[1].Split('&')[0];
        var items = new Dictionary<string, object> { [itemId] = new { id = itemId, title = $"Title {itemId}" } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(items)) };
    }

    private static HttpResponseMessage ManifestResponse(HttpRequestMessage request)
    {
        var body = new
        {
            elements = new[]
            {
                new { manifests = new[] { new { uri = "https://epicgames-download1.akamaized.net/Builds/Org/o-x/hash/default/manifest.manifest" } } }
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    }

    // Serves a three-asset library; the third asset has no build version, so it never gets a manifest request.
    private sealed class LibraryHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> ManifestRequestedApps { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/assets/Windows?label=Live"))
            {
                var assets = new[]
                {
                    new { appName = "A", buildVersion = (string?)"1", catalogItemId = "ca", @namespace = "na" },
                    new { appName = "B", buildVersion = (string?)"1", catalogItemId = "cb", @namespace = "nb" },
                    new { appName = "C", buildVersion = (string?)null, catalogItemId = "cc", @namespace = "nc" }
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(assets))
                });
            }
            if (url.Contains("/assets/v2/platform/Windows"))
            {
                ManifestRequestedApps.Add(url.Split("/app/")[1].Split('/')[0]);
            }
            return Task.FromResult(answer(request));
        }
    }

    private sealed class TokenHandler(string? expiry, int refreshSeconds) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var fields = new Dictionary<string, object?>
            {
                ["access_token"] = "access",
                ["refresh_token"] = "refresh",
                ["expires_at"] = expiry,
                ["expires_in"] = 999999,
                ["refresh_expires"] = refreshSeconds
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(fields))
            });
        }
    }
}
