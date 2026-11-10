namespace Ripparr.Components;

/// <summary>
/// Runs an action at most once per interval, for pages that follow job events: progress ticks come
/// many times a second. A call inside the interval is not dropped but run when the interval is up,
/// so the last change of a burst (a job finishing right after its last progress tick) always shows.
/// Before, the queue kept the last job at "Finish" until the page was reloaded.
/// </summary>
public sealed class Throttle(TimeSpan every, Action action) : IDisposable
{
    private readonly Lock _lock = new();
    private DateTime _last = DateTime.MinValue;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>Something changed: run the action now, or once when the interval is up.</summary>
    public void Poke()
    {
        lock (_lock)
        {
            // One run is already waiting: it will see this change too.
            if (_disposed || _timer != null)
            {
                return;
            }

            var wait = _last + every - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                _timer = new Timer(_ => Fire(), null, wait, Timeout.InfiniteTimeSpan);
                return;
            }

            _last = DateTime.UtcNow;
        }

        Run();
    }

    private void Fire()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed)
            {
                return;
            }

            _last = DateTime.UtcNow;
        }

        Run();
    }

    private void Run()
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // Often on a timer thread: a page that went away must not take the app down.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
