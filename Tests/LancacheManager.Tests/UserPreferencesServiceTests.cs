using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Core.Services.UserPreferencesService;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the two rules that hold a session's clock preference together: a write that loses the race to
/// create the row must still land, and the three clock columns must never contradict each other.
/// </summary>
public sealed class UserPreferencesServiceTests
{
    // P-1: a session carries no preferences row until its first write, and the app sends the clock keys as
    // three requests at once. All three find no row, all three insert, and the unique index on SessionId
    // admits one. The losers must re-read and apply their value to the row that won, not give up.
    //
    // Racing real threads would not test this: whether the second caller reads before or after the first
    // commits is down to timing, and the run where it reads after takes the update path and passes against
    // the unfixed code too. The competing insert is driven from a save interceptor instead, which puts the
    // row in place after this call has already read and found nothing, exactly the window the race lives in.
    [Fact]
    public async Task CreateRaceLoser_AppliesItsValueToTheRowThatWon()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();

        var competingWrite = database.InterceptCompetingInsert(sessionId, WinnerTheme);
        var service = new UserPreferencesService(
            NullLogger<UserPreferencesService>.Instance,
            database.InterceptedFactory);

        var updated = await service.UpdatePreferenceAsync(
            sessionId, PreferenceKey.UseUtcTimezone, Value(true));

        // Without this the test could pass while never entering the vulnerable state.
        Assert.True(competingWrite.Fired);

        Assert.NotNull(updated);
        Assert.True(updated!.UseUtcTimezone);

        await using var context = database.Factory.CreateDbContext();
        var rows = await context.UserPreferences
            .Where(p => p.SessionId == sessionId)
            .ToListAsync();

