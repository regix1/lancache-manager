using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the unmappedCacheScan display-flag opt-in: the scheduled service computes the flag from
/// its effective notification mode + the run trigger, and every scan lifecycle payload carries that
/// run-stable flag on the wire as camelCase <c>showNotification</c>. Lifecycle events are always
/// emitted; the flag gates only whether the frontend shows the card.
/// </summary>
public class UnmappedScanNotificationFlagTests
{
    // Mirrors the wire: payloads serialize through the global camelCase policy.
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, true)]
    [InlineData(NotificationMode.All, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, true)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, false)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, false)]
    public void ScheduledRunStampsFlagFromModeAndTrigger(NotificationMode mode, RunTrigger trigger, bool expected)
    {
        // The exact decision the scheduled service makes once per run before threading it down.
        var show = mode.AllowsTrigger(trigger);

        Assert.Equal(expected, show);

        // All three scan phases carry that one run-stable value.
        var operationId = Guid.NewGuid();
        Assert.Equal(expected, ReadFlag(StartedPayload(operationId, show)));
        Assert.Equal(expected, ReadFlag(ProgressPayload(operationId, show)));
        Assert.Equal(expected, ReadFlag(CompletePayload(operationId, show)));
    }

    [Fact]
    public void FlagSerializesAsCamelCaseOnEveryLifecyclePayload()
    {
        var operationId = Guid.NewGuid();

        var startedJson = JsonSerializer.Serialize(StartedPayload(operationId, false), WireOptions);
        var progressJson = JsonSerializer.Serialize(ProgressPayload(operationId, false), WireOptions);
        var completeJson = JsonSerializer.Serialize(CompletePayload(operationId, true), WireOptions);

        Assert.Contains("\"showNotification\":false", startedJson);
        Assert.Contains("\"showNotification\":false", progressJson);
        Assert.Contains("\"showNotification\":true", completeJson);
    }

    [Fact]
    public void ScheduledServiceOptsIntoNotifications()
    {
        // ServiceScheduleRegistry fills supportsNotifications by reflection on this property name,
        // and ScheduleController.SetNotificationModeAsync answers 409 Conflict while it is false.
        var property = typeof(UnmappedCacheScanScheduledService).GetProperty(
            "SupportsNotifications",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);

        // The override is expression-bodied and reads no field, so an uninitialized instance is
        // enough to evaluate it without the service's collaborators.
        var service = RuntimeHelpers.GetUninitializedObject(typeof(UnmappedCacheScanScheduledService));
        Assert.True((bool)property!.GetValue(service)!);
    }

    [Fact]
    public void StartScanDefaultsToVisibleForNonScheduledCallers()
    {
        // The manual controller trigger calls StartScanAsync() with no argument, so the default is
        // what keeps a user-initiated scan's card visible.
        var parameter = typeof(UnmappedCacheService)
            .GetMethod(nameof(UnmappedCacheService.StartScanAsync))!
            .GetParameters()
            .Single(p => p.Name == "showNotification");

        Assert.Equal(typeof(bool), parameter.ParameterType);
        Assert.True(parameter.HasDefaultValue);
        Assert.Equal(true, parameter.DefaultValue);
    }

    [Fact]
    public void ActiveScanStatusReportsTheRunFlag()
    {
        // The recovery endpoint reads this property, so a page reload mid-run learns whether the
        // run was silent instead of resurrecting a card the silent terminal can never clear.
        var service = (UnmappedCacheService)RuntimeHelpers.GetUninitializedObject(typeof(UnmappedCacheService));
        SetShowNotification(service, false);

        Assert.False(service.CurrentScanShowNotification);
    }

    private static void SetShowNotification(UnmappedCacheService service, bool value)
    {
        var field = typeof(UnmappedCacheService).GetField(
            "_currentScanShowNotification",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(service, value);
    }

    private static bool ReadFlag(object payload) =>
        (bool)payload.GetType().GetProperty("ShowNotification")!.GetValue(payload)!;

    // The three shapes UnmappedCacheService emits for the scan, as anonymous objects.
    private static object StartedPayload(Guid operationId, bool showNotification) => new
    {
        OperationId = operationId,
        StageKey = "signalr.unmappedScan.starting",
        ShowNotification = showNotification
    };

    private static object ProgressPayload(Guid operationId, bool showNotification) => new
    {
        OperationId = operationId,
        StageKey = "signalr.unmappedScan.enumerating",
        Context = (IReadOnlyDictionary<string, object?>?)null,
        PercentComplete = 42.0,
        Status = OperationStatus.Running,
        ShowNotification = showNotification
    };

    private static object CompletePayload(Guid operationId, bool showNotification) => new
    {
        OperationId = operationId,
        StageKey = "signalr.unmappedScan.complete",
        Success = true,
        Cancelled = false,
        Error = (string?)null,
        Status = OperationStatus.Completed,
        PercentComplete = 100,
        ShowNotification = showNotification
    };
}
