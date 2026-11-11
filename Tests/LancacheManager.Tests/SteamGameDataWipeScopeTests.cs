using System.Text.RegularExpressions;

namespace LancacheManager.Tests;

/// <summary>
/// One place blanks GameName, GameImageUrl and GameAppId on the Downloads table: the
/// SteamDepotMappings arm of the selected-tables reset in DatabaseService.cs. It exists so Steam rows
/// can be renamed from fresh depot mappings, and only Steam traffic carries a DepotId. Xbox, Blizzard,
/// Riot and Epic rows hold their identity in GameName alone, so an unscoped wipe left them null for
/// good and the cache detection queries, which select on GameName IS NOT NULL, dropped those games
/// into the plain service bucket. This pins the DepotId filter in front of that statement. A full PICS
/// scan renames downloads only after its mappings are saved, so the mapping code holds no wipe at all.
///
/// The statements are checked against their source text because ExecuteUpdateAsync is translated by
/// the database provider and the in-memory provider used by the rest of these tests cannot run it.
/// </summary>
public sealed partial class SteamGameDataWipeScopeTests
{
    [Fact]
    public void FullScanWipesNoDownloadNames()
    {
        Assert.Equal(0, GameNameWipeRegex().Count(ReadApiFile(
            "Core", "Services", "SteamKit2", "SteamKit2Service.Mapping.cs")));
    }

    [Fact]
    public void DepotMappingResetWipe_OnlyTouchesDownloadsWithADepot()
    {
        AssertEveryGameNameWipeIsScopedToDepotRows(ReadApiFile(
            "Infrastructure", "Services", "System", "DatabaseService.cs"));
    }

    private static void AssertEveryGameNameWipeIsScopedToDepotRows(string source)
    {
        var wipes = GameNameWipeRegex().Count(source);
        var scopedWipes = ScopedGameNameWipeRegex().Count(source);

        Assert.True(wipes > 0, "Expected an ExecuteUpdateAsync that clears GameName in this file");
        Assert.Equal(wipes, scopedWipes);
    }

    private static string ReadApiFile(params string[] pathSegments)
        => File.ReadAllText(Path.Combine(
            [FindRepositoryRoot(), "Api", "LancacheManager", .. pathSegments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    [GeneratedRegex(@"\.SetProperty\(\s*d\s*=>\s*d\.GameName\s*,\s*\(string\?\)null\s*\)")]
    private static partial Regex GameNameWipeRegex();

    [GeneratedRegex(@"\.Where\(\s*d\s*=>\s*d\.DepotId\s*!=\s*null\s*\)\s*\.ExecuteUpdateAsync\(\s*s\s*=>\s*s\s*\.SetProperty\(\s*d\s*=>\s*d\.GameName\s*,\s*\(string\?\)null\s*\)")]
    private static partial Regex ScopedGameNameWipeRegex();
}
