using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Lazily surfaces Riot host-resolution telemetry from one Rust log-processor invocation. A run with
/// no Riot host observations stays invisible; once an observation arrives it uses the canonical
/// tracked mapping lifecycle and the Rust byte percent as its real progress denominator.
/// </summary>
internal sealed class RiotMappingRunReporter : IAsyncDisposable
{
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _tracker;
    private readonly ILogger _logger;
    private readonly CancellationToken _sourceToken;
    private readonly bool _showNotification;
    private readonly Action _cancelOwner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private MappingOperationReporter? _reporter;
    private CancellationTokenRegistration _cancelRegistration;
    private long _processed;
    private long _mapped;
    private double _percent;
    private bool _completed;

    public RiotMappingRunReporter(
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker tracker,
        ILogger logger,
        CancellationToken sourceToken,
        bool showNotification,
        Action cancelOwner)
    {
        _notifications = notifications;
        _tracker = tracker;
        _logger = logger;
        _sourceToken = sourceToken;
        _showNotification = showNotification;
        _cancelOwner = cancelOwner;
    }

    public async Task ObserveAsync(long processed, long mapped, double percent)
    {
        if (processed <= 0 || _completed)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_completed)
            {
                return;
            }

            if (_reporter is null)
            {
                _reporter = new MappingOperationReporter(
                    _notifications,
                    _tracker,
                    MappingOperations.Riot,
                    _showNotification,
                    _sourceToken,
                    _logger,
                    bestEffortNotifications: true);
                _processed = processed;
                _mapped = mapped;
                _percent = Math.Clamp(percent, 0, 100);
                await _reporter.StartAsync(Context());
                _cancelRegistration = _reporter.Token.Register(CancelOwner);
            }

            var changed = processed != _processed || mapped != _mapped || percent > _percent;
            _processed = Math.Max(_processed, processed);
            _mapped = Math.Max(_mapped, mapped);
            _percent = Math.Max(_percent, Math.Clamp(percent, 0, 100));
            if (changed && !_reporter.Token.IsCancellationRequested)
            {
                await _reporter.ReportAsync(
                    _percent,
                    "signalr.riotMapping.resolving",
                    Context());
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(bool success, bool cancelled, string? error)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            if (_reporter is null || _completed)
            {
                return;
            }

            _completed = true;
            await _reporter.CompleteAsync(
                success,
                error,
                cancelled,
                context: Context(error));
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, object?> Context(string? errorDetail = null) =>
        new()
        {
            ["processed"] = _processed,
            ["mapped"] = _mapped,
            ["errorDetail"] = errorDetail,
        };

    private void CancelOwner()
    {
        try
        {
            _cancelOwner();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to propagate Riot mapping cancellation to log processing");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancelRegistration.Dispose();
        if (_reporter is not null)
        {
            await _reporter.DisposeAsync();
        }

        _gate.Dispose();
    }
}
