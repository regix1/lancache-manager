using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class IntegrationStorageTests
{
    [Theory]
    [InlineData("Steam", "target")]
    [InlineData("Steam", "primary")]
    [InlineData("Steam", "shared")]
    [InlineData("Steam", "orphan")]
    [InlineData("Epic", "target")]
    [InlineData("Epic", "primary")]
    [InlineData("Epic", "shared")]
    [InlineData("Epic", "orphan")]
    [InlineData("Xbox", "target")]
    [InlineData("Xbox", "primary")]
    [InlineData("Xbox", "shared")]
    [InlineData("Xbox", "orphan")]
    public async Task AccountCleanupOnlyRemovesExactTargetsAcrossPlatforms(string platform, string ownership)
    {
        using var fixture = new IntegrationFixture();
        var target = fixture.Owner.AccountId!.Value;
        var preserved = fixture.Other.AccountId!.Value;
        Guid? owner = ownership switch { "target" => target, "primary" => preserved, "shared" => null, _ => Guid.NewGuid() };
        var directory = platform switch { "Steam" => fixture.Storage.GetAuthDirectory(), "Epic" => fixture.Epic.GetAuthDirectory(), _ => fixture.Xbox.GetAuthDirectory() };
        var path = Path.Combine(directory, "credentials.json");
        var json = platform switch
        {
            "Steam" => System.Text.Json.JsonSerializer.Serialize(new PersistedSteamAuthData { OwnerAccountId = owner, RefreshToken = "unreadable", SteamApiKey = "ENC2:unchanged" }),
            "Epic" => System.Text.Json.JsonSerializer.Serialize(new PersistedEpicAuthData { OwnerAccountId = owner, RefreshToken = "unreadable" }),
            _ => System.Text.Json.JsonSerializer.Serialize(new PersistedXboxAuthData { OwnerAccountId = owner, RefreshToken = "unreadable" })
        };
        File.WriteAllText(path, json);
        File.WriteAllText(path + ".tmp", json);
        Directory.CreateDirectory(Path.Combine(directory, "saved"));
        var saved = Path.Combine(directory, "saved", $"{target:N}.json");
        var kept = Path.Combine(directory, "saved", $"{preserved:N}.json");
        File.WriteAllText(saved, "target");
        File.WriteAllText(saved + ".tmp", "target scratch");
        File.WriteAllText(kept, "preserved");
        var ids = new HashSet<Guid> { target };
        await (platform switch
        {
            "Steam" => fixture.Storage.ClearAccountLoginsAsync(ids, CancellationToken.None),
            "Epic" => fixture.Epic.ClearAccountLoginsAsync(ids, CancellationToken.None),
            _ => fixture.Xbox.ClearAccountLoginsAsync(ids, CancellationToken.None)
        });
        Assert.False(File.Exists(saved));
        Assert.False(File.Exists(saved + ".tmp"));
        Assert.Equal("preserved", File.ReadAllText(kept));
        if (ownership == "target")
        {
            Assert.False(File.Exists(path + ".tmp"));
            if (platform == "Steam")
            {
                var retained = System.Text.Json.JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(path))!;
                Assert.Null(retained.OwnerAccountId);
                Assert.Null(retained.RefreshToken);
                Assert.Equal("ENC2:unchanged", retained.SteamApiKey);
            }
            else Assert.False(File.Exists(path));
        }
        else
        {
            Assert.Equal(json, File.ReadAllText(path));
            Assert.Equal(json, File.ReadAllText(path + ".tmp"));
        }
    }

    [Fact]
    public async Task InterruptedSteamReplacementResumesOnlyItsExactRetainedRecord()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var path = fixture.Storage.GetCredentialsFilePath();
        var persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(path))!;
        var retained = new PersistedSteamAuthData { Mode = "anonymous", SteamApiKey = persisted.SteamApiKey };
        File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(retained));
        await fixture.Storage.ClearAccountLoginsAsync(new HashSet<Guid> { fixture.Owner.AccountId!.Value }, CancellationToken.None);
        Assert.False(File.Exists(path + ".tmp"));
        var completed = System.Text.Json.JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(path))!;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(retained), System.Text.Json.JsonSerializer.Serialize(completed));
    }

    [Theory]
    [InlineData("Username", "preserved")]
    [InlineData("RefreshToken", "preserved")]
    [InlineData("Mode", "authenticated")]
    [InlineData("SteamApiKey", "different-key")]
    [InlineData("LastAuthenticated", "2026-09-13T00:00:00Z")]
    public async Task NearMatchScratchCannotBeTreatedAsInterruptedSteamReplacement(string property, string value)
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var path = fixture.Storage.GetCredentialsFilePath();
        var before = File.ReadAllBytes(path);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(path))!;
        var scratch = System.Text.Json.JsonSerializer.SerializeToNode(new PersistedSteamAuthData { SteamApiKey = persisted.SteamApiKey })!;
        scratch[property] = value;
        var json = scratch.ToJsonString();
        File.WriteAllText(path + ".tmp", json);
        await Assert.ThrowsAsync<IOException>(() => fixture.Storage.ClearAccountLoginsAsync(
            new HashSet<Guid> { fixture.Owner.AccountId!.Value }, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(json, File.ReadAllText(path + ".tmp"));
    }

    [Fact]
    public async Task PreservedScratchBlocksSteamReplacementUntilItIsResolved()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var path = fixture.Storage.GetCredentialsFilePath();
        var before = File.ReadAllBytes(path);
        var scratch = System.Text.Json.JsonSerializer.Serialize(new PersistedSteamAuthData
        {
            OwnerAccountId = fixture.Other.AccountId, RefreshToken = "preserved"
        });
        File.WriteAllText(path + ".tmp", scratch);
        await Assert.ThrowsAsync<IOException>(() => fixture.Storage.ClearAccountLoginsAsync(
            new HashSet<Guid> { fixture.Owner.AccountId!.Value }, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(scratch, File.ReadAllText(path + ".tmp"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"UnexpectedLogin\":\"preserved\"}")]
    public async Task MalformedScratchBlocksCleanupWithoutOverwritingActiveCredentials(string scratch)
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var path = fixture.Storage.GetCredentialsFilePath();
        var before = File.ReadAllBytes(path);
        File.WriteAllText(path + ".tmp", scratch);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => fixture.Storage.ClearAccountLoginsAsync(
            new HashSet<Guid> { fixture.Owner.AccountId!.Value }, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(scratch, File.ReadAllText(path + ".tmp"));
    }
    [Theory]
    [InlineData("ENC2:unreadable-key")]
    [InlineData(null)]
    public async Task AccountCleanupRetainsExactSteamKeyWithoutDecrypting(string? key)
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var path = fixture.Storage.GetCredentialsFilePath();
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new PersistedSteamAuthData
        {
            OwnerAccountId = fixture.Owner.AccountId,
            Mode = "authenticated",
            Username = "removed",
            RefreshToken = "unreadable-token",
            SteamApiKey = key,
            LastAuthenticated = DateTime.UtcNow
        }));
        File.Copy(fixture.SavedPath(fixture.Owner.AccountId!.Value), fixture.SavedPath(fixture.Owner.AccountId.Value) + ".tmp");
        await fixture.Storage.ClearAccountLoginsAsync(new HashSet<Guid> { fixture.Owner.AccountId.Value }, CancellationToken.None);
        var persisted = System.Text.Json.JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(path))!;
        Assert.Equal(key, persisted.SteamApiKey);
        Assert.Equal("anonymous", persisted.Mode);
        Assert.Null(persisted.OwnerAccountId);
        Assert.Null(persisted.Username);
        Assert.Null(persisted.RefreshToken);
        Assert.Null(persisted.LastAuthenticated);
        Assert.False(File.Exists(fixture.SavedPath(fixture.Owner.AccountId.Value)));
        Assert.False(File.Exists(fixture.SavedPath(fixture.Owner.AccountId.Value) + ".tmp"));
        fixture.Storage.MigrateFromStateJson(new SteamAuthState { Mode = SteamAuthMode.Authenticated, Username = "legacy", RefreshToken = "legacy-token" });
        Assert.Equal(key, System.Text.Json.JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(path))!.SteamApiKey);
        Assert.Null(fixture.FreshStorage().GetAuthData().RefreshToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountCleanupLeavesUntargetedAndOwnerlessBytesUntouched(bool ownerless)
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var path = fixture.Storage.GetCredentialsFilePath();
        var persisted = new PersistedSteamAuthData { OwnerAccountId = ownerless ? null : fixture.Owner.AccountId, RefreshToken = "unreadable", SteamApiKey = "unreadable-key" };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(persisted));
        File.Copy(path, path + ".tmp");
        var before = File.ReadAllBytes(path);
        var saved = File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId!.Value));
        await fixture.Storage.ClearAccountLoginsAsync(new HashSet<Guid> { fixture.Other.AccountId!.Value }, CancellationToken.None);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(before, File.ReadAllBytes(path + ".tmp"));
        Assert.Equal(saved, File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId.Value)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid")]
    [InlineData("{\"OwnerAccountId\":\"invalid\"}")]
    [InlineData("{\"OwnerAccountId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public async Task AccountCleanupRefusesUnreadableActiveOwnership(string json)
    {
        using var fixture = new IntegrationFixture();
        File.WriteAllText(fixture.Storage.GetCredentialsFilePath(), json);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => fixture.Storage.ClearAccountLoginsAsync(
            new HashSet<Guid> { fixture.Owner.AccountId!.Value }, CancellationToken.None));
        Assert.Equal(json, File.ReadAllText(fixture.Storage.GetCredentialsFilePath()));
    }

    [Fact]
    public async Task AccountCleanupFaultsAndCancellationNeverReportSuccess()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var ids = new HashSet<Guid> { fixture.Owner.AccountId!.Value };
        var active = File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath());
        fixture.Storage.FailRead = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Storage.ClearAccountLoginsAsync(ids, CancellationToken.None));
        fixture.Storage.FailRead = false;
        fixture.Storage.FailStoredWrite = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Storage.ClearAccountLoginsAsync(ids, CancellationToken.None));
        Assert.Equal(active, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
        fixture.Storage.FailStoredWrite = false;
        fixture.Storage.FailDelete = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Storage.ClearAccountLoginsAsync(ids, CancellationToken.None));
        fixture.Storage.FailDelete = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Storage.ClearAccountLoginsAsync(ids, cancellation.Token));
        Assert.Equal(active, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
        await fixture.Storage.ClearAccountLoginsAsync(ids, CancellationToken.None);
        Assert.Null(fixture.Storage.GetAuthData().OwnerAccountId);
    }

    [Fact]
    public async Task EmptyAccountCleanupDoesNotReadOrWriteAnyFile()
    {
        using var fixture = new IntegrationFixture();
        fixture.Storage.FailRead = true;
        fixture.Storage.FailDelete = true;
        fixture.Storage.FailStoredWrite = true;
        await fixture.Storage.ClearAccountLoginsAsync(new HashSet<Guid>(), CancellationToken.None);
        Assert.Equal(0, fixture.Storage.Writes);
    }
    [Fact]
    public void LegacySavedKeyIsIgnoredAndRemovedOnTheNextSavedWrite()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var savedPath = fixture.SavedPath(fixture.Owner.AccountId!.Value);
        File.Copy(fixture.Storage.GetCredentialsFilePath(), savedPath, overwrite: true);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(savedPath))!;
        legacy["SteamApiKey"] = "unused-legacy-key";
        File.WriteAllText(savedPath, legacy.ToJsonString());
        var restarted = fixture.FreshStorage();
        var saved = restarted.GetSavedLogin(fixture.Owner.AccountId.Value);
        Assert.Equal("token", saved.RefreshToken);
        Assert.Null(saved.SteamApiKey);
        restarted.SaveSavedLogin(fixture.Owner.AccountId.Value, saved);
        Assert.DoesNotContain("unused-legacy-key", File.ReadAllText(savedPath), StringComparison.Ordinal);
        Assert.Equal("key", restarted.GetAuthData().SteamApiKey);
    }

    [Fact]
    public async Task KeyValidationStartedBeforeReleaseCannotRestoreAKeyAfterwards()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var version = fixture.Storage.IntegrationReleaseVersion;
        await using (var release = await fixture.Storage.BeginIntegrationReleaseAsync(fixture.Owner))
            fixture.Storage.CompleteIntegrationRelease(release);
        var writes = fixture.Storage.Writes;
        Assert.Throws<LancacheManager.Middleware.ConflictException>(() => fixture.Storage.UpdateAuthData(
            auth => auth.SteamApiKey = "late", saveSavedLogin: false, expectedReleaseVersion: version));
        Assert.Equal(writes, fixture.Storage.Writes);
        Assert.Null(fixture.Storage.GetAuthData().SteamApiKey);
    }
    [Fact]
    public void ReturnedCredentialsAndFailedUpdatesCannotMutateTheActiveCache()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var before = File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath());
        fixture.Storage.GetAuthData().RefreshToken = "detached";
        Assert.Throws<InvalidOperationException>(() => fixture.Storage.UpdateAuthData(auth =>
        {
            auth.RefreshToken = "not-committed";
            throw new InvalidOperationException("Update interrupted");
        }));
        Assert.Equal("token", fixture.Storage.GetAuthData().RefreshToken);
        Assert.Equal(before, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
    }

    [Fact]
    public void FailedActiveWritePreservesBothFilesAndCache()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var before = File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath());
        var saved = File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId!.Value));
        fixture.Storage.FailActiveWrite = true;
        Assert.Throws<IOException>(() => fixture.Storage.UpdateAuthData(auth => auth.RefreshToken = "next"));
        Assert.Equal("token", fixture.Storage.GetAuthData().RefreshToken);
        Assert.Equal(before, File.ReadAllBytes(fixture.Storage.GetCredentialsFilePath()));
        Assert.Equal(saved, File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId!.Value)));
    }

    [Fact]
    public void SavedWriteFailureKeepsTheDurablyCommittedOwnerAndAllowsRepair()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        fixture.Storage.FailSavedWrite = true;
        Assert.Throws<IOException>(() => fixture.Storage.UpdateAuthData(auth => auth.RefreshToken = "next"));
        Assert.Equal("next", fixture.Storage.GetAuthData().RefreshToken);
        Assert.Equal("next", fixture.FreshStorage().GetAuthData().RefreshToken);
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner.AccountId!.Value).RefreshToken);
        fixture.Storage.FailSavedWrite = false;
        fixture.Storage.SaveAuthData(fixture.Storage.GetAuthData());
        Assert.Equal("next", fixture.Storage.GetSavedLogin(fixture.Owner.AccountId.Value).RefreshToken);
    }

    [Fact]
    public async Task FailedReleaseRetainsTheOwnerAndRejectsOtherAccounts()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        await using (var lease = await fixture.Storage.BeginIntegrationReleaseAsync(fixture.Owner))
        {
            fixture.Storage.FailDelete = true;
            Assert.Throws<IOException>(() => fixture.Storage.CompleteIntegrationRelease(lease));
        }
        Assert.Equal(fixture.Owner.AccountId, fixture.Storage.GetAuthData().OwnerAccountId);
        Assert.Equal("token", fixture.Storage.GetAuthData().RefreshToken);
        Assert.Equal("owned-by-another-account", fixture.Storage.GetIntegrationAccess(fixture.Other).OwnershipReason);
    }

    [Fact]
    public async Task GlobalKeyChangesDoNotWriteSavedLoginsOrGetRestoredBySlowLogin()
    {
        using var fixture = new IntegrationFixture();
        fixture.Storage.UpdateAuthData(auth => auth.SteamApiKey = "global", saveSavedLogin: false);
        Assert.True(fixture.Storage.GetIntegrationAccess(fixture.Owner).CanSignIn);
        var login = await fixture.Storage.BeginIntegrationLoginAsync(fixture.Owner);
        fixture.Storage.UpdateAuthData(auth => auth.SteamApiKey = null, saveSavedLogin: false);
        var auth = fixture.Credentials("new-token");
        auth.SteamApiKey = "stale-saved-key";
        Assert.True(fixture.Storage.CompleteIntegrationLogin(login, auth));
        Assert.Null(fixture.Storage.GetAuthData().SteamApiKey);
        Assert.Null(fixture.Storage.GetSavedLogin(fixture.Owner.AccountId!.Value).SteamApiKey);
        var saved = File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId.Value));
        fixture.Storage.UpdateAuthData(current => current.SteamApiKey = "updated", saveSavedLogin: false);
        Assert.Equal(saved, File.ReadAllBytes(fixture.SavedPath(fixture.Owner.AccountId.Value)));
        Assert.Equal(fixture.Owner.AccountId, fixture.Storage.GetAuthData().OwnerAccountId);
    }

    [Fact]
    public async Task CredentialDispatchLeaseOrdersReleaseAndPreventsStaleCommit()
    {
        using var fixture = new IntegrationFixture();
        fixture.Seed();
        var snapshot = fixture.Storage.GetIntegrationSnapshot();
        using var dispatch = await fixture.Storage.AcquireIntegrationLoginAsync(fixture.Owner);
        var releasing = fixture.Storage.BeginIntegrationReleaseAsync(fixture.Owner);
        Assert.False(releasing.IsCompleted);
        Assert.Equal("token", fixture.Storage.GetIntegrationLogin(dispatch).RefreshToken);
        dispatch.Dispose();
        await using var release = await releasing.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Storage.CompleteIntegrationRelease(release);
        Assert.Null(fixture.Storage.TryUpdateAuth(snapshot.Version, auth => auth.RefreshToken = "late"));
        Assert.Null(fixture.Storage.GetAuthData().OwnerAccountId);
        Assert.Equal("token", fixture.Storage.GetSavedLogin(fixture.Owner.AccountId!.Value).RefreshToken);
    }
}

