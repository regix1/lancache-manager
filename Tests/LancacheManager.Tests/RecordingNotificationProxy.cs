using System.Reflection;

namespace LancacheManager.Tests;

/// <summary>
/// Shared <see cref="DispatchProxy"/> that records every interface call it receives (member name plus
/// arguments) so a test can assert which SignalR events were, and were not, broadcast. Return values
/// come from <see cref="NullReturningProxy"/>, so members a test never asserts on stay harmless.
/// <para>
/// Not sealed: <see cref="DispatchProxy.Create{T, TProxy}"/> generates a subtype of the proxy class
/// at runtime.
/// </para>
/// </summary>
internal class RecordingNotificationProxy : NullReturningProxy
{
    public List<(string Method, object?[] Args)> Invocations { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is not null)
        {
            Invocations.Add((targetMethod.Name, args ?? Array.Empty<object?>()));
        }

        return base.Invoke(targetMethod, args);
    }
}
