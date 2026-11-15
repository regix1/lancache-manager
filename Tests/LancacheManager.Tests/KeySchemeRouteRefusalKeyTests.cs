using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// When no log source says which cache-key scheme a datasource uses, the cache routes refuse a scan or
/// a removal before it starts. Every such route answers through one shared guard, and the answer
/// carries the typed key <c>errors.corruption.keySchemeUnavailable</c>, which the page translates, not
/// only the server's English sentence, which a zh reader would see untranslated.
/// </summary>
public sealed class KeySchemeRouteRefusalKeyTests : IDisposable
{
    private const string KeySchemeUnavailable = "errors.corruption.keySchemeUnavailable";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lm-key-scheme-route-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ARouteRefusedForAnUnknownKeyScheme_AnswersTheKeySchemeKey()
    {
        Directory.CreateDirectory(_root);
        var paths = new AuthCredentialFormatTests.TempDirPathResolver(_root);
        // The folders live under this test's own root: a relative path would land in the test process's
        // working folder, where another class's access log would give the cache a known key scheme.
        var datasourceConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:LogPath"] = Path.Combine(_root, "logs"),
                ["LanCache:CachePath"] = Path.Combine(_root, "cache")
            })
            .Build();
        var datasources = new DatasourceService(datasourceConfig, paths, NullLogger<DatasourceService>.Instance);
        var datasource = Assert.Single(datasources.GetDatasources());
        // A log folder with no log source in it: nothing says which cache-key recipe the cache uses.
        Directory.CreateDirectory(datasource.LogPath);
        Directory.CreateDirectory(datasource.CachePath);
        var capabilities = new DatasourceCapabilityService(datasources);
        Assert.True(capabilities.CheckAllCanMapLogicalObjects() is not null, "the datasource's key scheme is known, so the test proves nothing");

        object? refusal = CacheRouteGuards.DenyIfKeyDependentUnavailable(capabilities);

        var answer = Assert.IsAssignableFrom<ObjectResult>(refusal);
        var body = JsonSerializer.SerializeToElement(answer.Value, JsonSerializerOptions.Web);
        var stageKey = body.ValueKind == JsonValueKind.Object
            ? body.EnumerateObject()
                .Where(property => property.NameEquals("stageKey") && property.Value.ValueKind == JsonValueKind.String)
                .Select(property => property.Value.GetString())
                .FirstOrDefault()
            : null;
        Assert.True(
            stageKey == KeySchemeUnavailable,
            $"a cache route refused for an unknown key scheme answers {body}, whose stage key is not {KeySchemeUnavailable}, "
            + "so the page shows the English server sentence instead of the translated text");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
