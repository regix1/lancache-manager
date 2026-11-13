using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LancacheManager.Core.Services.StatusCheck;
using Xunit;

namespace LancacheManager.Tests;

/// <summary>
/// Covers how <see cref="CacheDomainsService.GetAddedDatesAsync"/> dates failing names from the
/// domain list's GitHub history: only names added after the newest commit whose names the DNS server
/// sends to the cache get a date, a line moved inside a file is not new, and a lookup that fails or
/// runs too long gives no date and waits before asking again. The history is the uklans one around
/// the Nintendo, Xbox and Blur additions of 2026-10-30.
/// </summary>
public sealed class CacheDomainsAddedDatesTests : IDisposable
{
    private const string CommitsUrl = "https://api.github.com/repos/uklans/cache-domains/commits";

    private static readonly DateTime _blurAddedUtc = new(2026, 10, 30, 18, 31, 41, DateTimeKind.Utc);
    private static readonly DateTime _nintendoAddedUtc = new(2026, 10, 30, 17, 55, 58, DateTimeKind.Utc);

    private static readonly string[] _nintendoAdded =
    [
        "*.hac.p01.d4c.nintendo.net",
        "*.hac.p01.eshop.nintendo.net",
        "samurai-wup.cdn.nintendo.net",
        "kanzashi-wup.cdn.nintendo.net",
        "*.ctr.shop.nintendo.net",
        "*.cdn.c.shop.nintendowifi.net",
        "*.cdn.t.shop.nintendowifi.net",
        "*.cdn.shop.wii.com"
    ];

    private static readonly string[] _xboxAdded = ["download.xbox.com", "download.xbox.com.edgesuite.net"];

