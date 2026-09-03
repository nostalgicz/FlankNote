using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace FlankNote;

/// <summary>System tray entry backed by an application-styled WPF command menu.</summary>
class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly Drawing.Icon _trayImage;
    readonly DeckWindow _deck;
    TrayMenuWindow? _menu;

    public TrayIcon(DeckWindow deck)
    {
        _deck = deck;
        _trayImage = MakeIcon();
        _icon = new Forms.NotifyIcon
        {
            Text = AppIdentity.DisplayName,
            Visible = true,
            Icon = _trayImage,
        };
        _icon.MouseUp += (_, e) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.Button == Forms.MouseButtons.Left) _deck.ExpandForTray();
            else if (e.Button == Forms.MouseButtons.Right) ShowMenu();
        });
    }

    Drawing.Icon MakeIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/icon.ico"));
            if (resource != null)
            {
                uint dpi = Native.GetDpiForSystem();
                int size = Math.Clamp((int)Math.Round(16 * Math.Max(96, dpi) / 96.0), 16, 48);
                using (resource.Stream)
                using (var source = new Drawing.Icon(resource.Stream, new Drawing.Size(size, size)))
                    return (Drawing.Icon)source.Clone();
            }
        }
        catch { }

        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var associated = Drawing.Icon.ExtractAssociatedIcon(executable);
                if (associated != null) return associated;
            }
        }
        catch { }
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    void ShowMenu()
    {
        _menu?.Close();
        var menu = new TrayMenuWindow(this);
        _menu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
        };
        menu.Show();
        menu.Activate();
    }

    internal void SetDeckStyle(string style)
    {
        Settings.DeckStyle = style;
        NotesStore.I.Save();
        App.Deck.Refresh();
    }

    internal void ExportNotes()
    {
        var dialog = new Forms.SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = $"{AppIdentity.DefaultExportStem}-{DateTime.Now:yyyyMMdd-HHmm}.md",
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        try
        {
            NotesStore.I.ExportMarkdown(dialog.FileName);
            _icon.ShowBalloonTip(2500, AppIdentity.DisplayName,
                Loc.T("Notes exported.", "便签已导出。"), Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            App.ReportError($"Markdown export failed: {ex}");
        }
    }

    public void RefreshMenu() => _menu?.Close();

    public void NotifyError(string message)
    {
        _icon.ShowBalloonTip(
            4500,
            AppIdentity.DisplayName,
            Loc.T("An error occurred. Details were written to the application log.",
                  "发生错误，详细信息已写入应用日志。"),
            Forms.ToolTipIcon.Error);
    }

    public void Dispose()
    {
        _menu?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _trayImage.Dispose();
    }
}

sealed class TrayMenuWindow : Window
{
    readonly TrayIcon _tray;
    readonly List<Border> _focusableItems = [];
    bool _runningAction;

    public TrayMenuWindow(TrayIcon tray)
    {
        _tray = tray;
        // Keep the tray menu compact while leaving enough room for the longest
        // localized command ("Check for updates").
        Width = 115;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Opacity = 0;
        FontFamily = UiTheme.Font;
        Foreground = UiTheme.Text;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Content = BuildMenu();

        SourceInitialized += (_, _) => Native.MarkToolWindow(this);
        Loaded += (_, _) => PositionAtTray();
        Deactivated += (_, _) =>
        {
            if (!_runningAction) Close();
        };
        Activated += (_, _) =>
        {
            if (_focusableItems.Count > 0) _focusableItems[0].Focus();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _focusableItems.Clear();
            Content = null;
        };
    }

