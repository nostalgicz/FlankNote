using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Globalization;

namespace FlankNote;

/// <summary>
///  The open note: a paper sheet level with its own tab, carrying the tab
///  along as a gutter separated by a dashed rule. Markdown is styled in place;
///  the caret line exposes its source markers while other lines are rendered,
///  tasks live inline as ☐/☑ prefixes, and Ctrl+F finds within the note.
/// </summary>
class NoteWindow : Window
{
    readonly Note _note;
    readonly DeckWindow _deck;
    readonly bool _onRight;
    readonly Rect _workArea;
    readonly RichTextBox _body;
    readonly TextBox _title;
    readonly TextBlock _saved;
    readonly Border _pinBtn;
    readonly Border _modeBtn;
    readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DateTime _createdAt = DateTime.Now;
    bool _closing;
    bool _settingTitle;
    bool _titleEdited;
    bool _modalUiOpen;

    // find bar
    readonly Grid _findBar = new();
    readonly TextBox _findBox = new();
    readonly TextBlock _findCount = new();
    List<(Paragraph P, int Offset, string Text)> _findMatches = [];
    int _findIndex = -1;

    // palette sync: everything coloured by the note's palette gets refreshed together
    readonly List<Border> _headBtns = [];
    readonly List<Ellipse> _dots = [];
    Border? _customColourButton;
    TextBlock? _gutterTitle;
    Grid? _gutterLabelHost;
    Rectangle? _rule;
    Path? _resizeGrip;
    Border? _gutter;
    readonly ScaleTransform _sheetScale = new(0.965, 0.965);
    DispatcherTimer? _transition;
    DispatcherTimer? _deactivationCheck;
    HwndSource? _windowSource;
    bool _nativeResizing;
    bool _clampingBounds;
    Paragraph? _editingMarkdownParagraph;

    public bool Pinned => _note.Pinned;
    public bool HasModalInteraction => _modalUiOpen;
    public string NoteId => _note.Id;
    bool OnRight => _onRight;
    NoteColor Pal => _note.Palette;
    double ConfiguredOpacity => 1 - Math.Max(0, Settings.ClampNoteTransparency(Settings.NoteTransparency));

    static void Log(string s)
    {
        App.ReportError($"[NoteWindow] {s}");
    }
    SolidColorBrush DimBrush => new(Pal.Ink) { Opacity = 0.35 };

