using System.Windows.Threading;
using Microsoft.Win32;

namespace PlanLite.Services;

/// <summary>
/// Event-driven midnight rollover monitor with zero high-frequency polling.
/// </summary>
public sealed class MidnightRefreshService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private DateOnly _currentDate;
    private bool _disposed;

    public MidnightRefreshService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _currentDate = DateOnly.FromDateTime(DateTime.Now);
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
        _timer.Tick += OnTimerTick;

        SystemEvents.TimeChanged += OnSystemTimeOrPowerChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        ScheduleNextMidnight();
    }

    public event Action<DateOnly>? DateChanged;

    private void OnTimerTick(object? sender, EventArgs e)
    {
        CheckDateRollover();
        ScheduleNextMidnight();
    }

    private void OnSystemTimeOrPowerChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            CheckDateRollover();
            ScheduleNextMidnight();
        });
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            OnSystemTimeOrPowerChanged(sender, EventArgs.Empty);
        }
    }

    private void CheckDateRollover()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _currentDate)
        {
            _currentDate = today;
            DateChanged?.Invoke(today);
        }
    }

    private void ScheduleNextMidnight()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();
        var now = DateTime.Now;
        var nextMidnight = now.Date.AddDays(1).AddSeconds(1);
        var delay = nextMidnight - now;
        if (delay < TimeSpan.FromSeconds(1))
        {
            delay = TimeSpan.FromSeconds(1);
        }

        _timer.Interval = delay;
        _timer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        SystemEvents.TimeChanged -= OnSystemTimeOrPowerChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
