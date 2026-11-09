using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // A guest's Log in that the server refuses, here a second click while a sign-in holds the session, answers the
    // refusal's key so the guest reads the reason in their own language, not the hub's generic error sentence.
    [Fact]
    public async Task AGuestsRefusedLogInAnswersTheRefusalKeyAsync()
    {
        var (_, database) = NewDatabase();
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
        var client = new ScriptedLoginDaemonClient(challengeOnLogin: UsernameChallenge("guest-login"));
        var session = InjectedTemporarySteamSession(daemon, client);
        session.AuthState = DaemonAuthState.NotAuthenticated;
        var hub = new SteamDaemonHub(daemon, null!, NullLogger<SteamDaemonHub>.Instance)
        {
            Context = new GuestHubContext(new DefaultHttpContext(), session.UserId)
        };
        await session.LoginLock.WaitAsync(TestTimeout);

        object? answer;
        try
        {
            answer = await InvokeAsPageAsync(hub, "BeginLoginAsync", session.Id).WaitAsync(TestTimeout);
        }
        finally
        {
            session.LoginLock.Release();
        }

        var wire = JsonSerializer.SerializeToElement(answer, HubWire);
        Assert.Equal("errors.prefill.loginInProgress", wire.GetProperty("stageKey").GetString());
        Assert.Equal(session.Id, wire.GetProperty("context").GetProperty("sessionId").GetString());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("challenge").ValueKind);
        Assert.Equal(0, client.StartLoginCallCount);
    }
}
