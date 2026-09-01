using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the ownership rule that keeps log processing from wedging "busy" forever. The service is a
/// singleton and the interactive path clears IsProcessing before its display delay, so a second run
/// can register its own operation while the first is still finishing. A run must therefore complete
/// the operation it registered rather than whatever the field currently holds, and must tear down
/// only the state it installed - while a run that set the busy flag and installed nothing still has
/// to clear that flag on the way out.
/// </summary>
public sealed class LogProcessingOperationOwnershipTests
{
    [Fact]
    public void OwnsOperationState_TrueWhenTheFieldStillHoldsThisRunsId()
    {
        var operationId = Guid.NewGuid();

        Assert.True(RustLogProcessorService.OwnsOperationState(operationId, operationId));
    }

    [Fact]
    public void OwnsOperationState_FalseWhenALaterRunInstalledItsOwnId()
    {
        // The interleaving this exists for: interactive run A is inside its display delay when live
        // tick B registers. A must leave B's id, cancellation source and busy flag alone.
        var interactiveId = Guid.NewGuid();
        var liveTickId = Guid.NewGuid();

        Assert.False(RustLogProcessorService.OwnsOperationState(liveTickId, interactiveId));
    }

    [Fact]
    public void OwnsOperationState_FalseWhenATerminalCleanupAlreadyClearedTheField()
    {
        // A force-kill ran the terminal cleanup, which already reset the busy flags and disposed the
        // cancellation source. Repeating that teardown would clear whatever came after it.
        Assert.False(RustLogProcessorService.OwnsOperationState(null, Guid.NewGuid()));
    }

    [Fact]
    public void OwnsOperationState_TrueWhenTheRunNeverRegisteredAnything()
    {
        // Both absent is the run that set IsProcessing and failed before registering. It owns the
        // flag it set, so it must still be allowed to clear it.
        Assert.True(RustLogProcessorService.OwnsOperationState(null, null));
    }

    [Fact]
    public async Task FailedRun_ClearsTheBusyFlagAndTheOperationIdAsync()
    {
        using var fixture = new ProcessorFixture();

        // The resolved rust executable does not exist, so the run fails after registering. Whichever
        // path it leaves by, it must not strand the busy flag: the conflict checker reads it, and a
        // stuck one blocks every other heavy operation until the process restarts.
        var started = await fixture.Processor.StartProcessingAsync(
            fixture.LogFilePath,
            silentMode: true);

        Assert.False(started);
        Assert.False(fixture.Processor.IsProcessing);
        Assert.Null(fixture.Processor.CurrentOperationId);
    }

    [Fact]
    public async Task FailedRun_LeavesNoOperationRunningAsync()
    {
        using var fixture = new ProcessorFixture();

        await fixture.Processor.StartProcessingAsync(fixture.LogFilePath, silentMode: true);

        var stillRunning = fixture.Tracker.GetActiveOperations()
            .Where(o => o.Type == OperationType.LogProcessing)
            .ToList();

        Assert.Empty(stillRunning);
    }

    private sealed class ProcessorFixture : IDisposable
    {
        private readonly string _root;

        public ProcessorFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), $"log-processing-ownership-{Guid.NewGuid():N}");
            var logPath = Path.Combine(_root, "logs");
            Directory.CreateDirectory(logPath);
            Directory.CreateDirectory(Path.Combine(_root, "cache"));

            // PathResolverProxy answers every string-returning member with <root>/<member name>, so
            // the operations directory has to exist under that name for the positions file write.
            Directory.CreateDirectory(Path.Combine(_root, "GetOperationsDirectory"));

            LogFilePath = Path.Combine(logPath, "access.log");
            File.WriteAllText(LogFilePath, string.Empty);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "default",
                    ["LanCache:DataSources:0:CachePath"] = Path.Combine(_root, "cache"),
                    ["LanCache:DataSources:0:LogPath"] = logPath,
                    ["LanCache:DataSources:0:Enabled"] = "true"
                })
                .Build();

            var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)pathResolver).Root = _root;

            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();

            Tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);

            Processor = new RustLogProcessorService(
                NullLogger<RustLogProcessorService>.Instance,
                pathResolver,
                notifications,
                CreateStateService(_root, configuration, pathResolver),
                serviceProvider: null!,
                new RustProcessHelper(
                    NullLogger<RustProcessHelper>.Instance,
                    new ProcessManager(NullLogger<ProcessManager>.Instance),
                    pathResolver,
                    Tracker),
                new DatasourceService(
                    configuration,
                    pathResolver,
                    NullLogger<DatasourceService>.Instance),
                Tracker);
        }

        public string LogFilePath { get; }
        public UnifiedOperationTracker Tracker { get; }
        public RustLogProcessorService Processor { get; }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }

        private static StateService CreateStateService(
            string root,
            IConfiguration configuration,
            IPathResolver pathResolver)
        {
            var dataProtection = DataProtectionProvider.Create(
                new DirectoryInfo(Path.Combine(root, "dp-keys")));
            var apiKeyService = new ApiKeyService(
                NullLogger<ApiKeyService>.Instance,
                configuration,
                pathResolver);
            var encryption = new SecureStateEncryptionService(
                dataProtection,
                apiKeyService,
                NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance,
                pathResolver,
                encryption);
            var state = new StateService(
                NullLogger<StateService>.Instance,
                pathResolver,
                encryption,
                steamAuthStorage);

            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(state, new AppState());
            return state;
        }
    }
}
