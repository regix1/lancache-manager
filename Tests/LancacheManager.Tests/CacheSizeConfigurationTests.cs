using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Tests;

public sealed class CacheSizeConfigurationTests
{
    [Theory]
    [InlineData("2000g", 2_147_483_648_000L)]
    [InlineData("500G", 536_870_912_000L)]
    [InlineData("2t", 2_199_023_255_552L)]
    [InlineData("1.5T", 1_649_267_441_664L)]
    [InlineData("'4000g'", 4_294_967_296_000L)]
    [InlineData("123456", 123_456L)]
    public void SharedParser_AcceptsConfiguredAndManualFormats(string input, long expectedBytes)
    {
        Assert.True(CacheSizeParser.TryParse(input, out var bytes));
        Assert.Equal(expectedBytes, bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2xb")]
    [InlineData("999999999999999999999999t")]
    public void SharedParser_RejectsInvalidValues(string input)
    {
        Assert.False(CacheSizeParser.TryParse(input, out _));
    }

    [Fact]
    public void Request_AcceptsJsonByteCountAsNumber()
    {
        var request = JsonSerializer.Deserialize<SetDatasourceCacheSizeRequest>("{\"size\":2147483648}");

        Assert.NotNull(request);
        Assert.Equal("2147483648", request.Size);
        Assert.Equal(2_147_483_648L, DatasourceConfigurationController.ResolveCacheSizeOverride(request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    public void BlankOrZeroInput_ClearsOverride(string? input)
    {
        var bytes = DatasourceConfigurationController.ResolveCacheSizeOverride(
            new SetDatasourceCacheSizeRequest { Size = input });

        Assert.Null(bytes);
    }

    [Fact]
    public async Task InvalidInput_IsRejectedBeforeStateMutation()
    {
        var stateService = DispatchProxy.Create<IStateService, RecordingStateService>();
        var controller = new DatasourceConfigurationController(
            stateService,
            datasourceService: null!,
            cacheManagementService: null!,
            dashboardBatchService: null!);

        await Assert.ThrowsAsync<ValidationException>(() => controller.SetCacheSizeAsync(
            "default",
            new SetDatasourceCacheSizeRequest { Size = "not-a-size" }));

        Assert.False(((RecordingStateService)(object)stateService).SetOverrideCalled);
    }

    [Fact]
    public void Resolution_UsesManualThenDockerAndIgnoresDisabledDatasources()
    {
        var datasources = new[]
        {
            new ResolvedDatasource { Name = "alpha", Enabled = true },
            new ResolvedDatasource { Name = "beta", Enabled = true },
            new ResolvedDatasource { Name = "disabled", Enabled = false }
        };
        var overrides = new Dictionary<string, long> { ["ALPHA"] = 200 };

        var resolutions = CacheManagementService.ResolveDatasourceCacheSizes(
            datasources,
            overrides,
            detectedBytes: 100,
            detectedSource: CacheSizeSource.Docker);

        Assert.Collection(
            resolutions,
            alpha =>
            {
                Assert.Equal("alpha", alpha.DatasourceName);
                Assert.Equal(200, alpha.OverrideBytes);
                Assert.Equal(200, alpha.ResolvedBytes);
                Assert.Equal(CacheSizeSource.Manual, alpha.Source);
            },
            beta =>
            {
                Assert.Equal("beta", beta.DatasourceName);
                Assert.Null(beta.OverrideBytes);
                Assert.Equal(100, beta.ResolvedBytes);
                Assert.Equal(CacheSizeSource.Docker, beta.Source);
            });
        Assert.Equal(300, CacheManagementService.SumKnownConfiguredSizes(resolutions));
    }

    [Fact]
    public void Resolution_UsesEnvThenFullDiskWhenNoLimitIsDetected()
    {
        var datasource = new[] { new ResolvedDatasource { Name = "default", Enabled = true } };

        var fromEnv = CacheManagementService.ResolveDatasourceCacheSizes(
            datasource,
            new Dictionary<string, long>(),
            detectedBytes: 400,
            detectedSource: CacheSizeSource.Env);
        var fullDisk = CacheManagementService.ResolveDatasourceCacheSizes(
            datasource,
            new Dictionary<string, long>(),
            detectedBytes: 0,
            detectedSource: CacheSizeSource.FullDisk);

        Assert.Equal(CacheSizeSource.Env, Assert.Single(fromEnv).Source);
        Assert.Equal(400, CacheManagementService.SumKnownConfiguredSizes(fromEnv));
        Assert.Equal(CacheSizeSource.FullDisk, Assert.Single(fullDisk).Source);
        Assert.Equal(0, Assert.Single(fullDisk).ResolvedBytes);
        Assert.Equal(0, CacheManagementService.SumKnownConfiguredSizes(fullDisk));
    }

    [Fact]
    public void WriteEndpoint_IsAdminOnlyAndUsesExpectedRoute()
    {
        var method = typeof(DatasourceConfigurationController)
            .GetMethod(nameof(DatasourceConfigurationController.SetCacheSizeAsync))!;

        var authorize = method.GetCustomAttributes<AuthorizeAttribute>()
            .Single(attribute => attribute.Policy == "AdminOnly");
        var route = method.GetCustomAttribute<HttpPutAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal("{datasourceName}/cache-size", route?.Template);
    }

    [Fact]
    public void ConfiguredAndManualPaths_CallSharedParser()
    {
        var cacheService = File.ReadAllText(GetRepositoryPath(
            "Api", "LancacheManager", "Core", "Services", "CacheManagementService.cs"));
        var controller = File.ReadAllText(GetRepositoryPath(
            "Api", "LancacheManager", "Controllers", "DatasourceConfigurationController.cs"));
        var systemController = File.ReadAllText(GetRepositoryPath(
            "Api", "LancacheManager", "Controllers", "SystemController.cs"));

        Assert.Contains("CacheSizeParser.TryParse(value", cacheService, StringComparison.Ordinal);
        Assert.Contains("CacheSizeParser.TryParse(request.Size", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("ParseCacheSize(", cacheService, StringComparison.Ordinal);
        Assert.Contains("CacheSizeOverrideBytes = cacheSize.OverrideBytes", systemController, StringComparison.Ordinal);
        Assert.Contains("ResolvedCacheSizeBytes = cacheSize.ResolvedBytes", systemController, StringComparison.Ordinal);
        Assert.Contains("CacheSizeSource = cacheSize.Source.ToWireValue()", systemController, StringComparison.Ordinal);
        Assert.Contains("InvalidateConfiguredCacheSize()", controller, StringComparison.Ordinal);
        Assert.Contains("InvalidateLiveCache()", controller, StringComparison.Ordinal);
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, Path.Combine(segments));
    }

    private class RecordingStateService : DispatchProxy
    {
        public bool SetOverrideCalled { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.SetDatasourceCacheSizeOverride))
            {
                SetOverrideCalled = true;
            }

            return targetMethod?.ReturnType == typeof(void)
                ? null
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
        }
    }
}
