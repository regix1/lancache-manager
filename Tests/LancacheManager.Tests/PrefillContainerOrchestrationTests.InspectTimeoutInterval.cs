using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // An image check whose first inspect runs out its limit ends with that timeout, asks the registry nothing and
    // pulls nothing, and leaves the next check for the end of the interval, so a Start right after it only inspects.
    [Fact]
    public async Task ImageCheckAfterAnInspectTimeoutWaitsForTheIntervalAsync()
    {
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.OnImageInspect = () =>
            throw new DockerTimeoutException("Docker did not answer within 10 seconds.", new OperationCanceledException());
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);

        await Assert.ThrowsAsync<DockerTimeoutException>(() => PullAsync(daemon, CancellationToken.None));
        Assert.Equal(1, gateway.CountOf("InspectImage:"));

        await Assert.ThrowsAsync<DockerTimeoutException>(() => PullAsync(daemon, CancellationToken.None));
        Assert.Equal(2, gateway.CountOf("InspectImage:"));
        Assert.Equal(0, gateway.CountOf("DistributionDigest:"));
        Assert.Empty(gateway.ImagePulls);
    }
}
