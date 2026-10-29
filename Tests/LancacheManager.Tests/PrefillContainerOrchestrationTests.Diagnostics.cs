using System.Reflection;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnUnansweredDockerExecDoesNotRecordAnInternetFailure(int successesBeforeFailure)
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway
        {
            ExecFailure = RecordingContainerGateway.NoAnswer(),
            ExecSuccessesBeforeFailure = successesBeforeFailure
        };
        var log = new DiagnosticsLog();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)),
            gateway,
            logger: log);

        var diagnostics = await RunContainerConnectivityAsync(daemon);

        var execCalls = gateway.Calls.Count(call => call.StartsWith("Exec", StringComparison.Ordinal));
        Assert.Equal(successesBeforeFailure + 1, execCalls);
        Assert.DoesNotContain(log.Messages, message => message.Contains("cannot reach the internet", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Messages, message => message.Contains("connectivity check timed out", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log.Messages, message => message.Contains("Not tested (Docker did not answer)", StringComparison.Ordinal));
        Assert.Null(diagnostics.InternetConnectivityIpv4);
        Assert.Null(diagnostics.InternetConnectivityIpv4Error);
        Assert.Null(diagnostics.InternetConnectivityIpv6);
        Assert.Null(diagnostics.InternetConnectivityIpv6Error);
        Assert.Empty(diagnostics.DnsResults);
        Assert.Null(diagnostics.InternetConnectivityError);
        if (successesBeforeFailure < 3)
        {
            Assert.False(diagnostics.InternetConnectivity == true);
            Assert.False(diagnostics.InternetConnectivity == false);
        }
        else
        {
            Assert.True(diagnostics.InternetConnectivity == true);
        }
    }

    [Fact]
    public async Task AFailedCommandIsKeptWhenTheNextDockerExecIsUnanswered()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway
        {
            ExecFailure = RecordingContainerGateway.NoAnswer(),
            ExecSuccessesBeforeFailure = 3,
            ExecExitCode = 1
        };
        var log = new DiagnosticsLog();
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)),
            gateway,
            logger: log);

        var diagnostics = await RunContainerConnectivityAsync(daemon);

        var execCalls = gateway.Calls.Count(call => call.StartsWith("Exec", StringComparison.Ordinal));
        Assert.Equal(4, execCalls);
        Assert.Equal(false, diagnostics.InternetConnectivity);
        Assert.Contains("exit code", diagnostics.InternetConnectivityError, StringComparison.Ordinal);
        Assert.Null(diagnostics.InternetConnectivityIpv4);
        Assert.Null(diagnostics.InternetConnectivityIpv6);
        Assert.Empty(diagnostics.DnsResults);
        Assert.False(diagnostics.DnsFinished);
        Assert.DoesNotContain(log.Messages, message => message.Contains("Not tested (Docker did not answer)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACompletedDnsSweepMarksDnsFinished()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway
        {
            ExecFailure = RecordingContainerGateway.NoAnswer(),
            ExecSuccessesBeforeFailure = 100
        };
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.KeepAcrossRestart, steamEnabled: true)),
            gateway,
            logger: new DiagnosticsLog());

        var diagnostics = await RunContainerConnectivityAsync(daemon);

        Assert.Equal(true, diagnostics.InternetConnectivity);
        Assert.Equal(2, diagnostics.DnsResults.Count);
        Assert.True(diagnostics.DnsFinished);
    }

    private static async Task<NetworkDiagnostics> RunContainerConnectivityAsync(PrefillDaemonServiceBase daemon)
    {
        var method = typeof(PrefillDaemonServiceBase).GetMethod(
            "TestContainerConnectivityAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var pending = method!.Invoke(daemon, ["container-1", "steam-daemon", false, CancellationToken.None]);
        return await (Task<NetworkDiagnostics>)pending!;
    }

    private sealed class DiagnosticsLog : ILogger<SteamDaemonService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
