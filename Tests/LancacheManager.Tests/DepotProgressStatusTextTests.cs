using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Services.SteamKit2;

namespace LancacheManager.Tests;

/// <summary>
/// A depot import tells the browser how far it got through DepotMappingProgress. Screens draw each
/// step from its stage key through the translator, so the event carries keys and numbers only: an
/// English sentence in its status reaches a reader in any locale who opens the payload, and invites
/// a screen to print it. The import runs on the same fixture <see cref="DepotImportTests"/> uses.
/// </summary>
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class DepotProgressStatusTextTests
{
    [Fact]
    public async Task DepotProgressEventsOfAGitHubImportCarryNoEnglishStatusText()
    {
        var fixtureType = typeof(DepotImportTests).GetNestedType("Fixture", BindingFlags.NonPublic)!;
        var creating = (Task)fixtureType.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, null)!;
        await creating;
        var fixture = (IAsyncDisposable)creating.GetType().GetProperty("Result")!.GetValue(creating)!;
        await using var _ = fixture;
        await (Task)fixtureType.GetMethod("SeedAsync")!.Invoke(fixture, null)!;
        var service = (SteamKit2Service)fixtureType.GetProperty("Service")!.GetValue(fixture)!;
        var events = (DepotImportTests.RecordingNotifications)fixtureType.GetProperty("Events")!.GetValue(fixture)!;

        Assert.True(await service.ImportFromGitHubAsync());

        var progress = events.Snapshots.Where(sent => sent.Event == "DepotMappingProgress").ToList();
        Assert.NotEmpty(progress);
        var sentences = progress
            .SelectMany(sent => StatusValues(sent.Content))
            .Where(status => status.Any(char.IsWhiteSpace))
            .Distinct()
            .ToList();
        Assert.True(
            sentences.Count == 0,
            "DepotMappingProgress carries English status text: " + string.Join(" | ", sentences));
    }

    /// <summary>The event's top-level status and the status inside its context, whichever it sends.</summary>
    private static IEnumerable<string> StatusValues(JsonElement content)
    {
        foreach (var property in content.EnumerateObject())
        {
            if (property.NameEquals("Status") && property.Value.ValueKind == JsonValueKind.String)
                yield return property.Value.GetString()!;
            if (property.NameEquals("Context") && property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("status", out var inner) && inner.ValueKind == JsonValueKind.String)
                yield return inner.GetString()!;
        }
    }
}
