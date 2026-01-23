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

/// <summary>
/// Test implementation with controllable time.
/// </summary>
public class FakeTimeProvider : ITimeProvider
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
    public DateTime Now => UtcNow.ToLocalTime();
    public DateTimeOffset UtcNowOffset => new(UtcNow);

    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
    public void SetTime(DateTime utcTime) => UtcNow = utcTime;
}
