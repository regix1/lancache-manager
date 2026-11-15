using System.Text.Json;

namespace LancacheManager.Tests;

/// <summary>Reads keys out of the shipped en and zh locale files.</summary>
internal static class LocaleKeys
{
    private static readonly Lazy<JsonElement[]> Locales = new(() => new[] { "en", "zh" }
        .Select(locale => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            EndpointAuthorizationHost.FindRepositoryRoot(), "Web", "src", "i18n", "locales", locale + ".json"))).RootElement)
        .ToArray());

    /// <summary>True when <paramref name="key"/> names a string in both the en and the zh locale file.</summary>
    public static bool TranslatedInBothLocales(string key)
        => key.Contains('.') && Locales.Value.All(root => TextAt(root, key) is { Length: > 0 });

    private static string? TextAt(JsonElement root, string key)
    {
        var current = root;
        foreach (var segment in key.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }
}