        Assert.Single(rows);
        // The losing write landed...
        Assert.True(rows[0].UseUtcTimezone);
        // ...as an update, so the winner's own column is still there rather than overwritten.
        Assert.Equal(WinnerTheme, rows[0].SelectedTheme);
    }

    // P-2: choosing UTC settles all three columns. UTC is a clock of its own, so local goes off, and it has
    // no 12-hour face worth offering, so the 24-hour flag goes on.
    [Fact]
    public async Task ChoosingUtc_ClearsLocalAndTurnsOn24Hour()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: false, useLocal: true, use24Hour: false);

        var service = NewService(database);
        var updated = await service.UpdatePreferenceAsync(
            sessionId, PreferenceKey.UseUtcTimezone, Value(true));

        Assert.NotNull(updated);
        Assert.True(updated!.UseUtcTimezone);
        Assert.False(updated.UseLocalTimezone);
        Assert.True(updated.Use24HourFormat);
    }

    // P-3: choosing local turns UTC off. The clock the request names outranks the one already stored,
    // otherwise the user picks local and keeps reading UTC.
    [Fact]
    public async Task ChoosingLocal_TurnsUtcOff()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: true, useLocal: false, use24Hour: true);

        var service = NewService(database);
        var updated = await service.UpdatePreferenceAsync(
            sessionId, PreferenceKey.UseLocalTimezone, Value(true));

        Assert.NotNull(updated);
        Assert.True(updated!.UseLocalTimezone);
        Assert.False(updated.UseUtcTimezone);
    }

    // P-4: the one that is easy to get wrong. Switching from UTC to a 12-hour clock sends three separate
    // requests, and the one carrying the 12-hour choice can be applied while UTC is still on. If turning
    // UTC off, or turning local off, reshaped the other columns, that choice would be overruled and the
    // user would land back on the 24-hour clock having asked for 12.
    [Fact]
    public async Task LeavingUtc_KeepsThe12HourChoiceTheUserJustMade()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: true, useLocal: false, use24Hour: true);

        var service = NewService(database);

        // The order the requests happen to be applied in, worst case first.
        await service.UpdatePreferenceAsync(sessionId, PreferenceKey.Use24HourFormat, Value(false));
        await service.UpdatePreferenceAsync(sessionId, PreferenceKey.UseLocalTimezone, Value(false));
        var updated = await service.UpdatePreferenceAsync(
            sessionId, PreferenceKey.UseUtcTimezone, Value(false));

        Assert.NotNull(updated);
        Assert.False(updated!.UseUtcTimezone);
        Assert.False(updated.UseLocalTimezone);
        Assert.False(updated.Use24HourFormat);
    }

    // P-5: the three clock columns land in ONE save. Sent as three requests they commit one at a time, so a
    // second click can be applied between two writes of the first and leave the row naming a clock neither
    // click asked for: pick UTC then a 12-hour server clock quickly and the UTC write normalises the
    // 12-hour choice back to 24, while the write that turns UTC off leaves it there.
    [Fact]
    public async Task ClockChange_CommitsTheThreeColumnsInOneSave()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: true, useLocal: false, use24Hour: true);

        var saves = database.CountSaves();
        var service = new UserPreferencesService(
            NullLogger<UserPreferencesService>.Instance,
            database.InterceptedFactory);

        var updated = await service.UpdateClockPreferencesAsync(sessionId, new ClockPreferences
        {
            UseUtcTimezone = false,
            UseLocalTimezone = false,
            Use24HourFormat = false
        });

        Assert.NotNull(updated);
        Assert.False(updated!.UseUtcTimezone);
        Assert.False(updated.UseLocalTimezone);
        Assert.False(updated.Use24HourFormat);

        // One save is the whole point: there is no moment where two columns have moved and the third
        // has not.
        Assert.Equal(1, saves.Count);
    }

    // P-6: a caller sending both clocks on lands on UTC, which is the precedence every reader already
    // applies. The write order inside the combined request is what decides this.
    [Fact]
    public async Task ClockChange_LandsOnUtcWhenBothClocksArriveOn()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: false, useLocal: false, use24Hour: false);

        var service = NewService(database);
        var updated = await service.UpdateClockPreferencesAsync(sessionId, new ClockPreferences
        {
            UseUtcTimezone = true,
            UseLocalTimezone = true,
            Use24HourFormat = false
        });

        Assert.NotNull(updated);
        Assert.True(updated!.UseUtcTimezone);
        Assert.False(updated.UseLocalTimezone);
        Assert.True(updated.Use24HourFormat);
    }

    // P-7: the combined write shares the create-race retry with the per-key write, so a first-ever clock
    // change that loses the insert race still lands on the row that won.
    [Fact]
    public async Task ClockChangeRaceLoser_AppliesItsValueToTheRowThatWon()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();

        var competingWrite = database.InterceptCompetingInsert(sessionId, WinnerTheme);
        var service = new UserPreferencesService(
            NullLogger<UserPreferencesService>.Instance,
            database.InterceptedFactory);

        var updated = await service.UpdateClockPreferencesAsync(sessionId, new ClockPreferences
        {
            UseUtcTimezone = true,
            UseLocalTimezone = false,
            Use24HourFormat = true
        });

        Assert.True(competingWrite.Fired);

        Assert.NotNull(updated);
        Assert.True(updated!.UseUtcTimezone);

        await using var context = database.Factory.CreateDbContext();
        var rows = await context.UserPreferences
            .Where(p => p.SessionId == sessionId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.True(rows[0].UseUtcTimezone);
        Assert.Equal(WinnerTheme, rows[0].SelectedTheme);
    }

    // P-8: a guest who logs in AFTER an admin changed the defaults gets them. Only the guests already
    // connected receive the broadcast, so without this a later arrival reads the built-in values however
    // the admin set things. All six defaults travel, not only the clock ones.
    [Fact]
    public async Task NewSession_TakesTheGuestDefaults()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();

        var service = NewService(database);
        var seeded = await service.SeedGuestDefaultsAsync(sessionId, GuestDefaults());

        Assert.True(seeded);

        var stored = service.GetPreferences(sessionId);
        Assert.NotNull(stored);
        Assert.True(stored!.UseLocalTimezone);
        Assert.False(stored.UseUtcTimezone);
        Assert.False(stored.Use24HourFormat);
        Assert.True(stored.SharpCorners);
        Assert.True(stored.DisableTooltips);
        Assert.False(stored.ShowDatasourceLabels);
    }

    // P-9: the other half of the same rule, and the one that would hurt. A session that already carries a
    // preferences row is the person's own saved choice, so seeding leaves every column exactly as it was
    // instead of resetting people every time an admin touches a default.
    [Fact]
    public async Task SessionWithItsOwnPreferences_KeepsThem()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: true, useLocal: false, use24Hour: true);

        var service = NewService(database);
        var seeded = await service.SeedGuestDefaultsAsync(sessionId, GuestDefaults());

        Assert.False(seeded);

        var stored = service.GetPreferences(sessionId);
        Assert.NotNull(stored);
        Assert.True(stored!.UseUtcTimezone);
        Assert.False(stored.UseLocalTimezone);
        Assert.True(stored.Use24HourFormat);

        await using var context = database.Factory.CreateDbContext();
        Assert.Single(await context.UserPreferences.Where(p => p.SessionId == sessionId).ToListAsync());
    }

    // P-10: a UTC default arrives settled, the same as a UTC choice made by hand. An admin can set UTC
    // and a 12-hour clock together, and UTC has no 12-hour face worth offering.
    [Fact]
    public async Task UtcDefault_ArrivesSettled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();

        var defaults = GuestDefaults();
        defaults.UseUtcTimezone = true;
        defaults.UseLocalTimezone = true;
        defaults.Use24HourFormat = false;

        var service = NewService(database);
        Assert.True(await service.SeedGuestDefaultsAsync(sessionId, defaults));

        var stored = service.GetPreferences(sessionId);
        Assert.NotNull(stored);
        Assert.True(stored!.UseUtcTimezone);
        Assert.False(stored.UseLocalTimezone);
        Assert.True(stored.Use24HourFormat);
    }

    // P-11: the seed runs while a session is being created, so a write that cannot land must report
    // failure rather than throw. A login that fails because a cosmetic default could not be written
    // would be a far worse outcome than a guest seeing the built-in clock. The unknown session id makes
    // the insert violate the foreign key, which is a real database failure rather than a stubbed one.
    [Fact]
    public async Task SeedThatCannotBeWritten_ReportsFailureInsteadOfThrowing()
    {
        await using var database = await TestDatabase.CreateAsync();

        var service = NewService(database);
        var seeded = await service.SeedGuestDefaultsAsync(Guid.NewGuid(), GuestDefaults());

        Assert.False(seeded);
    }

    // P-12: a write sends only the columns it changed, so the entity it wrote through still holds whatever
    // this request's own read returned for everything else. A change that commits in that window is missing
    // from it, and these preferences go out to every client of the session. They must be read back from the
    // row rather than taken from what this request was holding.
    //
    // Racing real threads would not reach this: whether the competing write lands inside this call's read
    // and save is down to timing, and the run where it lands outside holds the newer value anyway. The
    // competing write is driven from a save interceptor instead, which lands it after this call has read
    // and before it saves, exactly the window the staleness lives in.
    [Fact]
    public async Task WriteThatCommitsMidRequest_IsInTheReturnedPreferences()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: false, useLocal: false, use24Hour: false);

        var competingWrite = database.InterceptCompetingUpdate(sessionId, WinnerTheme);
        var service = new UserPreferencesService(
            NullLogger<UserPreferencesService>.Instance,
            database.InterceptedFactory);

        var updated = await service.UpdatePreferenceAsync(
            sessionId, PreferenceKey.UseUtcTimezone, Value(true));

        // Without this the test could pass while never entering the vulnerable state.
        Assert.True(competingWrite.Fired);

        Assert.NotNull(updated);
        // This request's own column landed...
        Assert.True(updated!.UseUtcTimezone);
        // ...and the theme the other write committed came back with it, rather than the value this request
        // read before that write happened.
        Assert.Equal(WinnerTheme, updated.SelectedTheme);
    }

    // P-13: the whole-object save reports what was stored, not what arrived. The clock columns are settled
    // on the way in, so a caller passing its own request body on would tell every client that a 12-hour UTC
    // clock had been taken as written.
    [Fact]
    public async Task WholeObjectSave_ReturnsTheStoredClockNotTheOneSent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();

        var service = NewService(database);
        var stored = await service.SavePreferencesAsync(sessionId, new UserPreferencesDto
        {
            UseUtcTimezone = true,
            UseLocalTimezone = true,
            Use24HourFormat = false
        });

        Assert.NotNull(stored);
        Assert.True(stored!.UseUtcTimezone);
        Assert.False(stored.UseLocalTimezone);
        Assert.True(stored.Use24HourFormat);
    }

    /// <summary>
    /// A guest request can carry the administrator-owned values it read earlier. If an administrator
    /// changes them before the guest save begins, the guest write must leave the newer stored values alone.
    /// </summary>
    [Fact]
    public async Task GuestSave_PreservesAdminFieldsChangedAfterRequestRead()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();
        await database.AddPreferencesAsync(sessionId, useUtc: false, useLocal: false, use24Hour: true);

        await using (var context = database.Factory.CreateDbContext())
        {
            var preferences = await context.UserPreferences.SingleAsync(p => p.SessionId == sessionId);
            preferences.AllowedTimeFormats = JsonSerializer.Serialize(new[] { "server-24h" });
            preferences.RefreshRateLocked = false;
            preferences.SteamMaxThreadCount = 2;
            preferences.EpicMaxThreadCount = 3;
            await context.SaveChangesAsync();
        }

        var service = NewService(database);
        var staleGuestRequest = Assert.IsType<UserPreferencesDto>(service.GetPreferences(sessionId));

        await using (var context = database.Factory.CreateDbContext())
        {
            var preferences = await context.UserPreferences.SingleAsync(p => p.SessionId == sessionId);
            preferences.AllowedTimeFormats = JsonSerializer.Serialize(new[] { "local-12h", "utc" });
            preferences.RefreshRateLocked = true;
            preferences.SteamMaxThreadCount = 8;
            preferences.EpicMaxThreadCount = 6;
            await context.SaveChangesAsync();
        }

        staleGuestRequest.SharpCorners = true;
        var stored = await service.SavePreferencesAsync(
            sessionId, staleGuestRequest, preserveAdminFields: true);

        Assert.NotNull(stored);
        Assert.Equal(new[] { "local-12h", "utc" }, stored!.AllowedTimeFormats);
        Assert.True(stored.RefreshRateLocked);
        Assert.Equal(8, stored.SteamMaxThreadCount);
        Assert.Equal(6, stored.EpicMaxThreadCount);
        Assert.True(stored.SharpCorners);
    }

    // P-10: the full save has the same first-write race as the per-key one. Two tabs saving a session's
    // preferences for the first time both find no row and both insert, and the unique index on SessionId
    // admits one. The loser must re-read and save onto the row that won rather than handing its caller an
    // unhandled unique-key error.
    [Fact]
    public async Task FullSaveRaceLoser_SavesOntoTheRowThatWon()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();

        var competingWrite = database.InterceptCompetingInsert(sessionId, WinnerTheme);
        var logger = new CapturingLogger<UserPreferencesService>();
        var service = new UserPreferencesService(logger, database.InterceptedFactory);

        var stored = await service.SavePreferencesAsync(sessionId, new UserPreferencesDto
        {
            SharpCorners = true,
            UseLocalTimezone = true,
            Use24HourFormat = false
        });

        // Without this the test could pass while never entering the vulnerable state.
        Assert.True(competingWrite.Fired);

        Assert.NotNull(stored);
        Assert.True(stored!.SharpCorners);
        Assert.True(stored.UseLocalTimezone);
        Assert.False(stored.Use24HourFormat);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);

        await using var context = database.Factory.CreateDbContext();
        var rows = await context.UserPreferences
            .Where(p => p.SessionId == sessionId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.True(rows[0].SharpCorners);
    }

    // P-11: the seed loses the same race whenever a session writes its own preferences while it is being
    // created. That is the outcome the method is built around, and its false return already says so, so it
    // is not a fault to report. Logging it as an error is what put a normal race in front of whoever reads
    // the log looking for a real one.
    [Fact]
    public async Task SeedLosingTheRace_ReportsNoError()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = await database.AddSessionAsync();

        var competingWrite = database.InterceptCompetingInsert(sessionId, WinnerTheme);
        var logger = new CapturingLogger<UserPreferencesService>();
        var service = new UserPreferencesService(logger, database.InterceptedFactory);

        var seeded = await service.SeedGuestDefaultsAsync(sessionId, GuestDefaults());

        Assert.True(competingWrite.Fired);
        Assert.False(seeded);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);

        // The row the race winner wrote is untouched: a default must never overrule a session's own choice.
        await using var context = database.Factory.CreateDbContext();
        var rows = await context.UserPreferences
            .Where(p => p.SessionId == sessionId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal(WinnerTheme, rows[0].SelectedTheme);
    }

    // Every default set away from its built-in value, so a column that is not carried across shows up as
    // a failed assertion rather than passing by coincidence.
    private static UserPreferencesDto GuestDefaults() => new()
    {
        UseLocalTimezone = true,
        UseUtcTimezone = false,
        Use24HourFormat = false,
        SharpCorners = true,
        DisableTooltips = true,
        ShowDatasourceLabels = false
    };

    private const string WinnerTheme = "theme-from-the-first-write";

    private static UserPreferencesService NewService(TestDatabase database) =>
        new(NullLogger<UserPreferencesService>.Instance, database.Factory);

    private static JsonElement Value(bool value) => JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// Inserts a second row for the same session the first time a save runs, then stands down. Building its
    /// own context from the plain options keeps it out of its own way.
    /// </summary>
    private sealed class CompetingInsert(
        DbContextOptions<AppDbContext> options,
        Guid sessionId,
        string selectedTheme) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;

                await using var context = new AppDbContext(options);
                context.UserPreferences.Add(new UserPreferences
                {
                    SessionId = sessionId,
                    SelectedTheme = selectedTheme,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                await context.SaveChangesAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Commits a change to a different column of the same session's row the first time a save runs, then
    /// stands down. Building its own context from the plain options keeps it out of its own way.
    /// </summary>
    private sealed class CompetingUpdate(
        DbContextOptions<AppDbContext> options,
        Guid sessionId,
        string selectedTheme) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;

                await using var context = new AppDbContext(options);
                var preferences = await context.UserPreferences
                    .FirstAsync(p => p.SessionId == sessionId, cancellationToken);
                preferences.SelectedTheme = selectedTheme;
                await context.SaveChangesAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Counts the saves a service run makes, so a write that is supposed to commit as one unit can be
    /// held to it.
    /// </summary>
    private sealed class SaveCount : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly LancacheManager.Tests.TestDatabase _database;
        private readonly DbContextOptions<AppDbContext> _options;
        private TestDbContextFactory? _interceptedFactory;

        private TestDatabase(LancacheManager.Tests.TestDatabase database)
        {
            _database = database;
            _options = database.Options;
            Factory = database.Factory;
        }

        public TestDbContextFactory Factory { get; }

        public TestDbContextFactory InterceptedFactory =>
            _interceptedFactory ?? throw new InvalidOperationException(
                "InterceptCompetingInsert must run before the intercepted factory is used.");

        public static async Task<TestDatabase> CreateAsync() =>
            new(await LancacheManager.Tests.TestDatabase.CreateAsync());

        public async Task<Guid> AddSessionAsync()
        {
            var sessionId = Guid.NewGuid();
            await using var context = Factory.CreateDbContext();
            context.UserSessions.Add(new UserSession
            {
                Id = sessionId,
                SessionTokenHash = sessionId.ToString("N"),
                SessionType = SessionType.Guest,
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
                LastSeenAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            return sessionId;
        }

        public async Task AddPreferencesAsync(Guid sessionId, bool useUtc, bool useLocal, bool use24Hour)
        {
            await using var context = Factory.CreateDbContext();
            context.UserPreferences.Add(new UserPreferences
            {
                SessionId = sessionId,
                UseUtcTimezone = useUtc,
                UseLocalTimezone = useLocal,
                Use24HourFormat = use24Hour,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Arms the competing insert and hands back a factory whose contexts carry it. The session row is
        /// added through the plain factory so setup does not spend the one interception.
        /// </summary>
        public CompetingInsert InterceptCompetingInsert(Guid sessionId, string selectedTheme)
        {
            var competingInsert = new CompetingInsert(_options, sessionId, selectedTheme);
            _interceptedFactory = Intercepted(competingInsert);
            return competingInsert;
        }

        /// <summary>
        /// Arms the competing update and hands back a factory whose contexts carry it. The row it changes is
        /// written through the plain factory so setup does not spend the one interception.
        /// </summary>
        public CompetingUpdate InterceptCompetingUpdate(Guid sessionId, string selectedTheme)
        {
            var competingUpdate = new CompetingUpdate(_options, sessionId, selectedTheme);
            _interceptedFactory = Intercepted(competingUpdate);
            return competingUpdate;
        }

        /// <summary>
        /// Arms the save counter and hands back a factory whose contexts carry it. Setup runs through the
        /// plain factory so only the service's own saves are counted.
        /// </summary>
        public SaveCount CountSaves()
        {
            var saveCount = new SaveCount();
            _interceptedFactory = Intercepted(saveCount);
            return saveCount;
        }

        private TestDbContextFactory Intercepted(IInterceptor interceptor) =>
            new(new DbContextOptionsBuilder<AppDbContext>(_options)
                .AddInterceptors(interceptor)
                .Options);

        public async ValueTask DisposeAsync() => await _database.DisposeAsync();
    }
}

/// <summary>
/// Hands out contexts on some options a test already built, whichever provider those options name. Every
/// test that needs a database needs exactly this and nothing more, so there is one of it rather than a
/// private copy per test file.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options)
    : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());
}
