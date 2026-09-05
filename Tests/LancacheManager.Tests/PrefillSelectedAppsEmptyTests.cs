using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Saving an empty app selection is the UI's "clear selection" action and must be accepted, not
/// rejected as a malformed request; only a missing/null AppIds list is a real 400.
/// </summary>
public class PrefillSelectedAppsEmptyTests
{
    [Fact]
    public async Task SetSelectedAppsAsync_EmptyList_SavesAndForwardsEmptySelectionToDaemon()
    {
        var (controller, client) = CreateControllerWithOwnedSession(out var sessionId);

        var result = await controller.SetSelectedAppsAsync(
            sessionId,
            new SetSelectedAppsRequest { AppIds = new List<string>() });

        Assert.IsType<OkObjectResult>(result);
        Assert.Contains(nameof(IDaemonClient.SetSelectedAppsAsync), client.InvokedMethods);
        Assert.NotNull(client.LastSelectedAppIds);
        Assert.Empty(client.LastSelectedAppIds!);
    }

    [Fact]
    public async Task SetSelectedAppsAsync_NullAppIds_ReturnsBadRequest()
    {
        var (controller, client) = CreateControllerWithOwnedSession(out var sessionId);

        var result = await controller.SetSelectedAppsAsync(
            sessionId,
            new SetSelectedAppsRequest { AppIds = null });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.DoesNotContain(nameof(IDaemonClient.SetSelectedAppsAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task PersistentStart_EmptyList_ClearsSelectionBeforeStartingAsync()
    {
        var (controller, daemon, client, sessionId) = CreatePersistentController();

        var response = await controller.StartPrefillAsync(
            new PersistentStartPrefillRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = []
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.True(daemon.PersistentEditSessionGate.TryEnterMutation(out var releasedMutation));
        await releasedMutation!.DisposeAsync();
        Assert.Equal(
            [nameof(IDaemonClient.SetSelectedAppsAsync), nameof(IDaemonClient.PrefillAsync)],
            client.InvokedMethods.Where(name =>
                name is nameof(IDaemonClient.SetSelectedAppsAsync) or nameof(IDaemonClient.PrefillAsync)));
        Assert.NotNull(client.LastSelectedAppIds);
        Assert.Empty(client.LastSelectedAppIds);
    }

    [Fact]
    public async Task PersistentStart_NullList_PreservesSelectionAsync()
    {
        var (controller, _, client, sessionId) = CreatePersistentController();

        var response = await controller.StartPrefillAsync(
            new PersistentStartPrefillRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = null
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(response.Result);
        Assert.DoesNotContain(nameof(IDaemonClient.SetSelectedAppsAsync), client.InvokedMethods);
        Assert.Contains(nameof(IDaemonClient.PrefillAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task PersistentMutations_ReturnConflictWhileTheDaemonClaimIsHeldAsync()
    {
        var (controller, daemon, client, sessionId) = CreatePersistentController();
        Assert.True(daemon.PersistentEditSessionGate.TryEnterMutation(out var heldMutation));

        await using (heldMutation!)
        {
            var selection = await controller.SetSelectedAppsAsync(
                new PersistentSelectedAppsRequest
                {
                    Service = PrefillPlatform.Steam,
                    SessionId = sessionId,
                    AppIds = ["10"]
                },
                CancellationToken.None);
            var start = await controller.StartPrefillAsync(
                new PersistentStartPrefillRequest
                {
                    Service = PrefillPlatform.Steam,
                    SessionId = sessionId,
                    AppIds = ["20"]
                },
                CancellationToken.None);

            Assert.IsType<ConflictObjectResult>(selection);
            Assert.IsType<ConflictObjectResult>(start.Result);
        }

        Assert.DoesNotContain(nameof(IDaemonClient.SetSelectedAppsAsync), client.InvokedMethods);
        Assert.DoesNotContain(nameof(IDaemonClient.PrefillAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task PersistentSelectionCannotInterleaveBetweenStartSelectionAndAcknowledgementAsync()
    {
        var (controller, _, client, sessionId) = CreatePersistentController();
        client.BlockSelection = true;

        var startTask = controller.StartPrefillAsync(
            new PersistentStartPrefillRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = ["first"]
            },
            CancellationToken.None);
        await client.SelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var interleavingSelection = await controller.SetSelectedAppsAsync(
            new PersistentSelectedAppsRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = ["interleaver"]
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(interleavingSelection);
        client.SelectionRelease.TrySetResult();
        var start = await startTask;

        Assert.IsType<OkObjectResult>(start.Result);
        Assert.Equal(
            [nameof(IDaemonClient.SetSelectedAppsAsync), nameof(IDaemonClient.PrefillAsync)],
            client.InvokedMethods.Where(name =>
                name is nameof(IDaemonClient.SetSelectedAppsAsync) or nameof(IDaemonClient.PrefillAsync)));
        Assert.Equal(["first"], client.LastSelectedAppIds);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task PersistentStart_RechecksBusyStateBeforeChangingSelectionAsync(
        bool isPrefilling,
        bool loginPending,
        bool startPending)
    {
        var (controller, daemon, client, sessionId) = CreatePersistentController();
        var session = daemon.GetSession(sessionId)!;
        session.IsPrefilling = isPrefilling;
        session.LoginOperationId = loginPending ? Guid.NewGuid() : null;
        if (startPending)
        {
            var start = daemon.PersistentEditSessionGate.BeginStart("edit", "start");
            Assert.True(start.Accepted);
            Assert.True(start.IsOwner);
        }

        var response = await controller.StartPrefillAsync(
            new PersistentStartPrefillRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = ["secret-app-id"]
            },
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.DoesNotContain("secret-app-id", Assert.IsType<ErrorResponse>(conflict.Value).Error);
        Assert.DoesNotContain(nameof(IDaemonClient.SetSelectedAppsAsync), client.InvokedMethods);
        Assert.DoesNotContain(nameof(IDaemonClient.PrefillAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task PersistentSelectionRejectsChangesWhilePrefillIsRunningAsync()
    {
        var (controller, daemon, client, sessionId) = CreatePersistentController();
        daemon.GetSession(sessionId)!.IsPrefilling = true;

        var response = await controller.SetSelectedAppsAsync(
            new PersistentSelectedAppsRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = ["replacement"]
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response);
        Assert.DoesNotContain(nameof(IDaemonClient.SetSelectedAppsAsync), client.InvokedMethods);
    }

    [Fact]
    public async Task StaleRunCannotCancelANewerRunOnTheSamePersistentSessionAsync()
    {
        var (controller, daemon, client, sessionId) = CreatePersistentController();

        var firstResponse = await controller.StartPrefillAsync(
            new PersistentStartPrefillRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = ["first"]
            },
            CancellationToken.None);
        var first = Assert.IsType<PrefillResult>(
            Assert.IsType<OkObjectResult>(firstResponse.Result).Value);
        Assert.NotNull(first.RunId);

        daemon.GetSession(sessionId)!.IsPrefilling = false;
        var secondResponse = await controller.StartPrefillAsync(
            new PersistentStartPrefillRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                AppIds = ["second"]
            },
            CancellationToken.None);
        var second = Assert.IsType<PrefillResult>(
            Assert.IsType<OkObjectResult>(secondResponse.Result).Value);
        Assert.NotNull(second.RunId);
        Assert.NotEqual(first.RunId, second.RunId);

        var staleCancel = await controller.CancelPrefillAsync(
            new PersistentServiceRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                RunId = first.RunId
            },
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(staleCancel);
        Assert.DoesNotContain(nameof(IDaemonClient.CancelPrefillAsync), client.InvokedMethods);

        var currentCancel = await controller.CancelPrefillAsync(
            new PersistentServiceRequest
            {
                Service = PrefillPlatform.Steam,
                SessionId = sessionId,
                RunId = second.RunId
            },
            CancellationToken.None);

        Assert.IsType<OkResult>(currentCancel);
        Assert.Contains(nameof(IDaemonClient.CancelPrefillAsync), client.InvokedMethods);
    }

    // ---- Fixtures -------------------------------------------------------------------------------

    private static (SteamDaemonController Controller, RecordingDaemonClientProxy Client) CreateControllerWithOwnedSession(
        out string sessionId)
    {
        sessionId = $"session-{Guid.NewGuid():N}";
        var userId = Guid.NewGuid();

        var daemon = CreateDaemon();
        var client = DispatchProxy.Create<IDaemonClient, RecordingDaemonClientProxy>();
        var recorder = (RecordingDaemonClientProxy)client;

        var session = new DaemonSession
        {
            Id = sessionId,
            UserId = userId,
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = client
        };
        daemon.InjectSession(session);

        var controller = new SteamDaemonController(
            daemon,
            NullLogger<SteamDaemonController>.Instance,
            stateService: null!,
            userPreferencesService: null!);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = new UserSession { Id = userId };
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        return (controller, recorder);
    }

    private static (
        PersistentPrefillController Controller,
        TestableSteamDaemonService Daemon,
        RecordingDaemonClientProxy Client,
        string SessionId) CreatePersistentController()
    {
        var daemon = CreateDaemon();
        var client = DispatchProxy.Create<IDaemonClient, RecordingDaemonClientProxy>();
        var recorder = (RecordingDaemonClientProxy)client;
        var sessionId = $"persistent-{Guid.NewGuid():N}";
        daemon.InjectSession(new DaemonSession
        {
            Id = sessionId,
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            IsPersistent = true,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = client
        });

        var services = new ServiceCollection()
            .AddSingleton<SteamDaemonService>(daemon)
            .BuildServiceProvider();
        var controller = new PersistentPrefillController(
            services,
            stateService: null!,
            cacheService: null!,
            NullLogger<PersistentPrefillController>.Instance);

        return (controller, daemon, recorder, sessionId);
    }

    private static TestableSteamDaemonService CreateDaemon()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"selected_apps_empty_{Guid.NewGuid():N}")
            .Options;
        var dbFactory = new TestDbContextFactory(dbOptions);
        var sessionService = new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance);
        var cacheService = new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        var networkOptions = new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions());

        return new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance, notifications, configuration, pathResolver,
            stateService, sessionService, cacheService, networkOptions);
    }

    /// <summary>
    /// Records every <see cref="IDaemonClient"/> method invoked and captures the app-id list passed
    /// to <see cref="IDaemonClient.SetSelectedAppsAsync"/> so a test can prove an empty selection
    /// reaches the daemon unchanged.
    /// </summary>
    // Not sealed: DispatchProxy.Create returns an instance that is both IDaemonClient and this proxy,
    // and the (RecordingDaemonClientProxy)client cast a test uses is only legal when the target class
    // is not sealed.
    private class RecordingDaemonClientProxy : DispatchProxy
    {
        public List<string> InvokedMethods { get; } = new();
        public List<string>? LastSelectedAppIds { get; private set; }
        public bool BlockSelection { get; set; }
        public TaskCompletionSource SelectionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SelectionRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null)
            {
                InvokedMethods.Add(targetMethod.Name);
            }

            if (targetMethod?.Name == nameof(IDaemonClient.SetSelectedAppsAsync))
            {
                LastSelectedAppIds = args?[0] as List<string>;
                SelectionStarted.TrySetResult();
                return BlockSelection ? SelectionRelease.Task : Task.CompletedTask;
            }

            if (targetMethod?.Name == nameof(IDaemonClient.PrefillAsync))
            {
                return Task.FromResult(new PrefillResult { Success = true });
            }

            return DefaultReturnValue(targetMethod);
        }

        private static object? DefaultReturnValue(MethodInfo? targetMethod)
        {
            var returnType = targetMethod?.ReturnType;

            if (returnType is null || returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returnType.GetGenericArguments()[0];
                var value = inner.IsValueType && Nullable.GetUnderlyingType(inner) is null
                    ? Activator.CreateInstance(inner)
                    : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
            }

            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}
