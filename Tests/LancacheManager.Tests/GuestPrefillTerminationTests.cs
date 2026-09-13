using LancacheManager.Core.Services;
using System.Reflection;
using System.Net;
using Docker.DotNet;
using LancacheManager.Core.Interfaces;
using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GuestRevocation_DispatchesCleanupBeforeSavingAndClearKeepsItsCapturedOwners(bool failSave, bool clear)
    {
        var writes = new SessionWrites { HoldRevocation = true, FailRevocation = failSave };
        var (_, contexts) = NewDatabase(writes);
        var owner = await SeedGuestAsync(contexts);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var firstGateway = new RecordingContainerGateway();
        var secondGateway = new RecordingContainerGateway();
        var daemons = new PrefillDaemonServiceBase[]
        {
            new TestSteamDaemon(deps, firstGateway), new TestEpicDaemon(deps, secondGateway),
            new TestXboxDaemon(deps, new RecordingContainerGateway()), new TestBattleNetDaemon(deps, new RecordingContainerGateway()),
            new TestRiotDaemon(deps, new RecordingContainerGateway())
        };
        var setup = CreateGuestControllers(contexts, deps, daemons);
        using var services = setup.Services;
        var first = AddGuest(daemons[0], firstGateway, owner);
        var second = AddGuest(daemons[1], secondGateway, owner);
        async Task ActAsync()
        {
            if (clear) await setup.Controller.ClearGuestsAsync();
            else await setup.Controller.RevokeAsync(owner);
        }
        var action = ActAsync();
        await writes.RevocationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(firstGateway.ContainsContainer(first.ContainerId));
        Assert.False(secondGateway.ContainsContainer(second.ContainerId));
        Assert.False(action.IsCompleted);
        await Assert.ThrowsAsync<ForbiddenException>(() => daemons[0].CreateSessionAsync(owner, sessionType: SessionType.Guest));
        var newOwner = await SeedGuestAsync(contexts);
        var newSession = AddGuest(daemons[0], firstGateway, newOwner);
        writes.ReleaseRevocation.TrySetResult();
        if (failSave) await Assert.ThrowsAsync<InvalidOperationException>(() => action);
        else await action;
        Assert.Equal(!failSave, (await setup.Auth.GetSessionByIdAsync(owner))!.IsRevoked);
        Assert.False((await setup.Auth.GetSessionByIdAsync(newOwner))!.IsRevoked);
        Assert.Same(newSession, daemons[0].GetSession(newSession.Id));
        Assert.Empty(PrefillDaemonServiceBase.GuestGate.GetStarts(owner));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestCreate_StopDuringRegistrationOrPublicationCannotLeaveAnActiveRow(bool holdPublication)
    {
        var writes = new SessionWrites { HoldRegistration = !holdPublication };
        var (_, contexts) = NewDatabase(writes);
        var owner = await SeedGuestAsync(contexts);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var publication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recorder.SendHandler = async name =>
        {
            if (holdPublication && name == "DaemonSessionCreated")
            {
                publication.TrySetResult();
                await release.Task;
            }
        };
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true), notifications: notifications), gateway);
        var creating = daemon.CreateSessionAsync(owner, sessionType: SessionType.Guest);
        await (holdPublication ? publication.Task : writes.RegistrationEntered.Task).WaitAsync(TimeSpan.FromSeconds(10));
        var session = Assert.Single(daemon.GetUserSessions(owner));
        PrefillDaemonServiceBase.GuestGate.EnterStop(owner);
        try
        {
            var stopping = PrefillDaemonServiceBase.TerminateGuestSessionsAsync([daemon], owner, "Guest stopped");
            Assert.False(gateway.ContainsContainer(session.ContainerId));
            Assert.DoesNotContain("DaemonSessionTerminated", recorder.EventNames);
            Assert.False(stopping.IsCompleted);
            if (holdPublication) release.TrySetResult();
            else writes.ReleaseRegistration.TrySetResult();
            await Assert.ThrowsAsync<ForbiddenException>(() => creating);
            Assert.True((await stopping).Success);
            Assert.Equal(PrefillSessionStatus.Terminated, (await history.GetSessionAsync(session.Id))!.Status);
            Assert.Empty(daemon.GetAllSessions(includeTerminating: true));
            Assert.Equal(holdPublication ? 1 : 0, recorder.EventNames.Count(name => name == "DaemonSessionCreated"));
            Assert.Equal(1, recorder.EventNames.Count(name => name == "DaemonSessionTerminated"));
        }
        finally { PrefillDaemonServiceBase.GuestGate.ExitStop(owner); }
    }

    [Theory]
    [InlineData("revoked", false)]
    [InlineData("expired", false)]
    [InlineData("missing", false)]
    [InlineData("account", false)]
    [InlineData("persistentFlag", false)]
    [InlineData("persistentName", false)]
    [InlineData("revoked", true)]
    [InlineData("expired", true)]
    [InlineData("missing", true)]
    [InlineData("account", true)]
    [InlineData("persistentFlag", true)]
    [InlineData("persistentName", true)]
    public async Task GuestAdminAction_HistoricalIdentityRequiresAnActualGuestAndPreservesPersistentProtection(string state, bool ban)
    {
        var (_, contexts) = NewDatabase();
        var owner = state == "missing" ? Guid.NewGuid() : await SeedGuestAsync(contexts, session =>
        {
            if (state == "revoked") session.IsRevoked = true;
            if (state == "expired") session.ExpiresAtUtc = DateTime.UtcNow.AddHours(-1);
            if (state == "account") session.SessionType = SessionType.Admin;
        });
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var firstGateway = new RecordingContainerGateway();
        var secondGateway = new RecordingContainerGateway();
        using var first = new TestSteamDaemon(deps, firstGateway);
        using var second = new TestEpicDaemon(deps, secondGateway);
        using var xbox = new TestXboxDaemon(deps, new RecordingContainerGateway());
        using var battle = new TestBattleNetDaemon(deps, new RecordingContainerGateway());
        using var riot = new TestRiotDaemon(deps, new RecordingContainerGateway());
        var firstSession = AddGuest(first, firstGateway, owner);
        var secondSession = AddGuest(second, secondGateway, owner);
        var selected = Guid.NewGuid().ToString("N")[..16];
        await history.CreateSessionAsync(selected, owner, "historical", state == "persistentName" ? "steam-daemon-persistent" : "historical-guest", DateTime.UtcNow);
        if (state == "persistentFlag")
        {
            await using var database = await contexts.CreateDbContextAsync();
            var row = await database.PrefillSessions.SingleAsync(session => session.SessionId == selected);
            row.IsPersistent = true;
            await database.SaveChangesAsync();
        }
        var controller = new PrefillAdminController(history, first, second, battle, riot, xbox, deps.CacheService,
            deps.Notifications, NullLogger<PrefillAdminController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        var result = ban
            ? (ActionResult?)(await controller.BanBySessionAsync(selected, new BanRequest())).Result
            : (await controller.TerminateAsync(selected)).Result;
        if (state is "persistentFlag" or "persistentName")
        {
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Empty(await history.GetActiveBansAsync());
        }
        else Assert.IsType<OkObjectResult>(result);
        var stopsOwner = state is "revoked" or "expired";
        Assert.Equal(!stopsOwner, firstGateway.ContainsContainer(firstSession.ContainerId));
        Assert.Equal(!stopsOwner, secondGateway.ContainsContainer(secondSession.ContainerId));
    }

    [Fact]
    public async Task GuestTermination_ForceJoinDispatchesWhileGracefulShutdownIsHeld()
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var session = AddGuest(daemon, gateway, Guid.NewGuid());
        await history.CreateSessionAsync(session.Id, session.UserId, session.ContainerId, session.ContainerName, session.ExpiresAt);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((FakeReconnectDaemonClient)session.Client).ShutdownHandler = async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var graceful = daemon.TerminateSessionAsync(session.Id, "Initial reason", force: false, "first");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var forced = daemon.TerminateSessionAsync(session.Id, "Later reason", force: true, "second");
        var duplicate = daemon.TerminateSessionAsync(session.Id, force: true);
        Assert.Same(graceful, forced);
        Assert.Same(graceful, duplicate);
        Assert.Contains($"Kill:{session.ContainerId}", gateway.Calls);
        Assert.False(gateway.ContainsContainer(session.ContainerId));
        Assert.False(forced.IsCompleted);
        release.TrySetResult();
        await Task.WhenAll(graceful, forced, duplicate);
        Assert.Equal(1, gateway.CountOf("Kill:"));
        Assert.Equal(1, gateway.CountOf("Remove:"));
        Assert.Equal(0, gateway.CountOf("Stop:"));
        var row = await history.GetSessionAsync(session.Id);
        Assert.Equal("Initial reason", row!.TerminationReason);
        Assert.Equal("first", row.TerminatedBy);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("grant")]
    [InlineData("expiredGrant")]
    [InlineData("account")]
    [InlineData("otherPlatform")]
    public async Task GuestCreate_ServiceBoundaryRejectsInvalidOwner(string state)
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var owner = state == "missing" ? Guid.NewGuid() : await SeedGuestAsync(contexts, session =>
        {
            if (state == "revoked") session.IsRevoked = true;
            if (state == "expired") session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            if (state == "grant") session.SteamPrefillExpiresAtUtc = null;
            if (state == "expiredGrant") session.SteamPrefillExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
            if (state == "account") session.SessionType = SessionType.Admin;
            if (state == "otherPlatform") session.SteamPrefillExpiresAtUtc = null;
        });
        await Assert.ThrowsAsync<ForbiddenException>(() => daemon.CreateSessionAsync(owner, sessionType: SessionType.Guest));
        Assert.Equal(0, gateway.CountOf("Create:"));
        Assert.Empty(daemon.GetAllSessions());
        Assert.Empty(PrefillDaemonServiceBase.GuestGate.GetStarts(owner));
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.Xbox)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    public async Task GuestCreate_ValidPlatformGrantCreatesOneTemporarySession(PrefillPlatform platform)
    {
        var (_, contexts) = NewDatabase();
        var owner = await SeedGuestAsync(contexts);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = BuildDaemon(platform, MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var session = await daemon.CreateSessionAsync(owner, sessionType: SessionType.Guest);
        Assert.True(session.IsTemporary);
        Assert.Same(session, daemon.GetSession(session.Id));
        Assert.Equal(1, gateway.CountOf("Create:"));
        await daemon.TerminateSessionAsync(session.Id, force: true);
        Assert.True(Assert.Single(gateway.Removals).Parameters.RemoveVolumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestCreate_LateDockerResponseCannotEscapeOwnerStop(bool lateStart)
    {
        var (_, contexts) = NewDatabase();
        var owner = await SeedGuestAsync(contexts);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var gateway = new RecordingContainerGateway { HoldCreateContainer = !lateStart, CompleteStartAfterCancellation = lateStart };
        var siblingGateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(deps, gateway);
        using var sibling = new TestEpicDaemon(deps, siblingGateway);
        var existing = AddGuest(sibling, siblingGateway, owner);
        var creating = daemon.CreateSessionAsync(owner, sessionType: SessionType.Guest);
        await (lateStart ? gateway.StartContainerEntered.Task : gateway.CreateContainerEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        PrefillDaemonServiceBase.GuestGate.EnterStop(owner);
        try
        {
            var stopping = PrefillDaemonServiceBase.TerminateGuestSessionsAsync([daemon, sibling], owner, "Guest stopped");
            Assert.Contains($"Kill:{existing.ContainerId}", siblingGateway.Calls);
            Assert.False(stopping.IsCompleted);
            Assert.False(creating.IsCompleted);
            Assert.Equal(1, gateway.ContainerCount);
            if (lateStart) gateway.ReleaseStartContainer.TrySetResult();
            else gateway.ReleaseCreateContainer.TrySetResult();
            await Assert.ThrowsAsync<ForbiddenException>(() => creating);
            Assert.True((await stopping).Success);
            Assert.Empty(daemon.GetUserSessions(owner));
            Assert.Equal(0, gateway.ContainerCount);
            Assert.Equal(lateStart ? 1 : 0, gateway.CountOf("Start:"));
            if (lateStart)
                Assert.True(gateway.Calls.FindIndex(call => call.StartsWith("StartCompleted:", StringComparison.Ordinal))
                    < gateway.Calls.FindIndex(call => call.StartsWith("Remove:", StringComparison.Ordinal)));
            Assert.True(Assert.Single(gateway.Removals).Parameters.RemoveVolumes);
        }
        finally { PrefillDaemonServiceBase.GuestGate.ExitStop(owner); }
    }

    [Fact]
    public async Task GuestCreate_LostResponseFindsAndRemovesTheCreatedResource()
    {
        var (_, contexts) = NewDatabase();
        var owner = await SeedGuestAsync(contexts);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        gateway.FailCreatedContainer(new TimeoutException("Injected lost response"));
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        await Assert.ThrowsAsync<TimeoutException>(() => daemon.CreateSessionAsync(owner, sessionType: SessionType.Guest));
        Assert.Equal(0, gateway.ContainerCount);
        Assert.Equal(0, gateway.CountOf("Start:"));
        Assert.Empty(PrefillDaemonServiceBase.GuestGate.GetStarts(owner));
        Assert.True(Assert.Single(gateway.Removals).Parameters.RemoveVolumes);
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("removing")]
    [InlineData("unavailable")]
    public async Task GuestTermination_RequiresConfirmedRemoval(string failure)
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var session = AddGuest(daemon, gateway, Guid.NewGuid());
        if (failure == "kill") gateway.FailNextKillContainer(new InvalidOperationException("Injected kill failure"));
        if (failure == "removing") gateway.FailNextRemoveContainer(new DockerApiException(HttpStatusCode.Conflict, "removal already in progress"));
        if (failure == "unavailable") gateway.Reset();
        if (failure == "kill") await daemon.TerminateSessionAsync(session.Id, force: true);
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.TerminateSessionAsync(session.Id, force: true));
            Assert.Null(session.EndedAt);
            Assert.Same(session, Assert.Single(daemon.GetAllSessions(includeTerminating: true)));
            gateway.Connect(new Uri("http://localhost"));
            await daemon.TerminateSessionAsync(session.Id, force: true);
        }
        Assert.False(gateway.ContainsContainer(session.ContainerId));
        Assert.True(gateway.Removals.Last().Parameters.Force);
        Assert.True(gateway.Removals.Last().Parameters.RemoveVolumes);
    }

    [Fact]
    public async Task GuestCreate_AmbiguousOutcomeRetainsExactNameUntilExplicitRetry()
    {
        var (_, contexts) = NewDatabase();
        var owner = await SeedGuestAsync(contexts);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        gateway.FailNextCreateContainer(new TimeoutException("Injected unknown creation outcome"));
        await Assert.ThrowsAsync<TimeoutException>(() => daemon.CreateSessionAsync(owner, sessionType: SessionType.Guest));
        var start = Assert.Single(PrefillDaemonServiceBase.GuestGate.GetStarts(owner));
        Assert.True(start.CreateDispatched);
        Assert.False(start.Removed);
        Assert.Null(start.ContainerId);
        Assert.True(Directory.Exists(start.Directory));
        await Assert.ThrowsAsync<ForbiddenException>(() => daemon.CreateSessionAsync(owner, sessionType: SessionType.Guest));
        PrefillDaemonServiceBase.GuestGate.EnterStop(owner);
        try
        {
            var first = await PrefillDaemonServiceBase.TerminateGuestSessionsAsync([daemon], owner, "Guest access revoked");
            Assert.False(first.Success);
            Assert.Equal(1, first.PendingStarts);
            gateway.AddContainer(new FakeContainer { Id = "late", Name = start.ContainerName!, Running = true });
            gateway.AddContainer(new FakeContainer { Id = "decoy", Name = start.ContainerName! + "-other", Running = true });
            var second = await PrefillDaemonServiceBase.TerminateGuestSessionsAsync([daemon], owner, "Guest access revoked");
            Assert.True(second.Success);
            Assert.False(gateway.ContainsContainer("late"));
            Assert.True(gateway.ContainsContainer("decoy"));
            Assert.False(Directory.Exists(start.Directory));
            Assert.Equal(0, gateway.CountOf("Start:"));
        }
        finally { PrefillDaemonServiceBase.GuestGate.ExitStop(owner); }
    }

    [Theory]
    [InlineData("revoke", false)]
    [InlineData("delete", false)]
    [InlineData("clear", false)]
    [InlineData("terminate", false)]
    [InlineData("ban", false)]
    [InlineData("revoke", true)]
    [InlineData("delete", true)]
    [InlineData("clear", true)]
    [InlineData("terminate", true)]
    [InlineData("ban", true)]
    public async Task GuestAdminAction_StopsOnlyOwnedTemporarySessionsAndRetainsFailedCleanup(string action, bool failRemoval)
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var gateways = Enumerable.Range(0, 5).Select(_ => new RecordingContainerGateway()).ToArray();
        var daemons = new PrefillDaemonServiceBase[]
        {
            new TestSteamDaemon(deps, gateways[0]), new TestEpicDaemon(deps, gateways[1]),
            new TestXboxDaemon(deps, gateways[2]), new TestBattleNetDaemon(deps, gateways[3]),
            new TestRiotDaemon(deps, gateways[4])
        };
        var setup = CreateGuestControllers(contexts, deps, daemons);
        using var servicesRoot = setup.Services;
        var owner = new UserSession
        {
            Id = Guid.NewGuid(), SessionType = SessionType.Guest, SessionTokenHash = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        await using (var database = await contexts.CreateDbContextAsync())
        {
            database.UserSessions.Add(owner);
            await database.SaveChangesAsync();
        }
        var first = AddGuest(daemons[0], gateways[0], owner.Id);
        var second = AddGuest(daemons[1], gateways[1], owner.Id);
        var unrelated = AddGuest(daemons[1], gateways[1], Guid.NewGuid());
        var account = AddGuest(daemons[0], gateways[0], owner.Id, temporary: false);
        var persistent = AddGuest(daemons[0], gateways[0], owner.Id, persistent: true);
        await history.CreateSessionAsync(first.Id, owner.Id, first.ContainerId, first.ContainerName, first.ExpiresAt);
        await history.CreateSessionAsync(second.Id, owner.Id, second.ContainerId, second.ContainerName, second.ExpiresAt, "Epic");
        var auth = setup.Auth;
        var authController = setup.Controller;
        var prefillController = new PrefillAdminController(history, (SteamDaemonService)daemons[0], (EpicPrefillDaemonService)daemons[1],
            (BattleNetDaemonService)daemons[3], (RiotDaemonService)daemons[4], (XboxPrefillDaemonService)daemons[2],
            deps.CacheService, deps.Notifications, NullLogger<PrefillAdminController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin };
        http.RequestAborted = new CancellationToken(canceled: true);
        authController.ControllerContext = new ControllerContext { HttpContext = http };
        prefillController.ControllerContext = new ControllerContext { HttpContext = http };
        if (failRemoval)
            gateways[0].FailNextRemoveContainer(new InvalidOperationException("Injected removal failure"));

        async Task<ActionResult?> ActAsync() => action switch
        {
            "revoke" => (await authController.RevokeAsync(owner.Id)).Result,
            "delete" => (await authController.DeleteAsync(owner.Id)).Result,
            "clear" => (await authController.ClearGuestsAsync()).Result,
            "terminate" => (await prefillController.TerminateAsync(first.Id)).Result,
            "ban" => (await prefillController.BanBySessionAsync(first.Id, new BanRequest())).Result,
            _ => throw new InvalidOperationException("Unknown test action")
        };

        var result = Assert.IsAssignableFrom<ObjectResult>(await ActAsync());
        if (failRemoval)
        {
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
            var error = Assert.IsType<ErrorResponse>(result.Value);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, error.StatusCode);
            Assert.DoesNotContain("Injected", error.Error);
            Assert.True(gateways[0].ContainsContainer(first.ContainerId));
            Assert.False(gateways[1].ContainsContainer(second.ContainerId));
            if (action is "delete" or "revoke" or "clear")
                Assert.True((await auth.GetSessionByIdAsync(owner.Id))!.IsRevoked);
            if (action == "ban")
                Assert.Single(await history.GetActiveBansAsync());
            result = Assert.IsAssignableFrom<ObjectResult>(await ActAsync());
        }
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.False(gateways[0].ContainsContainer(first.ContainerId));
        Assert.False(gateways[1].ContainsContainer(second.ContainerId));
        Assert.NotNull(daemons[1].GetSession(unrelated.Id));
        Assert.NotNull(daemons[0].GetSession(account.Id));
        Assert.NotNull(daemons[0].GetSession(persistent.Id));
        if (action == "delete") Assert.Null(await auth.GetSessionByIdAsync(owner.Id));
        else if (action is "revoke" or "clear") Assert.True((await auth.GetSessionByIdAsync(owner.Id))!.IsRevoked);
        else Assert.False((await auth.GetSessionByIdAsync(owner.Id))!.IsRevoked);
    }

    [Fact]
    public async Task GuestTermination_DurableFailureRetriesWithoutRepeatingRemoval()
    {
        var writes = new SessionWrites();
        var (_, contexts) = NewDatabase(writes);
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var session = AddGuest(daemon, gateway, Guid.NewGuid());
        await history.CreateSessionAsync(session.Id, session.UserId, session.ContainerId, session.ContainerName, session.ExpiresAt);
        writes.FailTermination = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.TerminateSessionAsync(session.Id, force: true));
        Assert.False(gateway.ContainsContainer(session.ContainerId));
        Assert.Same(session, Assert.Single(daemon.GetAllSessions(includeTerminating: true)));
        Assert.Null(session.EndedAt);
        await daemon.TerminateSessionAsync(session.Id, force: true);
        Assert.Equal(1, gateway.CountOf("Kill:"));
        Assert.Equal(1, gateway.CountOf("Remove:"));
        Assert.Equal(PrefillSessionStatus.Terminated, (await history.GetSessionAsync(session.Id))!.Status);
    }

    [Fact]
    public async Task GuestTermination_SendFailureRetriesOnlyUnfinishedConnections()
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var failed = false;
        recorder.SendHandler = name =>
        {
            if (name == "second" && !failed)
            {
                failed = true;
                return Task.FromException(new InvalidOperationException("Injected terminal send failure"));
            }
            return Task.CompletedTask;
        };
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true), notifications: notifications), gateway);
        var session = AddGuest(daemon, gateway, Guid.NewGuid());
        session.SubscribedConnections.Add("first");
        session.SubscribedConnections.Add("second");
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.TerminateSessionAsync(session.Id, force: true));
        await daemon.TerminateSessionAsync(session.Id, force: true);
        Assert.Equal(1, recorder.EventNames.Count(name => name == "first"));
        Assert.Equal(2, recorder.EventNames.Count(name => name == "second"));
        Assert.Equal(1, gateway.CountOf("Remove:"));
        Assert.Equal(1, recorder.EventNames.Count(name => name == "DaemonSessionTerminated"));
    }

    [Fact]
    public async Task GuestTermination_RemovesContainerBeforePrefillWorkDrains()
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var session = AddGuest(daemon, gateway, Guid.NewGuid());
        await session.PrefillWork.WaitAsync();
        var stopping = daemon.TerminateSessionAsync(session.Id, force: true);
        try
        {
            Assert.Contains($"Kill:{session.ContainerId}", gateway.Calls);
            Assert.False(gateway.ContainsContainer(session.ContainerId));
            Assert.False(stopping.IsCompleted);
            Assert.Null(daemon.GetSession(session.Id));
            Assert.Null(session.EndedAt);
        }
        finally { session.PrefillWork.Release(); }
        await stopping;
        Assert.Empty(daemon.GetAllSessions(includeTerminating: true));
        Assert.Equal(DaemonSessionStatus.Terminated, session.Status);
    }

    [Fact]
    public async Task GuestTermination_FailedRemovalRetainsIdentityUntilExplicitRetry()
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var session = AddGuest(daemon, gateway, Guid.NewGuid());
        gateway.FailNextRemoveContainer(new InvalidOperationException("Injected removal failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.TerminateSessionAsync(session.Id, force: true));
        Assert.Null(daemon.GetSession(session.Id));
        Assert.Empty(daemon.GetUserSessions(session.UserId));
        Assert.Empty(daemon.GetAllSessions());
        Assert.Same(session, Assert.Single(daemon.GetAllSessions(includeTerminating: true)));
        Assert.Equal(DaemonSessionStatus.Error, session.Status);
        Assert.Null(session.EndedAt);
        Assert.False(((FakeReconnectDaemonClient)session.Client).Disposed);
        Assert.True(gateway.ContainsContainer(session.ContainerId));
        await daemon.TerminateSessionAsync(session.Id, force: true);
        Assert.False(gateway.ContainsContainer(session.ContainerId));
        Assert.Empty(daemon.GetAllSessions(includeTerminating: true));
        Assert.True(((FakeReconnectDaemonClient)session.Client).Disposed);
    }

    [Fact]
    public async Task GuestTermination_ConcurrentCallsJoinOneClaim()
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), gateway);
        var session = AddGuest(daemon, gateway, Guid.NewGuid());
        await session.PrefillWork.WaitAsync();
        var first = daemon.TerminateSessionAsync(session.Id, force: true);
        var second = daemon.TerminateSessionAsync(session.Id, force: true);
        Assert.Same(first, second);
        Assert.Equal(1, gateway.CountOf("Kill:"));
        session.PrefillWork.Release();
        await Task.WhenAll(first, second);
        Assert.Equal(1, gateway.CountOf("Remove:"));
    }

    [Fact]
    public async Task GuestOwnerStop_DispatchesEveryPlatformBeforeWaitingForWork()
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var firstGateway = new RecordingContainerGateway();
        var secondGateway = new RecordingContainerGateway();
        using var first = new TestSteamDaemon(deps, firstGateway);
        using var second = new TestEpicDaemon(deps, secondGateway);
        var owner = Guid.NewGuid();
        var firstSession = AddGuest(first, firstGateway, owner);
        var secondSession = AddGuest(second, secondGateway, owner);
        var unrelated = AddGuest(second, secondGateway, Guid.NewGuid());
        var account = AddGuest(first, firstGateway, owner, temporary: false);
        var persistent = AddGuest(first, firstGateway, owner, persistent: true);
        await firstSession.PrefillWork.WaitAsync();
        PrefillDaemonServiceBase.GuestGate.EnterStop(owner);
        try
        {
            var stop = PrefillDaemonServiceBase.TerminateGuestSessionsAsync([first, second], owner, "Guest access revoked");
            Assert.Contains($"Kill:{firstSession.ContainerId}", firstGateway.Calls);
            Assert.Contains($"Kill:{secondSession.ContainerId}", secondGateway.Calls);
            Assert.False(stop.IsCompleted);
            Assert.NotNull(second.GetSession(unrelated.Id));
            Assert.NotNull(first.GetSession(account.Id));
            Assert.NotNull(first.GetSession(persistent.Id));
            firstSession.PrefillWork.Release();
            Assert.True((await stop).Success);
        }
        finally { PrefillDaemonServiceBase.GuestGate.ExitStop(owner); }
    }

    [Fact]
    public void GuestGate_StoppedInvocationStaysStoppedAcrossConcurrentStopLeases()
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        using var daemon = new TestSteamDaemon(MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true)), new RecordingContainerGateway());
        var gate = new GuestPrefillGate();
        var owner = Guid.NewGuid();
        var start = gate.EnterCreate(owner, daemon);
        gate.EnterStop(owner);
        gate.EnterStop(owner);
        gate.ExitStop(owner);
        Assert.Throws<ForbiddenException>(() => gate.EnterCreate(owner, daemon));
        gate.ExitStop(owner);
        Assert.Throws<ForbiddenException>(() => gate.Check(start));
        gate.FinishCreate(start, cleanupPending: false);
        var next = gate.EnterCreate(owner, daemon);
        gate.Check(next);
        gate.FinishCreate(next, cleanupPending: false);
    }

    private static DaemonSession AddGuest(PrefillDaemonServiceBase daemon, RecordingContainerGateway gateway,
        Guid owner, bool temporary = true, bool persistent = false)
    {
        var id = Guid.NewGuid().ToString("N")[..16];
        var session = new DaemonSession
        {
            Id = id, UserId = owner, ContainerId = id,
            ContainerName = persistent ? "steam-daemon-persistent" : "guest-" + id,
            IsTemporary = temporary, IsPersistent = persistent,
            Client = new FakeReconnectDaemonClient(), ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        gateway.AddContainer(new FakeContainer { Id = id, Name = session.ContainerName, Running = true });
        SessionsOf(daemon)[id] = session;
        return session;
    }

    private sealed class SessionWrites : SaveChangesInterceptor
    {
        public bool FailTermination { get; set; }
        public bool HoldRegistration { get; set; }
        public TaskCompletionSource RegistrationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRegistration { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldRevocation { get; set; }
        public bool FailRevocation { get; set; }
        public TaskCompletionSource RevocationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRevocation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventContext, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (HoldRevocation && eventContext.Context!.ChangeTracker.Entries<UserSession>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.IsRevoked))
            {
                RevocationEntered.TrySetResult();
                await ReleaseRevocation.Task;
                if (FailRevocation)
                    throw new InvalidOperationException("Injected revocation failure");
            }
            if (HoldRegistration && eventContext.Context!.ChangeTracker.Entries<PrefillSession>()
                .Any(entry => entry.State == EntityState.Added))
            {
                RegistrationEntered.TrySetResult();
                await ReleaseRegistration.Task;
            }
            if (FailTermination && eventContext.Context!.ChangeTracker.Entries<PrefillSession>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.Status == PrefillSessionStatus.Terminated))
            {
                FailTermination = false;
                throw new InvalidOperationException("Injected session persistence failure");
            }
            return result;
        }
    }

    private static async Task<Guid> SeedGuestAsync(IDbContextFactory<AppDbContext> contexts, Action<UserSession>? change = null)
    {
        var guest = new UserSession
        {
            Id = Guid.NewGuid(), SessionType = SessionType.Guest, SessionTokenHash = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            SteamPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(1), EpicPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            XboxPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(1), BattleNetPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(1),
            RiotPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        };
        change?.Invoke(guest);
        await using var context = await contexts.CreateDbContextAsync();
        context.UserSessions.Add(guest);
        await context.SaveChangesAsync();
        return guest.Id;
    }

    private static (ServiceProvider Services, SessionService Auth, SessionsController Controller) CreateGuestControllers(
        IDbContextFactory<AppDbContext> contexts, DaemonDeps deps, PrefillDaemonServiceBase[] daemons)
    {
        var services = new ServiceCollection();
        services.AddSingleton((SteamDaemonService)daemons[0]);
        services.AddSingleton((EpicPrefillDaemonService)daemons[1]);
        services.AddSingleton((XboxPrefillDaemonService)daemons[2]);
        services.AddSingleton((BattleNetDaemonService)daemons[3]);
        services.AddSingleton((RiotDaemonService)daemons[4]);
        var root = services.BuildServiceProvider();
        var auth = new SessionService(contexts, null!, NullLogger<SessionService>.Instance, null!, deps.Notifications, deps.Configuration);
        var controller = new SessionsController(auth, deps.Notifications, root.GetRequiredService<IServiceScopeFactory>(), null!);
        var http = new DefaultHttpContext();
        http.Items["Session"] = new UserSession { Id = Guid.NewGuid(), SessionType = SessionType.Admin };
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (root, auth, controller);
    }
}
