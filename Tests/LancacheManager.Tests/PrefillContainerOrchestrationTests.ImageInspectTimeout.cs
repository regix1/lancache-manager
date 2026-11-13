using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // Docker stops answering, so every image inspect runs out its time limit. A Start's image check then ends with
    // the error after one limit: Docker already showed it does not answer, so the check does not wait out a second.
    [Fact]
    public async Task ImageCheckOnADockerThatStoppedAnsweringWaitsOutOneInspectLimitAsync()
    {
        const int inspectLimitSeconds = 10;
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var limitsWaited = 0;
        gateway.OnImageInspect = () =>
        {
            Interlocked.Increment(ref limitsWaited);
            throw new DockerTimeoutException(
                $"Docker did not answer within {inspectLimitSeconds} seconds.", new OperationCanceledException());
        };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway);

        await Assert.ThrowsAnyAsync<Exception>(() => PullAsync(daemon, CancellationToken.None));

        Assert.True(limitsWaited == 1,
            $"The image check on a Docker that stopped answering waited {limitsWaited * inspectLimitSeconds} s "
            + $"({limitsWaited} inspect limits) before its error instead of {inspectLimitSeconds} s.");
    }
}
