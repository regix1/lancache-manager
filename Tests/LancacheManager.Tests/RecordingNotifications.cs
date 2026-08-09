using System.Reflection;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Tests;

internal sealed record CapturedEvent(string EventName, object? Payload);

internal class RecordingNotifications : DispatchProxy
{
    private readonly List<CapturedEvent> _events = new();

    public IReadOnlyList<CapturedEvent> Events => _events;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
        {
            _events.Add(new CapturedEvent(
                Assert.IsType<string>(args![0]), args.Length > 1 ? args[1] : null));
        }

        var returnType = targetMethod?.ReturnType;
        if (returnType is null || returnType == typeof(void)) return null;
        if (returnType == typeof(Task)) return Task.CompletedTask;
        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }
        return null;
    }
}
