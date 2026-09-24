using LancacheManager.Core.Services;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// How a run is drawn comes only from the notice it was admitted with: every notification mode for
/// every trigger, and a run admitted with no notice at all. The registrations that deliberately carry
/// no notice, and that no other test runs, are pinned by reading their source. [89]
/// </summary>
public sealed class RunVisibilityMatrixTests
{
    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.Startup, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.RunAll, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.Startup, RunVisibility.Background)]
    [InlineData(NotificationMode.Manual, RunTrigger.RunAll, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Startup, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.RunAll, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Startup, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.RunAll, RunVisibility.Hidden)]
    [InlineData(null, null, RunVisibility.Card)]
    public void ARunIsDrawnFromItsNotice(NotificationMode? mode, RunTrigger? trigger, RunVisibility expected)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var notice = mode is { } admittedMode && trigger is { } admittedTrigger ? new RunNotice(admittedMode, admittedTrigger) : null;
        var id = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(), notice: notice);

        Assert.Same(notice, tracker.GetOperation(id)!.Notice);
        Assert.Equal(expected, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id).Visibility);
    }

    // Removals, imports and resets belong to no schedule and are always drawn as full cards, so none
    // of these registrations may pass a notice.
    [Fact]
    public void RegistrationsOfNoScheduleCarryNoNotice()
    {
        var sites = new (string File, string Declaration)[]
        {
            ("Controllers/Cache/CacheController.cs", "private async Task<bool> RunCorruptionRemovalCoreAsync("),
            ("Infrastructure/Services/System/DatabaseService.cs", "private Guid StartResetAsync(List<string> tableNames, bool fullReset)"),
            ("Controllers/System/DataMigrationController.cs", "> StartImportAsync("),
            ("Infrastructure/Services/Rust/RustLogRemovalService.cs", "private async Task<bool> StartRemovalAsync(string service)"),
            ("Infrastructure/Services/Rust/RustLogRemovalService.cs", "private async Task<bool> StartRemovalForDatasourceAsync(string service, string datasourceName)"),
            ("Core/Services/Cache/CacheReconciliationService.cs", "public async Task RemoveEvictedRecordsForEntityAsync("),
        };

        var withNotice = sites
            .Where(site => RegistrationArguments(site.File, site.Declaration).Contains("notice:", StringComparison.Ordinal))
            .Select(site => $"{site.File}: {site.Declaration}")
            .ToList();

        Assert.Empty(withNotice);
    }

    // The argument list of the first RegisterOperation call inside the method the declaration opens.
    private static string RegistrationArguments(string file, string declaration)
    {
        var source = File.ReadAllText(Path.Combine(
            [EndpointAuthorizationHost.FindRepositoryRoot(), "Api", "LancacheManager", .. file.Split('/')]));
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{declaration} not found in {file}");
        var bodyStart = source.IndexOf('{', start);
        var body = source[bodyStart..(MatchingClose(source, bodyStart, '{', '}') + 1)];
        var call = body.IndexOf("RegisterOperation(", StringComparison.Ordinal);
        Assert.True(call >= 0, $"{declaration} in {file} registers no operation");
        var open = call + "RegisterOperation".Length;
        return body[open..(MatchingClose(body, open, '(', ')') + 1)];
    }

    private static int MatchingClose(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == opening) depth++;
            else if (text[i] == closing && --depth == 0) return i;
        }

        throw new InvalidOperationException($"No closing '{closing}' for the '{opening}' at {open}");
    }
}
