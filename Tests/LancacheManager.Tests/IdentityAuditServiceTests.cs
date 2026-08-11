using System.Reflection;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The audit trail answers "who did this" for the events that decide who can sign in and what they
/// may do. Two properties matter as much as the row itself: a caller with no account still produces
/// a row, and a write that fails never reaches the operation being recorded.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class IdentityAuditServiceTests
{
    public static TheoryData<IdentityAuditEvent> EveryEvent()
    {
        var events = new TheoryData<IdentityAuditEvent>();

        foreach (var value in Enum.GetValues<IdentityAuditEvent>())
        {
            events.Add(value);
        }

        return events;
    }

    // The events named in the scope of the trail. Pinned by name so that removing one, or renaming
    // one out from under a caller, fails here rather than silently dropping a record.
    [Fact]
    public void EveryScopedIdentityEventHasAName()
    {
        Assert.Equal(
            new[]
            {
                "AccountCreated",
                "AccountDeleted",
                "AccountDisabled",
                "AccountEnabled",
                "RoleChanged",
                "PasswordChanged",
                "LoginSucceeded",
                "LoginFailed",
                "ApiKeyRotated",
                "MainAdminPasswordRecovered"
            },
            Enum.GetNames<IdentityAuditEvent>());
    }

    // Every event writes one row carrying the actor, the target and the time.
    [Theory]
    [MemberData(nameof(EveryEvent))]
    public async Task RecordedEventKeepsTheActorTheTargetAndTheTime(IdentityAuditEvent auditEvent)
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance);

        var actorAccountId = Guid.NewGuid();
        var actorSessionId = Guid.NewGuid();
        var targetAccountId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        await service.RecordAsync(auditEvent, actorAccountId, actorSessionId, targetAccountId);

        var after = DateTime.UtcNow;
        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(auditEvent, entry.Event);
        Assert.Equal(actorAccountId, entry.PerformedByAccountId);
        Assert.Equal(actorSessionId, entry.PerformedBySessionId);
        Assert.Equal(targetAccountId, entry.TargetAccountId);
        Assert.InRange(entry.PerformedAtUtc, before, after);
    }

    // A rotation carried out with only an API key has no account and no session behind it. The row
    // has to exist with a null actor rather than the write throwing.
    [Fact]
    public async Task EventFromACallerWithNoAccountIsRecordedWithANullActor()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance);

        await service.RecordAsync(IdentityAuditEvent.ApiKeyRotated, null, null, null);

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.ApiKeyRotated, entry.Event);
        Assert.Null(entry.PerformedByAccountId);
        Assert.Null(entry.PerformedBySessionId);
        Assert.Null(entry.TargetAccountId);
    }

    // A logging fault must not become an outage: the write fails, the caller does not hear about
    // it, and the operation being recorded finishes.
    [Fact]
    public async Task FailedWriteDoesNotReachTheCaller()
    {
        var factory = new UnreachableDbContextFactory();
        var service = new IdentityAuditService(factory, NullLogger<IdentityAuditService>.Instance);

        // Without this the test would still pass if the write quietly started succeeding.
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateDbContextAsync());

        await service.RecordAsync(
            IdentityAuditEvent.LoginSucceeded,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid());
    }

    // The operation half of the same criterion. The account change is pending on the caller's own
    // context when the audit write fails; it still commits, and the caller's context is left with
    // nothing of the writer's tracked on it.
    [Fact]
    public async Task OperationStillCommitsAfterAFailedWrite()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new IdentityAuditService(
            new UnreachableDbContextFactory(),
            NullLogger<IdentityAuditService>.Instance);

        var accountId = Guid.NewGuid();

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = accountId,
                Username = "alice",
                PasswordHash = "hash",
                Role = SessionType.Admin,
                CreatedAtUtc = DateTime.UtcNow
            });

            await service.RecordAsync(IdentityAuditEvent.AccountCreated, null, null, accountId);

            Assert.Empty(context.ChangeTracker.Entries<IdentityAuditEntry>());
            await context.SaveChangesAsync();
        }

        await using var reader = database.Factory.CreateDbContext();
        Assert.True(await reader.UserAccounts.AnyAsync(a => a.Id == accountId));
        Assert.Empty(await reader.IdentityAuditEntries.ToListAsync());
    }

    // Resolved out of the running application rather than constructed here, because the registration is
    // the whole question: every other test in this class builds the writer by hand and passes while a
    // caller that injects it fails at startup. Asking the root provider twice also pins the lifetime -
    // a scoped registration cannot be resolved from the root, and a transient one would answer with two
    // different instances.
    [Fact]
    public async Task WriterResolvesFromTheRunningApplication()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();
        await host.AssertIsolationAsync(client);

        Assert.Same(
            host.Application.Services.GetRequiredService<IdentityAuditService>(),
            host.Application.Services.GetRequiredService<IdentityAuditService>());
    }

    // Append-only means the writer offers one thing to do with the table.
    [Fact]
    public void WriterOffersNoUpdateOrDeletePath()
    {
        var methods = typeof(IdentityAuditService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        Assert.Equal(new[] { nameof(IdentityAuditService.RecordAsync) }, methods);
    }
}

/// <summary>
/// Stands in for a database the writer cannot reach, which is the only way to observe what a failed
/// audit write does to the operation that asked for it.
/// </summary>
internal sealed class UnreachableDbContextFactory : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => throw new InvalidOperationException("No database.");

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No database.");
}
