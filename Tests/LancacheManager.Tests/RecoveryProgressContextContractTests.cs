using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public partial class RecoveryProgressContextContractTests
{
    public static TheoryData<string, Dictionary<string, object?>> ActiveStages => new()
    {
        { "signalr.corruptionDetect.enumerating", new() { ["count"] = 12 } },
        { "signalr.logRemoval.starting.default", new() { ["service"] = "steam" } },
        { "signalr.logRemoval.starting.multi", new() { ["service"] = "steam", ["datasourceCount"] = 2 } },
        { "signalr.logRemoval.starting.single", new() { ["service"] = "steam", ["datasourceName"] = "primary" } },
        { "signalr.logRemoval.processingDatasource", new() { ["service"] = "steam", ["datasourceName"] = "primary" } },
        { "signalr.logRemoval.progressWithCount", new() { ["service"] = "steam", ["linesRemoved"] = 4L } },
        { "signalr.logRemoval.cleaningDatabase", new() { ["service"] = "steam" } },
        { "signalr.dataImport.progress", new() { ["processed"] = 5UL, ["total"] = 10UL } },
        { "signalr.dbReset.deleting", new() { ["tableName"] = "Downloads", ["deletedRows"] = 5L, ["totalRows"] = 10L } },
        { "signalr.dbReset.error.fatal", new() { ["errorDetail"] = "failure" } },
        { "signalr.dbReset.startingTables", new() { ["count"] = 2 } },
        { "signalr.dbReset.clearingLogEntries", new() { ["deleted"] = 5, ["total"] = 10, ["percent"] = 50.0 } },
        { "signalr.dbReset.clearedLogEntries", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedDownloads", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedClientStats", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedServiceStats", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedDepotMappings", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedGameDetections", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedUserPreferences", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedUserSessions", new() { ["count"] = 1 } },
        { "signalr.dbReset.clearedTable", new() { ["tableName"] = "Events", ["count"] = 1 } },
        { "signalr.evictionScan.progress", new() { ["totalProcessed"] = 4, ["totalEstimate"] = 8 } },
        { "signalr.evictionScan.scanningFiles", new() { ["filesFound"] = 4 } },
        { "signalr.evictionScan.refreshingSummaryCounted", new() { ["filesChecked"] = 4, ["filesTotal"] = 8 } },
        { "signalr.cacheSizeScan.scanning", new() { ["directoriesScanned"] = 2, ["totalDirectories"] = 4, ["totalFiles"] = 8 } },
        { "signalr.cacheSizeScan.calibrating", new() { ["step"] = 1, ["totalSteps"] = 3 } },
        { "signalr.gameDetect.matching.starting", new() { ["totalGames"] = 10 } },
        { "signalr.gameDetect.matching.progress", new() { ["processed"] = 5, ["totalGames"] = 10 } },
        { "signalr.gameDetect.epic.progress", new() { ["processed"] = 1, ["totalGames"] = 2, ["name"] = "Game" } },
        { "signalr.gameDetect.named.progress", new() { ["processed"] = 1, ["totalGames"] = 2, ["name"] = "Game" } },
        { "signalr.gameDetect.services.progress", new() { ["processed"] = 1, ["total"] = 2 } },
        { "signalr.gameDetect.loaded.gamesAndServices", new() { ["gamesCount"] = 1, ["servicesCount"] = 2 } },
        { "signalr.gameDetect.loaded.gamesOnly", new() { ["gamesCount"] = 1 } }
    };

    [Theory]
    [MemberData(nameof(ActiveStages))]
    public void ActiveRecoveryStagesHaveCompleteBilingualSerializableContext(
        string stageKey,
        Dictionary<string, object?> context)
    {
        var root = FindRepositoryRoot();
        using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Web", "src", "i18n", "locales", "en.json")));
        using var chinese = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Web", "src", "i18n", "locales", "zh.json")));
        var enText = Resolve(english.RootElement, stageKey).GetString()!;
        var zhText = Resolve(chinese.RootElement, stageKey).GetString()!;
        var enPlaceholders = ExtractPlaceholders(enText);
        var zhPlaceholders = ExtractPlaceholders(zhText);

        Assert.Equal(enPlaceholders, zhPlaceholders);

        object response = stageKey.Contains("corruptionDetect", StringComparison.Ordinal)
            ? new CorruptionDetectionStatusResponse { StageKey = stageKey, Context = context }
            : stageKey.Contains("logRemoval", StringComparison.Ordinal)
                ? new LogRemovalStatusResponse { StageKey = stageKey, Context = context }
                : stageKey.Contains("dataImport", StringComparison.Ordinal)
                    ? new DataImportStatusResponse { StageKey = stageKey, Context = context }
                    : new DatabaseResetStatusResponse { StageKey = stageKey, Context = context };
        var roundTrip = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var serializedContext = roundTrip.GetProperty("context");

        foreach (var placeholder in enPlaceholders)
        {
            Assert.True(context.ContainsKey(placeholder), $"{stageKey} lacks {placeholder}");
            Assert.True(serializedContext.TryGetProperty(placeholder, out _),
                $"serialized {stageKey} lacks {placeholder}");
        }

        var interpolated = PlaceholderRegex().Replace(enText, match =>
            context[match.Groups[1].Value]?.ToString() ?? string.Empty);
        Assert.DoesNotContain("{{", interpolated);
        Assert.DoesNotContain("}}", interpolated);
    }

    private static JsonElement Resolve(JsonElement root, string stageKey)
    {
        var current = root;
        foreach (var segment in stageKey.Split('.'))
        {
            current = current.GetProperty(segment);
        }

        return current;
    }

    private static string[] ExtractPlaceholders(string text) => PlaceholderRegex()
        .Matches(text)
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    [GeneratedRegex(@"{{\s*([A-Za-z0-9_]+)\s*}}")]
    private static partial Regex PlaceholderRegex();
}
