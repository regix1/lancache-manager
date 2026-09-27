using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Tests;

public sealed class NginxWriterProofTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ds-impl-a-writer-" + Guid.NewGuid().ToString("N"));

    public NginxWriterProofTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void WindowsFileIdentityLayout_MatchesNativeOffsets()
    {
        var layout = typeof(NginxWriterProbe).GetNestedType(
            "ByHandleFileInformation",
            BindingFlags.NonPublic)!;

        Assert.Equal(4, Marshal.OffsetOf(layout, "CreationTime").ToInt32());
        Assert.Equal(28, Marshal.OffsetOf(layout, "VolumeSerialNumber").ToInt32());
        Assert.Equal(44, Marshal.OffsetOf(layout, "FileIndexHigh").ToInt32());
        Assert.Equal(48, Marshal.OffsetOf(layout, "FileIndexLow").ToInt32());
        Assert.Equal(52, Marshal.SizeOf(layout));
    }

    [Fact]
    public void TryAcquire_HoldsSupportedNoWriterProof()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var target = Path.Combine(_root, "access.log");
        File.WriteAllText(target, "original");

        var acquired = NginxWriterProbe.TryAcquire(
            new[] { target },
            out var proofs,
            out var error);

        Assert.True(acquired, error);
        var proof = Assert.Single(proofs);
        Assert.True(proof.Validate());
        proof.Dispose();
    }

    [Fact]
    public async Task MixedCheck_InvalidHeldProofFailsValidationAndReleasesOnDisposeAsync()
    {
        var alpha = Path.Combine(_root, "mixed-alpha.log");
        var beta = Path.Combine(_root, "mixed-beta.log");
        await File.WriteAllTextAsync(alpha, "alpha");
        await File.WriteAllTextAsync(beta, "beta");
        var released = false;
        var proof = new NginxHeldProof(
            beta,
            NginxWriterProbe.ReadIdentity(beta),
            validate: () => false,
            release: () => released = true);
        var check = new NginxReopenCheck(
            new[] { "alpha", "beta" },
            new[] { alpha, beta },
            new Dictionary<string, NginxFileIdentity>
            {
                [alpha] = NginxWriterProbe.ReadIdentity(alpha),
                [beta] = proof.Identity
            },
            new[] { new NginxWriterIdentity(NginxWriterKind.Host, "host", 501, "start") },
            NginxReopenRequirement.Required,
            new[] { proof },
            null,
            null,
            expectsPublication: false);
        var service = CreateService();

        var error = Assert.Throws<InvalidOperationException>(() => service.ValidateReopenCheck(check));

        Assert.Contains(beta, error.Message, StringComparison.Ordinal);
        Assert.False(released);
        await check.DisposeAsync();
        Assert.True(released);
        Assert.False(proof.Validate());
    }

    [Fact]
    public void WindowsProof_BlocksWritableOpen()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var target = Path.Combine(_root, "access.log");
        File.WriteAllText(target, "original");
        var acquired = NginxWriterProbe.TryAcquire(
            new[] { target },
            out var proofs,
            out var error);
        Assert.True(acquired, error);
        using var proof = Assert.Single(proofs);

        Assert.Throws<IOException>(() =>
            new FileStream(
                target,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete).Dispose());

        Assert.True(proof.Validate());
        Assert.Equal("original", File.ReadAllText(target));
    }

    [Fact]
    public async Task LinuxProof_DetectsLeaseBreakBeforeWriterContinuesAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var target = Path.Combine(_root, "access.log");
        File.WriteAllText(target, "original");
        var acquired = NginxWriterProbe.TryAcquire(
            new[] { target },
            out var proofs,
            out var error);
        Assert.True(acquired, error);
        using var proof = Assert.Single(proofs);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sh",
            RedirectStandardError = true,
            UseShellExecute = false,
            ArgumentList = { "-c", "printf break >> \"$1\"", "lease-break", target }
        });
        Assert.NotNull(process);

        Assert.True(
            SpinWait.SpinUntil(() => !proof.Validate(), TimeSpan.FromSeconds(5)),
            "The held lease did not report the writer's break request");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, process.ExitCode);
        Assert.Equal("originalbreak", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("ONE")]
    [InlineData("TWO")]
    public async Task NativeHostWriters_ReopenPublishedLogsAsync(string caseName)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var pathsValue = Environment.GetEnvironmentVariable($"DS_IMPL_NATIVE_{caseName}_LOGS");
        var portsValue = Environment.GetEnvironmentVariable($"DS_IMPL_NATIVE_{caseName}_PORTS");
        if (string.IsNullOrWhiteSpace(pathsValue) || string.IsNullOrWhiteSpace(portsValue))
        {
            return;
        }

        var paths = pathsValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ports = portsValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();
        Assert.Equal(paths.Length, ports.Length);
        Assert.All(paths, path => Assert.True(new FileInfo(path).Length > 0));
        var sources = paths.Select((path, index) => new ResolvedDatasource
        {
            Name = $"native-{caseName.ToLowerInvariant()}-{index}",
            CachePath = _root,
            ConfiguredLogPath = Path.GetDirectoryName(path)!,
            LogPath = Path.GetDirectoryName(path)!,
            LogFilePath = path,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        }).ToList();
        var service = CreateService();
        await using var check = await service.PrepareReopenCheckAsync(
            sources,
            paths,
            expectsPublication: false);

        Assert.Equal(NginxReopenRequirement.Required, check.Requirement);
        Assert.Equal(paths.Length, check.Writers.Count);
        var originalIdentities = paths.ToDictionary(path => path, NginxWriterProbe.ReadIdentity);
        foreach (var path in paths)
        {
            var replacement = path + ".ds-impl-a-new";
            await File.WriteAllTextAsync(replacement, string.Empty);
            File.Move(replacement, path, overwrite: true);
            Assert.NotEqual(originalIdentities[path], NginxWriterProbe.ReadIdentity(path));
        }

        var result = await service.CompleteReopenCheckAsync(check, physicalChange: true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(NginxReopenStatus.Succeeded, result.Status);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var index = 0; index < ports.Length; index++)
        {
            using var response = await client.GetAsync($"http://127.0.0.1:{ports[index]}/ds-impl-a-{caseName}-{index}");
            Assert.True(response.IsSuccessStatusCode);
        }
        Assert.True(
            SpinWait.SpinUntil(
                () => paths.All(path => new FileInfo(path).Length > 0),
                TimeSpan.FromSeconds(5)),
            "nginx did not append to every published log after the verified reopen");
    }

    [Fact]
    public async Task NativeHostWriter_AlreadyUnlinkedLogReopensOnZeroChangeRetryAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("LCM_EVICTION_REOPEN_NATIVE_LOG");
        var portValue = Environment.GetEnvironmentVariable("LCM_EVICTION_REOPEN_NATIVE_PORT");
        if (string.IsNullOrWhiteSpace(path) ||
            !int.TryParse(portValue, out var port))
        {
            return;
        }

        Assert.True(File.Exists(path));
        var replacement = path + ".reopen-new-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(replacement, string.Empty);
        File.Move(replacement, path, overwrite: true);
        var source = new ResolvedDatasource
        {
            Name = "native-retry",
            CachePath = _root,
            ConfiguredLogPath = Path.GetDirectoryName(path)!,
            LogPath = Path.GetDirectoryName(path)!,
            LogFilePath = path,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        };
        var service = CreateService();
        await using var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { path },
            expectsPublication: false);

        Assert.Equal(NginxReopenRequirement.Required, check.Requirement);
        Assert.Single(check.Writers);
        var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(NginxReopenStatus.Succeeded, result.Status);
        var requestMarker = "reopen-retry-" + Guid.NewGuid().ToString("N");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/{requestMarker}");
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(
            SpinWait.SpinUntil(
                () => File.ReadAllText(path).Contains(requestMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            "nginx did not append the retry request to the current log after reopen");
    }

    [Fact]
    public async Task NativeHostWriter_ProcessRootAliasReopensOnZeroChangeRetryAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var path = Environment.GetEnvironmentVariable("LCM_EVICTION_REOPEN_ALIAS_LOG");
        var portValue = Environment.GetEnvironmentVariable("LCM_EVICTION_REOPEN_ALIAS_PORT");
        if (string.IsNullOrWhiteSpace(path) ||
            !int.TryParse(portValue, out var port))
        {
            return;
        }

        Assert.True(File.Exists(path));
        var replacement = path + ".reopen-new-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(replacement, string.Empty);
        File.Move(replacement, path, overwrite: true);
        var source = new ResolvedDatasource
        {
            Name = "native-alias-retry",
            CachePath = _root,
            ConfiguredLogPath = Path.GetDirectoryName(path)!,
            LogPath = Path.GetDirectoryName(path)!,
            LogFilePath = path,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        };
        var service = CreateService();
        await using var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { path },
            expectsPublication: false);

        Assert.Equal(NginxReopenRequirement.Required, check.Requirement);
        Assert.Single(check.Writers);
        var result = await service.CompleteReopenCheckAsync(check, physicalChange: false);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(NginxReopenStatus.Succeeded, result.Status);
        var requestMarker = "alias-retry-" + Guid.NewGuid().ToString("N");
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var response = await client.GetAsync($"http://127.0.0.1:{port}/{requestMarker}");
        Assert.True(response.IsSuccessStatusCode);
        Assert.True(
            SpinWait.SpinUntil(
                () => File.ReadAllText(path).Contains(requestMarker, StringComparison.Ordinal),
                TimeSpan.FromSeconds(5)),
            "nginx did not append the alias retry request to the current log after reopen");
    }

    [Fact]
    public async Task CompleteReopenCheckAsync_MissingPublicationReceipt_ReturnsTypedFailureAsync()
    {
        var target = Path.Combine(_root, "access.log");
        File.WriteAllText(target, "original");
        var identity = NginxWriterProbe.ReadIdentity(target);
        var check = new NginxReopenCheck(
            new[] { "alpha" },
            new[] { target },
            new Dictionary<string, NginxFileIdentity> { [target] = identity },
            Array.Empty<NginxWriterIdentity>(),
            NginxReopenRequirement.NotRequired,
            Array.Empty<NginxHeldProof>(),
            Path.Combine(_root, "check.json"),
            Path.Combine(_root, "missing-result.json"),
            expectsPublication: true);
        await using (check)
        {
            var service = CreateService();

            var result = await service.CompleteReopenCheckAsync(
                check,
                physicalChange: true);

            Assert.False(result.Success);
            Assert.Equal(NginxReopenStatus.Failed, result.Status);
            Assert.Equal(NginxReopenRequirement.NotRequired, result.Requirement);
            Assert.True(result.PartialPhysicalEffects);
            Assert.Contains("did not publish", result.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AttachPublicationCheck_SetsOnlyTheChildEnvironment()
    {
        var check = new NginxReopenCheck(
            new[] { "alpha" },
            Array.Empty<string>(),
            new Dictionary<string, NginxFileIdentity>(),
            Array.Empty<NginxWriterIdentity>(),
            NginxReopenRequirement.NotRequired,
            Array.Empty<NginxHeldProof>(),
            Path.Combine(_root, "check.json"),
            Path.Combine(_root, "result.json"),
            expectsPublication: false);
        var process = new ProcessStartInfo();

        NginxLogRotationService.AttachPublicationCheck(check, process);

        Assert.Equal(check.CheckPath, process.Environment["LANCACHE_LOG_CHECK"]);
        Assert.Equal(check.ResultPath, process.Environment["LANCACHE_LOG_RESULT"]);
        Assert.Null(Environment.GetEnvironmentVariable("LANCACHE_LOG_CHECK"));
        Assert.Null(Environment.GetEnvironmentVariable("LANCACHE_LOG_RESULT"));
    }

    [Fact]
    public async Task CachePurgeCaller_PublishesUnderHeldWindowsProofAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var binaryDirectory = Environment.GetEnvironmentVariable("DS_IMPL_RUST_BIN_DIR");
        if (string.IsNullOrWhiteSpace(binaryDirectory))
        {
            return;
        }

        var binary = Path.Combine(binaryDirectory, "cache_purge_log_entries.exe");
        Assert.True(File.Exists(binary));
        var logs = Path.Combine(_root, "purge-logs");
        Directory.CreateDirectory(logs);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllLinesAsync(target,
        [
            "[steam] 192.0.2.10 / - - - [26/Sep/2026:18:00:00 +0000] \"GET /remove HTTP/1.1\" 200 12 \"-\" \"test\" \"HIT\" \"-\" \"-\"",
            "[steam] 192.0.2.10 / - - - [26/Sep/2026:18:00:01 +0000] \"GET /keep HTTP/1.1\" 200 8 \"-\" \"test\" \"HIT\" \"-\" \"-\""
        ]);
        var input = Path.Combine(_root, "purge-input.json");
        var output = Path.Combine(_root, "purge-output.json");
        await File.WriteAllTextAsync(input, "{\"urls\":[\"/remove\"],\"depot_ids\":[]}");
        var source = new ResolvedDatasource
        {
            Name = "windows-static",
            CachePath = _root,
            ConfiguredLogPath = logs,
            LogPath = logs,
            LogFilePath = target,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        };
        var service = CreateService();
        await using var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: true);
        Assert.Equal(NginxReopenRequirement.NotRequired, check.Requirement);
        var original = check.OriginalIdentities[target];
        var start = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(logs);
        start.ArgumentList.Add(input);
        start.ArgumentList.Add(output);
        NginxLogRotationService.AttachPublicationCheck(check, start);
        using var process = Process.Start(start);
        Assert.NotNull(process);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var error = await process.StandardError.ReadToEndAsync();
        Assert.True(process.ExitCode == 0, error);

        var result = await service.CompleteReopenCheckAsync(check, physicalChange: true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(NginxReopenStatus.NotRequired, result.Status);
        Assert.NotEqual(original, NginxWriterProbe.ReadIdentity(target));
        var contents = await File.ReadAllTextAsync(target);
        Assert.DoesNotContain("/remove", contents, StringComparison.Ordinal);
        Assert.Contains("/keep", contents, StringComparison.Ordinal);
        await File.AppendAllTextAsync(target, "post-publication");
        Assert.Contains("post-publication", await File.ReadAllTextAsync(target), StringComparison.Ordinal);
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.Temporary));
        Assert.Equal(new[] { "access.log" }, Directory.GetFiles(logs).Select(Path.GetFileName));
    }

    [Fact]
    public async Task CompleteReopenCheckAsync_MismatchedPublicationReceipt_ReturnsTypedFailureAsync()
    {
        var target = Path.Combine(_root, "mismatch-access.log");
        File.WriteAllText(target, "original");
        var identity = NginxWriterProbe.ReadIdentity(target);
        var resultPath = Path.Combine(_root, "mismatch-result.json");
        var check = new NginxReopenCheck(
            new[] { "alpha" },
            new[] { target },
            new Dictionary<string, NginxFileIdentity> { [target] = identity },
            Array.Empty<NginxWriterIdentity>(),
            NginxReopenRequirement.NotRequired,
            Array.Empty<NginxHeldProof>(),
            Path.Combine(_root, "mismatch-check.json"),
            resultPath,
            expectsPublication: true);
        var wrong = new NginxFileIdentity(identity.First, identity.Second + 1);
        await File.WriteAllTextAsync(
            resultPath,
            JsonSerializer.Serialize(
                new NginxPublicationResult(
                    true,
                    new[]
                    {
                        new NginxPublicationRecord(target, wrong, identity, identity, true, false)
                    }),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await using (check)
        {
            var service = CreateService();

            var result = await service.CompleteReopenCheckAsync(check, physicalChange: true);

            Assert.False(result.Success);
            Assert.Equal(NginxReopenStatus.Failed, result.Status);
            Assert.True(result.PartialPhysicalEffects);
            Assert.Contains("did not match", result.ErrorMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5351:Do Not Use Broken Cryptographic Algorithms",
        Justification = "The disposable test must reproduce nginx's fixed MD5 cache-key path.")]
    public async Task CorruptionCaller_PublishesUnderHeldWindowsProofAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var binaryDirectory = Environment.GetEnvironmentVariable("DS_IMPL_RUST_BIN_DIR");
        var connectionUrl = Environment.GetEnvironmentVariable("DS_IMPL_CORRUPTION_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(binaryDirectory) || string.IsNullOrWhiteSpace(connectionUrl))
        {
            return;
        }

        var binary = Path.Combine(binaryDirectory, "cache_corruption.exe");
        Assert.True(File.Exists(binary));
        var logs = Path.Combine(_root, "corruption-logs");
        var cache = Path.Combine(_root, "corruption-cache");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(cache);
        var target = Path.Combine(logs, "access.log");
        await File.WriteAllLinesAsync(target,
        [
            "[steam] 192.0.2.20 / - - - [26/Sep/2026:18:00:00 +0000] \"GET /depot/123/chunk/abc HTTP/1.1\" 200 100 \"-\" \"test\" \"MISS\" \"-\" \"-\"",
            "[steam] 192.0.2.20 / - - - [26/Sep/2026:18:00:01 +0000] \"GET /depot/123/chunk/abc HTTP/1.1\" 200 100 \"-\" \"test\" \"MISS\" \"-\" \"-\"",
            "[steam] 192.0.2.20 / - - - [26/Sep/2026:18:00:02 +0000] \"GET /depot/123/chunk/abc HTTP/1.1\" 200 100 \"-\" \"test\" \"MISS\" \"-\" \"-\"",
            "[steam] 192.0.2.20 / - - - [26/Sep/2026:18:00:03 +0000] \"GET /keep HTTP/1.1\" 200 8 \"-\" \"test\" \"HIT\" \"-\" \"-\""
        ]);
        var key = "steam/depot/123/chunk/abc";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var candidatePath = Path.Combine(cache, hash[^2..], hash[^4..^2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(candidatePath)!);
        await File.WriteAllTextAsync(candidatePath, "candidate");
        File.SetLastWriteTimeUtc(candidatePath, new DateTime(2026, 9, 26, 17, 0, 0, DateTimeKind.Utc));
        var reportPath = Path.Combine(_root, "corruption-report.json");
        var detect = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        detect.ArgumentList.Add("detect");
        detect.ArgumentList.Add(logs);
        detect.ArgumentList.Add(cache);
        detect.ArgumentList.Add(reportPath);
        detect.ArgumentList.Add("UTC");
        detect.ArgumentList.Add("3");
        detect.ArgumentList.Add("--scan-started-utc");
        detect.ArgumentList.Add("2026-09-26T18:01:00Z");
        using (var process = Process.Start(detect))
        {
            Assert.NotNull(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        }
        var report = JsonNode.Parse(await File.ReadAllTextAsync(reportPath))!.AsObject();
        var candidates = report["candidates"]!.AsArray();
        Assert.Single(candidates);
        foreach (var candidate in candidates)
        {
            candidate!.AsObject()["datasource"] = "alpha";
        }
        var evidencePath = Path.Combine(_root, "corruption-evidence.json");
        var evidence = new JsonObject
        {
            ["contract_version"] = 4,
            ["scan_id"] = Guid.NewGuid().ToString(),
            ["detection_method"] = "repeated_miss",
            ["threshold"] = 3,
            ["datasource"] = "alpha",
            ["candidates"] = candidates.DeepClone()
        };
        await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());
        var source = new ResolvedDatasource
        {
            Name = "alpha",
            CachePath = cache,
            ConfiguredLogPath = logs,
            LogPath = logs,
            LogFilePath = target,
            Enabled = true,
            CacheWritable = true,
            LogsWritable = true
        };
        var service = CreateService();
        await using var check = await service.PrepareReopenCheckAsync(
            new[] { source },
            new[] { target },
            expectsPublication: true);
        var original = check.OriginalIdentities[target];
        var progress = Path.Combine(_root, "corruption-progress.json");
        var remove = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        remove.ArgumentList.Add("remove");
        remove.ArgumentList.Add(logs);
        remove.ArgumentList.Add(cache);
        remove.ArgumentList.Add("steam");
        remove.ArgumentList.Add(progress);
        remove.ArgumentList.Add("--evidence-file");
        remove.ArgumentList.Add(evidencePath);
        remove.Environment["DATABASE_URL"] = connectionUrl;
        NginxLogRotationService.AttachPublicationCheck(check, remove);
        using (var process = Process.Start(remove))
        {
            Assert.NotNull(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        }

        var result = await service.CompleteReopenCheckAsync(check, physicalChange: true);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEqual(original, NginxWriterProbe.ReadIdentity(target));
        Assert.False(File.Exists(candidatePath));
        var contents = await File.ReadAllTextAsync(target);
        Assert.DoesNotContain("/depot/123/chunk/abc", contents, StringComparison.Ordinal);
        Assert.Contains("/keep", contents, StringComparison.Ordinal);
    }

    private static NginxLogRotationService CreateService()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<NginxLogRotationService>.Instance;
        return new NginxLogRotationService(
            logger,
            configuration,
            new LancacheManager.Infrastructure.Utilities.ProcessManager(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<
                    LancacheManager.Infrastructure.Utilities.ProcessManager>.Instance),
            new TestPathResolver(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
            TimeProvider.System);
    }

    private sealed class TestPathResolver(Microsoft.Extensions.Logging.ILogger logger)
        : LancacheManager.Infrastructure.Platform.PathResolverBase(logger)
    {
        protected override string BasePath => Path.GetTempPath();
        protected override string RustExecutableExtension => string.Empty;
        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }
}
