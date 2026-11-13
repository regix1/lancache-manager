using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // The recurring image check asks the registry which image the tag points at and pulls only when that
    // differs from the image the host already has, so an unchanged image is not downloaded again every
    // check. The server below answers both as the Docker engine (including its registry digest lookup) and
    // as the image's registry, so the check can ask either one. The shipped Linux image reaches Docker
    // through its unix socket, so the engine side is also served on one; the registry stays on loopback
    // TCP, since an image reference names a host and port. The Windows named pipe is not covered here.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageCheckPullsOnlyWhenTheTagMovesAsync(bool engineOnUnixSocket)
    {
        await using var engine = await ImageDigestEngine.StartAsync(
            "sha256:" + new string('a', 64), "sha256:" + new string('1', 64), engineOnUnixSocket);
        var gateway = new DockerPrefillContainerGateway();
        await gateway.ConnectAsync(engine.DockerUri, CancellationToken.None);
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true), image: engine.Repository),
            gateway);

        // The first check after the app starts; afterwards the host holds the image the registry serves.
        await PullAsync(daemon, CancellationToken.None);
        MakeImageCheckDue(daemon);
        var pullsBeforeUnchangedCheck = engine.Pulls;

        await PullAsync(daemon, CancellationToken.None);

        Assert.True(engine.Pulls == pullsBeforeUnchangedCheck,
            "The image check pulled the image again although the registry digest equals the local image's digest. "
            + $"Requests: {engine.Received}");

        // A newer image is published under the same tag.
        engine.PublishImage("sha256:" + new string('b', 64), "sha256:" + new string('2', 64));
        MakeImageCheckDue(daemon);

        await PullAsync(daemon, CancellationToken.None);

        Assert.True(engine.Pulls > pullsBeforeUnchangedCheck,
            $"The image check did not pull the newer image the registry now serves. Requests: {engine.Received}");

        static void MakeImageCheckDue(PrefillDaemonServiceBase daemon) =>
            typeof(PrefillDaemonServiceBase).GetField("_nextImageCheckAtUtc", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(daemon, DateTime.MinValue);
    }

    // One loopback server playing the Docker engine and the registry of the image it pulls. The host holds
    // one image of the repository; the registry serves one image under the tag; a pull makes the host hold
    // the image the registry serves.
    private sealed class ImageDigestEngine : IAsyncDisposable
    {
        private const string ManifestIndexType = "application/vnd.oci.image.index.v1+json";
        private readonly WebApplication _app;
        private readonly ConcurrentQueue<string> _received = new();
        private readonly object _sync = new();
        private readonly string? _socketPath;
        private string _servedDigest;
        private string _servedImageId;
        private string _localDigest;
        private string _localImageId;
        private int _pulls;

        private ImageDigestEngine(WebApplication app, string digest, string imageId, string? socketPath)
        {
            _app = app;
            _socketPath = socketPath;
            _servedDigest = digest;
            _servedImageId = imageId;
            _localDigest = digest;
            _localImageId = imageId;
            _app.Run(ServeAsync);
        }

        public static async Task<ImageDigestEngine> StartAsync(string digest, string imageId, bool engineOnUnixSocket)
        {
            var socketPath = engineOnUnixSocket ? Path.Combine(Path.GetTempPath(), $"lcm-{Guid.NewGuid():N}.sock") : null;
            var host = WebApplication.CreateSlimBuilder();
            host.Logging.ClearProviders();
            host.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                if (socketPath is not null)
                {
                    options.ListenUnixSocket(socketPath);
                }
            });
            var engine = new ImageDigestEngine(host.Build(), digest, imageId, socketPath);
            await engine._app.StartAsync();
            return engine;
        }

        private int Port => new Uri(_app.Urls.First(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))).Port;

        public Uri DockerUri => _socketPath is null ? new($"tcp://127.0.0.1:{Port}") : new($"unix://{_socketPath}");

        /// <summary>The image repository, hosted by this server's registry endpoints.</summary>
        public string Repository => $"127.0.0.1:{Port}/prefill";

        public int Pulls => Volatile.Read(ref _pulls);

        /// <summary>Every request the server received, for a failure message.</summary>
        public string Received => string.Join(", ", _received);

        public void PublishImage(string digest, string imageId)
        {
            lock (_sync)
            {
                _servedDigest = digest;
                _servedImageId = imageId;
            }
        }

        private async Task ServeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value!;
            _received.Enqueue($"{context.Request.Method} {path}{context.Request.QueryString}");
            string servedDigest;
            string localDigest;
            string localImageId;
            lock (_sync)
            {
                if (path.EndsWith("/images/create", StringComparison.Ordinal))
                {
                    _pulls++;
                    _localDigest = _servedDigest;
                    _localImageId = _servedImageId;
                }
                servedDigest = _servedDigest;
                localDigest = _localDigest;
                localImageId = _localImageId;
            }

            if (path.EndsWith("/version", StringComparison.Ordinal))
            {
                await WriteJsonAsync(context, """{"Version":"27.3.1","ApiVersion":"1.47","MinAPIVersion":"1.24","Os":"linux","Arch":"amd64"}""");
            }
            else if (path.EndsWith("/_ping", StringComparison.Ordinal))
            {
                await WriteJsonAsync(context, "OK");
            }
            else if (path.EndsWith("/images/create", StringComparison.Ordinal))
            {
                await WriteJsonAsync(context,
                    "{\"status\":\"Pulling from prefill\",\"id\":\"latest\"}\n"
                    + $"{{\"status\":\"Digest: {servedDigest}\"}}\n"
                    + $"{{\"status\":\"Status: Downloaded newer image for {Repository}:latest\"}}\n");
            }
            else if (path.Contains("/distribution/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
            {
                await WriteJsonAsync(context,
                    $"{{\"Descriptor\":{{\"mediaType\":\"{ManifestIndexType}\",\"digest\":\"{servedDigest}\",\"size\":512}},"
                    + "\"Platforms\":[{\"architecture\":\"amd64\",\"os\":\"linux\"}]}");
            }
            else if (path == "/v2/" || path == "/v2")
            {
                await WriteJsonAsync(context, "{}");
            }
            else if (path.StartsWith("/v2/", StringComparison.Ordinal) && path.Contains("/manifests/", StringComparison.Ordinal))
            {
                context.Response.Headers["Docker-Content-Digest"] = servedDigest;
                context.Response.Headers["Docker-Distribution-Api-Version"] = "registry/2.0";
                var manifest = Encoding.UTF8.GetBytes(
                    $"{{\"schemaVersion\":2,\"mediaType\":\"{ManifestIndexType}\",\"manifests\":[]}}");
                context.Response.ContentType = ManifestIndexType;
                context.Response.ContentLength = manifest.Length;
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await context.Response.Body.WriteAsync(manifest);
                }
            }
            else if (path.Contains("/images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
            {
                await WriteJsonAsync(context,
                    $"{{\"Id\":\"{localImageId}\",\"RepoTags\":[\"{Repository}:latest\"],"
                    + $"\"RepoDigests\":[\"{Repository}@{localDigest}\"]}}");
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await WriteJsonAsync(context, "{\"message\":\"page not found\"}");
            }
        }

        private static async Task WriteJsonAsync(HttpContext context, string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            if (_socketPath is not null)
            {
                File.Delete(_socketPath);
            }
        }
    }
}
