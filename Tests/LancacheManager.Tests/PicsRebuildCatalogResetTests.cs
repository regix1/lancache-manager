using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace LancacheManager.Tests;

/// <summary>
/// A full PICS rebuild has to start from an empty catalog. The crawl repopulates depot owners and
/// depot names with TryAdd, which keeps whatever value is already under the key, so anything the
/// rebuild branch leaves behind survives the rebuild and is written back out into the mappings JSON
/// and from there into the database. Two of these four clears were once missing, and a depot whose
/// owner app or name changed in Steam kept the value loaded at startup for the life of the process.
///
/// The reset sits inside a private async method that opens a scoped database context and awaits the
/// JSON load before it reaches the clears, so no test can drive it. This reads the branch out of the
/// source instead, cut at the branch's own braces so a clear moved outside the branch fails here
/// exactly like a deleted one.
/// </summary>
public class PicsRebuildCatalogResetTests
{
    [Theory]
    [InlineData("_depotToAppMappings")]
    [InlineData("_appNames")]
    [InlineData("_depotOwners")]
    [InlineData("_depotNames")]
    public void TheFullRebuildBranchEmptiesEveryCatalogDictionary(string dictionary)
    {
        Assert.True(
            Regex.IsMatch(FullRebuildBranch(), $@"{dictionary}\s*\.\s*Clear\s*\(\s*\)\s*;"),
            $"A full PICS rebuild no longer clears {dictionary} before the crawl. The crawl adds "
                + "entries with TryAdd, which does not replace, so a value left in this dictionary "
                + "is written straight back into the rebuilt mappings.");
    }

    /// <summary>
    /// The body of the non-incremental branch, from its opening brace to the brace that closes it,
    /// so the assertions above cannot be satisfied by a clear that sits outside the branch. The
    /// search starts at PrepareForScanAsync because the app enumeration further down the same file
    /// opens a second if (!incrementalOnly) that has nothing to do with the catalog.
    /// </summary>
    private static string FullRebuildBranch()
    {
        var source = ReadRepoFile(
            "Api",
            "LancacheManager",
            "Core",
            "Services",
            "SteamKit2",
            "SteamKit2Service.Pics.cs");

        var method = source.IndexOf("PrepareForScanAsync(CancellationToken", StringComparison.Ordinal);
        Assert.True(
            method >= 0,
            "SteamKit2Service.Pics.cs no longer declares PrepareForScanAsync. If the rebuild reset "
                + "moved, this guard must be pointed at its new home rather than deleted.");

        var branch = Regex.Match(source[method..], @"if\s*\(\s*!\s*incrementalOnly\s*\)\s*\{");
        Assert.True(
            branch.Success,
            "PrepareForScanAsync no longer has a full rebuild branch written as if "
                + "(!incrementalOnly). If it moved or was rewritten, this guard must be pointed at "
                + "its new home rather than deleted.");

        var start = method + branch.Index + branch.Length;
        var depth = 1;
        for (var index = start; index < source.Length; index++)
        {
            depth += source[index] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0
            };

            if (depth == 0)
            {
                return source[start..index];
            }
        }

        throw new InvalidOperationException(
            "The full rebuild branch in SteamKit2Service.Pics.cs has no closing brace");
    }

    /// <summary>
    /// Walked from this file's own compile-time path rather than from the test binary's location.
    /// Building with a scratch <c>OutDir</c>, which is the documented way around a locked
    /// <c>LancacheManager.dll</c>, puts the binary outside the repository, and the walk up from
    /// there then reaches the drive root without ever finding the solution file.
    /// </summary>
    private static string RepoRoot([CallerFilePath] string testFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(testFilePath)!);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    private static string ReadRepoFile(params string[] pathSegments)
        => File.ReadAllText(Path.Combine([RepoRoot(), .. pathSegments]));
}
