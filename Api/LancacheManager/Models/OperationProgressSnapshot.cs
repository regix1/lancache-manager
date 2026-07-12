using System.Collections.ObjectModel;
using System.Text.Json;

namespace LancacheManager.Models;

/// <summary>
/// Immutable, operation-owned progress state used to make reconnect recovery newer than a
/// deliberately throttled SignalR broadcast. Context is defensively copied at creation time.
/// </summary>
public sealed record OperationProgressSnapshot(
    string StageKey,
    double PercentComplete,
    IReadOnlyDictionary<string, object?> Context,
    long Revision,
    DateTimeOffset CapturedAtUtc)
{
    public static OperationProgressSnapshot Create(
        string stageKey,
        double percentComplete,
        IReadOnlyDictionary<string, object?>? context,
        long revision,
        IReadOnlyDictionary<string, object?>? authoritativeContext = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageKey);

        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        CopyInto(copy, context);
        CopyInto(copy, authoritativeContext);

        var finitePercent = double.IsFinite(percentComplete) ? percentComplete : 0.0;
        return new OperationProgressSnapshot(
            stageKey,
            Math.Clamp(finitePercent, 0.0, 100.0),
            new ReadOnlyDictionary<string, object?>(copy),
            revision,
            capturedAtUtc ?? DateTimeOffset.UtcNow);
    }

    internal bool HasSameProgress(
        string stageKey,
        double percentComplete,
        IReadOnlyDictionary<string, object?> context)
    {
        var finitePercent = double.IsFinite(percentComplete) ? Math.Clamp(percentComplete, 0.0, 100.0) : 0.0;
        if (!string.Equals(StageKey, stageKey, StringComparison.Ordinal)
            || PercentComplete != finitePercent
            || Context.Count != context.Count)
        {
            return false;
        }

        foreach (var pair in context)
        {
            if (!Context.TryGetValue(pair.Key, out var current) || !ValuesEqual(current, pair.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static void CopyInto(
        Dictionary<string, object?> destination,
        IReadOnlyDictionary<string, object?>? source)
    {
        if (source == null)
        {
            return;
        }

        foreach (var pair in source)
        {
            destination[pair.Key] = pair.Value is JsonElement element ? element.Clone() : pair.Value;
        }
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is JsonElement leftElement && right is JsonElement rightElement)
        {
            return JsonElement.DeepEquals(leftElement, rightElement);
        }

        if (left is JsonElement serializedLeft)
        {
            return JsonElement.DeepEquals(serializedLeft, JsonSerializer.SerializeToElement(right));
        }

        if (right is JsonElement serializedRight)
        {
            return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), serializedRight);
        }

        return Equals(left, right);
    }
}
