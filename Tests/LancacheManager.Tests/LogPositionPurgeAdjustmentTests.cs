using System.Text.Json;
using LancacheManager.Core.Services;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// A log purge rewrites the access log in place, so every saved line index past the removed
/// lines points into content the ingester has never read. These tests pin the compensation:
/// the saved position comes back by the ALREADY-READ removals (first map), the on-disk
/// total-line count comes down by ALL removals (second map), both clamped at zero.
/// </summary>
public class LogPositionPurgeAdjustmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lancache-tests", Guid.NewGuid().ToString("N"));

    public LogPositionPurgeAdjustmentTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static Dictionary<string, long> Map(params (string Stem, long Removed)[] entries) =>
        entries.ToDictionary(e => e.Stem, e => e.Removed);

    [Fact]
    public void Position_moves_by_the_already_read_subset_and_total_by_all_removals()
    {
        var state = CreateStateService(_root);
        state.SetLogSourcePositions("Default", new Dictionary<string, long>
        {
            ["access.log"] = 250_590,
            ["steam-access.log"] = 1_000
        });
        state.SetLogTotalLines("Default", 251_590);

        // 41 lines purged in total, but only 30 sat below the read position; the other 11
        // were never read, so the position must not move for them.
        state.ReduceLogPositionsAfterPurge(
            "Default",
            Map(("access.log", 30)),
            Map(("access.log", 41)));

        var positions = state.GetLogSourcePositions("Default");
        Assert.Equal(250_560, positions["access.log"]);
        Assert.Equal(1_000, positions["steam-access.log"]);
        Assert.Equal(251_549, state.GetLogTotalLines("Default"));
    }

    [Fact]
    public void Over_subtraction_clamps_to_zero_instead_of_going_negative()
    {
        var state = CreateStateService(_root);
        state.SetLogSourcePositions("Default", new Dictionary<string, long> { ["access.log"] = 5 });
        state.SetLogTotalLines("Default", 5);

        state.ReduceLogPositionsAfterPurge(
            "Default",
            Map(("access.log", 50)),
            Map(("access.log", 50)));

        Assert.Equal(0, state.GetLogSourcePositions("Default")["access.log"]);
        Assert.Equal(0, state.GetLogTotalLines("Default"));
    }

    [Fact]
    public void An_empty_or_zero_map_changes_nothing()
    {
        var state = CreateStateService(_root);
        state.SetLogSourcePositions("Default", new Dictionary<string, long> { ["access.log"] = 77 });
        state.SetLogTotalLines("Default", 77);

        state.ReduceLogPositionsAfterPurge("Default", Map(), Map());
        state.ReduceLogPositionsAfterPurge("Default", Map(("access.log", 0)), Map(("access.log", 0)));

        Assert.Equal(77, state.GetLogSourcePositions("Default")["access.log"]);
        Assert.Equal(77, state.GetLogTotalLines("Default"));
    }

    [Fact]
    public void A_stem_with_no_saved_position_still_reduces_the_total()
    {
        // A purge can touch a stem whose checkpoint was already cleared; the total-line count
        // still shrank on disk, so it must still come down.
        var state = CreateStateService(_root);
        state.SetLogSourcePositions("Default", new Dictionary<string, long> { ["access.log"] = 10 });
        state.SetLogTotalLines("Default", 30);

        state.ReduceLogPositionsAfterPurge(
            "Default",
            Map(("riot-access.log", 20)),
            Map(("riot-access.log", 20)));

        Assert.Equal(10, state.GetLogSourcePositions("Default")["access.log"]);
        Assert.Equal(10, state.GetLogTotalLines("Default"));
    }

    [Fact]
    public void The_rust_report_json_deserializes_into_both_maps()
    {
        // Crosses the Rust/C# boundary with the literal field names the binaries serialize.
        // A silently renamed field deserializes to an empty map and turns the whole position
        // fix into a green-tested no-op, which is exactly what this pins against.
        const string reportJson = """
            {
              "game_app_id": 4000,
              "game_name": "Garry's Mod",
              "cache_files_deleted": 2,
              "total_bytes_freed": 963302,
              "empty_dirs_removed": 0,
              "log_entries_removed": 41,
              "log_lines_removed_by_source": { "access.log": 41 },
              "log_lines_removed_before_position_by_source": { "access.log": 30 },
              "depot_ids": [4000]
            }
            """;

        var report = JsonSerializer.Deserialize<CacheManagementService.GameCacheRemovalReport>(reportJson)!;

        Assert.Equal(41, report.LogLinesRemovedBySource["access.log"]);
        Assert.Equal(30, report.LogLinesRemovedBeforePositionBySource["access.log"]);

        var state = CreateStateService(_root);
        state.SetLogSourcePositions("Default", new Dictionary<string, long> { ["access.log"] = 100 });
        state.SetLogTotalLines("Default", 120);

        state.ReduceLogPositionsAfterPurge(
            "Default",
            report.LogLinesRemovedBeforePositionBySource,
            report.LogLinesRemovedBySource);

        Assert.Equal(70, state.GetLogSourcePositions("Default")["access.log"]);
        Assert.Equal(79, state.GetLogTotalLines("Default"));
    }
}
