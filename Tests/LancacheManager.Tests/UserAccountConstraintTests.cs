using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// The username and main-admin constraints have to live in the database rather than in a caller's
/// read-then-write check: two simultaneous create requests both pass an application check, and only
/// a unique index stops both of them from committing.
/// </summary>
public sealed class UserAccountConstraintTests
{
    // Two accounts sharing a name make the login lookup return whichever row comes back first.
    // The SQLite store creates the unique index the model declares, so a refusal here is the store
    // refusing the write, not code above it. [19b]
    [Fact]
    public async Task SecondAccountWithTheSameUsername_IsRefusedByTheStore()
    {
        await using var database = await TestDatabase.CreateAsync();

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(NewAccount("admin", isMainAdmin: true));
            await context.SaveChangesAsync();
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(NewAccount("admin", isMainAdmin: false));
            await Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
        }
    }

    // A race to create the first account can carry two different usernames, which the username
    // index lets through. This partial index is the one that makes the first account singular. [19b]
    [Fact]
    public async Task SecondMainAdmin_IsRefusedByTheStore()
    {
        await using var database = await TestDatabase.CreateAsync();

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(NewAccount("alice", isMainAdmin: true));
            await context.SaveChangesAsync();
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(NewAccount("bob", isMainAdmin: true));
            await Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
        }
    }

    // A second account holder may still be a plain user, so the partial index must only bind rows
    // that set the flag.
    [Fact]
    public async Task SecondAccountThatIsNotMainAdmin_IsAccepted()
    {
        await using var database = await TestDatabase.CreateAsync();

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(NewAccount("alice", isMainAdmin: true));
            context.UserAccounts.Add(NewAccount("bob", isMainAdmin: false));
            await context.SaveChangesAsync();
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            Assert.Equal(2, await context.UserAccounts.CountAsync());
        }
    }

    // PostgreSQL gives no case-insensitive uniqueness on a text column, so the case half of the
    // constraint rides on the citext column type. The suite runs on SQLite, whose BINARY collation
    // would not reject "Admin" after "admin" whatever the column type says, so this asserts the
    // schema the PostgreSQL provider emits instead of asserting an insert it cannot observe. [19b]
    [Fact]
    public void PostgresSchema_MakesUsernameCitextAndUnique()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=lancache;Username=lancache;Password=lancache")
            .Options;
        using var context = new AppDbContext(options);

        var script = context.Database.GenerateCreateScript();

        Assert.Contains("CREATE EXTENSION IF NOT EXISTS citext", script, StringComparison.Ordinal);
        Assert.Contains("\"Username\" citext NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_UserAccounts_Username\" ON \"UserAccounts\" (\"Username\")", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_UserAccounts_IsMainAdmin\" ON \"UserAccounts\" (\"IsMainAdmin\") WHERE \"IsMainAdmin\"", script, StringComparison.Ordinal);
    }

    // The account row is the source of truth for the role, so it has to survive a round trip in the
    // same lowercase form UserSession stores. [19]
    [Fact]
    public async Task RoleRoundTripsAsTheLowercaseString()
    {
        await using var database = await TestDatabase.CreateAsync();

        await using (var context = database.Factory.CreateDbContext())
        {
            var account = NewAccount("alice", isMainAdmin: false);
            account.Role = SessionType.User;
            context.UserAccounts.Add(account);
            await context.SaveChangesAsync();
        }

        await using (var context = database.Factory.CreateDbContext())
        {
            var stored = await context.Database
                .SqlQuery<string>($"SELECT \"Role\" AS \"Value\" FROM \"UserAccounts\"")
                .SingleAsync();
            Assert.Equal("user", stored);

            var account = await context.UserAccounts.SingleAsync();
            Assert.Equal(SessionType.User, account.Role);
        }
    }

    private static UserAccount NewAccount(string username, bool isMainAdmin) => new()
    {
        Id = Guid.NewGuid(),
        Username = username,
        PasswordHash = "hash",
        Role = SessionType.Admin,
        IsMainAdmin = isMainAdmin,
        CreatedAtUtc = DateTime.UtcNow
    };
}
