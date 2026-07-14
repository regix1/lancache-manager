using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class StructuralCorruptionBackendContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"lancache-structural-state-{Guid.NewGuid():N}");

    [Fact]
    public void DurableStateScopeIsStableSafeAndRootSpecific()
    {
        var resolver = new TestPathResolver(_root);
        var firstRoot = Path.Combine(_root, "cache root", "primary");
        var secondRoot = Path.Combine(_root, "cache root", "secondary");

        var scope = resolver.GetStructuralCorruptionStateScope("Primary / unsafe", firstRoot);
        var equivalent = resolver.GetStructuralCorruptionStateScope(
            " primary / UNSAFE ",
            firstRoot + Path.DirectorySeparatorChar);
        var different = resolver.GetStructuralCorruptionStateScope("Primary / unsafe", secondRoot);
        var databasePath = resolver.GetStructuralCorruptionStateDatabasePath(
            "Primary / unsafe",
            firstRoot);

        Assert.Matches("^[0-9a-f]{64}$", scope);
        Assert.Equal(scope, equivalent);
        Assert.NotEqual(scope, different);
        Assert.Equal($"{scope}.sqlite3", Path.GetFileName(databasePath));
        Assert.Equal(
            Path.Combine(resolver.GetStateDirectory(), "corruption-structural"),
            Path.GetDirectoryName(databasePath));
        Assert.DoesNotContain("Primary", databasePath, StringComparison.OrdinalIgnoreCase);
        Assert.False(databasePath.StartsWith(
            resolver.GetOperationsDirectory(),
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StructuralArgumentsUseExactWorkerOneCliAndPreserveSpecialPaths()
    {
        var cachePath = Path.Combine(_root, "cache path", "literal & $ value");
        var progressPath = Path.Combine(_root, "operations", "progress file.json");
        var statePath = Path.Combine(_root, "state path", "baseline file.sqlite3");
        var scope = new string('a', 64);
        var arguments = CorruptionDetectionService.BuildStructuralProcessArguments(
            cachePath,
            progressPath,
            "2026-07-12T18:00:00Z",
            StructuralScanMode.Incremental,
            statePath,
            scope);

        Assert.Equal(
            [
                "structural-summary",
                cachePath,
                progressPath,
                "--scan-started-utc",
                "2026-07-12T18:00:00Z",
                "--scan-mode",
                "incremental",
                "--state-db",
                statePath,
                "--state-scope",
                scope
            ],
            arguments);

        // This caller polls the progress JSON file. Keeping stdout event reporting disabled
        // reserves stdout for the single CorruptionReport consumed by JsonSerializer.
        Assert.DoesNotContain("--progress", arguments);

        var helper = new RustProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            processManager: null!,
            pathResolver: null!,
            operationTracker: null!);
        var startInfo = helper.CreateProcessStartInfo("cache_corruption", arguments);

        Assert.Equal(arguments.ToArray(), startInfo.ArgumentList.ToArray());
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.False(startInfo.UseShellExecute);
    }

    [Theory]
    [InlineData("cancelled", true)]
    [InlineData("CANCELLED", true)]
    [InlineData("completed", false)]
    [InlineData("scanning", false)]
    [InlineData("", false)]
    public void OnlyTerminalCancelledProgressStopsPersistence(string status, bool expected)
    {
        var progress = new CorruptionDetectionProgressData { Status = status };

        Assert.Equal(expected, CorruptionDetectionService.IsCancelledProgress(progress));
    }

    [Fact]
    public void SuccessfulClearQuarantinesOnlyAffectedStructuralState()
    {
        var resolver = new TestPathResolver(_root);
        var primaryRoot = Path.Combine(_root, "cache", "primary");
        var secondaryRoot = Path.Combine(_root, "cache", "secondary");
        var primary = resolver.GetStructuralCorruptionStateDatabasePath("primary", primaryRoot);
        var secondary = resolver.GetStructuralCorruptionStateDatabasePath("secondary", secondaryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(primary)!);
        File.WriteAllText(primary, "primary");
        File.WriteAllText($"{primary}-wal", "wal");
        File.WriteAllText($"{primary}-shm", "shm");
        File.WriteAllText(secondary, "secondary");

        CacheClearingService.InvalidateStructuralCorruptionState(
            resolver,
            [("primary", primaryRoot)]);

        Assert.False(File.Exists(primary));
        Assert.False(File.Exists($"{primary}-wal"));
        Assert.False(File.Exists($"{primary}-shm"));
        Assert.True(File.Exists(secondary));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestPathResolver(string basePath) : PathResolverBase(NullLogger.Instance)
    {
        protected override string BasePath { get; } = basePath;
        protected override string RustExecutableExtension => ".exe";
        public override string ResolvePath(string relativePath) => Path.GetFullPath(relativePath, BasePath);
        public override string NormalizePath(string path) => Path.GetFullPath(path);
        public override bool IsDockerSocketAvailable() => false;
    }
}
