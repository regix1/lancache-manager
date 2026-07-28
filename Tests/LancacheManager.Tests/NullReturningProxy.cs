using System.Reflection;

namespace LancacheManager.Tests;

/// <summary>
/// Shared <see cref="DispatchProxy"/> stand-in for interface members a test never exercises. Every
/// call yields a benign default for the member's return type:
/// <see cref="Task.CompletedTask"/> for <see cref="Task"/>, an already-completed
/// <c>Task&lt;T&gt;</c> carrying <c>default(T)</c> for generic tasks, a zeroed instance for
/// non-nullable value types, and <c>null</c> for everything else (including <c>void</c>).
/// <para>
/// The task cases matter: awaiting a null <see cref="Task"/> or <c>Task&lt;T&gt;</c> throws a
/// <see cref="NullReferenceException"/>, so a proxy that returned null for them would fail with an
/// unrelated error the moment a test started touching one of those members.
/// </para>
/// <para>
/// Not sealed: <see cref="DispatchProxy.Create{T, TProxy}"/> generates a subtype of the proxy class
/// at runtime.
/// </para>
/// </summary>
internal class NullReturningProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
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

        // Non-nullable value types need a concrete default; reference types and Nullable<T>
        // (e.g. double? / bool?) resolve to null.
        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }
}
