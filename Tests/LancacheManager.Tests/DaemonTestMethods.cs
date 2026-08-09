using System.Reflection;
using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

internal static class DaemonTestMethods
{
    internal static async Task InvokePrivateHandlerAsync(
        PrefillDaemonServiceBase daemon,
        string methodName,
        params object[] args)
    {
        var method = typeof(PrefillDaemonServiceBase).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"{methodName} not found on {nameof(PrefillDaemonServiceBase)}");
        await (Task)method.Invoke(daemon, args)!;
    }
}
