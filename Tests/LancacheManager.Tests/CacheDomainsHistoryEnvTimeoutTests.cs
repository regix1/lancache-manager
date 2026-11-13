using System.Net;
using System.Text.Json;
using LancacheManager.Core.Services.StatusCheck;

namespace LancacheManager.Tests;

/// <summary>
/// One Status Check whose added-to-the-list lookup ran out of its budget on a slow LANCache environment
/// read, before GitHub was asked. The next check, with the environment answering again, dates the newly
/// listed failing name instead of hiding the "added to the list" note for a day.
/// </summary>
public sealed class CacheDomainsHistoryEnvTimeoutTests : IDisposable
{
    private const string CommitsUrl = "https://api.github.com/repos/uklans/cache-domains/commits";
    private const string KnownName = "lancache.steamcontent.com";
    private const string NewName = "blur-updates.lanparty.fyi";
    private static readonly DateTime _newNameAddedUtc = new(2026, 10, 30, 18, 31, 41, DateTimeKind.Utc);

    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "cache-domains-env-timeout-" + Guid.NewGuid().ToString("N"));

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
    public async Task ASlowEnvironmentReadBeforeGitHubWasAsked_LeavesTheNextCheckDatingNewNames(string slowKey)
    {
        var environment = new CacheDomainsTestEnvironment { SlowKey = slowKey };
        var gitHub = new GitHub();
        var service = environment.NewService(_rootDirectory, gitHub, new CapturingLogger<CacheDomainsService>());

        var first = await service.GetAddedDatesAsync([NewName], [KnownName], CancellationToken.None);
        Assert.Empty(first);
        Assert.True(gitHub.Requests == 0, $"GitHub was asked {gitHub.Requests} time(s) while the {slowKey} read was still open");

        environment.SlowKey = null;
        var next = await service.GetAddedDatesAsync([NewName], [KnownName], CancellationToken.None);
        Assert.True(next.TryGetValue(NewName, out var added) && added == _newNameAddedUtc,
            $"After a slow {slowKey} read the next check gives no added-to-the-list date for {NewName}, though GitHub was never asked");
    }

    /// <summary>GitHub's commit history of the default list: the newest commit adds the new name, the one
    /// before it adds a name the DNS server already sends to the cache.</summary>
    private sealed class GitHub : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
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

            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
