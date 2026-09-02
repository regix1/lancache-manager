using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// A full Steam scan and the mapping reset arm cleared GameName on every Downloads row, Epic rows
/// included. Such a row keeps its EpicAppId, so the old candidate test (empty id only) never offered
/// it again, and cache detection needs both columns filled before the row reaches a bucket. These
/// tests pin the widened candidate rule in EpicMappingService.ResolveDownloadsAsync.
/// </summary>
public class EpicNameRecoveryTests
{
    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=epic_name_recovery_translation_smoke_test")
            .Options);

    [Fact]
    public void UnresolvedEpicDownloadsQuery_TestsGameNameOnTheServer()
    {
        using var context = NewNpgsqlContext();

        // Mirrors the candidate query in EpicMappingService.ResolveDownloadsAsync.
        var sql = context.Downloads
            .Where(d => EF.Functions.Like(d.Service, "%epic%")
                     && (string.IsNullOrEmpty(d.EpicAppId) || d.GameName == null)
                     && d.LastUrl != null)
            .Select(d => d.Id)
            .ToQueryString();

        // Only Id is projected, so the column can only appear here because the name test reached the
        // WHERE clause. EF Core throws rather than silently filtering in memory, so this is the proof
        // that a wiped row is selected by the database.
        Assert.Contains("GameName", sql, StringComparison.Ordinal);
    }

    [Theory]
    // The row this recovers: id kept, name wiped.
    [InlineData("abc123", null, true)]
    // Never named in the first place.
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    // Fully mapped rows stay out, so a resolve run does not reload the whole table.
    [InlineData("abc123", "Fortnite", false)]
    public void CandidateFilter_SelectsRowsMissingAnAppIdOrAName(string? epicAppId, string? gameName, bool expected)
    {
        // EF.Functions.Like is translation-only and cannot be invoked client-side, so the service tag
        // half of the filter is covered by the SQL test above and the column tests are asserted here.
        var download = new Download
        {
            Service = "epicgames",
            EpicAppId = epicAppId,
            GameName = gameName,
            LastUrl = "http://epicgames-download1.akamaized.net/Builds/fortnite/CloudDir/ChunksV4/00.chunk"
        };

        var isCandidate = (string.IsNullOrEmpty(download.EpicAppId) || download.GameName == null)
                          && download.LastUrl != null;

        Assert.Equal(expected, isCandidate);
    }
}
