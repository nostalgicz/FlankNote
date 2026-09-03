using System.Threading;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace FlankNote;

partial class App : Application
{
    internal static DeckWindow Deck = null!;
    internal static NoteWindow? OpenNote;
    internal static AllNotesWindow? AllNotes;
    internal static AllNotesWindow? ArchiveWin;
    internal static SettingsWindow? SettingsWin;
    internal static UpdateWindow? UpdateWin;
    internal static TrayIcon? Tray;
    internal static GitHubRelease? LatestRelease { get; private set; }
    internal static event Action? UpdateStateChanged;

    static Mutex? _mutex;
    static readonly object UpdateGate = new();
    static Task<GitHubRelease?>? _latestReleaseTask;

    internal static void ReportError(string message)
    {
        try { System.IO.File.AppendAllText(AppIdentity.DebugLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n"); }
        catch { }
        try { Tray?.NotifyError(message); }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        EventManager.RegisterClassHandler(
            typeof(ScrollBar),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => UiTheme.ApplySlimMetric((ScrollBar)sender)));

        DispatcherUnhandledException += (_, ex) =>
        {
            try { System.IO.File.AppendAllText(AppIdentity.DebugLogPath, $"[CRASH] {ex.Exception}\r\n"); }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { System.IO.File.AppendAllText(AppIdentity.DebugLogPath, $"[CRASH-AD] {ex.ExceptionObject}\r\n"); }
            catch { }
        };

        _mutex = new Mutex(true, AppIdentity.SingleInstanceId, out bool first);
        if (!first) { Shutdown(); return; }

        NotesStore.I.Load();

        Deck = new DeckWindow();
        Deck.Show();
        Tray = new TrayIcon(Deck);
        if (NotesStore.I.LoadError is { } loadError)
            Dispatcher.BeginInvoke(() => MessageBox.Show(loadError,
                Loc.T("Storage warning", "存储警告"), MessageBoxButton.OK, MessageBoxImage.Warning));
        else if (!Settings.FirstRunCompleted)
            Dispatcher.BeginInvoke(ShowFirstInstallWelcome);

        Dispatcher.BeginInvoke(StartBackgroundUpdateCheck,
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Dispatcher.BeginInvoke(() => MemoryCleanup.Schedule(Dispatcher),
            System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    static void ShowFirstInstallWelcome()
    {
        try
        {
            _ = new FirstRunWindow();
        }
        catch (Exception ex)
        {
            ReportError($"First-install welcome failed: {ex}");
            MessageBox.Show(
                Loc.T("The welcome page could not be opened. Check the application log for details.",
                      "无法打开欢迎页，详细信息请查看应用日志。"),
                Loc.T("Welcome page error", "欢迎页错误"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static void OpenUpdates()
    {
        try
        {
            if (UpdateWin == null) UpdateWin = new UpdateWindow();
            else
            {
                if (UpdateWin.WindowState == WindowState.Minimized)
                    UpdateWin.WindowState = WindowState.Normal;
                UpdateWin.Activate();
            }
        }
        catch (Exception ex)
        {
            ReportError($"Update window failed: {ex}");
            MessageBox.Show(
                Loc.T("The update checker could not be opened. Check the application log for details.",
                      "无法打开更新检查，详细信息请查看应用日志。"),
                Loc.T("Update error", "更新错误"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal static Task<GitHubRelease?> GetLatestReleaseAsync(bool forceRefresh = false)
    {
        lock (UpdateGate)
        {
            if (_latestReleaseTask == null
                || _latestReleaseTask.IsFaulted
                || (forceRefresh && _latestReleaseTask.IsCompleted))
                _latestReleaseTask = LoadLatestReleaseAsync();
            return _latestReleaseTask;
        }
    }

    static async Task<GitHubRelease?> LoadLatestReleaseAsync()
    {
        var release = await GitHubUpdateService.GetLatestAsync().ConfigureAwait(false);
        LatestRelease = release;
        UpdateStateChanged?.Invoke();
        return release;
    }

    static async void StartBackgroundUpdateCheck()
    {
        try { await GetLatestReleaseAsync().ConfigureAwait(false); }
        catch (Exception ex) { ReportError($"Background update check failed: {ex}"); }
        finally
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher is { HasShutdownStarted: false })
                await dispatcher.InvokeAsync(() => MemoryCleanup.Schedule(dispatcher));
        }
    }

    internal static void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppIdentity.RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ReportError($"Opening GitHub repository failed: {ex}");
        }
    }

    internal static Note CreateNote(string body = "", bool open = false)
    {
        var n = NotesStore.I.Create(body);
        if (open) Deck.OpenNote(n);
        return n;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (Window w in Windows)
            if (w is NoteWindow nw) nw.Save();
        NotesStore.I.Save();
        Tray?.Dispose();
        base.OnExit(e);
    }
}
