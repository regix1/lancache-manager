using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class AccountResetTests
{
    [Fact]
    public async Task FirstRunRemovesOnlyFrozenSecondaryAccountsAndAllTheirSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        var primary = Guid.NewGuid();
        var targets = new[] { files.Owner.AccountId!.Value, files.Other.AccountId!.Value };
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(Account(primary, true));
            context.UserAccounts.Add(Account(targets[0], false, SessionType.Admin));
            var disabled = Account(targets[1]);
            disabled.IsDisabled = true;
            context.UserAccounts.Add(disabled);
            foreach (var id in targets.Append(primary))
                foreach (var state in new[] { "live", "expired", "revoked" })
                    context.UserSessions.Add(Session(id, state));
            context.UserSessions.Add(Session(null, "live"));
            await context.SaveChangesAsync();
            Assert.Equal(3, await context.UserAccounts.CountAsync());
            Assert.Equal(10, await context.UserPreferences.CountAsync());
        }
        files.Seed();
        files.Epic.SaveAuthData(new EpicAuthData { OwnerAccountId = targets[0], RefreshToken = "epic", AccountId = "epic-account" });
        files.Xbox.SaveAuthData(new XboxAuthData { OwnerAccountId = targets[1], RefreshToken = "xbox", DeviceKeyPkcs8 = "device-key" });
        var key = JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(files.Storage.GetCredentialsFilePath()))!.SteamApiKey;
        await RunAsync(database, files);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(primary, (await check.UserAccounts.SingleAsync()).Id);
        Assert.Equal(4, await check.UserSessions.CountAsync());
        Assert.Equal(4, await check.UserPreferences.CountAsync());
        Assert.False(await check.UserSessions.AnyAsync(session => session.AccountId.HasValue && targets.Contains(session.AccountId.Value)));
        var reset = await check.AccountResets.SingleAsync();
        Assert.Equal(targets.Order(), reset.AccountIds);
        Assert.Equal(primary, reset.PrimaryAccountId);
        Assert.NotNull(reset.CompletedAtUtc);
        var audits = await check.IdentityAuditEntries.ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, audit => { Assert.Equal(IdentityAuditEvent.AccountDeleted, audit.Event); Assert.Null(audit.PerformedByAccountId); Assert.Null(audit.PerformedBySessionId); });
        Assert.Equal(targets.Order(), audits.Select(audit => audit.TargetAccountId!.Value).Order());
        Assert.Equal(key, JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(files.Storage.GetCredentialsFilePath()))!.SteamApiKey);
        Assert.False(File.Exists(files.Epic.GetCredentialsFilePath()));
        Assert.False(File.Exists(files.Xbox.GetCredentialsFilePath()));
        foreach (var target in targets)
            foreach (var directory in new[] { files.Storage.GetAuthDirectory(), files.Epic.GetAuthDirectory(), files.Xbox.GetAuthDirectory() })
                Assert.False(File.Exists(Path.Combine(directory, "saved", $"{target:N}.json")));
    }

    [Fact]
    public async Task CompletedResetNeverDiscoversLaterAccountsOrCredentials()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        await RunAsync(database, files);
        var later = files.Owner.AccountId!.Value;
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(Account(later));
            context.UserSessions.Add(Session(later, "live"));
            await context.SaveChangesAsync();
        }
        files.Seed();
        var before = File.ReadAllBytes(files.Storage.GetCredentialsFilePath());
        files.Storage.FailRead = true;
        files.Storage.FailDelete = true;
        files.Storage.FailStoredWrite = true;
        var fault = new ResetCommandFault("UserAccounts");
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(fault).Options;
        await RunAsync(database, files, options);
        await RunAsync(database, files, options);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(later, (await check.UserAccounts.SingleAsync()).Id);
        Assert.Single(await check.UserSessions.ToListAsync());
        Assert.Empty(await check.IdentityAuditEntries.ToListAsync());
        Assert.Equal(before, File.ReadAllBytes(files.Storage.GetCredentialsFilePath()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedModePreservesAccountlessSessionsAndAccessMode(bool withAccounts)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        files.State.UpdateAccess(access => { access.Mode = AccountMode.Unauthenticated; access.SetupVersion = AccessSettings.RequiredSetupVersion; });
        var primary = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            if (withAccounts) context.UserAccounts.AddRange(Account(primary, true), Account(files.Owner.AccountId!.Value));
            context.UserSessions.Add(Session(null, "live"));
            await context.SaveChangesAsync();
        }
        files.Storage.SaveAuthData(new SteamAuthData { OwnerAccountId = withAccounts ? primary : null, RefreshToken = "shared-active", Mode = "authenticated" });
        var before = File.ReadAllBytes(files.Storage.GetCredentialsFilePath());
        await RunAsync(database, files);
        Assert.Equal(before, File.ReadAllBytes(files.Storage.GetCredentialsFilePath()));
        Assert.Equal(AccountMode.Unauthenticated, files.State.GetState().Access.Mode);
        await using var check = database.Factory.CreateDbContext();
        Assert.Single(await check.UserPreferences.ToListAsync());
        var shared = new IntegrationCaller(null, null, false);
        using var lease = await files.Storage.AcquireIntegrationLoginAsync(shared);
        Assert.Equal("shared-active", files.Storage.GetIntegrationLogin(lease).RefreshToken);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("multiple")]
    [InlineData("role")]
    public async Task InvalidPrimaryFailsBeforeIntentOrFileCleanup(string condition)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        files.Seed();
        await using (var context = database.Factory.CreateDbContext())
        {
            if (condition == "multiple") await context.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_UserAccounts_IsMainAdmin\"");
            context.UserAccounts.Add(Account(files.Owner.AccountId!.Value));
            if (condition != "missing") context.UserAccounts.Add(Account(Guid.NewGuid(), true, condition == "role" ? SessionType.User : SessionType.Admin));
            if (condition == "multiple") context.UserAccounts.Add(Account(Guid.NewGuid(), true));
            await context.SaveChangesAsync();
        }
        var before = File.ReadAllBytes(files.Storage.GetCredentialsFilePath());
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(database, files));
        await using var check = database.Factory.CreateDbContext();
        Assert.Empty(await check.AccountResets.ToListAsync());
        Assert.Equal(before, File.ReadAllBytes(files.Storage.GetCredentialsFilePath()));
        Assert.True(File.Exists(files.SavedPath(files.Owner.AccountId!.Value)));
    }

    [Fact]
    public async Task PendingRetryKeepsOriginalTargetsWhenAnotherAccountAppears()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        var primary = Guid.NewGuid();
        var later = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(Account(primary, true), Account(files.Owner.AccountId!.Value));
            await context.SaveChangesAsync();
        }
        files.Seed();
        files.Storage.FailDelete = true;
        await Assert.ThrowsAsync<IOException>(() => RunAsync(database, files));
        await using (var context = database.Factory.CreateDbContext())
        {
            var pending = await context.AccountResets.SingleAsync();
            Assert.Null(pending.CompletedAtUtc);
            Assert.Equal([files.Owner.AccountId!.Value], pending.AccountIds);
            context.UserAccounts.Add(Account(later));
            context.UserSessions.Add(Session(later, "live"));
            await context.SaveChangesAsync();
        }
        files.Storage.FailDelete = false;
        await RunAsync(database, files);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(new[] { primary, later }.Order(), await check.UserAccounts.OrderBy(account => account.Id).Select(account => account.Id).ToListAsync());
        Assert.Single(await check.UserSessions.ToListAsync());
        Assert.Single(await check.IdentityAuditEntries.ToListAsync());
    }

    [Theory]
    [InlineData("Epic")]
    [InlineData("Xbox")]
    public async Task LaterPlatformFailureRetainsFrozenIntentAndRetriesEarlierCleanup(string platform)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        var target = files.Owner.AccountId!.Value;
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(Account(Guid.NewGuid(), true), Account(target));
            context.UserSessions.Add(Session(target, "live"));
            await context.SaveChangesAsync();
        }
        files.Seed();
        files.Epic.SaveAuthData(new EpicAuthData { OwnerAccountId = target, RefreshToken = "epic" });
        files.Xbox.SaveAuthData(new XboxAuthData { OwnerAccountId = target, RefreshToken = "xbox" });
        var broken = platform == "Epic" ? files.Epic.GetCredentialsFilePath() : files.Xbox.GetCredentialsFilePath();
        File.WriteAllText(broken, "null");
        await Assert.ThrowsAsync<JsonException>(() => RunAsync(database, files));
        Assert.Null(JsonSerializer.Deserialize<PersistedSteamAuthData>(File.ReadAllText(files.Storage.GetCredentialsFilePath()))!.OwnerAccountId);
        if (platform == "Xbox") Assert.False(File.Exists(files.Epic.GetCredentialsFilePath()));
        await using (var check = database.Factory.CreateDbContext())
        {
            Assert.Null((await check.AccountResets.SingleAsync()).CompletedAtUtc);
            Assert.Equal(2, await check.UserAccounts.CountAsync());
            Assert.Single(await check.UserSessions.ToListAsync());
        }
        File.WriteAllText(broken, platform == "Epic"
            ? JsonSerializer.Serialize(new PersistedEpicAuthData { OwnerAccountId = target })
            : JsonSerializer.Serialize(new PersistedXboxAuthData { OwnerAccountId = target }));
        await RunAsync(database, files);
        await RunAsync(database, files);
        await using var completed = database.Factory.CreateDbContext();
        Assert.Single(await completed.IdentityAuditEntries.ToListAsync());
        Assert.NotNull((await completed.AccountResets.SingleAsync()).CompletedAtUtc);
    }

    [Theory]
    [InlineData("INSERT INTO \"AccountResets\"")]
    [InlineData("DELETE FROM \"UserSessions\"")]
    [InlineData("DELETE FROM \"UserAccounts\"")]
    [InlineData("INSERT INTO \"IdentityAuditEntries\"")]
    [InlineData("UPDATE \"AccountResets\"")]
    public async Task DatabaseFailureRollsBackItsPhaseAndRestartCompletesExactlyOnce(string sql)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(Account(Guid.NewGuid(), true), Account(files.Owner.AccountId!.Value));
            context.UserSessions.Add(Session(files.Owner.AccountId, "revoked"));
            await context.SaveChangesAsync();
        }
        files.Seed();
        var before = File.ReadAllBytes(files.Storage.GetCredentialsFilePath());
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(new ResetCommandFault(sql)).Options;
        if (sql.StartsWith("DELETE", StringComparison.Ordinal))
            await Assert.ThrowsAsync<IOException>(() => RunAsync(database, files, options));
        else
        {
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(database, files, options));
            Assert.IsType<IOException>(failure.InnerException);
        }
        await using (var context = database.Factory.CreateDbContext())
        {
            Assert.Equal(2, await context.UserAccounts.CountAsync());
            Assert.Single(await context.UserSessions.ToListAsync());
            Assert.Single(await context.UserPreferences.ToListAsync());
            Assert.Empty(await context.IdentityAuditEntries.ToListAsync());
            var pending = await context.AccountResets.SingleOrDefaultAsync();
            if (sql == "INSERT INTO \"AccountResets\"")
            {
                Assert.Null(pending);
                Assert.Equal(before, File.ReadAllBytes(files.Storage.GetCredentialsFilePath()));
            }
            else Assert.Null(Assert.IsType<AccountReset>(pending).CompletedAtUtc);
        }
        await RunAsync(database, files);
        await RunAsync(database, files);
        await using var check = database.Factory.CreateDbContext();
        Assert.Single(await check.UserAccounts.ToListAsync());
        Assert.Empty(await check.UserSessions.ToListAsync());
        Assert.Single(await check.IdentityAuditEntries.ToListAsync());
        Assert.NotNull((await check.AccountResets.SingleAsync()).CompletedAtUtc);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task CommitBoundaryFailureHasAnUnambiguousDurableRetry(int transaction, bool after)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(Account(Guid.NewGuid(), true), Account(files.Owner.AccountId!.Value));
            await context.SaveChangesAsync();
        }
        files.Seed();
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(new ResetCommitFault(transaction, after)).Options;
        await Assert.ThrowsAsync<IOException>(() => RunAsync(database, files, options));
        await using (var context = database.Factory.CreateDbContext())
        {
            var reset = await context.AccountResets.SingleOrDefaultAsync();
            if (transaction == 1 && !after) Assert.Null(reset);
            else Assert.Equal(transaction == 2 && after, reset!.CompletedAtUtc.HasValue);
        }
        await RunAsync(database, files);
        await RunAsync(database, files);
        await using var check = database.Factory.CreateDbContext();
        Assert.Single(await check.IdentityAuditEntries.ToListAsync());
        Assert.NotNull((await check.AccountResets.SingleAsync()).CompletedAtUtc);
    }

    [Fact]
    public async Task ResetRemovesOnlyTargetBindingsFromActiveLegacyAndPendingLogins()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        var primary = Guid.NewGuid();
        var target = files.Owner.AccountId!.Value;
        var orphan = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(Account(primary, true), Account(target));
            await context.SaveChangesAsync();
        }
        const string issuer = "https://identity.example.test";
        var login = new OidcSettings
        {
            Authority = issuer, ClientId = "retained-client", ClientSecret = "retained-secret",
            OwnerAccountId = primary, OwnerIssuer = issuer, OwnerSubject = "primary",
            AllowedSubjects = ["primary", "target", "orphan"],
            AccountIds = new Dictionary<string, Guid>
            {
                [AccessService.IdentityKey(issuer, "primary")] = primary,
                [AccessService.IdentityKey(issuer, "target")] = target,
                [AccessService.IdentityKey(issuer, "orphan")] = orphan
            }
        };
        files.State.UpdateAccess(access =>
        {
            access.Mode = AccountMode.Unauthenticated;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
            access.Logins = [login.Copy()];
            access.Oidc = login.Copy();
            access.PendingOidc = login.Copy();
            access.PendingOidc.OwnerAccountId = target;
            access.PendingOidc.OwnerSubject = "target";
        });
        await RunAsync(database, files);
        var access = files.State.GetState().Access;
        Assert.Equal(AccountMode.Unauthenticated, access.Mode);
        Assert.Equal(AccessSettings.RequiredSetupVersion, access.SetupVersion);
        Assert.All(access.Logins.Concat([access.Oidc!, access.PendingOidc!]), settings =>
        {
            Assert.DoesNotContain(target, settings.AccountIds.Values);
            Assert.Contains(primary, settings.AccountIds.Values);
            Assert.Contains(orphan, settings.AccountIds.Values);
            Assert.Equal(new[] { "primary", "orphan" }, settings.AllowedSubjects);
            Assert.Equal("retained-client", settings.ClientId);
            Assert.Equal("retained-secret", settings.ClientSecret);
            Assert.Equal(issuer, settings.Authority);
        });
        Assert.Equal(primary, access.Logins[0].OwnerAccountId);
        Assert.Equal(primary, access.Oidc!.OwnerAccountId);
        Assert.Null(access.PendingOidc!.OwnerAccountId);
        Assert.Null(access.PendingOidc.OwnerIssuer);
        Assert.Null(access.PendingOidc.OwnerSubject);
    }

    [Fact]
    public async Task PendingTargetAlreadyDeletedStillHasItsExactFilesRemoved()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        var primary = Guid.NewGuid();
        var target = files.Owner.AccountId!.Value;
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(Account(primary, true));
            context.AccountResets.Add(new AccountReset
            {
                Id = 1, PrimaryAccountId = primary, AccountIds = [target], StartedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
        files.Seed();
        await RunAsync(database, files);
        Assert.False(File.Exists(files.SavedPath(target)));
        Assert.Null(files.Storage.GetAuthData().OwnerAccountId);
        await using var check = database.Factory.CreateDbContext();
        Assert.Empty(await check.IdentityAuditEntries.ToListAsync());
        Assert.NotNull((await check.AccountResets.SingleAsync()).CompletedAtUtc);
    }

    [Fact]
    public async Task AccessWriteFailureRetainsPendingIntentAndAllDatabaseRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        files.State.GetState();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(Account(Guid.NewGuid(), true), Account(files.Owner.AccountId!.Value));
            await context.SaveChangesAsync();
        }
        var failures = typeof(StateService).GetField("_consecutiveFailures", BindingFlags.Instance | BindingFlags.NonPublic)!;
        failures.SetValue(files.State, 6);
        await Assert.ThrowsAsync<LancacheManager.Middleware.ServiceUnavailableException>(() => RunAsync(database, files));
        await using (var context = database.Factory.CreateDbContext())
        {
            Assert.Null((await context.AccountResets.SingleAsync()).CompletedAtUtc);
            Assert.Equal(2, await context.UserAccounts.CountAsync());
        }
        failures.SetValue(files.State, 0);
        await RunAsync(database, files);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("target")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public async Task ChangedOrCorruptPendingIdentityStopsBeforeFurtherCleanup(string change)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        var primary = Guid.NewGuid();
        var target = files.Owner.AccountId!.Value;
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(Account(primary, true), Account(target));
            context.AccountResets.Add(new AccountReset { Id = 1, PrimaryAccountId = primary, AccountIds = [target], StartedAtUtc = DateTime.UtcNow });
            await context.SaveChangesAsync();
            if (change is "primary" or "target")
            {
                await context.UserAccounts.Where(account => account.Id == primary).ExecuteUpdateAsync(update => update.SetProperty(account => account.IsMainAdmin, false));
                if (change == "target") await context.UserAccounts.Where(account => account.Id == target).ExecuteUpdateAsync(update => update.SetProperty(account => account.IsMainAdmin, true));
            }
            else
            {
                var reset = await context.AccountResets.SingleAsync();
                reset.AccountIds = change == "duplicate" ? [target, target] : [Guid.Empty];
                await context.SaveChangesAsync();
            }
        }
        files.Seed();
        var before = File.ReadAllBytes(files.Storage.GetCredentialsFilePath());
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(database, files));
        Assert.Equal(before, File.ReadAllBytes(files.Storage.GetCredentialsFilePath()));
        Assert.True(File.Exists(files.SavedPath(target)));
    }

    internal static UserAccount Account(Guid id, bool primary = false, SessionType? role = null) => new()
    {
        Id = id, Username = id.ToString("N"), PasswordHash = "unchanged-password", IsMainAdmin = primary,
        Role = role ?? (primary ? SessionType.Admin : SessionType.User), CreatedAtUtc = DateTime.UtcNow
    };

    internal static UserSession Session(Guid? accountId, string state) => new()
    {
        Id = Guid.NewGuid(), AccountId = accountId, SessionTokenHash = Guid.NewGuid().ToString("N"),
        PreviousSessionTokenHash = Guid.NewGuid().ToString("N"), SessionType = accountId.HasValue ? SessionType.User : SessionType.Guest,
        CreatedAtUtc = DateTime.UtcNow, LastSeenAtUtc = DateTime.UtcNow,
        ExpiresAtUtc = DateTime.UtcNow.AddDays(state == "expired" ? -1 : 1), IsRevoked = state == "revoked",
        Preferences = new UserPreferences { SelectedTheme = "dark" }
    };

    internal static async Task RunAsync(TestDatabase database, IntegrationFixture files, DbContextOptions<AppDbContext>? options = null, CancellationToken cancellationToken = default)
    {
        await using var context = new AppDbContext(options ?? database.Options);
        using var services = new ServiceCollection()
            .AddSingleton<SteamAuthStorageService>(files.Storage).AddSingleton(files.Epic).AddSingleton(files.Xbox)
            .AddSingleton(new AccessService(files.State, new ConfigurationBuilder().Build(), database.Factory))
            .BuildServiceProvider();
        await new AccountResetService(context, services, NullLogger<AccountResetService>.Instance).RunAsync(cancellationToken);
    }
}

internal sealed class ResetCommandFault(string contains) : DbCommandInterceptor
{
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData evt,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains(contains, StringComparison.Ordinal)) throw new IOException("Injected database command failure");
        return base.ReaderExecutingAsync(command, evt, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData evt,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains(contains, StringComparison.Ordinal)) throw new IOException("Injected database command failure");
        return base.NonQueryExecutingAsync(command, evt, result, cancellationToken);
    }
}

internal sealed class ResetCommitFault(int transactionNumber, bool after) : DbTransactionInterceptor
{
    private int _commits;

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData evt,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        _commits++;
        if (!after && _commits == transactionNumber) throw new IOException("Injected commit failure");
        return base.TransactionCommittingAsync(transaction, evt, result, cancellationToken);
    }

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData evt, CancellationToken cancellationToken = default)
    {
        if (after && _commits == transactionNumber) throw new IOException("Injected interruption after commit");
        return base.TransactionCommittedAsync(transaction, evt, cancellationToken);
    }
}
