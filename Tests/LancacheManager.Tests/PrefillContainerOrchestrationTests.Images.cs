using System.Net;
using System.Reflection;
using Docker.DotNet;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.Xbox)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    public async Task ImagePullRetriesTransientFailureOnEveryPlatformAsync(PrefillPlatform platform)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.ImagePullFailures.Enqueue(new DockerApiException(HttpStatusCode.InternalServerError, "TLS handshake timeout"));
        using var daemon = BuildDaemon(platform, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);

        await PullAsync(daemon, CancellationToken.None);

        Assert.Equal(2, gateway.CountOf("CreateImage"));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
        Assert.Equal(0, gateway.DestructiveCallCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImagePullUsesCachedImageOnlyAfterBoundedRetriesAsync(bool cached)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ImageExists = cached };
        for (var attempt = 0; attempt < 3; attempt++)
            gateway.ImagePullFailures.Enqueue(new DockerApiException(HttpStatusCode.InternalServerError, "TLS handshake timeout"));
        using var daemon = BuildDaemon(PrefillPlatform.Epic, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);

        if (cached) await PullAsync(daemon, CancellationToken.None);
        else await Assert.ThrowsAsync<DockerImageNotFoundException>(() => PullAsync(daemon, CancellationToken.None));

        Assert.Equal(3, gateway.CountOf("CreateImage"));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
        Assert.Equal(0, gateway.DestructiveCallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ImagePullDoesNotRetryPermanentFailuresAsync(HttpStatusCode status)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.ImagePullFailures.Enqueue(new DockerApiException(status, "Image is unavailable"));
        using var daemon = BuildDaemon(PrefillPlatform.Epic, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);

        await PullAsync(daemon, CancellationToken.None);

        Assert.Equal(1, gateway.CountOf("CreateImage"));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledImagePullDoesNotRetryOrFallBackAsync(bool cancelledBeforePull)
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        using var cancel = new CancellationTokenSource();
        var gateway = new RecordingContainerGateway { OnImagePull = cancel.Cancel };
        using var daemon = BuildDaemon(PrefillPlatform.Epic, MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)), gateway);
        if (cancelledBeforePull) await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PullAsync(daemon, cancel.Token));

        Assert.Equal(cancelledBeforePull ? 0 : 1, gateway.CountOf("CreateImage"));
        Assert.Equal(0, gateway.CountOf("InspectImage:"));
    }

    private static Task PullAsync(PrefillDaemonServiceBase daemon, CancellationToken cancellationToken) =>
        (Task)typeof(PrefillDaemonServiceBase).GetMethod("EnsureImageExistsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(daemon, [cancellationToken])!;
}