    public NoteWindow(Note note, DeckWindow deck, Point pos, Size windowSize, Rect workArea)
    {
        _note = note;
        _deck = deck;
        _onRight = !Settings.EdgeLeft;
        _workArea = workArea;
        Title = note.DisplayTitle;

        var limits = Geom.WindowSizeLimits(workArea);
        Width = windowSize.Width;
        Height = windowSize.Height;
        MinWidth = limits.Min.Width;
        MinHeight = limits.Min.Height;
        MaxWidth = limits.Max.Width;
        MaxHeight = limits.Max.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Opacity = 0;
        Topmost = Settings.OverlayFullscreen;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;

        SourceInitialized += (_, _) =>
        {
            ApplyOverlay();
            _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _windowSource?.AddHook(WindowHook);
        };
        Closed += (_, _) => ReleaseWindowResources();
        Activated += (_, _) => ReassertOverlay();
        Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            ReassertOverlay();
            OnDeactivated();
        }));
        PreviewMouseDown += (_, _) => { ReassertOverlay(); _deck.NoteActivity(); };
        PreviewMouseMove += (_, _) => _deck.NoteActivity();
        PreviewKeyDown += OnKey;        // Esc only — all custom shortcuts disabled on request

        // ── the sheet ─────────────────────────────────────
        var sheet = new Border
        {
            Margin = new Thickness(8),
            // Square where the sheet meets the screen, rounded where it pulls
            // into the desktop: the silhouette used by the upstream editor.
            CornerRadius = OnRight
                ? new CornerRadius(14, 0, 0, 14)
                : new CornerRadius(0, 14, 14, 0),
            Background = PaperBrush(),
            BorderBrush = new SolidColorBrush(Pal.Ink) { Opacity = 0.07 },
            BorderThickness = new Thickness(0.5),
            // The WPF sheet has 8 px of transparent breathing room. A literal
            // port of the macOS 28 pt shadow is clipped by the HWND boundary and
            // turns into a dark band, so use an optically equivalent compact
            // shadow that can actually finish fading inside this window.
            Effect = new DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2.5,
                Opacity = 0.24,
                Direction = OnRight ? 225 : 315,
                Color = Colors.Black,
                RenderingBias = RenderingBias.Quality,
            },
        };
        sheet.RenderTransformOrigin = new Point(OnRight ? 1 : 0, 0.5);
        sheet.RenderTransform = _sheetScale;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = OnRight ? new GridLength(30) : new GridLength(1, GridUnitType.Star),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = OnRight ? new GridLength(1, GridUnitType.Star) : new GridLength(30),
        });

        int gutterCol = OnRight ? 0 : 2;        // gutter sits against the deck edge
        var gutter = new Border
        {
            // same shape as the tabs: rounded toward the screen centre
            CornerRadius = OnRight ? new CornerRadius(14, 0, 0, 14) : new CornerRadius(0, 14, 14, 0),
            Background = GutterBrush(),
            Child = GutterLabel(),
        };
        _gutter = gutter;
        Grid.SetColumn(gutter, gutterCol);
        grid.Children.Add(gutter);

        var rule = new Rectangle
        {
            Stroke = new SolidColorBrush(Pal.Ink) { Opacity = 0.18 },
            StrokeDashArray = new DoubleCollection { 2, 5 },
            StrokeThickness = 1,
            Margin = new Thickness(0),
        };
        _rule = rule;
        Grid.SetColumn(rule, 1);
        grid.Children.Add(rule);

        _resizeGrip = new Path
        {
            Data = Geometry.Parse(OnRight
                ? "M2,10 L10,2 M2,7 L7,2 M5,10 L10,5"
                : "M2,2 L10,10 M5,2 L10,7 M2,5 L7,10"),
            Stroke = new SolidColorBrush(Pal.Ink) { Opacity = 0.24 },
            StrokeThickness = 1,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 12,
            Height = 12,
            Stretch = Stretch.None,
            HorizontalAlignment = OnRight ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = OnRight ? new Thickness(4, 0, 0, 4) : new Thickness(0, 0, 4, 4),
            IsHitTestVisible = false,
        };
        Grid.SetColumn(_resizeGrip, gutterCol);
        Panel.SetZIndex(_resizeGrip, 2);
        grid.Children.Add(_resizeGrip);

        // ── header + body ─────────────────────────────────
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _title = new TextBox
        {
            FontFamily = UiTheme.Font,
            FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.92 },
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 6, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            CaretBrush = Pal.InkB,
            AcceptsReturn = false,
            ToolTip = Loc.T("Edit note title", "编辑便签标题"),
        };
        Grid.SetColumn(_title, 0);
        head.Children.Add(_title);

        _saved = new TextBlock
        {
            FontFamily = UiTheme.Font,
            FontSize = 10,
            Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.42 },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(_saved, 1);
        head.Children.Add(_saved);

        var headTools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        _pinBtn = ToolBtn("\uE718", (_, _) => TogglePin(), square: true, symbol: true);
        headTools.Children.Add(_pinBtn);
        _modeBtn = ToolBtn(_note.UsesMarkdown ? "MD" : "TXT", (_, _) => ToggleTextMode(), subtle: true);
        _modeBtn.Width = 34;
        UpdateModeButton();
        headTools.Children.Add(_modeBtn);
        headTools.Children.Add(ToolBtn("\uE8FD", (_, _) => ToggleTaskAtCaret(), square: true, symbol: true));
        headTools.Children.Add(ToolBtn("\uE721", (_, _) => ToggleFindBar(), square: true, symbol: true));
        var resetSize = ToolBtn("\uE73F", (_, _) => ResetWindowSize(), square: true, symbol: true);
        resetSize.ToolTip = Loc.T("Restore default size", "恢复当前便签的默认大小");
        headTools.Children.Add(resetSize);
        Grid.SetColumn(headTools, 2);
        head.Children.Add(headTools);
        stack.Children.Add(head);

        // One editor provides as-you-type Markdown: only the caret line exposes source markers.
        // Passing the document into the constructor avoids creating and then
        // discarding RichTextBox's default FlowDocument.
        _body = new RichTextBox(new FlowDocument { PagePadding = new Thickness(0) })
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = UiTheme.Font,
            FontSize = Settings.NoteFontSize,
            AcceptsReturn = true,
            AcceptsTab = true,
            Padding = new Thickness(15, 6, 15, 10),
            Foreground = Pal.InkB,
            CaretBrush = Pal.InkB,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            UndoLimit = 64,
        };
        _title.TextChanged += (_, _) =>
        {
            if (_settingTitle || _closing) return;
            _titleEdited = true;
            _deck.NoteActivity();
            _autosave.Stop(); _autosave.Start();
        };
        // Initial load and styling are not user edits. Keeping their formatting
        // operations in the undo manager retains a second graph of the document.
        _body.IsUndoEnabled = false;
        LoadBody(_note.Body);
        ApplyMarkdown();
        _body.IsUndoEnabled = true;
        _body.TextChanged += (_, _) =>
        {
            if (_applyingMarkdown) return;
            _deck.NoteActivity();
            ApplyMarkdownIfLineChanged();
            _autosave.Stop(); _autosave.Start();
            if (_findBar.Visibility == Visibility.Visible) RefreshFindMatches();
        };
        _body.SelectionChanged += (_, _) => ApplyMarkdownIfLineChanged();
        _body.GotKeyboardFocus += (_, _) => ApplyMarkdown();
        _body.LostKeyboardFocus += (_, _) =>
            Dispatcher.BeginInvoke(new Action(ApplyMarkdown));
        _autosave.Tick += (_, _) =>
        {
            _autosave.Stop();
            Save();
        };
        _body.PreviewMouseLeftButtonDown += OnBodyMouseDown;

        // Use the application-wide real ScrollBar template. It retains the
        // restrained appearance while supporting thumb drag and track clicks.
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetRow(_body, 2);
        stack.Children.Add(_body);

        // footer: the original has the palette on the LEFT (tap a dot to recolor)
        // and Archive / Delete / Close on the right
        var foot = new Grid { VerticalAlignment = VerticalAlignment.Center };
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < NoteColor.All.Length; i++) dots.Children.Add(ColorDot(i));
        _customColourButton = CustomColourButton();
        dots.Children.Add(_customColourButton);
        Grid.SetColumn(dots, 0);
        foot.Children.Add(dots);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btns.Margin = new Thickness(0, 0, 14, 0);
        btns.Children.Add(ToolBtn(Loc.T("Archive", "归档"), (_, _) => ArchiveNote(), subtle: true));
        btns.Children.Add(ToolBtn(Loc.T("Delete", "删除"), (_, _) => DeleteNote(), subtle: true));
        btns.Children.Add(ToolBtn(Loc.T("Close", "关闭"), (_, _) => SaveAndClose(), subtle: true));
        Grid.SetColumn(btns, 2);
        foot.Children.Add(btns);
        Grid.SetRow(foot, 3);
        stack.Children.Add(foot);

        // The upstream find bar belongs between the header and the document.
        BuildFindBar();
        Grid.SetRow(_findBar, 1);
        stack.Children.Add(_findBar);

        Grid.SetColumn(stack, OnRight ? 2 : 0);
        grid.Children.Add(stack);
        sheet.Child = grid;
        Content = sheet;
        ApplyPinState();                       // reflect a note that was pinned before it opened

        // slide in from the deck, level with its tab
        Left = pos.X + (OnRight ? 40 : -40);
        Top = pos.Y;
        SizeChanged += (_, _) =>
        {
            UpdateGutterLabelBounds();
            if (_nativeResizing) ClampToWorkArea();
        };
        LocationChanged += (_, _) =>
        {
            if (_nativeResizing) ClampToWorkArea();
        };
        UpdateTitle();
        UpdateSavedState();
    }

    IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_ENTERSIZEMOVE)
        {
            _nativeResizing = true;
            _deck.NoteActivity();
            return IntPtr.Zero;
        }
        if (msg == Native.WM_EXITSIZEMOVE)
        {
            _nativeResizing = false;
            ClampToWorkArea();
            PersistWindowSize(save: true);
            ReassertOverlay();
            return IntPtr.Zero;
        }
        if (msg != Native.WM_NCHITTEST)
            return IntPtr.Zero;
        if (_closing || _transition != null)
        {
            handled = true;
            return new IntPtr(Native.HTCLIENT);
        }

        long packed = lParam.ToInt64();
        var client = PointFromScreen(new Point(
            unchecked((short)(packed & 0xFFFF)),
            unchecked((short)((packed >> 16) & 0xFFFF))));
        int hit = ResizeHitTest(OnRight, new Size(ActualWidth, ActualHeight), client);
        handled = true;
        return new IntPtr(hit);
    }

    internal static int ResizeHitTest(
        bool onRight, Size size, Point client, double edgeGrip = 11, double innerGrip = 22)
    {
        bool top = client.Y >= 0 && client.Y <= edgeGrip;
        bool bottom = client.Y <= size.Height && client.Y >= size.Height - edgeGrip;
        bool innerEdge = onRight
            ? client.X >= 0 && client.X <= innerGrip
            : client.X <= size.Width && client.X >= size.Width - innerGrip;

        int hit = 0;
        if (innerEdge && top) hit = onRight ? Native.HTTOPLEFT : Native.HTTOPRIGHT;
        else if (innerEdge && bottom) hit = onRight ? Native.HTBOTTOMLEFT : Native.HTBOTTOMRIGHT;
        else if (innerEdge) hit = onRight ? Native.HTLEFT : Native.HTRIGHT;
        else if (top) hit = Native.HTTOP;
        else if (bottom) hit = Native.HTBOTTOM;
        return hit == 0 ? Native.HTCLIENT : hit;
    }

    void ClampToWorkArea()
    {
        if (_clampingBounds) return;
        _clampingBounds = true;
        try
        {
            Width = Math.Clamp(Width, MinWidth, MaxWidth);
            Height = Math.Clamp(Height, MinHeight, MaxHeight);
            Left = OnRight ? _workArea.Right - Width + 8 : _workArea.Left - 8;
            Top = Math.Clamp(Top, _workArea.Top - 8, _workArea.Bottom - Height + 8);
        }
        finally { _clampingBounds = false; }
    }

    void PersistWindowSize(bool save = false)
    {
        if (!double.IsFinite(Width) || !double.IsFinite(Height)) return;
        _note.WindowWidth = Math.Round(Math.Clamp(Width, MinWidth, MaxWidth), 1);
        _note.WindowHeight = Math.Round(Math.Clamp(Height, MinHeight, MaxHeight), 1);
        if (save) NotesStore.I.Save();
    }

    void ResetWindowSize()
    {
        if (_closing || _nativeResizing) return;
        var size = Geom.DefaultWindowSize(_workArea);
        Width = size.Width;
        Height = size.Height;
        ClampToWorkArea();
        PersistWindowSize(save: true);
        _deck.NoteActivity();
        ReassertOverlay();
        _body.Focus();
    }

    void UpdateGutterLabelBounds()
    {
        double available = Math.Max(1, Height - Geom.WindowInset);
        if (_gutterLabelHost != null) _gutterLabelHost.Height = available;
        if (_gutterTitle != null) _gutterTitle.MaxWidth = Math.Max(1, available - 12);
    }

    // ── visual helpers ──────────────────────────────────────
    void UpdateTitle()
    {
        var title = _note.DisplayTitle;
        if (_title.Text == title) return;
        _settingTitle = true;
        _title.Text = title;
        _settingTitle = false;
        Title = title;
    }

    void UpdateSavedState()
    {
        var age = DateTime.Now - _note.Updated;
        string ago = age.TotalMinutes < 1
            ? Loc.T("just now", "刚刚")
            : age.TotalHours < 1
                ? Loc.T($"{Math.Max(1, (int)age.TotalMinutes)}m ago", $"{Math.Max(1, (int)age.TotalMinutes)} 分钟前")
                : age.TotalDays < 1
                    ? Loc.T($"{Math.Max(1, (int)age.TotalHours)}h ago", $"{Math.Max(1, (int)age.TotalHours)} 小时前")
                    : _note.Updated.ToString("yyyy-MM-dd");
        _saved.Text = Loc.T($"Saved · {ago}", $"已保存 · {ago}");
    }

    Brush PaperBrush()
    {
        double opaqueBoost = Math.Clamp(
            -Settings.ClampNoteTransparency(Settings.NoteTransparency) / 0.70, 0, 1);
        byte bottomAlpha = (byte)Math.Round(224 + 31 * opaqueBoost);
        var bottom = Color.FromArgb(bottomAlpha, Pal.Paper.R, Pal.Paper.G, Pal.Paper.B);
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Pal.Paper, 0),
                new(bottom, 1),
            },
            new Point(0, 0), new Point(0, 1));
    }

    Brush GutterBrush() => new SolidColorBrush(Pal.Dash) { Opacity = 0.20 };

    static string LimitTextElements(string value, int maximum)
    {
        var starts = StringInfo.ParseCombiningCharacters(value);
        return starts.Length <= maximum ? value : value[..starts[maximum]] + "…";
    }

    public void AnimateIn(double targetLeft)
    {
        _transition?.Stop();
        double fromLeft = Left;
        double fromScale = _sheetScale.ScaleX;
        var started = DateTime.UtcNow;
        const double durationMs = 280;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / durationMs);
            double spring = UiTheme.Spring(p, 0.82);
            Left = fromLeft + (targetLeft - fromLeft) * spring;
            double scale = fromScale + (1 - fromScale) * spring;
            _sheetScale.ScaleX = _sheetScale.ScaleY = scale;
            Opacity = ConfiguredOpacity * Math.Min(1, p * 1.8);
            if (p >= 1)
            {
                timer.Stop();
                if (_transition == timer) _transition = null;
                Left = targetLeft;
                _sheetScale.ScaleX = _sheetScale.ScaleY = 1;
                Opacity = ConfiguredOpacity;
            }
        };
        _transition = timer;
        timer.Start();
    }

    void AnimateOut(Action completed)
    {
        _transition?.Stop();
        IsHitTestVisible = false;
        double fromLeft = Left;
        double fromScale = _sheetScale.ScaleX;
        double fromOpacity = Opacity;
        double targetLeft = fromLeft + (OnRight ? 40 : -40);
        var started = DateTime.UtcNow;
        const double durationMs = 220;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / durationMs);
            double spring = UiTheme.Spring(p, 0.88);
            Left = fromLeft + (targetLeft - fromLeft) * spring;
            double scale = fromScale + (0.965 - fromScale) * spring;
            _sheetScale.ScaleX = _sheetScale.ScaleY = scale;
            Opacity = Math.Max(0, fromOpacity * (1 - UiTheme.EaseOut(p)));
            if (p >= 1)
            {
                timer.Stop();
                if (_transition == timer) _transition = null;
                Close();
                completed();
            }
        };
        _transition = timer;
        timer.Start();
    }

    FrameworkElement GutterLabel()
    {
        // the label runs vertically along the gutter (left of the dashed rule).
        // Same size as the deck tabs (10.5 Bold) — long titles ellipsise, they
        // don't shrink, so the open note and the hidden deck read identically
        var title = LimitTextElements(_note.DisplayTitle.ToUpperInvariant(), 24);
        double avail = Height - Geom.WindowInset - 12;         // vertical room for the rotated label
        double size = 9.5;

        var tb = new TextBlock
        {
            Text = title,
            FontFamily = UiTheme.Font, FontSize = size, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.70 },
            MaxWidth = avail,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            // WPF's rendered reading direction is the inverse of SwiftUI's for
            // this transformed horizontal label.
            LayoutTransform = new RotateTransform(OnRight ? -90 : 90),
        };
        _gutterTitle = tb;
        var host = new Grid
        {
            Width = 30,
            Height = Height - Geom.WindowInset,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        _gutterLabelHost = host;
        host.Children.Add(tb);
        return host;
    }

    Border ToolBtn(string text, MouseButtonEventHandler onClick, bool square = false,
                   bool subtle = false, bool symbol = false)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Cursor = Cursors.Hand,
            Margin = subtle ? new Thickness(7, 0, 0, 0) : new Thickness(4, 0, 0, 0),
            Padding = subtle ? new Thickness(8, 0, 8, 0) : new Thickness(0),
            Height = subtle ? 20 : double.NaN,
            Background = subtle ? new SolidColorBrush(Pal.Ink) { Opacity = 0.08 } : Brushes.Transparent,
            Child = new TextBlock
            {
                Text = text,
                FontFamily = symbol ? UiTheme.Symbols : UiTheme.Font,
                FontSize = symbol ? 11 : 10.5,
                FontWeight = symbol ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = new SolidColorBrush(Pal.Ink) { Opacity = symbol ? 0.50 : 0.72 },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (square)   // header icon buttons are square blocks, not pills
        {
            b.Width = 22; b.Height = 22;
            b.Padding = new Thickness(0);
        }
        b.MouseEnter += (_, _) => b.Background = new SolidColorBrush(Pal.Ink) { Opacity = 0.14 };
        b.MouseLeave += (_, _) => b.Background = subtle
            ? new SolidColorBrush(Pal.Ink) { Opacity = 0.08 }
            : Brushes.Transparent;
        b.MouseLeftButtonUp += onClick;
        _headBtns.Add(b);
        return b;
    }

    /// <summary>A palette dot, exactly like the original footer: 11px circle in the
    /// colour's dash, ringed when it is the note's current colour.</summary>
    Ellipse ColorDot(int i)
    {
        var pal = NoteColor.All[i];
        var e = new Ellipse
        {
            Width = 11, Height = 11,
            Fill = pal.DashB,
            Stroke = !_note.HasCustomColor && i == _note.Color
                ? new SolidColorBrush(Pal.Ink) { Opacity = 0.55 }
                : null,
            StrokeThickness = 1.5,
            Cursor = Cursors.Hand,
            ToolTip = Loc.ColourName(pal.Name),
            // left 10 clears the dashed rule (the original's footer pads 14pt);
            // top 4 lines the dots up with the action buttons; 1px gaps
            Margin = i == 0 ? new Thickness(14, 0, 7, 0) : new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        int idx = i;
        e.MouseLeftButtonUp += (_, _) => SetColor(idx);
        _dots.Add(e);
        return e;
    }

    void SetColor(int i)
    {
        try
        {
            if (_note.Color == i && !_note.HasCustomColor) return;
            _note.Color = i;
            _note.CustomColor = null;
            NotesStore.I.Update(_note);
            RebuildVisual();                    // recolour the whole note together
            _body.Focus();
        }
        catch (Exception ex) { Log($"[SetColor EX] {ex}"); }
    }

    Border CustomColourButton()
    {
        var swatch = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = UiTheme.ColourSpectrum,
            Stroke = new SolidColorBrush(Colors.White) { Opacity = 0.75 },
            StrokeThickness = 0.7,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border
        {
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 7, 0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1.5),
            BorderBrush = _note.HasCustomColor
                ? new SolidColorBrush(Pal.Ink) { Opacity = 0.55 }
                : Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("Custom colour…", "自定义颜色…"),
            Child = swatch,
        };
        button.MouseEnter += (_, _) =>
            button.Background = new SolidColorBrush(Pal.Ink) { Opacity = 0.12 };
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            ChooseCustomColour();
            e.Handled = true;
        };
        return button;
    }

    void ChooseCustomColour()
    {
        _modalUiOpen = true;
        StopDeactivationCheck();
        try
        {
            var initial = NoteColor.TryParse(_note.CustomColor, out var custom)
                ? custom
                : Pal.Dash;
            var selected = ColourPickerDialog.Show(this, initial);
            if (selected is not { } color) return;
            _note.CustomColor = NoteColor.ToHex(color);
            NotesStore.I.Update(_note);
            RebuildVisual();
        }
        catch (Exception ex) { Log($"[ChooseCustomColour EX] {ex}"); }
        finally
        {
            _modalUiOpen = false;
            ReassertOverlay();
            Activate();
            _body.Focus();
        }
    }

    void BuildFindBar()
    {
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _findBar.Height = 28;
        _findBar.Margin = new Thickness(0);
        _findBar.Background = new SolidColorBrush(Pal.Dash) { Opacity = 0.12 };

        _findBox.FontFamily = UiTheme.Font;
        _findBox.FontSize = 11.5;
        _findBox.Margin = new Thickness(14, 3, 0, 3);
        _findBox.Padding = new Thickness(4, 0, 4, 0);
        _findBox.Background = Brushes.Transparent;
        _findBox.BorderThickness = new Thickness(0);
        _findBox.Foreground = Pal.InkB;
        _findBox.CaretBrush = Pal.InkB;
        _findBox.TextChanged += (_, _) => RefreshFindMatches();
        _findBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { FindNext(shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; }
            else if (e.Key == Key.Escape) { ToggleFindBar(); e.Handled = true; }
        };
        Grid.SetColumn(_findBox, 0);
        _findBar.Children.Add(_findBox);

        _findCount.FontSize = 11; _findCount.Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.6 };
        _findCount.VerticalAlignment = VerticalAlignment.Center; _findCount.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(_findCount, 1);
        _findBar.Children.Add(_findCount);

        var up = new TextBlock { Text = "▲", FontSize = 10, Cursor = Cursors.Hand, Foreground = Pal.InkB, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        up.MouseLeftButtonUp += (_, _) => FindNext(forward: false);
        var down = new TextBlock { Text = "▼", FontSize = 10, Cursor = Cursors.Hand, Foreground = Pal.InkB, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        down.MouseLeftButtonUp += (_, _) => FindNext(forward: true);
        var close = new TextBlock { Text = "✕", FontSize = 10, Cursor = Cursors.Hand, Foreground = Pal.InkB, VerticalAlignment = VerticalAlignment.Center };
        close.MouseLeftButtonUp += (_, _) => ToggleFindBar();
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btns.Children.Add(up); btns.Children.Add(down); btns.Children.Add(close);
        Grid.SetColumn(btns, 2);
        _findBar.Children.Add(btns);
        _findBar.Visibility = Visibility.Collapsed;
    }

    // ── body loading / saving ──────────────────────────────
    void LoadBody(string text)
    {
        foreach (var line in text.Split('\n'))
            _body.Document.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))) { Margin = new Thickness(0) });
    }

    string BodyText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (Block b in _body.Document.Blocks)
            if (b is Paragraph p)
                sb.Append(Markdown.SourceText(p)).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    public void Save()
    {
        PersistWindowSize();
        _note.Body = BodyText();
        if (_titleEdited)
        {
            var title = _title.Text.Trim();
            if (title.Length == 0)
            {
                _note.HasCustomTitle = false;
                _note.DeriveTitle();
            }
            else
            {
                _note.Title = title;
                _note.HasCustomTitle = true;
            }
        }
        NotesStore.I.Update(_note);
        UpdateTitle();
        UpdateSavedState();
    }

    void ToggleTextMode()
    {
        Save();
        _note.MarkdownEnabled = !_note.UsesMarkdown;
        _body.Focus();
        ApplyMarkdown();
        NotesStore.I.Update(_note);
        UpdateModeButton();
        UpdateTitle();
        UpdateSavedState();
        _deck.NoteActivity();
    }

    void UpdateModeButton()
    {
        if (_modeBtn.Child is not TextBlock label) return;
        bool markdown = _note.UsesMarkdown;
        label.Text = markdown ? "MD" : "TXT";
        _modeBtn.ToolTip = markdown
            ? Loc.T("Markdown mode. Click for plain text.", "Markdown 模式，点击切换到纯文本。")
            : Loc.T("Plain text mode. Click for Markdown.", "纯文本模式，点击切换到 Markdown。 ");
    }

    bool _applyingMarkdown;

    Paragraph? CurrentEditingParagraph()
        => _body.IsKeyboardFocusWithin
            ? Markdown.ParagraphAt(_body.CaretPosition)
            : null;

    void ApplyMarkdownIfLineChanged()
    {
        if (!ReferenceEquals(CurrentEditingParagraph(), _editingMarkdownParagraph))
            ApplyMarkdown();
    }

    // The caret line stays as plain source; all other lines render in place.
    void ApplyMarkdown()
    {
        if (_applyingMarkdown) return;   // re-entrancy guard: caret/format writes
                                         // can re-fire TextChanged → infinite recursion
        _applyingMarkdown = true;
        bool changeStarted = false;
        try
        {
            if (_body.Document == null) return;
            // One undo unit is enough for a formatting refresh. Without this,
            // every TextRange property assignment can retain another undo item.
            _body.BeginChange();
            changeStarted = true;
            var editingParagraph = CurrentEditingParagraph();
            _editingMarkdownParagraph = editingParagraph;
            var doc = _body.Document;
            if (_note.UsesMarkdown)
                Markdown.StyleDocument(doc, Pal, Settings.NoteFontSize,
                    editingParagraph);
            else
            {
                Markdown.RestoreSourceMarkers(doc);
                Markdown.ResetDocument(doc, Pal.InkB, Settings.NoteFontSize);
            }
        }
        catch (Exception ex) { Log($"[ApplyMarkdown EX] {ex}"); }
        finally
        {
            if (changeStarted) _body.EndChange();
            _applyingMarkdown = false;
        }
    }

    // ── tasks ──────────────────────────────────────────────
    Paragraph? ParagraphAtCaret()
        => Markdown.ParagraphAt(_body.CaretPosition);

    void ToggleTaskAtCaret()
    {
        try
        {
            var p = ParagraphAtCaret();
            if (p == null) return;
            var text = new TextRange(p.ContentStart, p.ContentEnd).Text;
            if (text.Trim().Length == 0) { ReplaceParagraphText(p, "☐ "); return; }
            bool isNativeTask = text.StartsWith(Tasks.Open + " ") || text.StartsWith(Tasks.Done + " ");
            string nl = isNativeTask || (_note.UsesMarkdown && Tasks.IsTask(text))
                ? Tasks.Toggle(text)
                : "☐ " + text;
            ReplaceParagraphText(p, nl);
        }
        catch (Exception ex) { Log($"[ToggleTaskAtCaret EX] {ex}"); }
    }

    void ReplaceParagraphText(Paragraph p, string text)
    {
        var range = new TextRange(p.ContentStart, p.ContentEnd);
        range.Text = text;
        _body.CaretPosition = Markdown.PositionAtTextOffset(p, text.Length);
    }

    void OnBodyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Markdown.TryOpenLink(_body, e, _note.UsesMarkdown)) return;
        if (Markdown.TryToggleTaskAtPoint(_body, e, _note.UsesMarkdown))
            _deck.NoteActivity();
    }

    // ── find (Ctrl+F) ──────────────────────────────────────
    void ToggleFindBar()
    {
        try
        {
            bool show = _findBar.Visibility != Visibility.Visible;
            _findBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) { _findBox.Focus(); _findBox.SelectAll(); }
            else _body.Focus();
        }
        catch (Exception ex) { Log($"[ToggleFindBar EX] {ex}"); }
    }

    void RefreshFindMatches()
    {
        try
        {
            _findMatches.Clear();
            _findIndex = -1;
            string q = _findBox.Text;
            if (q.Length == 0) { _findCount.Text = ""; return; }
            foreach (Block b in _body.Document.Blocks)
            {
                if (b is not Paragraph p) continue;
                var t = Markdown.SourceText(p);
                int i = 0;
                while ((i = t.IndexOf(q, i, StringComparison.CurrentCultureIgnoreCase)) >= 0)
                {
                    _findMatches.Add((p, i, q));
                    i += q.Length;
                }
            }
            UpdateFindCount();
        }
        catch (Exception ex) { Log($"[RefreshFindMatches EX] {ex}"); }
    }

    void UpdateFindCount()
    {
        _findCount.Text = _findMatches.Count == 0 ? "0/0"
            : $"{Math.Max(0, _findIndex) + 1}/{_findMatches.Count}";
    }

    void FindNext(bool forward = true, bool shift = false)
    {
        if (shift) forward = false;
        if (_findMatches.Count == 0 || _findBox.Text.Length == 0) return;
        _findIndex = forward
            ? (_findIndex + 1) % _findMatches.Count
            : (_findIndex <= 0 ? _findMatches.Count - 1 : _findIndex - 1);
        var (p, offset, q) = _findMatches[_findIndex];
                var start = Markdown.PositionAtTextOffset(p, offset);
                var end = Markdown.PositionAtTextOffset(p, offset + q.Length);
        if (start == null || end == null) return;
        _body.Selection.Select(start, end);
        _body.ScrollToVerticalOffset(0);   // keep it simple; Selection scrolls into view on focus
        _findBox.Focus();
        UpdateFindCount();
    }

    // ── keyboard ─────────────────────────────────────────
    // All custom shortcuts are disabled on request — the only key handled
    // is Esc (close), since the sheet has no close button.
    void OnKey(object sender, KeyEventArgs e)
    {
        _deck.NoteActivity();
        if (e.Key == Key.Return && Markdown.HandleTaskReturn(_body, _note.UsesMarkdown))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_findBar.Visibility == Visibility.Visible) { ToggleFindBar(); e.Handled = true; return; }
            SaveAndClose(); e.Handled = true;
        }
    }

    // ── lifecycle ──────────────────────────────────────────
    void OnDeactivated()
    {
        if (_closing || _note.Pinned || _modalUiOpen || IsActive)
        {
            StopDeactivationCheck();
            return;
        }
        // A real outside click can occur during the activation-settling window.
        // Delay the decision, rather than discarding that deactivation forever.
        double age = (DateTime.Now - _createdAt).TotalMilliseconds;
        if (age < 600)
        {
            ScheduleDeactivationCheck(Math.Max(40, 610 - age));
            return;
        }

        // The deck is the one legitimate non-note foreground window: its tab
        // click is allowed to finish and explicitly switch notes. Foreground
        // ownership can remain transiently on the deck, so keep checking until
        // Windows reports the settled target instead of dropping the event.
        var fg = Native.GetForegroundWindow();
        var noteHandle = new WindowInteropHelper(this).Handle;
        var deckHandle = new WindowInteropHelper(_deck).Handle;
        if (fg == IntPtr.Zero || fg == noteHandle || fg == deckHandle)
        {
            ScheduleDeactivationCheck(140);
            return;
        }
        Dismiss();
    }

    void ScheduleDeactivationCheck(double delayMilliseconds)
    {
        StopDeactivationCheck();
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(delayMilliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_deactivationCheck == timer) _deactivationCheck = null;
            OnDeactivated();
        };
        _deactivationCheck = timer;
        timer.Start();
    }

    void StopDeactivationCheck()
    {
        _deactivationCheck?.Stop();
        _deactivationCheck = null;
    }

    void ReleaseWindowResources()
    {
        _autosave.Stop();
        StopDeactivationCheck();
        _transition?.Stop();
        _transition = null;
        _findMatches.Clear();
        if (_windowSource != null)
        {
            _windowSource.RemoveHook(WindowHook);
            _windowSource = null;
        }
        Content = null;
    }

    public void Dismiss()                       // click-away / idle → the whole deck goes to sleep
    {
        if (_closing) return;
        Save();
        _closing = true;
        StopDeactivationCheck();
        AnimateOut(_deck.DismissAll);
    }

    /// <summary>Save and close synchronously before another app window opens.</summary>
    public void CloseImmediately()
    {
        if (!_closing) Save();
        _closing = true;
        StopDeactivationCheck();
        _transition?.Stop();
        IsHitTestVisible = false;
        if (IsVisible) Close();
        _deck.NoteClosed(this);
    }

    public void SaveAndClose()                  // Esc / Close → back to the deck
    {
        if (_closing) return;
        Save();
        _closing = true;
        StopDeactivationCheck();
        AnimateOut(() => _deck.NoteClosed(this));
    }

    void TogglePin()
    {
        try
        {
            _note.Pinned = !_note.Pinned;
            ApplyPinState();
            ReassertOverlay();
            NotesStore.I.Save();
        }
        catch (Exception ex) { Log($"[TogglePin EX] {ex}"); }
    }

    void ApplyPinState()
    {
        _pinBtn.Background = Brushes.Transparent;
        if (_pinBtn.Child is TextBlock t)
        {
            t.Text = "\uE718";
            t.FontFamily = UiTheme.Symbols;
            t.Foreground = new SolidColorBrush(Pal.Ink) { Opacity = _note.Pinned ? 0.85 : 0.40 };
            t.RenderTransformOrigin = new Point(0.5, 0.5);
            t.RenderTransform = _note.Pinned ? null : new RotateTransform(32);
            t.FontWeight = FontWeights.SemiBold;
        }
    }

    void DeleteNote()
    {
        Save();
        var doomed = _note;
        NotesStore.I.Delete(_note.Id);
        _closing = true;
        StopDeactivationCheck();
        _transition?.Stop();
        Close();
        _deck.NoteClosed(this);
        new UndoToast(doomed);
    }

    /// <summary>⇧⌘A: file the note away — it leaves the deck and can be
    /// restored from the All Notes window.</summary>
    void ArchiveNote()
    {
        Save();
        var doomed = _note;
        NotesStore.I.Archive(_note.Id);
        _closing = true;
        StopDeactivationCheck();
        _transition?.Stop();
        Close();
        _deck.NoteClosed(this);
        new UndoToast(doomed, archived: true);
    }

    void RebuildVisual()                        // re-apply the palette across the whole note
    {
        var sheet = (Border)Content;
        sheet.Background = PaperBrush();
        sheet.BorderBrush = new SolidColorBrush(Pal.Ink) { Opacity = 0.07 };
        if (_gutter != null) _gutter.Background = GutterBrush();
        _title.Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.92 };
        _saved.Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.42 };
        _body.Foreground = Pal.InkB;
        _body.CaretBrush = Pal.InkB;
        _findBar.Background = new SolidColorBrush(Pal.Dash) { Opacity = 0.12 };
        _findBox.Foreground = Pal.InkB;
        _findBox.CaretBrush = Pal.InkB;
        _findCount.Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.45 };
        if (_gutterTitle != null) _gutterTitle.Foreground = new SolidColorBrush(Pal.Ink) { Opacity = 0.70 };
        if (_rule != null) _rule.Stroke = new SolidColorBrush(Pal.Ink) { Opacity = 0.18 };
        if (_resizeGrip != null) _resizeGrip.Stroke = new SolidColorBrush(Pal.Ink) { Opacity = 0.24 };
        ApplyPinState();
        foreach (var b in _headBtns)
        {
            if (b.Child is TextBlock t && b != _pinBtn)
                t.Foreground = new SolidColorBrush(Pal.Ink) { Opacity = b.Height == 20 ? 0.72 : 0.50 };
            if (b.Height == 20 && !b.IsMouseOver)
                b.Background = new SolidColorBrush(Pal.Ink) { Opacity = 0.08 };
        }
        for (int i = 0; i < _dots.Count; i++)
            _dots[i].Stroke = !_note.HasCustomColor && i == _note.Color
                ? new SolidColorBrush(Pal.Ink) { Opacity = 0.55 }
                : null;
        if (_customColourButton != null)
            _customColourButton.BorderBrush = _note.HasCustomColor
                ? new SolidColorBrush(Pal.Ink) { Opacity = 0.55 }
                : Brushes.Transparent;
        ApplyMarkdown();
    }

    /// <summary>Live-apply settings to an open note.</summary>
    public void ApplySettings()
    {
        _body.FontSize = Settings.NoteFontSize;
        Opacity = ConfiguredOpacity;
        RebuildVisual();
    }

    /// <summary>Apply the user-selected window layer to an open note.</summary>
    public void ApplyOverlay()
    {
        bool enabled = Settings.OverlayFullscreen;
        Topmost = enabled;
        Native.SetTopmost(this, enabled);
    }

    void ReassertOverlay()
    {
        if (Settings.OverlayFullscreen)
            Native.EnsureTopmost(this);
    }
}
