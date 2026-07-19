using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Guards that the C# access-log source recognizer agrees with the Rust log_layout module,
/// and that datasource writability is measured against the directory the sources actually
/// live in (after the logs/http descent), not the pre-descent parent.
/// </summary>
public sealed class LogSourceLayoutParityTests
{
    // A compressed name is only a source when it is a numbered rotation the record processor
    // can replay. A compression-only name cannot join the live file series, so both sides
    // reject it; accepting it here would let this side claim a layout the processor ignores.
    [Theory]
    [InlineData("access.log.gz")]
    [InlineData("steam-access.log.zst")]
    [InlineData("blizzard-access.log.gz")]
    public void LogicalStem_RejectsCompressionWithoutRotation(string fileName)
    {
        Assert.Null(LogSourceLayout.LogicalStem(fileName));
    }

    [Theory]
    [InlineData("access.log.1.gz", "access.log")]
    [InlineData("access.log.10.zst", "access.log")]
    [InlineData("steam-access.log.2.zst", "steam-access.log")]
    [InlineData("windows-update-access.log.3.gz", "windows-update-access.log")]
    public void LogicalStem_AcceptsRotatedCompressed(string fileName, string expected)
    {
        Assert.Equal(expected, LogSourceLayout.LogicalStem(fileName));
    }

    [Theory]
    [InlineData("access.log", "access.log")]
    [InlineData("access.log.1", "access.log")]
    [InlineData("steam-access.log", "steam-access.log")]
    [InlineData("steam-access.log.1", "steam-access.log")]
    [InlineData("fallback-access.log", "fallback-access.log")]
    public void LogicalStem_AcceptsPlainAndRotatedNames(string fileName, string expected)
    {
        Assert.Equal(expected, LogSourceLayout.LogicalStem(fileName));
    }

    [Theory]
    [InlineData("stream-access.log")]
    [InlineData("nginx-access.log")]
    [InlineData("stream-access.log.1")]
    [InlineData("nginx-error.log")]
    [InlineData("-access.log")]
    public void LogicalStem_RejectsUnknownAndNonSourceNames(string fileName)
    {
        Assert.Null(LogSourceLayout.LogicalStem(fileName));
    }

    // The recognized-prefix match is case-insensitive (matching the Rust lowercasing), while
    // the "-access.log" suffix and the ".gz"/".zst" strip are ordinal on both sides.
    [Theory]
    [InlineData("Steam-access.log", "Steam-access.log")]
    [InlineData("STEAM-ACCESS.LOG", null)]
    [InlineData("access.log.GZ", null)]
    [InlineData("ACCESS.LOG", null)]
    public void LogicalStem_MatchesRustCaseHandling(string fileName, string? expected)
    {
        Assert.Equal(expected, LogSourceLayout.LogicalStem(fileName));
    }

    // A bare-metal tree keeps only nginx-error.log at the parent and the per-service access
    // logs under http/. RefreshLogSources must move LogPath to that child, because that is the
    // directory whose write permission gates ingestion; probing the parent would report the
    // wrong LogsWritable when parent and child ownership differ.
    [Fact]
    public void RefreshLogSources_DescendsIntoHttpChildForBareMetalTopology()
    {
        var root = Directory.CreateTempSubdirectory("logsourcelayout_");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "nginx-error.log"), string.Empty);
            var httpDir = Directory.CreateDirectory(Path.Combine(root.FullName, "http"));
            File.WriteAllText(Path.Combine(httpDir.FullName, "steam-access.log"), string.Empty);
            File.WriteAllText(Path.Combine(httpDir.FullName, "riot-access.log"), string.Empty);

            var datasource = new ResolvedDatasource
            {
                Name = "bare-metal",
                ConfiguredLogPath = root.FullName,
                LogPath = root.FullName
            };

            datasource.RefreshLogSources();

            Assert.Equal(httpDir.FullName, datasource.LogPath);
            Assert.Equal(LogSourceLayout.LayoutBareMetal, datasource.Layout);
            Assert.Equal(2, datasource.LogSourceStems.Count);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    // Locks the fix ordering: writability is probed only after RefreshLogSources has run the
    // descent, so LogsWritable reflects the resolved LogPath rather than the configured parent,
    // on both initial construction and the periodic permission refresh.
    [Fact]
    public void DatasourceService_ProbesWritabilityAfterSourceRefresh()
    {
        var source = ReadRepoFile("Api", "LancacheManager", "Core", "Services", "DatasourceService.cs");

        AssertOrderedWithin(
            source,
            regionStart: "private ResolvedDatasource? ResolveDatasource",
            regionEnd: "public IReadOnlyList<ResolvedDatasource> GetDatasources",
            first: "datasource.RefreshLogSources();",
            second: "datasource.LogsWritable = _pathResolver.IsDirectoryWritable(datasource.LogPath)");

        AssertOrderedWithin(
            source,
            regionStart: "public void RefreshPermissions",
            regionEnd: "public bool HasMultipleDatasources",
            first: "ds.RefreshLogSources();",
            second: "ds.LogsWritable = _pathResolver.IsDirectoryWritable(ds.LogPath)");
    }

    private static void AssertOrderedWithin(
        string source,
        string regionStart,
        string regionEnd,
        string first,
        string second)
    {
        var start = source.IndexOf(regionStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Region start not found: {regionStart}");
        var end = source.IndexOf(regionEnd, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Region end not found after start: {regionEnd}");

        var region = source[start..end];
        var firstIndex = region.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = region.IndexOf(second, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Expected call not found: {first}");
        Assert.True(secondIndex >= 0, $"Expected assignment not found: {second}");
        Assert.True(firstIndex < secondIndex, "Writability must be probed after the source refresh");
    }

    private static string ReadRepoFile(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, .. pathSegments]));
    }
}
