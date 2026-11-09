using System.Text.Json;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public sealed class CacheSliceSizeContractTests
{
    [Fact]
    public void DetectionReportSliceSizesAreRead()
    {
        var game = JsonSerializer.Deserialize<GameCacheInfo>(
            """{"game_app_id":10,"game_name":"Game","slice_sizes":"1048576,8388608"}""");

        Assert.NotNull(game);
        Assert.Equal("1048576,8388608", game.SliceSizes);
    }

    [Fact]
    public void ManualSliceSizeIsPassedAndAutoLeavesTheFlagOut()
    {
        Assert.Equal(" --slice-size 8388608", RustProcessHelper.SliceSizeFlag(8_388_608));
        Assert.Equal(string.Empty, RustProcessHelper.SliceSizeFlag(null));
        Assert.Equal(["--slice-size", "8388608"], RustProcessHelper.SliceSizeArguments(8_388_608));
        Assert.Empty(RustProcessHelper.SliceSizeArguments(null));

        var arguments = RustProcessHelper.BuildCorruptionManagerArguments(
            "remove-logs",
            "C:/logs",
            "C:/cache",
            "steam",
            "C:/ops/evidence.json",
            "C:/ops/progress.json",
            "monolithic",
            sliceSizeBytes: 8_388_608);
        Assert.EndsWith("--key-scheme monolithic --slice-size 8388608", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void BothEvictionConfigWritersCarryTheSliceSizeAndOnlyTheScanCarriesTheMode()
    {
        var source = ReadSource("Cache/CacheReconciliationService.cs");
        const string sliceSize = "sliceSize = _stateService.GetCacheSliceSizeBytes()";
        const string removeMode = "removeMode = isRemoveMode";

        var scanWriter = source.IndexOf("datasourceConfigPath = Path.GetTempFileName();", StringComparison.Ordinal);
        var scanWrite = source.IndexOf("await File.WriteAllTextAsync(datasourceConfigPath", scanWriter, StringComparison.Ordinal);
        var scanSlice = source.IndexOf(sliceSize, scanWriter, StringComparison.Ordinal);
        var scanMode = source.IndexOf(removeMode, scanWriter, StringComparison.Ordinal);
        Assert.True(scanWriter >= 0, "eviction scan config writer is missing");
        Assert.InRange(scanSlice, scanWriter, scanWrite);
        Assert.InRange(scanMode, scanWriter, scanWrite);

        var repairWriter = source.IndexOf("var datasourcePath = Path.GetTempFileName();", StringComparison.Ordinal);
        var repairDocument = source.IndexOf("new CacheRepairDocument", repairWriter, StringComparison.Ordinal);
        Assert.True(repairWriter >= 0, "repair eviction config writer is missing");
        Assert.InRange(source.IndexOf(sliceSize, repairWriter, StringComparison.Ordinal), repairWriter, repairDocument);

        Assert.Equal(source.IndexOf(removeMode, StringComparison.Ordinal), source.LastIndexOf(removeMode, StringComparison.Ordinal));
    }

    private static string ReadSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine(root, "Api", "LancacheManager", "Core", "Services", fileName));
    }
}
