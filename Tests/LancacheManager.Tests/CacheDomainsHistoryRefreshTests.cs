using System.Net;
using System.Text.Json;
using LancacheManager.Core.Services.StatusCheck;

namespace LancacheManager.Tests;

/// <summary>
/// One Status Check whose GitHub history lookup was rate limited, then the person presses Refresh list
/// and the list downloads. The next check dates the newly listed failing name again instead of hiding
/// the "added to the list" note for a day.
/// </summary>
public sealed class CacheDomainsHistoryRefreshTests : IDisposable
{
    private const string CommitsUrl = "https://api.github.com/repos/uklans/cache-domains/commits";
    private const string ArchiveUrl = "https://github.com/uklans/cache-domains/archive/master.tar.gz";
    private const string KnownName = "lancache.steamcontent.com";
    private const string NewName = "blur-updates.lanparty.fyi";
    private static readonly DateTime _newNameAddedUtc = new(2026, 10, 30, 18, 31, 41, DateTimeKind.Utc);

    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "cache-domains-history-refresh-" + Guid.NewGuid().ToString("N"));
    private readonly GitHub _gitHub = new();
    private readonly CacheDomainsTestEnvironment _environment = new();

    public CacheDomainsHistoryRefreshTests()
    {
        _gitHub.Answers[$"{CommitsUrl}?sha=master"] = JsonSerializer.Serialize(new object[]
        {
            new { sha = "c298", commit = new { committer = new { date = "2026-10-30T18:31:41Z" } } },
            new { sha = "c296", commit = new { committer = new { date = "2026-10-12T11:05:57Z" } } }
        });
        _gitHub.Answers[$"{CommitsUrl}/c298"] = JsonSerializer.Serialize(new
        {
            files = new[] { new { filename = "blur.txt", patch = "@@ -0,0 +1 @@\n+" + NewName } }
        });
        _gitHub.Answers[$"{CommitsUrl}/c296"] = JsonSerializer.Serialize(new
        {
            files = new[] { new { filename = "steam.txt", patch = "@@ -0,0 +1 @@\n+" + KnownName } }
        });
        _gitHub.Archive = Archive(
            ("cache_domains.json", """{"cache_domains":[{"name":"steam","description":"Steam","domain_files":["steam.txt"]},{"name":"blur","description":"Blur","domain_files":["blur.txt"]}]}"""),
            ("steam.txt", KnownName),
            ("blur.txt", NewName));
    }

    public void Dispose()
    {
        _gitHub.Dispose();
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ARefreshAfterARateLimitedHistoryLookup_DatesTheNextChecksNewNames()
    {
        var service = _environment.NewService(_rootDirectory, _gitHub, new CapturingLogger<CacheDomainsService>());
        _gitHub.ApiStatus = HttpStatusCode.Forbidden;
        var first = await service.GetAddedDatesAsync([NewName], [KnownName], CancellationToken.None);
        Assert.Empty(first);

        _gitHub.ApiStatus = HttpStatusCode.OK;
        var refresh = await service.RefreshDomainsAsync(CancellationToken.None);
        Assert.True(refresh.Success, "Refresh list did not download the list");

        var next = await service.GetAddedDatesAsync([NewName], [KnownName], CancellationToken.None);
        Assert.True(next.TryGetValue(NewName, out var added) && added == _newNameAddedUtc,
            "After Refresh list the next check still gives no added-to-the-list date for " + NewName);
    }

    /// <summary>Builds a gzipped tarball as GitHub serves a branch, with every file under a
    /// "{repo}-{branch}/" folder.</summary>
    private static byte[] Archive(params (string Name, string Content)[] files)
    {
        using var archive = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(archive, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        using (var writer = new System.Formats.Tar.TarWriter(gzip))
        {
            foreach (var (name, content) in files)
            {
                writer.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "cache-domains-master/" + name)
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))
                });
            }
        }

        return archive.ToArray();
    }

    /// <summary>Serves the branch archive, and answers GitHub API URLs from <see cref="Answers"/> with
    /// <see cref="ApiStatus"/>.</summary>
    private sealed class GitHub : HttpMessageHandler
    {
        public Dictionary<string, string> Answers { get; } = new();

        public byte[] Archive { get; set; } = [];

        public HttpStatusCode ApiStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url == ArchiveUrl)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) });
            }

            return Task.FromResult(ApiStatus == HttpStatusCode.OK && Answers.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(ApiStatus == HttpStatusCode.OK ? HttpStatusCode.NotFound : ApiStatus));
        }
    }
}
