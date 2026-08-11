using System.Net;
using System.Net.Http.Json;
using FluentValidation;
using LancacheManager.Controllers;
using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Validators;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// The password hasher the application registers and the one place an account's password rules live.
/// The hasher is read out of the running application rather than built here, because what Program.cs
/// configured is the whole question: a hasher constructed in the test would pass while the application
/// wrote every hash at a different iteration count.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountPasswordTests
{
    private const string Password = "Xk8!vqR2tzLm";

    /// <summary>
    /// Microsoft.Extensions.Identity.Core is part of the Microsoft.AspNetCore.App shared framework, so
    /// PasswordHasher&lt;T&gt; compiles with nothing added to the project.
    /// </summary>
    [Fact]
    public void HashingAddsNoPackageReference()
    {
        var project = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Api", "LancacheManager", "LancacheManager.csproj"));

        Assert.DoesNotContain("Microsoft.AspNetCore.Identity", project, StringComparison.Ordinal);
    }

    /// <summary>
    /// AddIdentity sets DefaultAuthenticateScheme, DefaultChallengeScheme and DefaultSignInScheme. This
    /// application's default is the Session scheme, so calling it would change what every [Authorize]
    /// means without changing a single attribute. AddIdentityCore leaves the schemes alone but requires
    /// an IUserStore that nothing here needs.
    /// </summary>
    [Fact]
    public void StartupDoesNotCallAddIdentity()
    {
        var program = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Api", "LancacheManager", "Program.cs"));

        Assert.DoesNotContain("AddIdentity<", program, StringComparison.Ordinal);
        Assert.DoesNotContain("AddIdentityCore<", program, StringComparison.Ordinal);
    }

    /// <summary>
    /// IPasswordValidator&lt;TUser&gt; takes a concrete UserManager&lt;TUser&gt;, which cannot be built
    /// without AddIdentityCore and a store. The rules are written out instead.
    /// </summary>
    [Fact]
    public void PasswordRulesDoNotUseTheIdentityValidator()
    {
        var api = Path.Combine(FindRepositoryRoot(), "Api", "LancacheManager");

        var users = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("IPasswordValidator", StringComparison.Ordinal))
            .ToList();

        Assert.True(users.Count == 0, $"IPasswordValidator is used by: {string.Join(", ", users)}");
    }

    /// <summary>
    /// IterationCount is read only in IdentityV3, so the two settings only mean anything together.
    /// </summary>
    [Fact]
    public async Task HasherIsConfiguredForTheIterationCountWeChose()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();
        await host.AssertIsolationAsync(client);

        var options = host.Application.Services.GetRequiredService<IOptions<PasswordHasherOptions>>().Value;

        Assert.Equal(PasswordHasherCompatibilityMode.IdentityV3, options.CompatibilityMode);
        Assert.Equal(220_000, options.IterationCount);
    }

    /// <summary>
    /// The hasher is resolved through the interface an endpoint would ask for, so the registration is
    /// under test as much as the algorithm is.
    /// </summary>
    [Fact]
    public async Task RightPasswordVerifiesAndAWrongOneDoesNot()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();
        await host.AssertIsolationAsync(client);

        var hasher = host.Application.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        var account = new UserAccount { Username = "operator" };
        var stored = hasher.HashPassword(account, Password);

        Assert.Equal(
            PasswordVerificationResult.Success,
            hasher.VerifyHashedPassword(account, stored, Password));
        Assert.Equal(
            PasswordVerificationResult.Failed,
            hasher.VerifyHashedPassword(account, stored, Password[..^1]));
    }

    /// <summary>
    /// What an account whose password was hashed before the count was raised has stored. The verify has
    /// to say so, or raising the count later would leave every existing account on the old one forever.
    /// </summary>
    [Fact]
    public async Task HashWrittenAtALowerIterationCountAsksToBeRewritten()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();
        await host.AssertIsolationAsync(client);

        var hasher = host.Application.Services.GetRequiredService<IPasswordHasher<UserAccount>>();
        var account = new UserAccount { Username = "operator" };
        var weaker = new PasswordHasher<UserAccount>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = 100_000
        }));

        var result = hasher.VerifyHashedPassword(account, weaker.HashPassword(account, Password), Password);

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, result);
    }

    /// <summary>
    /// Nothing registers this validator by hand. AddValidatorsFromAssemblyContaining finds it and
    /// ValidationFilter runs it against any action argument of this type.
    /// </summary>
    [Fact]
    public async Task ValidatorIsRegisteredWithoutBeingWiredUp()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();
        await host.AssertIsolationAsync(client);

        using var scope = host.Application.Services.CreateScope();

        Assert.IsType<AccountCredentialsRequestValidator>(
            scope.ServiceProvider.GetRequiredService<IValidator<AccountCredentialsRequest>>());
    }

    [Theory]
    [InlineData("operator", Password, true)]
    [InlineData("operator", "Xk8!vqR2tzL", false)]
    [InlineData("operator", "abcdefghijklmnop", false)]
    [InlineData("operator", "", false)]
    [InlineData("", Password, false)]
    [InlineData(Password, "xK8!VQr2TZlM", false)]
    public void CredentialsAreAcceptedOnlyWhenEveryRulePasses(string username, string password, bool accepted)
    {
        var result = new AccountCredentialsRequestValidator().Validate(
            new AccountCredentialsRequest { Username = username, Password = password });

        Assert.Equal(accepted, result.IsValid);
    }

    /// <summary>
    /// PBKDF2 runs over the whole password at 220,000 iterations, so an unbounded one buys a lot of
    /// server time for one request.
    /// </summary>
    [Fact]
    public void PasswordLongerThanTheCapIsRefused()
    {
        var validator = new AccountCredentialsRequestValidator();
        var atTheCap = new string('a', 252) + "Xk8!";
        var overIt = atTheCap + "a";

        Assert.True(validator.Validate(
            new AccountCredentialsRequest { Username = "operator", Password = atTheCap }).IsValid);
        Assert.False(validator.Validate(
            new AccountCredentialsRequest { Username = "operator", Password = overIt }).IsValid);
    }

    /// <summary>
    /// The username column is citext, which has no length variant, so nothing bounds it at the
    /// database and a btree index entry past roughly 2704 bytes fails at insert.
    /// </summary>
    [Fact]
    public void UsernameLongerThanTheCapIsRefused()
    {
        var validator = new AccountCredentialsRequestValidator();

        Assert.True(validator.Validate(
            new AccountCredentialsRequest { Username = new string('a', 64), Password = Password }).IsValid);
        Assert.False(validator.Validate(
            new AccountCredentialsRequest { Username = new string('a', 65), Password = Password }).IsValid);
    }

    /// <summary>
    /// Two sessions on one account, and the password changed on one of them. Every other place that
    /// replaces a password ends the account's sessions, and this is the one a person reaches for when
    /// they think somebody else knows the password, so the session they are not looking at must not
    /// outlive the change. The session the change was made on does survive, which is deliberate: being
    /// signed out of the screen you just used is not what changing a password is for.
    /// </summary>
    [Fact]
    public async Task ChangingAPasswordEndsTheAccountsOtherSessionsAndKeepsTheCallersOwn()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        var (username, password) = await host.NewAccountAsync();
        using var changing = await SignedInClientAsync(host, username, password);
        using var abandoned = await SignedInClientAsync(host, username, password);

        var before = await abandoned.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");
        Assert.NotNull(before);
        Assert.True(before.IsAuthenticated);
        Assert.NotEqual(
            (await changing.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status"))!.SessionId,
            before.SessionId);

        using var changed = await changing.PostAsJsonAsync(
            "/api/auth/password",
            new ChangePasswordRequest { CurrentPassword = password, NewPassword = "Nv6!tqB3zdWx" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var after = await abandoned.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");
        Assert.NotNull(after);
        Assert.False(after.IsAuthenticated);

        var kept = await changing.GetFromJsonAsync<AuthStatusResponse>("/api/auth/status");
        Assert.NotNull(kept);
        Assert.True(kept.IsAuthenticated);
    }

    /// <summary>
    /// A client signed in as an account that already exists, so two of them can be opened on the one
    /// account. The shared admin-client helper makes an account of its own each time it is called and
    /// cannot be asked for a second session on the first one.
    /// </summary>
    private static async Task<HttpClient> SignedInClientAsync(
        EndpointAuthorizationHost host,
        string username,
        string password)
    {
        var client = host.Application.CreateClient();

        try
        {
            var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();

            await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
            using var login = await client.PostAsJsonAsync(
                "/api/auth/login",
                new LoginRequest { ApiKey = apiKey, Username = username, Password = password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);

            // The token belongs to the caller it was issued to, and the caller has just changed from
            // nobody to this account.
            await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }
}
