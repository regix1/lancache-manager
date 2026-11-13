using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Configuration;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The pieces a job's force stop and a later request for the same job meet in the app: the operation tracker,
/// the repair owner, the conflict checker, the operation queue the controllers enter, and the real force stop.
/// One datasource ("alpha") with an empty cache and log folder sits under a temp root. A job service is built
/// from these with whatever constructor its version declares, and one named notification send can be held:
/// a step of the job's code that does not see the stop. Shared by the tests of a force-stopped job whose code
/// still runs while the same job is asked for again.
/// </summary>
internal sealed class ForceStoppedOwnerHost : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>What the queue answered and whether the job's start was called: what a user sees.</summary>
    public const string Waits = "waiting on a card";

    private readonly OperationCancellationService _cancellation;
    private readonly ServiceProvider _container;

    private ForceStoppedOwnerHost(
        string root, UnifiedOperationTracker tracker, OperationStateService owner, OperationConflictChecker checker,
        OperationWaitlistService queue, OperationCancellationService cancellation, HeldNotifications notifications,
        ServiceProvider container)
    {
        Root = root;
        Tracker = tracker;
        _owner = owner;
        Checker = checker;
        Queue = queue;
        _cancellation = cancellation;
        Notifications = notifications;
        _container = container;
    }

    private readonly OperationStateService _owner;

    public string Root { get; }

    public UnifiedOperationTracker Tracker { get; }

    public OperationConflictChecker Checker { get; }

    public OperationWaitlistService Queue { get; }

    public HeldNotifications Notifications { get; }

    public static async Task<ForceStoppedOwnerHost> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "force-stopped-owner-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(root, "cache", "alpha");
        var logs = Path.Combine(root, "logs", "alpha");
        Directory.CreateDirectory(cache);
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "access.log"), string.Empty);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:LogPath"] = logs,
                ["LanCache:DataSources:0:CachePath"] = cache,
                ["LanCache:DataSources:0:Enabled"] = "true",
                // One log source, so the jobs that map cache keys to games are allowed to start.
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();
        var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)paths).Root = root;
        Directory.CreateDirectory(paths.GetOperationsDirectory());

        var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var state = StateTestMethods.CreateStateService(Path.Combine(root, "state"));
        state.SetSetupCompleted(true);
        var notifications = (HeldNotifications)(object)DispatchProxy.Create<ISignalRNotificationService, HeldNotifications>();
        var datasources = new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(paths);
        services.AddSingleton(processes);
        services.AddSingleton(state);
        services.AddSingleton<IStateService>(state);
        services.AddSingleton(tracker);
        services.AddSingleton<IUnifiedOperationTracker>(tracker);
        services.AddSingleton(DispatchProxy.Create<IHostApplicationLifetime, NullReturningProxy>());
        services.AddSingleton(built => new OperationStateService(
            NullLogger<OperationStateService>.Instance, configuration, state,
            built.GetRequiredService<IServiceScopeFactory>(), built.GetRequiredService<IHostApplicationLifetime>(),
            processes, tracker));
        services.AddSingleton(built => new OperationConflictChecker(
            tracker, built.GetRequiredService<OperationStateService>(), NullLogger<OperationConflictChecker>.Instance));
        services.AddSingleton<IOperationConflictChecker>(built => built.GetRequiredService<OperationConflictChecker>());
        services.AddSingleton(built => new OperationWaitlistService(
            tracker, built.GetRequiredService<IOperationConflictChecker>(), NullLogger<OperationWaitlistService>.Instance));
        services.AddSingleton<IOperationWaitlist>(built => built.GetRequiredService<OperationWaitlistService>());
        services.AddSingleton(built => new OperationCancellationService(
            tracker, processes, built.GetRequiredService<OperationStateService>(),
            NullLogger<OperationCancellationService>.Instance));
        services.AddSingleton((ISignalRNotificationService)(object)notifications);
        services.AddSingleton(datasources);
        services.AddSingleton(new DatasourceCapabilityService(datasources));
        services.AddSingleton<RustProcessHelper>();
        services.AddSingleton(CacheScanGateHarness.Idle());
        services.AddSingleton(DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>());
        var container = services.BuildServiceProvider();

        var owner = container.GetRequiredService<OperationStateService>();
        await owner.StartAsync(CancellationToken.None);
        return new ForceStoppedOwnerHost(
            root, tracker, owner, container.GetRequiredService<OperationConflictChecker>(),
            container.GetRequiredService<OperationWaitlistService>(),
            container.GetRequiredService<OperationCancellationService>(), notifications, container);
    }

    /// <summary>Builds <typeparamref name="T"/> through the longest constructor its version declares, from
    /// <paramref name="extra"/> and the host's pieces. An argument neither holds is passed as null, as the
    /// existing tests pass null for a service the case never reaches.</summary>
    public T Create<T>(params object[] extra) =>
        CreateThrough<T>(typeof(T).GetConstructors().OrderByDescending(candidate => candidate.GetParameters().Length).First(), extra);

    /// <summary>Builds <typeparamref name="T"/> through the longest constructor, public or not, that has a parameter
    /// of <paramref name="marker"/>'s type, the way <see cref="Create{T}"/> fills arguments.</summary>
    public T CreateWithParameter<T>(Type marker, params object[] extra) =>
        CreateThrough<T>(
            typeof(T).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(candidate => candidate.GetParameters().Any(parameter => parameter.ParameterType == marker))
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .First(),
            extra);

    /// <summary>The host's pieces, for a job that resolves its services itself.</summary>
    public IServiceProvider Services => _container;

    private T CreateThrough<T>(ConstructorInfo constructor, object[] extra)
    {
        var arguments = constructor.GetParameters()
            .Select(parameter =>
                extra.FirstOrDefault(parameter.ParameterType.IsInstanceOfType)
                ?? (parameter.ParameterType == typeof(IServiceProvider) ? _container : _container.GetService(parameter.ParameterType)))
            .ToArray();
        return (T)constructor.Invoke(arguments);
    }

    /// <summary>
    /// Asserts that a job asked for while a force-stopped job's code still ran waits on a card. <paramref name="asked"/>
    /// opens the failure message, so it carries its article ("A cache clear", "The corruption scan");
    /// <paramref name="stopped"/> names the stopped job.
    /// </summary>
    public static void AssertWaits(string asked, string stopped, string outcome) =>
        Assert.True(outcome == Waits,
            $"{asked} asked for while the force-stopped {stopped}'s code still ran was {outcome}; it must be " +
            $"{Waits} until that code has ended");

    /// <summary>
    /// Asserts that once the force-stopped job's code ended, the waiting card's start ran a new job.
    /// <paramref name="asked"/> names the waiting job, <paramref name="stopped"/> the stopped one.
    /// </summary>
    public static async Task AssertRunsAfterAsync(string asked, string stopped, Guid stoppedId, Task<Guid?> started)
    {
        var next = await started.WaitAsync(Timeout);
        Assert.True(next is not null,
            $"Once the force-stopped {stopped}'s code ended, the waiting {asked}'s start answered no run");
        Assert.True(next != stoppedId,
            $"Once the force-stopped {stopped}'s code ended, the waiting {asked}'s start answered {next}");
    }

    /// <summary>The force stop a user asks for from the operation card.</summary>
    public async Task ForceStopAsync(Guid operationId)
        => Assert.True(await _cancellation.ForceKillAsync(operationId), "The force stop was refused");

    /// <summary>
    /// Asks for a job through the operation queue, as its controller does, with the job's real start. Answers
    /// what the queue said and the start's result, which completes when the queue called the start: at once, or
    /// when a waiting card is promoted.
    /// </summary>
    public async Task<(string Outcome, Task<Guid?> Started)> AskThroughTheQueueAsync(
        OperationType type, ConflictScope scope, string displayName, Func<Task<Guid?>> start,
        OperationRequestKey? requestKey = null)
    {
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = Queue.EnqueueAsync(
            type,
            scope,
            displayName,
            async () =>
            {
                called.TrySetResult();
                try
                {
                    var id = await start();
                    started.TrySetResult(id);
                    return id;
                }
                catch (Exception exception)
                {
                    started.TrySetException(exception);
                    throw;
                }
            },
            CancellationToken.None,
            requestKey ?? OperationRequestKey.Plain);

        // A start the queue calls at once may itself wait on the stopped run, so the call is the answer.
        await Task.WhenAny(answer, called.Task).WaitAsync(Timeout);
        if (called.Task.IsCompleted)
        {
            return ("started at once beside the stopped run (the queue called the job's start)", started.Task);
        }

        var status = (await answer).Status;
        return (status switch
        {
            OperationStartStatus.Waiting => Waits,
            OperationStartStatus.Skipped => "skipped with nothing shown",
            _ => $"answered {status} without calling the job's start"
        }, started.Task);
    }

    public async ValueTask DisposeAsync()
    {
        Notifications.Release();
        await _owner.StopAsync(CancellationToken.None);
        await _container.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Sends every notification at once, except the first send of the event named by <see cref="Hold"/>, which
    /// returns only after <see cref="Release"/>. Not sealed, for DispatchProxy.
    /// </summary>
    internal class HeldNotifications : NullReturningProxy
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _held;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold(string eventName) => Volatile.Write(ref _held, eventName);

        public void Release() => _release.TrySetResult();

        private readonly ConcurrentDictionary<string, TaskCompletionSource> _watched = new();

        /// <summary>Completes on the next send of <paramref name="eventName"/> after this call.</summary>
        public Task WhenSent(string eventName) =>
            _watched.GetOrAdd(eventName, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args?[0] is string sent
                && _watched.TryRemove(sent, out var watcher))
            {
                watcher.TrySetResult();
            }

            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args?[0] is string eventName
                && Volatile.Read(ref _held) is { } held
                && eventName == held
                && Interlocked.CompareExchange(ref _held, null, held) == held)
            {
                Entered.TrySetResult();
                return _release.Task;
            }

            return base.Invoke(targetMethod, args);
        }
    }
}
