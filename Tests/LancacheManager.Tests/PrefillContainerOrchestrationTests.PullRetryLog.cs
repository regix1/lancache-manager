using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // A pull attempt that fails and is then recovered by the retry is not a problem the administrator
    // has to read about, so the console shows no warning and no stack trace for it. A pull that fails on
    // every attempt is still reported, once. The shared logger is named in full because this class nests
    // its own CapturingLogger<T>, which does not keep the exception a console line prints.
    [Fact]
    public async Task PullThatRecoversOnRetryLogsNoWarningAsync()
    {
        var (_, recoveredDatabase) = NewDatabase();
        var recoveredSessions = new PrefillSessionService(recoveredDatabase, NullLogger<PrefillSessionService>.Instance);
        var recoveredGateway = new RecordingContainerGateway();
        recoveredGateway.ImagePullFailures.Enqueue(new IOException("The connection to the registry was reset."));
        var recoveredLog = new global::LancacheManager.Tests.CapturingLogger<SteamDaemonService>();
        using (var daemon = new TestSteamDaemon(
            MakeDeps(recoveredDatabase, recoveredSessions, Config(PersistenceMode.FullPersistence, true)),
            recoveredGateway,
            logger: recoveredLog))
        {
            await PullAsync(daemon, CancellationToken.None);
        }

        Assert.Empty(recoveredGateway.ImagePullFailures);
        var recoveredNoise = recoveredLog.Entries
            .Where(entry => entry.Level >= LogLevel.Warning || (entry.Level >= LogLevel.Information && entry.Exception is not null))
            .ToList();
        Assert.True(recoveredNoise.Count == 0,
            "A pull that failed once and then succeeded on retry printed to the console: "
            + string.Join(" | ", recoveredNoise.Select(entry => $"{entry.Level} '{entry.Message}' exception={entry.Exception?.GetType().Name}")));

        var (_, failedDatabase) = NewDatabase();
        var failedSessions = new PrefillSessionService(failedDatabase, NullLogger<PrefillSessionService>.Instance);
        var failedGateway = new RecordingContainerGateway();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            failedGateway.ImagePullFailures.Enqueue(new IOException("The connection to the registry was reset."));
        }
        var failedLog = new global::LancacheManager.Tests.CapturingLogger<SteamDaemonService>();
        using (var daemon = new TestSteamDaemon(
            MakeDeps(failedDatabase, failedSessions, Config(PersistenceMode.FullPersistence, true)),
            failedGateway,
            logger: failedLog))
        {
            await PullAsync(daemon, CancellationToken.None);
        }

        Assert.Empty(failedGateway.ImagePullFailures);
        var failedReports = failedLog.Entries.Where(entry => entry.Level >= LogLevel.Warning).ToList();
        Assert.True(failedReports.Count == 1,
            $"A pull that failed on every attempt printed {failedReports.Count} warnings or errors instead of one: "
            + string.Join(" | ", failedReports.Select(entry => $"{entry.Level} '{entry.Message}'")));
    }

    // The recurring persistent image check, with no image on the host and a pull that fails every attempt,
    // prints the failure's stack trace once; any other line it keeps names the outcome without the exception.
    // The failed pull's Error line still says why the pull failed.
    [Fact]
    public async Task FailedPersistentImageCheckWithNoCachedImagePrintsOneStackTraceAsync()
    {
        const string pullFailure = "The connection to the registry was reset.";
        var (_, database) = NewDatabase();
        var sessions = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway { ImageExists = false };
        for (var attempt = 0; attempt < 3; attempt++)
        {
            gateway.ImagePullFailures.Enqueue(new IOException(pullFailure));
        }
        var daemonLog = new global::LancacheManager.Tests.CapturingLogger<SteamDaemonService>();
        var checkLog = new global::LancacheManager.Tests.CapturingLogger<PersistentContainerImageService>();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessions, Config(PersistenceMode.FullPersistence, true)),
            gateway,
            logger: daemonLog);
        InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        using var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .BuildServiceProvider();
        var check = new PersistentContainerImageService(
            checkLog,
            new ConfigurationBuilder().Build(),
            services.GetRequiredService<IServiceScopeFactory>());

        await check.RunOnceAsync(CancellationToken.None);

        Assert.Empty(gateway.ImagePullFailures);
        var stackTraces = daemonLog.Entries.Concat(checkLog.Entries)
            .Where(entry => entry.Level >= LogLevel.Warning && entry.Exception is not null)
            .ToList();
        Assert.True(stackTraces.Count == 1,
            $"One failed persistent image check with no cached image printed {stackTraces.Count} stack traces instead of one: "
            + string.Join(" | ", stackTraces.Select(entry => $"{entry.Level} '{entry.Message}' exception={entry.Exception?.GetType().Name}")));

        var pullErrors = daemonLog.Entries
            .Where(entry => entry.Level == LogLevel.Error
                && entry.Message.Contains("Failed to pull image", StringComparison.Ordinal)
                && entry.Message.Contains("no cached version is available", StringComparison.Ordinal))
            .ToList();
        var pullError = Assert.Single(pullErrors);
        Assert.True(pullError.Message.Contains(pullFailure, StringComparison.Ordinal),
            $"The failed pull's Error line does not say why the pull failed ('{pullFailure}'): '{pullError.Message}'");
    }
}
