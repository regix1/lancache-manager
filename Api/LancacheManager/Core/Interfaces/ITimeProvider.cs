namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Abstraction for system time to enable testing of time-dependent code.
/// </summary>
public interface ITimeProvider
{
    DateTime UtcNow { get; }
    DateTime Now { get; }
    DateTimeOffset UtcNowOffset { get; }
}

/// <summary>
/// Default implementation using system clock.
/// </summary>
public class SystemTimeProvider : ITimeProvider
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;
    public DateTimeOffset UtcNowOffset => DateTimeOffset.UtcNow;
}