internal sealed class IntegrationFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "integration-login-tests", Guid.NewGuid().ToString("N"));
    private readonly AuthCredentialFormatTests.TempDirPathResolver _paths;
    private readonly SecureStateEncryptionService _encryption;
    public IntegrationCaller Owner { get; } = new(Guid.NewGuid(), Guid.NewGuid(), true);
    public IntegrationCaller Other { get; } = new(Guid.NewGuid(), Guid.NewGuid(), true, true);
    public FailingStorage Storage { get; }
    public StateService State { get; }
    public EpicAuthStorageService Epic { get; }
    public XboxAuthStorageService Xbox { get; }

    public IntegrationFixture()
    {
        Directory.CreateDirectory(_root);
        _paths = new AuthCredentialFormatTests.TempDirPathResolver(_root);
        var keys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), _paths);
        _encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "keys"))), keys,
            NullLogger<SecureStateEncryptionService>.Instance);
        Storage = new FailingStorage(_paths, _encryption);
        Epic = new EpicAuthStorageService(NullLogger<EpicAuthStorageService>.Instance, _paths, _encryption);
        Xbox = new XboxAuthStorageService(NullLogger<XboxAuthStorageService>.Instance, _paths, _encryption);
        State = new StateService(NullLogger<StateService>.Instance, _paths, _encryption, Storage);
    }

    public SteamAuthStorageService FreshStorage() => new(NullLogger<SteamAuthStorageService>.Instance, _paths, _encryption);
    public SteamAuthData Credentials(string token = "token") => new()
    {
        OwnerAccountId = Owner.AccountId,
        Mode = "authenticated",
        Username = "steam-account",
        RefreshToken = token,
        SteamApiKey = "key"
    };
    public void Seed() => Storage.SaveAuthData(Credentials());
    public string SavedPath(Guid account) => Path.Combine(Storage.GetAuthDirectory(), "saved", $"{account:N}.json");
    public void Dispose() => Directory.Delete(_root, true);

    internal sealed class FailingStorage : SteamAuthStorageService
    {
        public bool FailActiveWrite { get; set; }
        public bool FailSavedWrite { get; set; }
        public bool FailDelete { get; set; }
        public bool FailRead { get; set; }
        public bool FailStoredWrite { get; set; }
        public int Writes { get; private set; }

        public FailingStorage(AuthCredentialFormatTests.TempDirPathResolver paths, SecureStateEncryptionService encryption)
            : base(NullLogger<SteamAuthStorageService>.Instance, paths, encryption) { }

        protected override PersistedSteamAuthData SaveFile(string path, SteamAuthData auth, bool savedLogin = false)
        {
            Writes++;
            if (savedLogin ? FailSavedWrite : FailActiveWrite) throw new IOException("Injected write failure");
            return base.SaveFile(path, auth, savedLogin);
        }

        protected override void DeleteFile(string path)
        {
            if (FailDelete) throw new IOException("Injected delete failure");
            base.DeleteFile(path);
        }

        protected override PersistedSteamAuthData? ReadFile(string path)
        {
            if (FailRead) throw new IOException("Injected read failure");
            return base.ReadFile(path);
        }

        protected override void WriteFile(string path, PersistedSteamAuthData persisted)
        {
            if (FailStoredWrite) throw new IOException("Injected persisted write failure");
            base.WriteFile(path, persisted);
        }
    }
}
