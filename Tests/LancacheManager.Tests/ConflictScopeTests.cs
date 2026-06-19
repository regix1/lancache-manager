using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Unit tests for <see cref="ConflictScope"/>, focused on the named (Blizzard/Riot) game scope
/// added so name-keyed games can be individually tracked/removed alongside Steam and Epic.
/// </summary>
public class ConflictScopeTests
{
    [Fact]
    public void NamedGame_BuildsKindNamed_WithServiceColonGameNameKey()
    {
        var scope = ConflictScope.NamedGame("Blizzard", "Diablo IV");

        Assert.Equal("named", scope.Kind);
        // Service is lowercased; gameName is preserved verbatim.
        Assert.Equal("blizzard:Diablo IV", scope.Key);
        Assert.Equal("named:blizzard:Diablo IV", scope.ToTrackerKey());
    }

    [Fact]
    public void NamedGame_Matches_SameServiceAndGameName()
    {
        var a = ConflictScope.NamedGame("blizzard", "Diablo IV");
        var b = ConflictScope.NamedGame("BLIZZARD", "Diablo IV");

        Assert.True(a.Matches(b));
    }

    [Fact]
    public void NamedGame_DoesNotMatch_DifferentGameName()
    {
        var a = ConflictScope.NamedGame("blizzard", "Diablo IV");
        var b = ConflictScope.NamedGame("blizzard", "Overwatch");

        Assert.False(a.Matches(b));
    }

    [Fact]
    public void NamedGame_DoesNotMatch_DifferentService()
    {
        // A Blizzard game and a Riot game with the same name are distinct entities.
        var blizzard = ConflictScope.NamedGame("blizzard", "Heroes");
        var riot = ConflictScope.NamedGame("riot", "Heroes");

        Assert.False(blizzard.Matches(riot));
    }

    [Fact]
    public void ServiceScope_Covers_NamedGameOfSameService()
    {
        // A service-level removal for "blizzard" must cover a named "blizzard" game.
        var serviceScope = ConflictScope.Service("blizzard");
        var namedGame = ConflictScope.NamedGame("blizzard", "Diablo IV");

        Assert.True(serviceScope.Covers(namedGame, otherGameService: "blizzard"));
    }

    [Fact]
    public void ServiceScope_DoesNotCover_NamedGameOfDifferentService()
    {
        var serviceScope = ConflictScope.Service("riot");
        var namedGame = ConflictScope.NamedGame("blizzard", "Diablo IV");

        Assert.False(serviceScope.Covers(namedGame, otherGameService: "blizzard"));
    }

    [Fact]
    public void NamedScope_DoesNotCover_Anything()
    {
        // Covers is only meaningful for a service-level scope on the LHS.
        var namedGame = ConflictScope.NamedGame("blizzard", "Diablo IV");
        var other = ConflictScope.NamedGame("blizzard", "Overwatch");

        Assert.False(namedGame.Covers(other, otherGameService: "blizzard"));
    }
}
