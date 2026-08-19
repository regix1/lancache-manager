using System.Text;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>Accumulates either all text or a bounded diagnostic tail with an overflow marker.</summary>
internal sealed class BoundedTextTail
{
    internal const string TruncationMarker = "[stderr truncated; showing retained tail]";

    private readonly int? _maximumChars;
    private readonly StringBuilder _buffer = new();
    private bool _truncated;

    internal BoundedTextTail(int? maximumChars)
    {
        if (maximumChars is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChars));
        }

        _maximumChars = maximumChars;
    }

    internal void AppendLine(string line)
    {
        _buffer.Append(line).AppendLine();
        if (_maximumChars is not int maximumChars || _buffer.Length <= maximumChars)
        {
            return;
        }

        _buffer.Remove(0, _buffer.Length - maximumChars);
        _truncated = true;
    }

    public override string ToString() => _truncated
        ? $"{TruncationMarker}{Environment.NewLine}{_buffer}"
        : _buffer.ToString();
}
