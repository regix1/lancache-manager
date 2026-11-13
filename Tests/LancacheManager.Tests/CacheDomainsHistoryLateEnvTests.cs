using System.Net;
using System.Text.Json;
using LancacheManager.Core.Services.StatusCheck;

namespace LancacheManager.Tests;

/// <summary>
/// One Status Check whose LANCache environment read answered only as the added-to-the-list lookup's budget
/// ran out, so GitHub was asked with no time left. GitHub itself never failed, so the next check, with the
/// environment answering at once, dates the newly listed failing name.
/// </summary>
public sealed class CacheDomainsHistoryLateEnvTests : IDisposable
{
    private const string CommitsUrl = "https://api.github.com/repos/uklans/cache-domains/commits";
    private const string KnownName = "lancache.steamcontent.com";
    private const string NewName = "blur-updates.lanparty.fyi";
    private static readonly DateTime _newNameAddedUtc = new(2026, 10, 30, 18, 31, 41, DateTimeKind.Utc);

    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "cache-domains-late-env-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("NOFETCH")]
    [InlineData("CACHE_DOMAINS_REPO")]
    [InlineData("CACHE_DOMAINS_BRANCH")]
    public async Task AnEnvironmentReadThatAnswersAsTheBudgetEnds_LeavesTheNextCheckDatingNewNames(string lateKey)
    {
        var environment = new CacheDomainsTestEnvironment { LateKey = lateKey };
        var gitHub = new GitHub();
        var service = environment.NewService(_rootDirectory, gitHub, new CapturingLogger<CacheDomainsService>());

        await service.GetAddedDatesAsync([NewName], [KnownName], CancellationToken.None);

        environment.LateKey = null;
        var next = await service.GetAddedDatesAsync([NewName], [KnownName], CancellationToken.None);
        Assert.True(next.TryGetValue(NewName, out var added) && added == _newNameAddedUtc,
            $"After a {lateKey} read that answered as the lookup's budget ran out, the next check gives no " +
            $"added-to-the-list date for {NewName}, though GitHub sent {gitHub.Failures} failing answer(s)");
    }

    /// <summary>GitHub's commit history of the default list: the newest commit adds the new name, the one
    /// before it adds a name the DNS server already sends to the cache. Every request it gets is answered.</summary>
    private sealed class GitHub : HttpMessageHandler
    {
        private int _failures;

        public int Failures => Volatile.Read(ref _failures);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.ToString() switch
            {
                $"{CommitsUrl}?sha=master" => JsonSerializer.Serialize(new object[]
                {
                    new { sha = "c298", commit = new { committer = new { date = "2026-10-30T18:31:41Z" } } },
                    new { sha = "c296", commit = new { committer = new { date = "2026-10-12T11:05:57Z" } } }
                }),
                $"{CommitsUrl}/c298" => JsonSerializer.Serialize(new
                {
                    files = new[] { new { filename = "blur.txt", patch = "@@ -0,0 +1 @@\n+" + NewName } }
                }),
                $"{CommitsUrl}/c296" => JsonSerializer.Serialize(new
                {
                    files = new[] { new { filename = "steam.txt", patch = "@@ -0,0 +1 @@\n+" + KnownName } }
                }),
                _ => null
            };

            if (body is null)
            {
                Interlocked.Increment(ref _failures);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
