using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Stored credentials come in three shapes and each is treated differently, which nothing covered
/// before: current (ENC2), an older encrypted format (ENC), and plaintext from the oldest installs.
/// Plaintext has to be assumed exposed, so it is thrown away and the user signs in again rather than
/// being wrapped and kept. Getting this wrong either strands a secret in the clear or signs people
/// out who did not need to be.
///
/// Each case builds its file through the service's own save path and then edits the stored secret,
/// so the fixtures cannot drift from the real on-disk format.
/// </summary>
public sealed class AuthCredentialFormatTests : IDisposable
{
    private const string LegacyProtectorPurpose = "LancacheManager.SteamAuth.v1";

    private readonly string _root;
    private readonly TempDirPathResolver _paths;
    private readonly IDataProtectionProvider _dataProtection;
    private readonly SecureStateEncryptionService _encryption;

    public AuthCredentialFormatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lcm-auth-format-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        _paths = new TempDirPathResolver(_root);
        var apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), _paths);
        _dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
        _encryption = new SecureStateEncryptionService(
            _dataProtection, apiKeyService, NullLogger<SecureStateEncryptionService>.Instance);
    }

    [Fact]
    public void PlaintextEpicCredentials_AreDiscardedAndTheFileRemoved()
    {
        var path = EpicCredentialsPath();
        NewEpicStorage().SaveAuthData(new EpicAuthData { RefreshToken = "epic-secret" });
        ReplaceStoredSecret(path, "epic-secret");

        // A fresh instance so the read comes off disk rather than the previous one's cache.
        var loaded = NewEpicStorage().GetAuthData();

        Assert.Null(loaded.RefreshToken);
        Assert.False(File.Exists(path), "a credentials file stored in the clear must not survive the read");
    }

    [Fact]
    public void CurrentFormatEpicCredentials_AreReadWithoutRewritingTheFile()
    {
        var path = EpicCredentialsPath();
        NewEpicStorage().SaveAuthData(new EpicAuthData { RefreshToken = "epic-secret" });
        var before = File.ReadAllText(path);

        var loaded = NewEpicStorage().GetAuthData();

        Assert.Equal("epic-secret", loaded.RefreshToken);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void LegacyEncryptedEpicCredentials_AreRewrittenWithTheCurrentKey()
    {
        var path = EpicCredentialsPath();
        NewEpicStorage().SaveAuthData(new EpicAuthData { RefreshToken = "epic-secret" });
        var legacy = "ENC:" + _dataProtection.CreateProtector(LegacyProtectorPurpose).Protect("epic-secret");
        ReplaceStoredSecret(path, legacy);

        var loaded = NewEpicStorage().GetAuthData();

        // Still signed in, and the file no longer holds a secret under the old key.
        Assert.Equal("epic-secret", loaded.RefreshToken);
        Assert.True(File.Exists(path), "a v1 file is upgraded in place, never discarded");
        Assert.DoesNotContain("\"ENC:", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SteamCredentialsWithOnePlaintextSecret_RemoveTheWholeFile()
    {
        var path = SteamCredentialsPath();
        NewSteamStorage().SaveAuthData(new SteamAuthData
        {
            RefreshToken = "steam-token",
            SteamApiKey = "steam-api-key"
        });

        // Only the refresh token goes back to plaintext. Steam deliberately survives one secret
        // failing, so without an explicit check this half-file would have kept the token in the clear.
        ReplaceStoredSecret(path, "steam-token");

        var loaded = NewSteamStorage().GetAuthData();

        Assert.Null(loaded.RefreshToken);
        Assert.Null(loaded.SteamApiKey);
        Assert.False(File.Exists(path), "one secret in the clear must take the whole file with it");
    }

    [Fact]
    public void SteamLegacyEncryptedCredentials_AreRewrittenWithTheCurrentKey()
    {
        var path = SteamCredentialsPath();
        NewSteamStorage().SaveAuthData(new SteamAuthData
        {
            RefreshToken = "steam-token",
            SteamApiKey = "steam-api-key"
        });
        ReplaceStoredSecret(path, "ENC:" + LegacyProtect("steam-token"));

        var loaded = NewSteamStorage().GetAuthData();

        // Both secrets survive, and the rewrite only runs because both decrypted.
        Assert.Equal("steam-token", loaded.RefreshToken);
        Assert.Equal("steam-api-key", loaded.SteamApiKey);
        Assert.True(File.Exists(path), "a v1 file is upgraded in place, never discarded");
        Assert.DoesNotContain("\"ENC:", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void XboxCredentialsWithOnePlaintextSecret_RemoveTheWholeFile()
    {
        var path = XboxCredentialsPath();
        NewXboxStorage().SaveAuthData(new XboxAuthData
        {
            RefreshToken = "xbox-token",
            DeviceKeyPkcs8 = "xbox-device-key"
        });

        // Only the refresh token goes back to plaintext; the device key stays encrypted. Xbox holds
        // two secrets, so this is the shape that let a plaintext one survive on Steam.
        ReplaceStoredSecret(path, "xbox-token");

        var loaded = NewXboxStorage().GetAuthData();

        Assert.Null(loaded.RefreshToken);
        Assert.Null(loaded.DeviceKeyPkcs8);
        Assert.False(File.Exists(path), "one secret in the clear must take the whole file with it");
    }

    [Fact]
    public void XboxLegacyEncryptedCredentials_AreRewrittenWithTheCurrentKey()
    {
        var path = XboxCredentialsPath();
        NewXboxStorage().SaveAuthData(new XboxAuthData
        {
            RefreshToken = "xbox-token",
            DeviceKeyPkcs8 = "xbox-device-key"
        });
        ReplaceStoredSecret(path, "ENC:" + LegacyProtect("xbox-token"));

        var loaded = NewXboxStorage().GetAuthData();

        Assert.Equal("xbox-token", loaded.RefreshToken);
        Assert.Equal("xbox-device-key", loaded.DeviceKeyPkcs8);
        Assert.True(File.Exists(path), "a v1 file is upgraded in place, never discarded");
        Assert.DoesNotContain("\"ENC:", File.ReadAllText(path), StringComparison.Ordinal);
    }

    private string LegacyProtect(string value) =>
        _dataProtection.CreateProtector(LegacyProtectorPurpose).Protect(value);

    private EpicAuthStorageService NewEpicStorage() => new(
        NullLogger<EpicAuthStorageService>.Instance, _paths, _encryption);

    private SteamAuthStorageService NewSteamStorage() => new(
        NullLogger<SteamAuthStorageService>.Instance, _paths, _encryption);

    private XboxAuthStorageService NewXboxStorage() => new(
        NullLogger<XboxAuthStorageService>.Instance, _paths, _encryption);

    private string EpicCredentialsPath() =>
        Path.Combine(_paths.GetSecurityDirectory(), "epic_auth", "credentials.json");

    private string SteamCredentialsPath() =>
        Path.Combine(_paths.GetSecurityDirectory(), "steam_auth", "credentials.json");

    private string XboxCredentialsPath() =>
        Path.Combine(_paths.GetSecurityDirectory(), "xbox_auth", "credentials.json");

    /// <summary>
    /// Swaps the first stored ENC2 secret for <paramref name="replacement"/>, leaving the rest of the
    /// file exactly as the service wrote it.
    /// </summary>
    private static void ReplaceStoredSecret(string path, string replacement)
    {
        var json = File.ReadAllText(path);
        var start = json.IndexOf("\"ENC2:", StringComparison.Ordinal);
        Assert.True(start >= 0, "expected the saved file to hold an ENC2 secret");
        var end = json.IndexOf('"', start + 1);
        Assert.True(end > start, "expected the ENC2 secret to be a closed JSON string");

        File.WriteAllText(path, json[..(start + 1)] + replacement + json[end..]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not worth failing a test over.
        }
    }

    private sealed class TempDirPathResolver : PathResolverBase
    {
        private readonly string _basePath;

        public TempDirPathResolver(string basePath) : base(NullLogger.Instance)
        {
            _basePath = basePath;
        }

        protected override string BasePath => _basePath;
        protected override string RustExecutableExtension => string.Empty;

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }
}
