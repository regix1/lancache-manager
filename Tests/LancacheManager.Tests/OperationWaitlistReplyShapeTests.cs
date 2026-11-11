using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The queue's reply states its outcome once, in <c>status</c>, and every caller says what it asks for.
/// The queue is called by parameter name, so these checks hold whatever the method's other parameters
/// are. They read every property of the reply type, null ones included, so a second outcome flag beside
/// <c>status</c> fails them even when the browser's reply leaves it out for being null.
/// </summary>
public sealed class OperationWaitlistReplyShapeTests
{
    [Fact]
    public async Task EveryReplyCarriesItsOutcomeOnlyInStatusAsync()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        var queue = new OperationWaitlistService(tracker,
            OperationConflictTestServices.Create(tracker, NullLogger<OperationConflictChecker>.Instance),
            NullLogger<OperationWaitlistService>.Instance);
        Task<Guid?> StartScanAsync() =>
            Task.FromResult<Guid?>(tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource()));

        var started = await EnqueueScanAsync(queue, StartScanAsync);
        AssertReply(started, "started");

        // The scan it started is still running, so the same request is answered by it.
        var alreadyRunning = await EnqueueScanAsync(queue, StartScanAsync);
        AssertReply(alreadyRunning, "alreadyRunning");
        Assert.Equal(started.GetProperty("operationId").GetGuid(), alreadyRunning.GetProperty("operationId").GetGuid());
        tracker.CompleteOperation(started.GetProperty("operationId").GetGuid(), success: true);

        // A removal of evicted data blocks a scan, so the request waits, and the same request joins its card.
        var blocker = tracker.RegisterOperation(OperationType.EvictionRemoval, "Evicted Data Removal (all)", new CancellationTokenSource());
        var waiting = await EnqueueScanAsync(queue, StartScanAsync);
        AssertReply(waiting, "waiting");
        var joined = await EnqueueScanAsync(queue, StartScanAsync);
        AssertReply(joined, "waiting");
        Assert.Equal(waiting.GetProperty("operationId").GetGuid(), joined.GetProperty("operationId").GetGuid());

        tracker.CancelOperation(waiting.GetProperty("operationId").GetGuid());
        tracker.CompleteOperation(blocker, success: true);
    }

    [Fact]
    public void BothQueueMethodsRequireARequestKey()
    {
        foreach (var name in new[] { nameof(IOperationWaitlist.EnqueueAsync), nameof(IOperationWaitlist.StartUnlessBusyAsync) })
        {
            var parameters = typeof(IOperationWaitlist).GetMethod(name)!.GetParameters();
            var requestKey = Assert.Single(parameters, parameter => parameter.Name == "requestKey");
            Assert.False(requestKey.HasDefaultValue);
            Assert.DoesNotContain(parameters, parameter => parameter.Name == "requestSummary");
        }
    }

    private static async Task<JsonElement> EnqueueScanAsync(OperationWaitlistService queue, Func<Task<Guid?>> start)
    {
        var method = typeof(OperationWaitlistService).GetMethod(nameof(OperationWaitlistService.EnqueueAsync))!;
        var arguments = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "type" => (object?)OperationType.EvictionScan,
            "scope" => ConflictScope.Bulk(),
            "displayName" => "Eviction Scan",
            "start" => start,
            "ct" => CancellationToken.None,
            "requestKey" => parameter.ParameterType.GetProperty("Plain")!.GetValue(null),
            _ => parameter.HasDefaultValue ? parameter.DefaultValue : throw new InvalidOperationException(parameter.Name)
        }).ToArray();
        var call = (Task)method.Invoke(queue, arguments)!;
        await call;
        var reply = call.GetType().GetProperty("Result")!.GetValue(call)!;
        return JsonSerializer.SerializeToElement(reply, reply.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    // The outcome is the status alone: no flag beside it repeats what the status says.
    private static void AssertReply(JsonElement reply, string status)
    {
        Assert.Equal(status, reply.GetProperty("status").GetString());
        Assert.Equal(
            new[] { "followUpWaiting", "operationId", "skippedReason", "status" },
            reply.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }
}
