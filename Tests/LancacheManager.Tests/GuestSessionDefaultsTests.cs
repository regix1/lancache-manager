using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Covers a guest session picking up the defaults an admin chose. The admin's change reaches the guests who
/// are connected at the time through a broadcast and nothing else carried it, so a guest who logs in
/// afterwards used to read the built-in values however the admin had set things.
/// </summary>
public sealed class GuestSessionDefaultsTests : IDisposable
{
    private readonly string _root;

    public GuestSessionDefaultsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-guest-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // G-1: the admin sets the defaults, THEN a guest logs in. The new session carries the admin's values,
    // and all six travel rather than only the clock ones.
    [Fact]
    public async Task GuestLoggingInAfterTheDefaultsChanged_GetsThem()
    {
        await using var database = await TestDatabase.CreateAsync();

        var stateService = CreateStateService(_root);
        stateService.UpdateState(state =>
        {
            state.DefaultGuestUseLocalTimezone = true;
            state.DefaultGuestUse24HourFormat = false;
            state.DefaultGuestSharpCorners = true;
            state.DefaultGuestDisableTooltips = true;
            state.DefaultGuestShowDatasourceLabels = false;
        });

        var preferences = NewPreferencesService(database);
        var created = await NewSessionService(database, stateService, preferences)
            .CreateGuestSessionAsync(new DefaultHttpContext());

        Assert.NotNull(created);

        var stored = preferences.GetPreferences(created!.Value.Session.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.UseLocalTimezone);
        Assert.False(stored.Use24HourFormat);
        Assert.True(stored.SharpCorners);
        Assert.True(stored.DisableTooltips);
        Assert.False(stored.ShowDatasourceLabels);
    }

    // G-2: a login still completes when the defaults cannot be written. The seed runs on the session
    // creation path, which every login goes through, so a failure there has to stay cosmetic: the guest
    // gets the built-in clock and is logged in, rather than being unable to log in at all. The
    // preferences table is dropped so the write fails for real rather than through a stub.
    [Fact]
    public async Task LoginStillSucceedsWhenTheDefaultsCannotBeWritten()
    {
        await using var database = await TestDatabase.CreateAsync();

        var stateService = CreateStateService(_root);
        stateService.UpdateState(state => state.DefaultGuestSharpCorners = true);

        await database.DropPreferencesTableAsync();

        var created = await NewSessionService(database, stateService, NewPreferencesService(database))
            .CreateGuestSessionAsync(new DefaultHttpContext());

        Assert.NotNull(created);
        Assert.False(string.IsNullOrEmpty(created!.Value.RawToken));

        await using var context = database.Factory.CreateDbContext();
        Assert.Single(await context.UserSessions.ToListAsync());
    }

    // G-3: the three clock fields hold one choice between them, so a new guest must be seeded with a clock
    // an admin could actually have picked. UTC is its own clock: it has no local face and no 12-hour face.
    [Fact]
    public async Task GuestSeededWhileTheDefaultIsUtc_GetsTheWholeUtcClock()
    {
        await using var database = await TestDatabase.CreateAsync();

        var stateService = CreateStateService(_root);
        stateService.UpdateState(state =>
        {
            state.DefaultGuestUseUtcTimezone = true;
            state.DefaultGuestUseLocalTimezone = true;
            state.DefaultGuestUse24HourFormat = false;
        });

        var preferences = NewPreferencesService(database);
        var created = await NewSessionService(database, stateService, preferences)
            .CreateGuestSessionAsync(new DefaultHttpContext());

        Assert.NotNull(created);

        var stored = preferences.GetPreferences(created!.Value.Session.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.UseUtcTimezone);
        Assert.False(stored.UseLocalTimezone);
        Assert.True(stored.Use24HourFormat);
    }

    // G-4: every clock a guest can be seeded with names one of the five options the selector offers. A row
    // saying UTC and local at once, or UTC on a 12-hour face, names none of them.
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task EveryStoredDefault_SeedsAClockThatNamesOneOption(bool useUtc, bool useLocal, bool use24Hour)
    {
        await using var database = await TestDatabase.CreateAsync();

        var stateService = CreateStateService(_root);
        stateService.UpdateState(state =>
        {
            state.DefaultGuestUseUtcTimezone = useUtc;
            state.DefaultGuestUseLocalTimezone = useLocal;
            state.DefaultGuestUse24HourFormat = use24Hour;
        });

        var preferences = NewPreferencesService(database);
        var created = await NewSessionService(database, stateService, preferences)
            .CreateGuestSessionAsync(new DefaultHttpContext());

        Assert.NotNull(created);

        var stored = preferences.GetPreferences(created!.Value.Session.Id);
        Assert.NotNull(stored);

        if (stored!.UseUtcTimezone)
        {
            Assert.False(stored.UseLocalTimezone);
            Assert.True(stored.Use24HourFormat);
        }
    }

    private static SessionService NewSessionService(
        TestDatabase database, StateService stateService, UserPreferencesService preferences) =>
        new(
            database.Factory,
            apiKeyService: null!,
            NullLogger<SessionService>.Instance,
            stateService,
            signalR: null!,
            new ConfigurationBuilder().Build(),
            activityRegistry: null,
            preferences);

    private static UserPreferencesService NewPreferencesService(TestDatabase database) =>
        new(NullLogger<UserPreferencesService>.Instance, database.Factory);

}
