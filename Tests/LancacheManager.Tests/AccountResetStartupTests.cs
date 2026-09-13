using System.Data.Common;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LancacheManager.Tests;

[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountResetStartupTests
{
    [Fact]
    public async Task SetupOnlyBootDoesNotCreateTheResetIntent()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        await using var context = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
        Assert.False(await context.AccountResets.AnyAsync());
    }

    [Fact]
    public async Task ActualMigrationUpgradeCreatesJournalWithoutDeletingAccounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        await using var original = database.Factory.CreateDbContext();
        var schema = $"reset_upgrade_{Guid.NewGuid():N}";
        await original.Database.OpenConnectionAsync();
        await using (var command = original.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = $"CREATE SCHEMA \"{schema}\"";
            await command.ExecuteNonQueryAsync();
        }
        var settings = new NpgsqlConnectionStringBuilder(original.Database.GetConnectionString()) { SearchPath = $"{schema},public" };
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(settings.ConnectionString).Options;
        try
        {
            await using var context = new AppDbContext(options);
            var migrations = context.Database.GetMigrations().ToArray();
            var position = Array.IndexOf(migrations, "20260913210000_AddAccountReset");
            Assert.True(position > 0);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[position - 1]);
            var primary = Guid.NewGuid();
            context.UserAccounts.AddRange(AccountResetTests.Account(primary, true), AccountResetTests.Account(files.Owner.AccountId!.Value));
            await context.SaveChangesAsync();
            await migrator.MigrateAsync("20260913210000_AddAccountReset");
            Assert.Equal(2, await context.UserAccounts.CountAsync());
            Assert.Empty(await context.AccountResets.ToListAsync());
            Assert.False(context.Database.HasPendingModelChanges());
            await AccountResetTests.RunAsync(database, files, options);
            context.ChangeTracker.Clear();
            Assert.Equal(primary, (await context.UserAccounts.SingleAsync()).Id);
            Assert.NotNull((await context.AccountResets.SingleAsync()).CompletedAtUtc);
            await Assert.ThrowsAsync<NotSupportedException>(() => migrator.MigrateAsync(migrations[position - 1]));
            Assert.NotNull((await context.AccountResets.AsNoTracking().SingleAsync()).CompletedAtUtc);
        }
        finally
        {
            using var connection = new NpgsqlConnection(settings.ConnectionString);
            NpgsqlConnection.ClearPool(connection);
            await using var command = original.Database.GetDbConnection().CreateCommand();
            command.CommandText = $"DROP SCHEMA \"{schema}\" CASCADE";
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ConcurrentStartsWaitForTheSameDurableReset()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(AccountResetTests.Account(Guid.NewGuid(), true), AccountResetTests.Account(files.Owner.AccountId!.Value));
            await context.SaveChangesAsync();
        }
        var pause = new ResetPause();
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(pause).Options;
        var first = AccountResetTests.RunAsync(database, files, options);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = AccountResetTests.RunAsync(database, files);
        try
        {
            await using var context = database.Factory.CreateDbContext();
            await context.Database.OpenConnectionAsync();
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND classid = ((hashtextextended(current_database() || ':' || current_schema() || ':account-reset', 0) >> 32) & 4294967295)::oid AND objid = (hashtextextended(current_database() || ':' || current_schema() || ':account-reset', 0) & 4294967295)::oid)";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await command.ExecuteScalarAsync(timeout.Token) is not true)
                await Task.Delay(10, timeout.Token);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally { pause.Resume.TrySetResult(); }
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        await using var check = database.Factory.CreateDbContext();
        Assert.Single(await check.IdentityAuditEntries.ToListAsync());
        Assert.NotNull((await check.AccountResets.SingleAsync()).CompletedAtUtc);
        await AccountResetTests.RunAsync(database, files);
    }

    [Fact]
    public async Task CancellationWhileCleanupIsPendingLeavesARetryableJournalAndUnlocks()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(AccountResetTests.Account(Guid.NewGuid(), true), AccountResetTests.Account(files.Owner.AccountId!.Value));
            await context.SaveChangesAsync();
        }
        var pause = new ResetPause();
        var options = new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(pause).Options;
        using var cancellation = new CancellationTokenSource();
        var running = AccountResetTests.RunAsync(database, files, options, cancellation.Token);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await using (var check = database.Factory.CreateDbContext())
        {
            Assert.Null((await check.AccountResets.SingleAsync()).CompletedAtUtc);
            Assert.Equal(2, await check.UserAccounts.CountAsync());
        }
        await AccountResetTests.RunAsync(database, files).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ProgramAwaitsResetInsideNormalDatabaseStartupBeforeHostedServiceResolution()
    {
        var source = File.ReadAllText(Path.Combine(EndpointAuthorizationHost.FindRepositoryRoot(), "Api", "LancacheManager", "Program.cs"));
        var setupOnly = source.IndexOf("if (externalCredsMissing)", source.IndexOf("// IMPORTANT: Apply database migrations", StringComparison.Ordinal), StringComparison.Ordinal);
        var normal = source.IndexOf("else", setupOnly, StringComparison.Ordinal);
        var reset = source.IndexOf("await scope.ServiceProvider.GetRequiredService<AccountResetService>()", normal, StringComparison.Ordinal);
        var reconcile = source.IndexOf("await detectionDataService.ReconcileMissingDiskSummaryAsync()", normal, StringComparison.Ordinal);
        var hosted = source.IndexOf("app.Services.GetRequiredService<IServiceScheduleRegistry>()", normal, StringComparison.Ordinal);
        Assert.True(setupOnly >= 0 && normal > setupOnly && reset > normal && reconcile > reset && hosted > reconcile);
        Assert.Contains(".RunAsync(app.Lifetime.ApplicationStopping)", source, StringComparison.Ordinal);
    }
}

internal sealed class ResetPause : DbTransactionInterceptor
{
    private int _commits;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData evt, CancellationToken cancellationToken = default)
    {
        if (++_commits == 1)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
        }
        await base.TransactionCommittedAsync(transaction, evt, cancellationToken);
    }
}
