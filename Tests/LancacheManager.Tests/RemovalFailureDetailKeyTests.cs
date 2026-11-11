using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A removal that fails with an error nobody keyed keeps the error's text as the value of its failure key.
/// A card drawn from the run after a reload, or from the saved repair after a restart, words the failure
/// with that key, so the key's sentence has to name the text or the reason is gone from both.
/// </summary>
public sealed class RemovalFailureDetailKeyTests
{
    [Fact]
    public async Task AnUnkeyedRemovalFailureKeepsItsTextInTheSentenceTheCardShowsAsync()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var repairFinished = new TaskCompletionSource<(string? Error, RunWarning? FailureReason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var operationId = await TrackedRemovalOperationRunner.StartAsync(tracker, notifications, new RemovalOperationConfig<int>(
            OperationType: OperationType.ServiceRemoval,
            OperationLabel: "Service removal: steam",
            Target: "steam",
            Metrics: new RemovalMetrics(),
            StartedEventName: "ServiceRemovalStarted",
            BuildStarted: id => id,
            ProgressEventName: "ServiceRemovalProgress",
            InitialStageKey: "signalr.serviceRemove.starting",
            BuildInitialProgress: id => id,
            BuildProgress: (id, _) => id,
            CompleteEventName: "ServiceRemovalComplete",
            FinalizingStageKey: "signalr.serviceRemove.finalizing",
            BuildFinalizingProgress: (id, _) => id,
            BuildSuccess: (id, _) => id,
            BuildCanceled: id => id,
            BuildErrorProgress: (id, _) => id,
            BuildErrorComplete: (id, _) => new SignalRNotifications.ServiceRemovalComplete(
                false, "steam", id, "signalr.serviceRemove.error.fatal"),
            ExecuteAsync: (_, _, _) => throw new IOException("The cache disk went away"),
            BuildRepair: id => new OperationRepair
            {
                Id = id,
                Type = OperationType.ServiceRemoval,
                Name = "Service removal: steam",
                StartedAt = DateTime.UtcNow
            },
            PrepareRepairAsync: (_, _) => Task.CompletedTask,
            FinishRepairAsync: (_, _, _, error, failureReason) =>
            {
                repairFinished.TrySetResult((error, failureReason));
                return Task.CompletedTask;
            }));

        var (savedError, savedReason) = await repairFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var operation = await WaitForTerminalAsync(tracker, operationId);

        Assert.Equal(OperationStatus.Failed, operation.Status);
        var reason = Assert.IsType<RunWarning>(operation.FailureReason);
        Assert.Equal("The cache disk went away", reason.Context["errorDetail"]);
        // One key on the run, its failure reason and the saved repair: a restart restores the reason only
        // when the saved key and the reason's key are the same.
        Assert.Equal(reason.StageKey, operation.Message);
        Assert.Equal(reason.StageKey, savedError);
        Assert.Equal(reason.StageKey, Assert.IsType<RunWarning>(savedReason).StageKey);
        Assert.Equal("The cache disk went away", savedReason!.Context["errorDetail"]);

        var root = FindRepositoryRoot();
        foreach (var locale in new[] { "en", "zh" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(root, "Web", "src", "i18n", "locales", locale + ".json")));
            var sentence = Resolve(document.RootElement, reason.StageKey);
            Assert.NotNull(sentence);
            Assert.Contains("{{errorDetail}}", sentence);
        }
    }

    private static async Task<OperationInfo> WaitForTerminalAsync(UnifiedOperationTracker tracker, Guid operationId)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (tracker.GetOperation(operationId) is { } operation && operation.Status.IsTerminal())
            {
                return operation;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The removal did not end within 5 seconds.");
    }

    private static string? Resolve(JsonElement root, string key)
    {
        var current = root;
        foreach (var segment in key.Split('.'))
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
