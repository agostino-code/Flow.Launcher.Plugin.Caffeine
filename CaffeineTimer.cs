using System;
using System.Timers;

namespace Flow.Launcher.Plugin.Caffeine;

/// <summary>
/// Countdown timer for timed caffeine activation.
/// </summary>
internal class CaffeineTimer : IDisposable
{
    private Timer _timer;
    private readonly object _lock = new();

    /// <summary>
    /// The absolute time when caffeine will expire.
    /// </summary>
    public DateTime ExpiresAt { get; private set; }

    /// <summary>
    /// Fired when the countdown reaches zero. Fires on a thread pool thread.
    /// </summary>
    public event EventHandler Expired;

    /// <summary>
    /// Start or restart the countdown with a new duration.
    /// </summary>
    public void Start(TimeSpan duration)
    {
        lock (_lock)
        {
            _timer?.Stop();
            _timer?.Dispose();

            ExpiresAt = DateTime.Now + duration;
            _timer = new Timer(duration.TotalMilliseconds);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }
    }

    /// <summary>
    /// Get the time remaining until expiry.
    /// </summary>
    public TimeSpan GetRemainingTime()
    {
        var remaining = ExpiresAt - DateTime.Now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Format remaining time for display. Example: "1h 23m remaining" or "42m remaining".
    /// </summary>
    public string FormatRemainingTime()
    {
        var remaining = GetRemainingTime();
        if (remaining <= TimeSpan.Zero)
            return "0m remaining";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m remaining";
        return $"{(int)remaining.TotalMinutes}m remaining";
    }

    private void OnTimerElapsed(object sender, ElapsedEventArgs e)
    {
        Expired?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
    }
}
