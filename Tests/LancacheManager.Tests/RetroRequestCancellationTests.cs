using System.Reflection;
using System.Text;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class RetroRequestCancellationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRequestStopsBeforeTheDownloadsQuery(bool grouped)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database);
        var commands = new RecordingCommandInterceptor();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await using (var context = Context(database, commands))
        {
            var controllerLog = new RecordingLogger<DownloadsController>();
            var controller = Controller(context, controllerLog, cancelled.Token);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => controller.GetRetroDownloadsAsync(Query(grouped), cancelled.Token));
            Assert.DoesNotContain(commands.Commands, IsDownloadsCommand);
            Assert.Equal(0, controllerLog.ErrorCount);
        }

        commands.Clear();
        await using (var context = Context(database, commands))
        {
            var controllerLog = new RecordingLogger<DownloadsController>();
            var controller = Controller(context, controllerLog, cancelled.Token);
            var middlewareLog = new RecordingLogger<GlobalExceptionMiddleware>();
            var http = new DefaultHttpContext
            {
                RequestAborted = cancelled.Token,
                TraceIdentifier = "retro-cancelled"
            };
            var body = new MemoryStream();
            http.Response.Body = body;
            var middleware = new GlobalExceptionMiddleware(
                async request =>
                {
                    controller.ControllerContext = new ControllerContext { HttpContext = request };
                    await controller.GetRetroDownloadsAsync(Query(grouped), request.RequestAborted);
                },
                middlewareLog,
                Host("Production"));

            await middleware.InvokeAsync(http);

            Assert.Equal(499, http.Response.StatusCode);
            Assert.Equal(0, body.Length);
            Assert.DoesNotContain(commands.Commands, IsDownloadsCommand);
            Assert.Equal(0, middlewareLog.ErrorCount);
            Assert.Equal(0, controllerLog.ErrorCount);
        }

        commands.Clear();
        await using (var context = Context(database, commands))
        {
            var controller = Controller(context, new RecordingLogger<DownloadsController>(), CancellationToken.None);
            var action = await controller.GetRetroDownloadsAsync(Query(grouped), CancellationToken.None);
            var page = Assert.IsType<RetroDownloadResponse>(Assert.IsType<OkObjectResult>(action.Result).Value);

            Assert.NotEmpty(page.Items);
            Assert.Contains(commands.Commands, IsDownloadsCommand);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryFailureUsesTheGlobalProductionBoundary(bool grouped)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedAsync(database);
        var commands = new RecordingCommandInterceptor
        {
            OnExecuting = command =>
            {
                if (IsDownloadsCommand(command.CommandText))
                {
                    throw new InvalidOperationException("boom");
                }
            }
        };

        await using (var context = Context(database, commands))
        {
            var controllerLog = new RecordingLogger<DownloadsController>();
            var controller = Controller(context, controllerLog, CancellationToken.None);

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.GetRetroDownloadsAsync(Query(grouped), CancellationToken.None));

            Assert.Equal("boom", thrown.Message);
            Assert.Equal(0, controllerLog.ErrorCount);
        }

        await using (var context = Context(database, commands))
        {
            var controllerLog = new RecordingLogger<DownloadsController>();
            var controller = Controller(context, controllerLog, CancellationToken.None);
            var middlewareLog = new RecordingLogger<GlobalExceptionMiddleware>();
            var http = new DefaultHttpContext { TraceIdentifier = "retro-failure" };
            var body = new MemoryStream();
            http.Response.Body = body;
            var middleware = new GlobalExceptionMiddleware(
                async request =>
                {
                    controller.ControllerContext = new ControllerContext { HttpContext = request };
                    await controller.GetRetroDownloadsAsync(Query(grouped), request.RequestAborted);
                },
                middlewareLog,
                Host("Production"));

            await middleware.InvokeAsync(http);

            var json = Encoding.UTF8.GetString(body.ToArray());
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Assert.Equal(StatusCodes.Status500InternalServerError, http.Response.StatusCode);
            Assert.Equal("An unexpected error occurred", root.GetProperty("error").GetString());
            Assert.Equal("errors.http.unexpected", root.GetProperty("stageKey").GetString());
            Assert.Equal(StatusCodes.Status500InternalServerError, root.GetProperty("statusCode").GetInt32());
            Assert.Equal("retro-failure", root.GetProperty("traceId").GetString());
            Assert.False(root.TryGetProperty("details", out _));
            Assert.DoesNotContain("boom", json, StringComparison.Ordinal);
            Assert.Equal(1, middlewareLog.ErrorCount);
            Assert.Equal(0, controllerLog.ErrorCount);
        }
    }

    private static RetroDownloadQuery Query(bool grouped) => new()
    {
        GroupByGame = grouped,
        Page = 1,
        PageSize = 20
    };

    private static bool IsDownloadsCommand(string command) =>
        command.Contains("\"Downloads\"", StringComparison.Ordinal);

    private static AppDbContext Context(TestDatabase database, RecordingCommandInterceptor commands)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(commands)
            .Options;
        return new AppDbContext(options);
    }

    private static DownloadsController Controller(
        AppDbContext context,
        ILogger<DownloadsController> logger,
        CancellationToken requestAborted)
    {
        var state = DispatchProxy.Create<IStateService, TestStateProxy>();
        var controller = new DownloadsController(
            context,
            state,
            new EventsService(context, NullLogger<EventsService>.Instance),
            logger)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.RequestAborted = requestAborted;
        return controller;
    }

    private static async Task SeedAsync(TestDatabase database)
    {
        await using var context = database.Factory.CreateDbContext();
        var start = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
        context.Downloads.AddRange(
            Download("Game One", "10.0.0.1", start),
            Download("Game Two", "10.0.0.2", start.AddMinutes(10)));
        await context.SaveChangesAsync();
    }

    private static Download Download(string gameName, string clientIp, DateTime start) => new()
    {
        Service = "steam",
        ClientIp = clientIp,
        Datasource = "default",
        GameName = gameName,
        StartTimeUtc = start,
        EndTimeUtc = start.AddMinutes(5),
        CacheMissBytes = 100,
        IsActive = false
    };

    private class TestStateProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(IStateService.GetHiddenClientIps) => new List<string>(),
                "GetEvictedDataMode" => EvictedDataMode.Show.ToWireString(),
                _ => throw new NotSupportedException(targetMethod?.Name)
            };
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private int _errorCount;

        public int ErrorCount => Volatile.Read(ref _errorCount);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                Interlocked.Increment(ref _errorCount);
            }
        }
    }

    private static IHostEnvironment Host(string environmentName)
    {
        var host = DispatchProxy.Create<IHostEnvironment, HostProxy>();
        ((HostProxy)(object)host).EnvironmentName = environmentName;
        return host;
    }

    private class HostProxy : DispatchProxy
    {
        public string EnvironmentName { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_EnvironmentName"
                ? EnvironmentName
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
