using System.Text.RegularExpressions;
using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Tests;

/// <summary>
/// The Database Management screen and the reset allowlist are two hand-maintained lists of the same
/// table names, and when they drift the screen offers a checkbox the backend then discards without
/// saying so: XboxGameMappings and XboxCdnPatterns were both offered and both cleared nothing.
/// Reading the names out of the screen's own source is what makes the next drift fail here rather
/// than on a user's box. [1][2]
/// </summary>
public sealed partial class DatabaseResetOfferedTablesTests
{
    [Fact]
    public void EveryTableTheScreenOffersSurvivesResetResolution()
    {
        var offered = OfferedTableNames();

        // Both were the reported defect, and naming them keeps an extraction that silently returned
        // nothing from passing the loop below.
        Assert.Contains("XboxGameMappings", offered);
        Assert.Contains("XboxCdnPatterns", offered);

        foreach (var table in offered)
        {
            Assert.Contains(table, DatabaseService.ResolveResetTables([table]));
        }
    }

    /// <summary>
    /// Whitespace-tolerant wherever the formatter can rewrap, because a pre-commit hook runs
    /// prettier over DataSection.tsx and reformatting it must not fail a backend test.
    /// </summary>
    private static List<string> OfferedTableNames()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Web",
            "src",
            "components",
            "features",
            "management",
            "sections",
            "DataSection.tsx"));

        var list = TableListRegex().Match(source);
        Assert.True(
            list.Success,
            "DataSection.tsx no longer declares the reset table list. If it moved or was renamed, "
                + "this guard must be pointed at its new home rather than deleted.");

        return TableNameRegex().Matches(list.Groups[1].Value)
            .Select(match => match.Groups[1].Value)
            .ToList();
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

    [GeneratedRegex(@"const\s+tables\s*=\s*\[(.*?)\n\s*\];", RegexOptions.Singleline)]
    private static partial Regex TableListRegex();

    [GeneratedRegex(@"name:\s*['""]([A-Za-z]+)['""]")]
    private static partial Regex TableNameRegex();
}
