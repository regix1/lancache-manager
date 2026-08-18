using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
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

        Assert.Matches("^[0-9a-f]{64}$", scope);
        Assert.Equal(scope, equivalent);
        Assert.NotEqual(scope, different);
        // The scope is what the scanner stores against each baseline row and what a cache clear
        // deletes by, so a datasource name must not survive into it in readable form.
        Assert.DoesNotContain("Primary", scope, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StructuralArgumentsUseExactWorkerOneCliAndPreserveSpecialPaths()
    {
        var cachePath = Path.Combine(_root, "cache path", "literal & $ value");
        var progressPath = Path.Combine(_root, "operations", "progress file.json");
        var scope = new string('a', 64);
        var arguments = CorruptionDetectionService.BuildStructuralProcessArguments(
            cachePath,
            progressPath,
            "2026-07-12T18:00:00Z",
            StructuralScanMode.Incremental,
            scope,
            "bare_metal");

        Assert.Equal(
            [
                "structural-summary",
                cachePath,
                progressPath,
                "--scan-started-utc",
                "2026-07-12T18:00:00Z",
                "--scan-mode",
                "incremental",
                "--state-scope",
                scope,
                "--key-scheme",
                "bare_metal"
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

    /// <summary>
    /// Mirrors the three tables the scanner creates in cache_structural_state.rs. The C# side
    /// never creates them, so this is the only place the shape the delete relies on is written
    /// down on this side of the boundary: a scope column to match on, and cascades that carry one
    /// namespace delete through the runs into the per-file rows.
    /// </summary>
    private const string ScannerStateSchema = """
        CREATE TABLE structural_namespaces(
            namespace_hash TEXT PRIMARY KEY,
            namespace_json TEXT NOT NULL,
            scope TEXT NOT NULL,
            active_generation TEXT NULL);
        CREATE TABLE structural_runs(
            generation TEXT PRIMARY KEY,
            namespace_hash TEXT NOT NULL,
            requested_mode TEXT NOT NULL CHECK(requested_mode IN ('full','incremental')),
            effective_mode TEXT NOT NULL CHECK(effective_mode IN ('full','incremental','baseline')),
            status TEXT NOT NULL CHECK(status IN ('running','interrupted','complete')),
            started_at BIGINT NOT NULL,
            heartbeat_at BIGINT NOT NULL,
            enumeration_complete BOOLEAN NOT NULL,
            FOREIGN KEY(namespace_hash) REFERENCES structural_namespaces(namespace_hash) ON DELETE CASCADE);
        CREATE TABLE structural_file_state(
            namespace_hash TEXT NOT NULL,
            generation TEXT NOT NULL,
            digest BYTEA NOT NULL CHECK(octet_length(digest) = 16),
            dev BIGINT NOT NULL, ino BIGINT NOT NULL, len BIGINT NOT NULL,
            mtime_ns BIGINT NOT NULL, ctime_ns BIGINT NOT NULL,
            outcome BOOLEAN NOT NULL,
            candidate_json TEXT NULL,
            seen_epoch TEXT NOT NULL,
            PRIMARY KEY(namespace_hash, generation, digest),
            FOREIGN KEY(generation) REFERENCES structural_runs(generation) ON DELETE CASCADE);
        """;

    [Fact]
    public async Task SuccessfulClearDropsOnlyTheClearedRootsStructuralState()
    {
        var resolver = new TestPathResolver(_root);
        var primaryRoot = Path.Combine(_root, "cache", "primary");
        var secondaryRoot = Path.Combine(_root, "cache", "secondary");
        var primaryScope = resolver.GetStructuralCorruptionStateScope("primary", primaryRoot);
        var secondaryScope = resolver.GetStructuralCorruptionStateScope("secondary", secondaryRoot);

        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(ScannerStateSchema);
        await SeedBaselineAsync(context, primaryScope, "gen-primary");
        await SeedBaselineAsync(context, secondaryScope, "gen-secondary");

        await CacheClearingService.InvalidateStructuralCorruptionStateAsync(
            context,
            resolver,
            [("primary", primaryRoot)],
            CancellationToken.None);

        Assert.Equal([secondaryScope], await ScalarsAsync(context, "SELECT scope FROM structural_namespaces"));
        // The cleared root's run and per-file rows have to go with it, or an Incremental could
        // reuse a baseline describing files the clear already deleted.
        Assert.Equal(["gen-secondary"], await ScalarsAsync(context, "SELECT generation FROM structural_runs"));
        Assert.Equal(["gen-secondary"], await ScalarsAsync(context, "SELECT generation FROM structural_file_state"));
    }

    /// <summary>
    /// An install that has never run a structural scan has no scanner tables at all, because the
    /// scanner creates them on its first run. Clearing the cache still has to work there.
    /// </summary>
    [Fact]
    public async Task ClearWithoutAnyScannerStateDoesNotThrow()
    {
        var resolver = new TestPathResolver(_root);

        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();

        await CacheClearingService.InvalidateStructuralCorruptionStateAsync(
            context,
            resolver,
            [("primary", Path.Combine(_root, "cache", "primary"))],
            CancellationToken.None);
    }

    private static async Task SeedBaselineAsync(AppDbContext context, string scope, string generation)
    {
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO structural_namespaces(namespace_hash, namespace_json, scope, active_generation)
                VALUES({0}, '{{}}', {1}, {2});
            INSERT INTO structural_runs(generation, namespace_hash, requested_mode, effective_mode,
                    status, started_at, heartbeat_at, enumeration_complete)
                VALUES({2}, {0}, 'full', 'full', 'complete', 0, 0, true);
            INSERT INTO structural_file_state(namespace_hash, generation, digest, dev, ino, len,
                    mtime_ns, ctime_ns, outcome, seen_epoch)
                VALUES({0}, {2}, decode(repeat('ab', 16), 'hex'), 1, 1, 1, 1, 1, true, 'epoch');
            """,
            $"hash-{generation}", scope, generation);
    }

    private static async Task<List<string>> ScalarsAsync(AppDbContext context, string sql)
    {
        var values = new List<string>();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        await context.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
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