    UIElement BuildMenu()
    {
        var items = new StackPanel();
        items.Children.Add(Command("\uE710", Loc.T("New note", "新建便签"),
            () => App.CreateNote(open: true)));
        items.Children.Add(Command("\uE8A5", Loc.T("All notes", "全部便签"), OpenAllNotes));
        items.Children.Add(Command("\uE7B8", Loc.T("Archive", "归档便签"), OpenArchive));
        items.Children.Add(Divider());
        items.Children.Add(Command("\uE74E", Loc.T("Export", "导出"), _tray.ExportNotes));
        items.Children.Add(Command("\uE713", Loc.T("Settings", "设置"), OpenSettings));
        bool updateAvailable = App.LatestRelease is { } release
            && GitHubUpdateService.IsNewer(release.TagName);
        items.Children.Add(Command("\uE72C", Loc.T("Check for updates", "检查更新"), OpenUpdates,
            updateAvailable ? Loc.T("NEW", "新") : null));
        items.Children.Add(Divider());
        items.Children.Add(Command("\uE7E8", Loc.T("Quit", "退出"),
            () => System.Windows.Application.Current.Shutdown(), danger: true));

        var shell = new Border
        {
            Background = UiTheme.Surface,
            BorderBrush = UiTheme.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Margin = new Thickness(6),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.20,
            },
            Child = items,
        };
        return shell;
    }

    Border Command(string glyph, string label, Action action, string? badge = null, bool danger = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = danger ? UiTheme.Danger : UiTheme.Text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(text);
        if (badge != null)
        {
            var badgeText = new TextBlock
            {
                Text = badge,
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiTheme.Accent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(badgeText, 2);
            grid.Children.Add(badgeText);
        }
        var row = InteractiveRow(grid, () => Run(action));
        return row;
    }

    Border InteractiveRow(UIElement content, Action action, Brush? rest = null)
    {
        rest ??= Brushes.Transparent;
        var row = new Border
        {
            Height = 34,
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = UiTheme.ControlRadius,
            Background = rest,
            Cursor = Cursors.Hand,
            Focusable = true,
            Child = content,
        };
        void Hover(bool active) => row.Background = active ? UiTheme.Selection : rest;
        row.MouseEnter += (_, _) => Hover(true);
        row.MouseLeave += (_, _) => Hover(row.IsKeyboardFocusWithin);
        row.GotKeyboardFocus += (_, _) => Hover(true);
        row.LostKeyboardFocus += (_, _) => Hover(row.IsMouseOver);
        row.MouseLeftButtonUp += (_, _) => action();
        row.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                action();
                e.Handled = true;
            }
        };
        _focusableItems.Add(row);
        return row;
    }

    static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(9, 5, 9, 5),
        Background = new SolidColorBrush(UiTheme.Hairline.Color) { Opacity = 0.75 },
    };

    void OpenAllNotes()
    {
        App.Deck.DismissAll();
        if (App.AllNotes == null) App.AllNotes = new AllNotesWindow();
        else App.AllNotes.Activate();
    }

    void OpenArchive()
    {
        App.Deck.DismissAll();
        if (App.ArchiveWin == null) App.ArchiveWin = new AllNotesWindow(archivedOnly: true);
        else App.ArchiveWin.Activate();
    }

    void OpenSettings()
    {
        try
        {
            App.Deck.DismissAll();
            if (App.SettingsWin == null) App.SettingsWin = new SettingsWindow();
            else App.SettingsWin.Activate();
        }
        catch (Exception ex)
        {
            App.ReportError($"Settings window failed: {ex}");
        }
    }

    void OpenUpdates()
    {
        App.Deck.DismissAll();
        App.OpenUpdates();
    }

    void Run(Action action)
    {
        if (_runningAction) return;
        _runningAction = true;
        Close();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key is not (Key.Up or Key.Down) || _focusableItems.Count == 0) return;
        int current = _focusableItems.FindIndex(item => item.IsKeyboardFocusWithin);
        int delta = e.Key == Key.Down ? 1 : -1;
        int next = (current + delta + _focusableItems.Count) % _focusableItems.Count;
        _focusableItems[next].Focus();
        e.Handled = true;
    }

    void PositionAtTray()
    {
        var cursor = Forms.Cursor.Position;
        var work = Forms.Screen.FromPoint(cursor).WorkingArea;
        var handle = new WindowInteropHelper(this).Handle;
        // Move invisibly first so GetDpiForWindow reflects the tray monitor,
        // rather than whichever monitor WPF used while creating the HWND.
        double provisionalScale = Math.Max(1, Native.GetDpiForSystem() / 96.0);
        var provisional = CalculateBounds(cursor, work,
            (int)Math.Ceiling(ActualWidth * provisionalScale),
            (int)Math.Ceiling(ActualHeight * provisionalScale));
        Native.PositionTopmost(this, provisional.X, provisional.Y,
            provisional.Width, provisional.Height);
        double scale = Math.Max(1, Native.GetDpiForWindow(handle) / 96.0);
        int width = (int)Math.Ceiling(ActualWidth * scale);
        int height = (int)Math.Ceiling(ActualHeight * scale);
        var bounds = CalculateBounds(cursor, work, width, height);
        Native.PositionTopmost(this, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        Opacity = 1;
    }

    internal static Drawing.Rectangle CalculateBounds(
        Drawing.Point cursor, Drawing.Rectangle workArea, int width, int height)
    {
        width = Math.Min(Math.Max(1, width), workArea.Width);
        height = Math.Min(Math.Max(1, height), workArea.Height);
        int preferredX = cursor.X - width + 12;
        int preferredY = cursor.Y - height - 10;
        if (preferredY < workArea.Top) preferredY = cursor.Y + 10;
        int x = Math.Clamp(preferredX, workArea.Left, workArea.Right - width);
        int y = Math.Clamp(preferredY, workArea.Top, workArea.Bottom - height);
        return new Drawing.Rectangle(x, y, width, height);
    }
}
