using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

/// <summary>
/// Who can read api_key.txt. Anyone holding that string is an admin on this installation, and left
/// alone the container's umask is what decides who that is, so the file is cut back to the account
/// running the app as soon as it is written, on the first start and again on every rotation.
///
/// Windows has no one-call equivalent and the file keeps what it inherits from the data directory
/// there, so the mode itself is asserted only off Windows. Both platforms are held to the rest of
/// the rule: writing the key reports no failure, which is what catches the restriction throwing on
/// a platform it does not belong to.
/// </summary>
public sealed class ApiKeyFileModeTests : IDisposable
{
    private readonly string _root;
    private readonly string _keyPath;
    private readonly CapturingLogger<ApiKeyService> _logger = new();
    private readonly ApiKeyService _apiKeyService;

    public ApiKeyFileModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-api-key-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _keyPath = Path.Combine(_root, "api_key.txt");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = _keyPath
            })
            .Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        _apiKeyService = new ApiKeyService(_logger, configuration, pathResolver);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void AFreshKeyFile_IsReadableOnlyByTheAccountThatWroteIt()
    {
        _apiKeyService.GetApiKey();

        AssertOwnerOnly();
    }

    [Fact]
    public void ARotatedKeyFile_IsReadableOnlyByTheAccountThatWroteIt()
    {
        _apiKeyService.GetApiKey();
        _apiKeyService.RegenerateApiKey();

        AssertOwnerOnly();
    }

    private void AssertOwnerOnly()
    {
        Assert.True(File.Exists(_keyPath));
        Assert.DoesNotContain(_logger.Entries, entry => entry.Level == LogLevel.Error);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(_keyPath));
        }
    }
}
