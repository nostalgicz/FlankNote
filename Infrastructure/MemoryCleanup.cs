using System.Windows.Threading;

namespace FlankNote;

/// <summary>
/// Releases short-lived editor graphs and cold process pages after the UI is idle.
/// Active input and animations postpone cleanup, keeping interaction responsive.
/// </summary>
static class MemoryCleanup
{
    static DispatcherTimer? _timer;

    public static void Schedule(Dispatcher dispatcher)
    {
        if (_timer == null || _timer.Dispatcher != dispatcher)
        {
            _timer?.Stop();
            _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _timer.Tick += OnTimerTick;
        }

        // Reuse one timer. Editor input can schedule this frequently; the deck
        // tracks the latest activity time, so an already-running timer does not
        // need to be restarted on every mouse move or keystroke.
        if (!_timer.IsEnabled) _timer.Start();
    }

    static void OnTimerTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer) return;
        timer.Stop();
        if (!ReferenceEquals(_timer, timer)) return;
        if (!App.Deck.CanTrimMemory
            || System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>()
                .Any(window => window.IsVisible
                    && window is not DeckWindow
                    && window is not NoteWindow) == true)
        {
            timer.Start();
            return;
        }

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced,
            blocking: true, compacting: false);
        Native.TrimCurrentProcessWorkingSet();
    }
}
