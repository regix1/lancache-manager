using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Verifies that xboxlive and microsoft service rows fold into a single xbox row,
/// wsus is left untouched, and all numeric aggregates are correct after the merge.
/// </summary>
public class ServiceBreakdownMergerTests
{
    // ── ServiceBreakdownItem overload ─────────────────────────────────────────

    [Fact]
    public void MergeXboxRows_BreakdownItem_FoldsXboxliveAndMicrosoftIntoXbox()
    {
        var rows = new List<ServiceBreakdownItem>
        {
            new() { Service = "steam",    Bytes = 1000, Percentage = 50.0 },
            new() { Service = "xboxlive", Bytes = 400,  Percentage = 20.0 },
            new() { Service = "microsoft",Bytes = 200,  Percentage = 10.0 },
            new() { Service = "wsus",     Bytes = 400,  Percentage = 20.0 },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        // wsus untouched
        Assert.Contains(result, r => r.Service == "wsus" && r.Bytes == 400);
        // steam untouched
        Assert.Contains(result, r => r.Service == "steam" && r.Bytes == 1000);
        // single xbox row with summed values
        var xbox = Assert.Single(result, r => r.Service == "xbox");
        Assert.Equal(600, xbox.Bytes);
        Assert.Equal(30.0, xbox.Percentage, precision: 10);
        // no alias rows remain
        Assert.DoesNotContain(result, r => r.Service == "xboxlive");
        Assert.DoesNotContain(result, r => r.Service == "microsoft");
    }

    [Fact]
    public void MergeXboxRows_BreakdownItem_XboxliveOnlyRowRenamedToXbox()
    {
        var rows = new List<ServiceBreakdownItem>
        {
            new() { Service = "xboxlive", Bytes = 500, Percentage = 100.0 },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        var xbox = Assert.Single(result);
        Assert.Equal("xbox", xbox.Service);
        Assert.Equal(500, xbox.Bytes);
    }

    [Fact]
    public void MergeXboxRows_BreakdownItem_ExistingXboxRowAndXboxliveAreMerged()
    {
        var rows = new List<ServiceBreakdownItem>
        {
            new() { Service = "xbox",     Bytes = 300, Percentage = 30.0 },
            new() { Service = "xboxlive", Bytes = 200, Percentage = 20.0 },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        var xbox = Assert.Single(result, r => r.Service == "xbox");
        Assert.Equal(500, xbox.Bytes);
        Assert.Equal(50.0, xbox.Percentage, precision: 10);
    }

    [Fact]
    public void MergeXboxRows_BreakdownItem_WsusStaysSeparate()
    {
        var rows = new List<ServiceBreakdownItem>
        {
            new() { Service = "wsus",     Bytes = 800, Percentage = 80.0 },
            new() { Service = "xboxlive", Bytes = 200, Percentage = 20.0 },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Service == "wsus" && r.Bytes == 800);
        Assert.Contains(result, r => r.Service == "xbox" && r.Bytes == 200);
    }

    [Fact]
    public void MergeXboxRows_BreakdownItem_SortsByBytesDescending()
    {
        var rows = new List<ServiceBreakdownItem>
        {
            new() { Service = "xboxlive", Bytes = 100, Percentage = 10.0 },
            new() { Service = "steam",    Bytes = 500, Percentage = 50.0 },
            new() { Service = "epic",     Bytes = 300, Percentage = 30.0 },
            new() { Service = "microsoft",Bytes = 100, Percentage = 10.0 },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        Assert.Equal("steam", result[0].Service);
        Assert.Equal("epic",  result[1].Service);
        Assert.Equal("xbox",  result[2].Service);
        Assert.Equal(200, result[2].Bytes);
    }

    [Fact]
    public void MergeXboxRows_BreakdownItem_NoXboxTrafficReturnsUnchanged()
    {
        var rows = new List<ServiceBreakdownItem>
        {
            new() { Service = "steam", Bytes = 1000, Percentage = 100.0 },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        // Same reference — no allocation when nothing to merge
        Assert.Same(rows, result);
    }

    // ── ServiceStats overload ─────────────────────────────────────────────────

    [Fact]
    public void MergeXboxRows_ServiceStats_FoldsXboxliveAndMicrosoftIntoXbox()
    {
        var now = DateTime.UtcNow;
        var rows = new List<ServiceStats>
        {
            new() { Service = "steam",     TotalCacheHitBytes = 500, TotalCacheMissBytes = 500, TotalDownloads = 10,
                    LastActivityUtc = now, LastActivityLocal = now },
            new() { Service = "xboxlive",  TotalCacheHitBytes = 200, TotalCacheMissBytes = 100, TotalDownloads = 3,
                    LastActivityUtc = now.AddHours(-2), LastActivityLocal = now.AddHours(-2) },
            new() { Service = "microsoft", TotalCacheHitBytes = 100, TotalCacheMissBytes = 50,  TotalDownloads = 2,
                    LastActivityUtc = now.AddHours(-1), LastActivityLocal = now.AddHours(-1) },
            new() { Service = "wsus",      TotalCacheHitBytes = 300, TotalCacheMissBytes = 100, TotalDownloads = 5,
                    LastActivityUtc = now, LastActivityLocal = now },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        Assert.Contains(result, r => r.Service == "wsus");
        Assert.DoesNotContain(result, r => r.Service == "xboxlive");
        Assert.DoesNotContain(result, r => r.Service == "microsoft");

        var xbox = Assert.Single(result, r => r.Service == "xbox");
        Assert.Equal(300, xbox.TotalCacheHitBytes);
        Assert.Equal(150, xbox.TotalCacheMissBytes);
        Assert.Equal(5,   xbox.TotalDownloads);
        // max of now-2h and now-1h
        Assert.Equal(now.AddHours(-1), xbox.LastActivityUtc);
    }

    [Fact]
    public void MergeXboxRows_ServiceStats_WsusStaysSeparate()
    {
        var now = DateTime.UtcNow;
        var rows = new List<ServiceStats>
        {
            new() { Service = "wsus",     TotalCacheHitBytes = 800, TotalCacheMissBytes = 0, TotalDownloads = 8,
                    LastActivityUtc = now, LastActivityLocal = now },
            new() { Service = "xboxlive", TotalCacheHitBytes = 200, TotalCacheMissBytes = 0, TotalDownloads = 2,
                    LastActivityUtc = now, LastActivityLocal = now },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Service == "wsus");
        Assert.Contains(result, r => r.Service == "xbox");
    }

    [Fact]
    public void MergeXboxRows_ServiceStats_NoXboxTrafficReturnsUnchanged()
    {
        var now = DateTime.UtcNow;
        var rows = new List<ServiceStats>
        {
            new() { Service = "steam", TotalCacheHitBytes = 1000, TotalCacheMissBytes = 0,
                    TotalDownloads = 5, LastActivityUtc = now, LastActivityLocal = now },
        };

        var result = ServiceBreakdownMerger.MergeXboxRows(rows);

        Assert.Same(rows, result);
    }
}
