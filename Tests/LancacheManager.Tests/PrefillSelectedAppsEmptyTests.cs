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

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null)
            {
                InvokedMethods.Add(targetMethod.Name);
            }

            if (targetMethod?.Name == nameof(IDaemonClient.SetSelectedAppsAsync))
            {
                LastSelectedAppIds = args?[0] as List<string>;
                return Task.CompletedTask;
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