    private const string MovedNintendoName = "ecs-lp1.hac.shop.nintendo.net";
    private const string KnownXboxName = "assets1.xboxlive.com.edgesuite.net";
    private const string OlderFailingName = "epicgames-download1.akamaized.net";

    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "cache-domains-dates-" + Guid.NewGuid().ToString("N"));
    private readonly GitHubHandler _handler = new();
    private readonly CacheDomainsTestEnvironment _environment = new();
    private readonly CapturingLogger<CacheDomainsService> _logger = new();

    public CacheDomainsAddedDatesTests()
    {
        _handler.Answers[$"{CommitsUrl}?sha=master"] = JsonSerializer.Serialize(new object[]
        {
            Commit("c300", "2026-11-01T19:58:51Z"),
            Commit("c298", "2026-10-30T18:31:41Z"),
            Commit("c299", "2026-10-30T17:55:58Z"),
            Commit("c296", "2026-10-12T11:05:57Z"),
            Commit("c295", "2025-09-07T07:48:40Z")
        });
        _handler.Answers[$"{CommitsUrl}/c300"] = Files(("windowsupdates.txt", "@@ -1,3 +1,2 @@\n dl.delivery.mp.microsoft.com\n-tlu.dl.delivery.mp.microsoft.com\n"));
        _handler.Answers[$"{CommitsUrl}/c298"] = Files(
            ("blur.txt", "@@ -0,0 +1 @@\n+blur-updates.lanparty.fyi"),
            ("cache_domains.json", "@@ -10,6 +10,11 @@\n+    {\n+      \"name\": \"blur\",\n+      \"domain_files\": [\"blur.txt\"]\n+    },"));
        _handler.Answers[$"{CommitsUrl}/c299"] = Files(
            ("nintendo.txt",
                "@@ -1,11 +1,29 @@\n+# Nintendo Switch 2\n+*.hac.p01.d4c.nintendo.net\n+*.hac.p01.eshop.nintendo.net\n+\n+# Nintendo Switch\n *.hac.lp1.d4c.nintendo.net\n+ecs-lp1.hac.shop.nintendo.net\n+\n *.wup.eshop.nintendo.net\n-ecs-lp1.hac.shop.nintendo.net\n+samurai-wup.cdn.nintendo.net\n+kanzashi-wup.cdn.nintendo.net\n+\n+# Nintendo 3DS\n+*.ctr.shop.nintendo.net\n+*.cdn.c.shop.nintendowifi.net\n+\n+# Nintendo DSi\n+*.cdn.t.shop.nintendowifi.net\n+\n+# Nintendo Wii\n+*.cdn.shop.wii.com\n\\ No newline at end of file"),
            ("xboxlive.txt", "@@ -6,3 +6,7 @@ xbox-mbr.xboxlive.com\n assets1.xboxlive.com.edgesuite.net\n+\n+# Xbox 360 content\n+download.xbox.com\n+download.xbox.com.edgesuite.net\n\\ No newline at end of file"));
        _handler.Answers[$"{CommitsUrl}/c296"] = Files(("xboxlive.txt", "@@ -5,2 +5,3 @@\n xvcf2.xboxlive.com\n+assets1.xboxlive.com.edgesuite.net"));
        _handler.Answers[$"{CommitsUrl}/c295"] = Files(("epicgames.txt", "@@ -1,2 +1,3 @@\n+epicgames-download1.akamaized.net"));
    }

    public void Dispose()
    {
        _handler.Dispose();
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NamesAddedAfterTheNewestKnownCommit_GetTheirCommitDate()
    {
        var failing = _nintendoAdded.Concat(_xboxAdded)
            .Append("blur-updates.lanparty.fyi")
            .Append(MovedNintendoName)
            .Append(OlderFailingName)
            .ToList();

        var dates = await NewService().GetAddedDatesAsync(failing, [KnownXboxName, "lancache.steamcontent.com"], CancellationToken.None);

        Assert.Equal(_blurAddedUtc, dates["blur-updates.lanparty.fyi"]);
        foreach (var name in _nintendoAdded.Concat(_xboxAdded))
        {
            Assert.Equal(_nintendoAddedUtc, dates[name]);
        }
        Assert.Equal(11, dates.Count);
        Assert.False(dates.ContainsKey(MovedNintendoName), "a line moved inside nintendo.txt was dated as new");
        Assert.False(dates.ContainsKey(OlderFailingName), "a name older than the newest known commit was dated");
        Assert.DoesNotContain($"{CommitsUrl}/c295", _handler.Requests);
    }

    [Fact]
    public async Task SecondLookup_ReadsEachCommitOnce()
    {
        var service = NewService();
        await service.GetAddedDatesAsync(["blur-updates.lanparty.fyi"], [KnownXboxName], CancellationToken.None);
        var firstCount = _handler.Requests.Count;

        var dates = await service.GetAddedDatesAsync(["blur-updates.lanparty.fyi"], [KnownXboxName], CancellationToken.None);

        Assert.Equal(_blurAddedUtc, dates["blur-updates.lanparty.fyi"]);
        Assert.Equal(firstCount + 1, _handler.Requests.Count);
    }

    [Fact]
    public async Task NoKnownCommitOnThePage_GivesNoDate()
    {
        var dates = await NewService().GetAddedDatesAsync(["blur-updates.lanparty.fyi"], ["not-on-the-page.example"], CancellationToken.None);

        Assert.Empty(dates);
    }

    [Fact]
    public async Task NoFetch_MakesNoRequest()
    {
        _environment.NoFetch = "true";

        var dates = await NewService().GetAddedDatesAsync(["blur-updates.lanparty.fyi"], [KnownXboxName], CancellationToken.None);

        Assert.Empty(dates);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task RateLimitedLookup_GivesNoDateAndWaitsBeforeAskingAgain()
    {
        _handler.Status = HttpStatusCode.Forbidden;
        var service = NewService();

        var first = await service.GetAddedDatesAsync(["blur-updates.lanparty.fyi"], [KnownXboxName], CancellationToken.None);
        _handler.Status = HttpStatusCode.OK;
        var second = await service.GetAddedDatesAsync(["blur-updates.lanparty.fyi"], [KnownXboxName], CancellationToken.None);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task UnansweredLookup_EndsWithinItsBudget()
    {
        _handler.Hang = true;
        var stopwatch = Stopwatch.StartNew();

        var dates = await NewService()
            .GetAddedDatesAsync(["blur-updates.lanparty.fyi"], [KnownXboxName], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Empty(dates);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void ParseAddedEntries_SkipsMovedLinesCommentsAndPatchMarkers()
    {
        var added = CacheDomainsService.ParseAddedEntries(Files(
            ("nintendo.txt", "@@ -1,3 +1,4 @@\n+# Nintendo Wii\n+*.cdn.shop.wii.com # shop\n+moved.example\n-moved.example\n context.example\n\\ No newline at end of file"),
            ("large.txt", null)));

        Assert.Equal(new[] { "*.cdn.shop.wii.com" }, added);
    }

    private static object Commit(string sha, string date) =>
        new { sha, commit = new { committer = new { date } } };

    private static string Files(params (string Name, string? Patch)[] files) =>
        JsonSerializer.Serialize(new
        {
            files = files.Select(file => file.Patch is null
                ? (object)new { filename = file.Name }
                : new { filename = file.Name, patch = file.Patch })
        });

    private CacheDomainsService NewService() => _environment.NewService(_rootDirectory, _handler, _logger);

    /// <summary>Answers GitHub API URLs from <see cref="Answers"/> with <see cref="Status"/>, or
    /// holds every request until it is canceled when <see cref="Hang"/> is set, and records each
    /// requested URL.</summary>
    private sealed class GitHubHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Answers { get; } = new();

        public List<string> Requests { get; } = new();

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public bool Hang { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests)
            {
                Requests.Add(url);
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Status == HttpStatusCode.OK && Answers.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(Status == HttpStatusCode.OK ? HttpStatusCode.NotFound : Status);
        }
    }
}
