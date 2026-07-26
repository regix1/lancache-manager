using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Services.SteamKit2;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;

namespace LancacheManager.Tests;

public sealed class SteamMappingConcurrencyTests
{
    [Fact]
    public async Task ManualApply_ActiveRebuildWithReporter_IsRejectedBeforeCoreMutationAsync()
    {
        var service = (SteamKit2Service)RuntimeHelpers.GetUninitializedObject(
            typeof(SteamKit2Service));
        var activeReporter = (MappingOperationReporter)RuntimeHelpers.GetUninitializedObject(
            typeof(MappingOperationReporter));

        SetPrivateField(service, "_rebuildActive", 1);
        SetPrivateField(service, "_currentMappingReporter", activeReporter);

        var error = await Assert.ThrowsAsync<ConflictException>(
            service.ManuallyApplyDepotMappingsAsync);

        Assert.Equal("A depot mapping operation is already running.", error.Message);
    }

    private static void SetPrivateField<T>(SteamKit2Service service, string name, T value)
    {
        var field = typeof(SteamKit2Service).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.");
        field.SetValue(service, value);
    }
}
