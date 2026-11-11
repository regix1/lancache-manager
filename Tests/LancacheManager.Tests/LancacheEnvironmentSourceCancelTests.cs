using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using Docker.DotNet;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.StatusCheck;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A Status Check sweep canceled while the environment source reads Docker must end canceled and
/// must leave nothing cached, so the next lookup in the same minute still reads the containers.
/// </summary>
public class LancacheEnvironmentSourceCancelTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task CanceledDockerRead_EndsCanceled_AndTheNextLookupStillReadsDocker()
    {
        await using var docker = await FakeDockerEngine.StartAsync();
        var source = new LancacheEnvironmentSource(
            NullLogger<LancacheEnvironmentSource>.Instance,
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>());
        typeof(LancacheEnvironmentSource)
            .GetField("_dockerClient", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(source, new DockerClientConfiguration(docker.Address).CreateClient());

        using var cts = new CancellationTokenSource();
        var canceledRead = source.GetValueAsync("DISABLE_STEAM", cts.Token);
        var first = await Task.WhenAny(docker.FirstRequestHeld, canceledRead, Task.Delay(Bound));
        Assert.True(
            first == docker.FirstRequestHeld,
            $"the read must be waiting on Docker before the cancel (engine read: {docker.Received}; read {canceledRead.Status}: {canceledRead.Exception})");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledRead.WaitAsync(Bound));

        docker.Answer = true;
        var nextRead = source.GetValueAsync("DISABLE_STEAM", CancellationToken.None);
        Assert.True(
            await Task.WhenAny(nextRead, Task.Delay(Bound)) == nextRead,
            $"the next lookup must end (engine read: {docker.Received})");
        var next = await nextRead;

        Assert.True(next.Value == "true", $"the next lookup must read Docker (engine read: {docker.Received})");
        Assert.Equal(EnvValueSource.DockerInspect, next.Source);
    }

    /// <summary>
    /// A Docker Engine API on a loopback port. Until <see cref="Answer"/> is set it holds each
    /// request open without a reply; after that it lists one lancache-dns container and its env.
    /// </summary>
    private sealed class FakeDockerEngine : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly TaskCompletionSource _firstRequestHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _received = new();

        private FakeDockerEngine(WebApplication app)
        {
            _app = app;
            _app.Run(ServeAsync);
        }

        public static async Task<FakeDockerEngine> StartAsync()
        {
            var host = WebApplication.CreateSlimBuilder();
            host.Logging.ClearProviders();
            host.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var engine = new FakeDockerEngine(host.Build());
            await engine._app.StartAsync();
            return engine;
        }

        public Uri Address => new(_app.Urls.First());

        public Task FirstRequestHeld => _firstRequestHeld.Task;

        public volatile bool Answer;

        /// <summary>The path of every request the engine received, for a failure message.</summary>
        public string Received => string.Join(", ", _received);

        private async Task ServeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value!;
            _received.Enqueue(path);
            if (!Answer)
            {
                _firstRequestHeld.TrySetResult();
                // The Docker call stays in flight until its caller gives up.
                try
                {
                    await Task.Delay(Timeout.Infinite, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                    // The caller canceled; the request ends with no reply.
                }
                return;
            }

            var body = Encoding.UTF8.GetBytes(path.EndsWith("/containers/json", StringComparison.Ordinal)
                ? """[{"Id":"dns1","Names":["/lancache-dns"],"Image":"lancachenet/lancache-dns:latest","State":"running"}]"""
                : """{"Id":"dns1","Config":{"Env":["DISABLE_STEAM=true"]}}""");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = body.Length;
            await context.Response.Body.WriteAsync(body);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
