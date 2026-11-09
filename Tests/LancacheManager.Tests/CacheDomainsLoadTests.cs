using System.Net;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.StatusCheck;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LancacheManager.Tests;

/// <summary>
/// Covers how <see cref="CacheDomainsService"/> loads behave while a GitHub fetch hangs or after it
/// fails: loads that already hold a list answer with it instead of waiting, and a failed fetch is
/// not retried by automatic loads until the normal 24 h interval passes.
/// </summary>
public sealed class CacheDomainsLoadTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), "cache-domains-load-" + Guid.NewGuid().ToString("N"));
    private readonly HeldHandler _handler = new();
    private readonly TestEnvironment _environment = new();
    private readonly CapturingLogger<CacheDomainsService> _logger = new();

    public void Dispose()
    {
        _handler.Dispose();
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadWithListHeld_AnswersWhileForcedFetchHangs()
    {
        var service = NewService();
        await PrimeFromStaleDiskCopy(service);

        var forced = service.GetDomainsAsync(true, CancellationToken.None);
        await _handler.Reached.WaitAsync(TimeSpan.FromSeconds(10));

        var quick = service.GetDomainsAsync(false, CancellationToken.None);
        var finished = await Task.WhenAny(quick, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(quick, finished);
        Assert.Equal("steam", Assert.Single((await quick).Services).Name);

        _handler.Release();
        await forced;
    }

    [Fact]
    public async Task FailedForcedFetch_StopsAutomaticLoadsFromRetrying()
    {
        var service = NewService();
        await PrimeFromStaleDiskCopy(service);
        _handler.Release();

        await service.GetDomainsAsync(true, CancellationToken.None);
        Assert.Equal(1, _handler.RequestCount);

        var later = await service.GetDomainsAsync(false, CancellationToken.None);

        Assert.Equal(1, _handler.RequestCount);
        Assert.Equal("steam", Assert.Single(later.Services).Name);
    }

    [Fact]
    public async Task NoDiskCopy_RetriesOnEveryLoad()
    {
        var service = NewService();
        _handler.Release();

        var first = await service.GetDomainsAsync(false, CancellationToken.None);
        Assert.Empty(first.Services);
        Assert.Equal(1, _handler.RequestCount);

        await service.GetDomainsAsync(false, CancellationToken.None);

        Assert.Equal(2, _handler.RequestCount);
    }

    [Fact]
    public async Task CanceledLoad_RecordsNoFailedFetch()
    {
        var service = NewService();
        await PrimeFromStaleDiskCopy(service);
        using var leave = new CancellationTokenSource();

        var load = service.GetDomainsAsync(false, leave.Token);
        await _handler.Reached.WaitAsync(TimeSpan.FromSeconds(10));
        await leave.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(service.GetCurrentSource().Error);

        // The next automatic load still asks GitHub, since nothing failed.
        _handler.Release();
        await service.GetDomainsAsync(false, CancellationToken.None);
        Assert.Equal(2, _handler.RequestCount);
    }

    [Fact]
    public async Task LoadCanceledAtTheDiskWrite_StoresTheWholeFetchedCopy()
    {
        var service = NewService();
        await PrimeFromStaleDiskCopy(service);
        _handler.Archive = Archive(
            ("cache_domains.json", """{"cache_domains":[{"name":"steam","description":"Steam","domain_files":["steam.txt"]},{"name":"epic","description":"Epic","domain_files":["epic.txt"]}]}"""),
            ("steam.txt", "new.steamcontent.com\n"));
        _handler.Release();
        using var leave = new CancellationTokenSource();
        // The missing epic.txt warning is logged after the download and before the disk writes,
        // which is where a person leaving the page cancels the load.
        _logger.OnLogged = entry =>
        {
            if (entry.Level == LogLevel.Warning) leave.Cancel();
        };

        var load = await Record.ExceptionAsync(() => service.GetDomainsAsync(true, leave.Token));

        var domainsDirectory = Path.Combine(_rootDirectory, "cache-domains");
        Assert.Contains("epic.txt", await File.ReadAllTextAsync(Path.Combine(domainsDirectory, "cache_domains.json")));
        Assert.Equal("new.steamcontent.com\n", await File.ReadAllTextAsync(Path.Combine(domainsDirectory, "steam.txt")));
        Assert.Null(load);

        // A restart serves the copy on disk.
        _logger.OnLogged = null;
        _environment.NoFetch = "true";
        var restarted = await NewService().GetDomainsAsync(false, CancellationToken.None);
        Assert.Equal(new[] { "new.steamcontent.com" }, restarted.Services.Single(s => s.Name == "steam").Domains);
    }

    [Fact]
    public async Task DiskWriteFails_ServesTheFetchedListAndKeepsTheOldManifest()
    {
        var service = NewService();
        await PrimeFromStaleDiskCopy(service);
        _handler.Archive = Archive(
            ("cache_domains.json", """{"cache_domains":[{"name":"steam","description":"Steam","domain_files":["steam.txt"]},{"name":"epic","description":"Epic","domain_files":["epic.txt"]}]}"""),
            ("steam.txt", "new.steamcontent.com\n"),
            ("epic.txt", "epicgames-download1.akamaized.net\n"));
        _handler.Release();
        // A folder where epic.txt belongs makes that one file write throw, on Windows and Linux alike.
        var domainsDirectory = Path.Combine(_rootDirectory, "cache-domains");
        Directory.CreateDirectory(Path.Combine(domainsDirectory, "epic.txt"));

        var loading = service.GetDomainsAsync(true, CancellationToken.None);
        Assert.Null(await Record.ExceptionAsync(() => loading));
        var load = await loading;

        Assert.Equal(new[] { "epic", "steam" }, load.Services.Select(s => s.Name).Order());
        var source = service.GetCurrentSource();
        Assert.Null(source.Error);
        Assert.False(source.FromCache);
        Assert.DoesNotContain("epic.txt", await File.ReadAllTextAsync(Path.Combine(domainsDirectory, "cache_domains.json")));

        // A restart serves the old manifest, not a new one beside a missing domain file.
        _environment.NoFetch = "true";
        var restarted = await NewService().GetDomainsAsync(false, CancellationToken.None);
        Assert.Equal("steam", Assert.Single(restarted.Services).Name);
    }

    [Fact]
    public async Task FailedFetchThenNoFetch_ReportsNoFetchFailure()
    {
        var service = NewService();
        await PrimeFromStaleDiskCopy(service);
        _handler.Release();
        await service.GetDomainsAsync(true, CancellationToken.None);

        _environment.NoFetch = "true";
        await service.GetDomainsAsync(false, CancellationToken.None);

        var source = service.GetCurrentSource();
        Assert.True(source.NoFetch);
        Assert.Null(source.Error);
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

    private CacheDomainsService NewService()
    {
        var paths = CacheScanGateHarness.CreateProxy<IPathResolver>((method, _) =>
            method.Name == nameof(IPathResolver.GetDataDirectory) ? _rootDirectory : null);

        return new CacheDomainsService(
            _logger,
            paths,
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
            _environment,
            new SingleHandlerHttpClients(_handler),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>());
    }

    /// <summary>Loads a disk copy written two days ago with NOFETCH set, so the service holds a
    /// stale list without any HTTP request, then lets later loads fetch.</summary>
    private async Task PrimeFromStaleDiskCopy(CacheDomainsService service)
    {
        var domainsDirectory = Path.Combine(_rootDirectory, "cache-domains");
        Directory.CreateDirectory(domainsDirectory);
        var manifestPath = Path.Combine(domainsDirectory, "cache_domains.json");
        await File.WriteAllTextAsync(manifestPath,
            """{"cache_domains":[{"name":"steam","description":"Steam","domain_files":["steam.txt"]}]}""");
        await File.WriteAllTextAsync(Path.Combine(domainsDirectory, "steam.txt"), "lancache.steamcontent.com\n");
        File.SetLastWriteTimeUtc(manifestPath, DateTime.UtcNow.AddDays(-2));

        _environment.NoFetch = "true";
        var primed = await service.GetDomainsAsync(false, CancellationToken.None);
        _environment.NoFetch = null;

        Assert.Equal("steam", Assert.Single(primed.Services).Name);
        Assert.Equal(0, _handler.RequestCount);
    }

    /// <summary>Supplies NOFETCH as the test sets it and no other variable, so the service uses its
    /// default GitHub repository.</summary>
    private sealed class TestEnvironment : ILancacheEnvironmentSource
    {
        public string? NoFetch { get; set; }

        public Task<EnvValueResult> GetValueAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new EnvValueResult
            {
                Value = key == "NOFETCH" ? NoFetch : null,
                Source = EnvValueSource.EnvFile
            });
    }

    /// <summary>Holds every request until the test releases it, then fails it, as an unreachable
    /// GitHub would, or answers with <see cref="Archive"/> when the test set one. Requests after the
    /// release answer at once.</summary>
    private sealed class HeldHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requests;

        public Task Reached => _reached.Task;

        public int RequestCount => Volatile.Read(ref _requests);

        public byte[]? Archive { get; set; }

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            _reached.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return Archive is { } archive
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        protected override void Dispose(bool disposing)
        {
            _released.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
