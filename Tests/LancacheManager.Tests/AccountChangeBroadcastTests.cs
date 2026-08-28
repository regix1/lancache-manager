using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// One administrator's account change reaches another administrator's open accounts table only if
/// the write broadcasts. Every route below leaves the table saying something different from what it
/// said a moment ago, so each is driven through a controller whose notification service records the
/// event names it was given, and the recording is read back.
///
/// The caller is the account that owns the installation in every case: it is the only one the wipe
/// accepts, and the only one that may hand out the administrator role, so the same caller can drive
/// all six routes without a role change confusing what is being tested.
/// </summary>
public sealed class AccountChangeBroadcastTests : IDisposable
{
    private const string SeedPassword = "Seeded-Walrus-7";
    private const string NewPassword = "Chosen-Badger-3";

    private readonly string _root;

    public AccountChangeBroadcastTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-account-broadcast-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task CreatingAnAccountBroadcastsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, sent) = await NewControllerAsync(database);

        var created = await controller.CreateAccountAsync(new CreateAccountRequest
        {
            Username = "newcomer",
            Password = NewPassword,
            Role = SessionType.User
        });

        Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
        Assert.Contains(SignalREvents.AccountsChanged, sent);
    }

    [Fact]
    public async Task RenamingAnAccountBroadcastsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, sent) = await NewControllerAsync(database);
        var target = await SeedAccountAsync(database, "renamed", SessionType.User);

        var edited = await controller.EditAccountAsync(
            target.Id, new EditAccountRequest { Username = "renamed-again", Password = null });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(edited));
        Assert.Contains(SignalREvents.AccountsChanged, sent);
    }

    [Fact]
    public async Task ChangingARoleBroadcastsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, sent) = await NewControllerAsync(database);
        var target = await SeedAccountAsync(database, "promoted", SessionType.User);

        var moved = await controller.SetRoleAsync(target.Id, new SetAccountRoleRequest { Role = SessionType.Admin });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(moved));
        Assert.Contains(SignalREvents.AccountsChanged, sent);
    }

    [Fact]
    public async Task DisablingAnAccountBroadcastsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, sent) = await NewControllerAsync(database);
        var target = await SeedAccountAsync(database, "switched-off", SessionType.User);

        var disabled = await controller.SetDisabledAsync(
            target.Id, new SetAccountDisabledRequest { Disabled = true });

        Assert.Equal(StatusCodes.Status200OK, StatusOf(disabled));
        Assert.Contains(SignalREvents.AccountsChanged, sent);
    }

    [Fact]
    public async Task DeletingAnAccountBroadcastsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, sent) = await NewControllerAsync(database);
        var target = await SeedAccountAsync(database, "removed", SessionType.User);

        var deleted = await controller.DeleteAccountAsync(target.Id);

        Assert.Equal(StatusCodes.Status200OK, StatusOf(deleted));
        Assert.Contains(SignalREvents.AccountsChanged, sent);
    }

    [Fact]
    public async Task WipingEveryAccountBroadcastsAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, sent) = await NewControllerAsync(database);
        await SeedAccountAsync(database, "also-removed", SessionType.User);

        var wiped = await controller.WipeAccountsAsync();

        Assert.Equal(StatusCodes.Status200OK, StatusOf(wiped));
        Assert.Contains(SignalREvents.AccountsChanged, sent);
    }

    [Fact]
    public async Task ARefusedChangeDoesNotBroadcastAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, sent) = await NewControllerAsync(database);

        // No such row, so nothing changed and an open table has nothing to reload for.
        var missing = await controller.DeleteAccountAsync(Guid.NewGuid());

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(missing));
        Assert.DoesNotContain(SignalREvents.AccountsChanged, sent);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<(AccountsController Controller, List<string> Sent)> NewControllerAsync(TestDatabase database)
    {
        var owner = await SeedAccountAsync(database, "owner", SessionType.Admin, mainAdmin: true);
        var session = await SeedSessionAsync(database, owner);
        var sent = new List<string>();
        var notifications = RecordingNotifications(sent);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            })
            .Build();

        var controller = new AccountsController(
            database.Factory,
            new PasswordHasher<UserAccount>(),
            new SessionService(
                database.Factory,
                new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!),
                NullLogger<SessionService>.Instance,
                stateService: null!,
                signalR: null!,
                configuration),
            new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance),
            new AccountLockout(NullLogger<AccountLockout>.Instance),
            new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance),
            notifications,
            NullLogger<AccountsController>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = session;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return (controller, sent);
    }

    private static async Task<UserAccount> SeedAccountAsync(
        TestDatabase database,
        string username,
        SessionType role,
        bool mainAdmin = false)
    {
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = username,
            Role = role,
            IsMainAdmin = mainAdmin,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(account, SeedPassword);

        await using var context = database.Factory.CreateDbContext();
        context.UserAccounts.Add(account);
        await context.SaveChangesAsync();
        return account;
    }

    private static async Task<UserSession> SeedSessionAsync(TestDatabase database, UserAccount account)
    {
        var now = DateTime.UtcNow;
        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = Guid.NewGuid().ToString("N"),
            SessionType = account.Role,
            AccountId = account.Id,
            CreatedAtUtc = now,
            LastSeenAtUtc = now,
            ExpiresAtUtc = now.AddDays(1)
        };

        await using var context = database.Factory.CreateDbContext();
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();
        return session;
    }

    private static ISignalRNotificationService RecordingNotifications(List<string> sent)
    {
        return CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync) && args?[0] is string eventName)
            {
                lock (sent)
                {
                    sent.Add(eventName);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
    }

    private static int StatusOf(ActionResult? result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? 0,
        StatusCodeResult statusResult => statusResult.StatusCode,
        _ => 0
    };

    private static int StatusOf<T>(ActionResult<T> result) => StatusOf(result.Result);

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = returnType.GetGenericArguments()[0];
            var value = inner.IsValueType && Nullable.GetUnderlyingType(inner) is null
                ? Activator.CreateInstance(inner)
                : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { value });
        }

        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod is null ? null : Handler?.Invoke(targetMethod, args);
    }
}
