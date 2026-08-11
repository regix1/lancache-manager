using System.Net;
using System.Net.Http.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// Two usernames differing only in case are the same account, and no line of code says so. The login
/// lookup compares with <c>==</c> and the unique index is a plain btree over one column, so the whole
/// rule is the citext type AppDbContext gives the Username column.
///
/// That makes the column type the thing worth asserting, and it can only be asserted against
/// PostgreSQL. SQLite accepts citext as a type name, gives it text affinity and then compares
/// case-sensitively, so both assertions below written against the in-memory store would pass whatever
/// the column type said. This class joins the collection that migrates a real server instead.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class UsernameCaseTests
{
    private const string Password = "Username-Case-9";

    /// <summary>
    /// Two accounts one login lookup cannot tell apart is the state the index exists to prevent, and
    /// on a plain text column PostgreSQL would accept this second row.
    /// </summary>
    [Fact]
    public async Task ASecondAccountDifferingOnlyInCase_IsRefusedByTheStore()
    {
        using var host = new EndpointAuthorizationHost();
        var username = $"username-case-{Guid.NewGuid():N}";

        try
        {
            await AddAccountAsync(host, username);

            await using var context = await NewContextAsync(host);
            context.UserAccounts.Add(new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = username.ToUpperInvariant(),
                PasswordHash = "not reached",
                Role = SessionType.Admin,
                CreatedAtUtc = DateTime.UtcNow
            });

            await Assert.ThrowsAsync<DbUpdateException>(async () => await context.SaveChangesAsync());
        }
        finally
        {
            await RemoveAccountAsync(host, username);
        }
    }

    /// <summary>
    /// The other half of the same rule: a name the index refuses to duplicate has to be a name the
    /// sign-in accepts in any case, or an account could be locked out of an installation by the shift
    /// key.
    /// </summary>
    [Fact]
    public async Task SigningInWithADifferentCase_ReachesTheStoredAccount()
    {
        using var host = new EndpointAuthorizationHost();
        var username = $"username-case-{Guid.NewGuid():N}";

        try
        {
            await AddAccountAsync(host, username);

            using var client = host.Application.CreateClient();
            await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);

            using var login = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest
                {
                    ApiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey(),
                    Username = username.ToUpperInvariant(),
                    Password = Password
                });

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        finally
        {
            await RemoveAccountAsync(host, username);
        }
    }

    /// <summary>
    /// Hashed by the application's own hasher, so the sign-in below accepts the password. The host's
    /// own account helper mints a name of its own and these tests need a name they can vary the case
    /// of.
    /// </summary>
    private static async Task AddAccountAsync(EndpointAuthorizationHost host, string username)
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = host.Application.Services
            .GetRequiredService<IPasswordHasher<UserAccount>>()
            .HashPassword(account, Password);

        await using var context = await NewContextAsync(host);
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The whole collection shares one database that is never dropped, and classes in it read the
    /// account table expecting only their own rows, so what these tests seed has to go again.
    /// </summary>
    private static async Task RemoveAccountAsync(EndpointAuthorizationHost host, string username)
    {
        await using var context = await NewContextAsync(host);
        await context.UserAccounts.Where(a => a.Username == username).ExecuteDeleteAsync();
    }

    private static Task<AppDbContext> NewContextAsync(EndpointAuthorizationHost host) =>
        host.Application.Services
            .GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();
}
