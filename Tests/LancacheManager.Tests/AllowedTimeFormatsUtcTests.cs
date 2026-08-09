using System.Text.Json;
using System.Text.Json.Nodes;
using LancacheManager.Models;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the one-time offer of the UTC clock to installs that predate it. The allowed-format list is
/// written back on every save, so an install created before UTC existed keeps its four-item list forever
/// and no guest there can pick UTC until an admin re-saves the list by hand. The offer has to reach those
/// installs once, and exactly once: an admin who removes UTC afterwards must not find it back.
/// </summary>
public sealed class AllowedTimeFormatsUtcTests : IDisposable
{
    private static readonly string[] _formatsBeforeUtc =
        { "server-24h", "server-12h", "local-24h", "local-12h" };

    private readonly string _root;

    public AllowedTimeFormatsUtcTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-time-formats-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // U-1: the install everyone upgrading from is in - the four formats of the pre-UTC era, no marker.
    // Loading it offers UTC, so a guest can pick it without an admin touching anything. [3]
    [Fact]
    public void UpgradedInstall_IsOfferedUtc()
    {
        WriteState(_formatsBeforeUtc, migrated: null);

        var state = CreateStateService(_root).GetState();

        Assert.Contains("utc", state.AllowedTimeFormats);
        Assert.True(state.UtcTimeFormatMigrated);
    }

    // U-2: the marker is persisted, so a SECOND startup reading the same file does not run the offer
    // again. Proven on the file rather than in memory, because a restart is the case that matters. [4]
    [Fact]
    public void SecondStartup_DoesNotRunTheOfferAgain()
    {
        WriteState(_formatsBeforeUtc, migrated: null);

        CreateStateService(_root).GetState();
        Assert.True(ReadPersistedMarker());

        // The admin removes UTC after the offer already ran, exactly as the settings page would.
        var afterAdminRemovedUtc = ReadPersistedFormats().Where(format => format != "utc").ToArray();
        WriteState(afterAdminRemovedUtc, migrated: true);

        var state = CreateStateService(_root).GetState();

        Assert.DoesNotContain("utc", state.AllowedTimeFormats);
    }

    // U-3: an admin who narrowed the list to fewer formats is left alone. A stored list cannot say
    // whether UTC was left out deliberately, so anything other than the untouched four is not touched. [4]
    [Fact]
    public void NarrowedList_IsLeftAsTheAdminSetIt()
    {
        WriteState(new[] { "server-24h", "server-12h" }, migrated: null);

        var state = CreateStateService(_root).GetState();

        Assert.Equal(new[] { "server-24h", "server-12h" }, state.AllowedTimeFormats);
        Assert.True(state.UtcTimeFormatMigrated);
    }

    // U-4: an install that already offers UTC keeps exactly the list it has, with no duplicate entry.
    [Fact]
    public void ListThatAlreadyOffersUtc_IsUnchanged()
    {
        WriteState(TimeFormats.All.ToArray(), migrated: null);

        var state = CreateStateService(_root).GetState();

        Assert.Equal(TimeFormats.All, state.AllowedTimeFormats);
    }

    private string StateFilePath => Path.Combine(_root, "GetStateDirectory", "state.json");

    private void WriteState(IEnumerable<string> allowedTimeFormats, bool? migrated)
    {
        var root = new JsonObject
        {
            ["AllowedTimeFormats"] = new JsonArray(allowedTimeFormats.Select(f => (JsonNode)f!).ToArray())
        };

        if (migrated.HasValue)
        {
            root["UtcTimeFormatMigrated"] = migrated.Value;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        File.WriteAllText(StateFilePath, root.ToJsonString());
    }

    private JsonObject ReadPersistedState() =>
        JsonNode.Parse(File.ReadAllText(StateFilePath))!.AsObject();

    private bool ReadPersistedMarker() =>
        ReadPersistedState()["UtcTimeFormatMigrated"]!.GetValue<bool>();

    private string[] ReadPersistedFormats() =>
        ReadPersistedState()["AllowedTimeFormats"]!.Deserialize<string[]>()!;

}
