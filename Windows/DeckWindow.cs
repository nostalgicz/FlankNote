using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Globalization;

namespace FlankNote;

enum DeckState { Rest, Fan }

/// <summary>
///  The deck: a thin pill of colour dashes at the screen edge that fans out
///  into shingled tabs when the pointer touches the edge.  Port of DeckPanel /
///  DeckController / DeckViews from the original.
/// </summary>
class DeckWindow : Window
{
    static double Scale => Math.Clamp(Settings.DeckScale, 0.7, 1.8);
    static double RestWidth => 27 * Scale;
    static double FanWidth => 86 * Scale;
    static double TabFontSize => 12.5 * Scale;
    static readonly FontFamily TabFamily = new("Segoe UI, Microsoft YaHei UI");
    static readonly Typeface TabFace = new(TabFamily, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

    // drag-to-reorder state
    Note? _dragNote;
    bool _dragging;
    readonly List<(FrameworkElement El, Note Note, double BaseY)> _dragSlots = [];

    DeckState _state = DeckState.Rest;
    readonly Canvas _cv = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(80) };
    DateTime _lastActivity = DateTime.Now;
    DateTime _lastNoteActivity = DateTime.Now;
    Point _lastCursor;
    bool _noteOpen;
    bool _showAll;   // the "+N" tab expands the deck to every note

    readonly List<FrameworkElement> _tabs = [];      // staged (opacity 0 → staggered reveal)
    readonly List<Rect> _tabRects = [];              // for per-pixel hit testing
    Border? _plus;
    FrameworkElement? _more;
    Rect _plusRect;
    DispatcherTimer? _revealAnim;
    readonly Dictionary<ScaleTransform, DispatcherTimer> _feedbackAnimations = [];
    Popup? _preview;
    DispatcherTimer? _previewTimer;

    public DeckWindow()
    {
        Title = AppIdentity.DisplayName;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextHintingMode(this, TextHintingMode.Fixed);
        // ClearType cannot be relied on in an AllowsTransparency window. Grayscale
        // avoids coloured fringes after the 90-degree transform and stays sharper
        // for CJK glyphs when combined with pixel-aligned placement.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        Content = _cv;

        SourceInitialized += (_, _) =>
        {
            Native.NoActivate(this);
            Native.EnsureTopmost(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!.AddHook(HitTest);
        };
        MouseEnter += (_, _) => { _lastActivity = DateTime.Now; SetState(DeckState.Fan); };

        _poll.Tick += (_, _) => Poll();
        _poll.Start();

        NotesStore.I.Changed += Refresh;
        Loaded += (_, _) =>
        {
            _lastCursor = CursorDip();
            if (Settings.KeepDeckOpen)
            {
                _state = DeckState.Fan;
                LayoutFan();
                BuildFan(staged: false);
            }
            else
            {
                LayoutRest();
                BuildPill();
            }
        };
    }

    // ── geometry ────────────────────────────────────────────
    Rect Work => DisplayService.WorkArea();
    bool OnRight => !Settings.EdgeLeft;

    Point CursorDip()
    {
        Native.GetCursorPos(out var p);
        var tr = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return tr?.Transform(new Point(p.X, p.Y)) ?? new Point(p.X, p.Y);
    }

    void LayoutRest()
    {
        var w = Work;
        double h = Geom.PillHeight(NotesStore.I.Active.Count());
        Width = RestWidth; Height = h;
        Left = OnRight ? w.Right - RestWidth : w.Left;
        Top = w.Top + (w.Height - h) / 2;
    }

    void LayoutFan()
    {
        var w = Work;
        double bleed = 6 * Scale;
        Width = FanWidth + bleed;
        Height = w.Height;
        Left = OnRight ? w.Right - FanWidth : w.Left;
        Top = w.Top;
    }

    // ── state machine ───────────────────────────────────────
    void SetState(DeckState s)
    {
        if (s == DeckState.Rest && Settings.KeepDeckOpen) s = DeckState.Fan;
        if (_state == s) return;
        _state = s;
        _geomAnim?.Stop();
        _revealAnim?.Stop();
        if (s == DeckState.Fan)
        {
            // the pill grows in place into the full-height deck — the window
            // geometry animates from the pill's rect so nothing "flies away"
            _cv.Children.Clear();
            var w = Work;
            double bleed = 6 * Scale;
            AnimateGeom(OnRight ? w.Right - FanWidth : w.Left, w.Top, FanWidth + bleed, w.Height, 150,
                () => BuildFan(staged: true));
        }
        else
        {
            CollapseToRest();
        }
    }

    DispatcherTimer? _geomAnim;

    /// <summary>Timer-driven window-geometry interpolation (layered windows don't
    /// animate reliably with BeginAnimation).  Direct property writes, cancel-safe.</summary>
    void AnimateGeom(double l, double t, double w, double h, int ms, Action? done = null)
    {
        _geomAnim?.Stop();
        var from = new Rect(Left, Top, Width, Height);
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double progress = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / ms);
            double k = 1 - Math.Pow(1 - progress, 3);   // cubic ease-out, independent of dropped frames
            Left = from.Left + (l - from.Left) * k;
            Top = from.Top + (t - from.Top) * k;
            Width = from.Width + (w - from.Width) * k;
            Height = from.Height + (h - from.Height) * k;
            if (progress >= 1)
            {
                timer.Stop();
                _geomAnim = null;
                done?.Invoke();
            }
        };
        _geomAnim = timer;
        timer.Start();
    }

    void AnimateFeedback(ScaleTransform scale, DropShadowEffect shadow,
                         double targetScale, double targetBlur, double targetOpacity, int ms)
    {
        if (_feedbackAnimations.Remove(scale, out var previous)) previous.Stop();
        double fromScale = scale.ScaleX;
        double fromBlur = shadow.BlurRadius;
        double fromOpacity = shadow.Opacity;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / ms);
            double eased = UiTheme.EaseOut(p);
            double value = fromScale + (targetScale - fromScale) * eased;
            scale.ScaleX = scale.ScaleY = value;
            shadow.BlurRadius = fromBlur + (targetBlur - fromBlur) * eased;
            shadow.Opacity = fromOpacity + (targetOpacity - fromOpacity) * eased;
            if (p >= 1)
            {
                timer.Stop();
                if (_feedbackAnimations.TryGetValue(scale, out var active) && active == timer)
                    _feedbackAnimations.Remove(scale);
            }
        };
        _feedbackAnimations[scale] = timer;
        timer.Start();
    }

    /// <summary>Fade the fan away, then smoothly shrink its host window back to
    /// the resting pill. Keep the large host alive until the exit transition is
    /// finished, matching the upstream deck's delayed panel shrink.</summary>
    void CollapseToRest()
    {
        _showAll = false;   // the original resets the expanded deck on rest
        _revealAnim?.Stop();
        var items = _tabs.ToList();
        if (_more != null) items.Add(_more);
        if (_plus != null) items.Add(_plus);

        void ShrinkHost()
        {
            var work = Work;
            double h = Geom.PillHeight(NotesStore.I.Active.Count());
            double left = OnRight ? work.Right - RestWidth : work.Left;
            double top = work.Top + (work.Height - h) / 2;
            AnimateGeom(left, top, RestWidth, h, 110, BuildPill);
        }

        if (items.Count == 0) { ShrinkHost(); return; }

        var starts = items.Select(Canvas.GetLeft).ToArray();
        var startOpacity = items.Select(item => item.Opacity).ToArray();
        double direction = OnRight ? 1 : -1;
        var started = DateTime.UtcNow;
        const double staggerMs = 22;
        const double exitMs = 145;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            for (int i = 0; i < items.Count; i++)
            {
                double p = Math.Clamp((elapsed - i * staggerMs) / exitMs, 0, 1);
                double eased = p * p * (3 - 2 * p); // smoothstep: no abrupt velocity change
                items[i].Opacity = startOpacity[i] * (1 - eased);
                Canvas.SetLeft(items[i], starts[i] + direction * 34 * Scale * eased);
            }
            if (elapsed >= (items.Count - 1) * staggerMs + exitMs)
            {
                timer.Stop();
                if (_revealAnim == timer) _revealAnim = null;
                ShrinkHost();
            }
        };
        _revealAnim = timer;
        timer.Start();
    }

    /// <summary>Wake/rest decisions, polled so the deck works even over click-through pixels.</summary>
    void Poll()
    {
        var cur = CursorDip();
        if (cur != _lastCursor) { _lastCursor = cur; _lastActivity = DateTime.Now; }

        if (Settings.KeepDeckOpen && _state == DeckState.Rest)
        {
            SetState(DeckState.Fan);
            return;
        }

        var w = Work;
        // wake strip: user setting, but never narrower than the deck itself needs
        double wake = Math.Max(Settings.WakeDistance, (_state == DeckState.Rest ? RestWidth : FanWidth) + 16 * Scale);
        var hot = new Rect(OnRight ? w.Right - wake : w.Left, w.Top, wake, w.Height);
        bool inHot = hot.Contains(cur);

        if (_state == DeckState.Rest)
        {
            if (inHot) SetState(DeckState.Fan);                       // pointer touches the edge → fan out
        }
        else if (!_noteOpen && !Settings.KeepDeckOpen)
        {
            var idle = DateTime.Now - _lastActivity;
            // inside the fanned window (on the tabs or their whitespace) the deck
            // stays open no matter how long the pointer rests — idle-folding there
            // would collapse to the pill, re-expand, and flicker forever
            bool inFanWindow = OnRight ? cur.X >= w.Right - FanWidth : cur.X <= w.Left + FanWidth;
            if (inHot)
            {
                // idle-fold only when parked in the hot strip but outside the fan window
                if (!inFanWindow && idle.TotalSeconds > 4) SetState(DeckState.Rest);
            }
            else if (idle.TotalMilliseconds > 150) SetState(DeckState.Rest);      // pointer left → sleep
        }

        // An open, unpinned note tidies itself away after a minute without
        // interaction with that note. Global pointer movement must not extend it.
        if (_noteOpen && App.OpenNote is { } nw && !nw.Pinned &&
            (DateTime.Now - _lastNoteActivity).TotalSeconds > 60)
            nw.Dismiss();
    }

    public void ApplyOverlay()                  // Settings.OverlayFullscreen → always-on-top behaviour
    {
        // The deck must always remain above ordinary application windows.
        // Windows has no direct equivalent of macOS' separate floating/statusBar
        // levels, so the full-screen preference must not demote it to a normal window.
        Topmost = true;
        Native.EnsureTopmost(this);
    }

    // ── content: pill ───────────────────────────────────────
    void BuildPill()
    {
        _cv.Children.Clear();
        _tabs.Clear(); _tabRects.Clear(); _plus = null; _more = null;
        _dragSlots.Clear();

        var notes = NotesStore.I.Active.ToList();
        var inner = new StackPanel { Orientation = Orientation.Vertical };
        void dash(Brush b, bool last)
        {
            // margin only below non-final dashes: total content = PillHeight exactly
            inner.Children.Add(new Border
            {
                Width = Geom.DashWidth, Height = Geom.DashHeight,
                CornerRadius = new CornerRadius(2.5 * Scale), Background = b,
                Margin = new Thickness(0, 0, 0, last ? 0 : Geom.DashGap),
            });
        }
        if (notes.Count == 0) dash(Brushes.Gray, last: true);
        for (int i = 0; i < Math.Min(notes.Count, Geom.MaxDashes); i++) dash(notes[i].Palette.DashB, last: i == Geom.MaxDashes - 1 || i == notes.Count - 1);
        if (notes.Count > Geom.MaxDashes) dash(Brushes.DarkGray, last: true);

        var pill = new Border
        {
            Width = Geom.PillWidth,
            Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            CornerRadius = new CornerRadius(6 * Scale),
            Padding = new Thickness(0, Geom.PillPad, 0, Geom.PillPad),
            Child = inner,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.22, Direction = OnRight ? 180 : 0 },
        };
        // right-click menu moved to the tray icon (per request) — the deck has none
        Canvas.SetLeft(pill, OnRight ? RestWidth - Geom.PillWidth - Scale : Scale);
        Canvas.SetTop(pill, 0);
        _cv.Children.Add(pill);
    }

    // ── content: fan ────────────────────────────────────────
    void BuildFan(bool staged)
    {
        _cv.Children.Clear();
        _revealAnim?.Stop();
        _tabs.Clear(); _tabRects.Clear(); _plus = null; _more = null;
        _dragSlots.Clear();

        // > fanLimit notes fold up into 5 tabs + a "+N" tab (tap to show every note)
        const int fanLimit = 5;                 // Settings.fanLimit in the original
        var all = NotesStore.I.Active.ToList();
        var visible = _showAll ? all : all.Take(fanLimit).ToList();
        int hiddenCount = _showAll ? 0 : Math.Max(0, all.Count - fanLimit);
        var lay = Geom.Fan(Work.Height, Math.Max(1, visible.Count), LongestLabel(visible), hiddenCount > 0);
        // bleed: the tabs hang a little past the screen edge so the 3° lean
        // never exposes a gap at the right/left edge (the original's bleed)
        double bleed = 6 * Scale;
        double x = OnRight ? FanWidth + bleed - Geom.TabWidth : -bleed;

        if (Settings.DeckStyle == "chips")
        {
            BuildChips(visible, hiddenCount, x);
            if (staged) RevealFan();
            return;
        }

        if (visible.Count == 0)
        {
            MakeTab(null, lay, x, lay.FanTop, "＋");        // empty deck: one tab that makes a note
        }
        else
        {
            for (int i = 0; i < visible.Count; i++)
            {
                var n = visible[i];
                var tab = MakeTab(n, lay, x, lay.FanTop + i * lay.Pitch, null);
                _dragSlots.Add((tab, n, lay.FanTop + i * lay.Pitch));
                HookDrag(tab, n, lay.FanTop + i * lay.Pitch, lay.Pitch, _dragSlots, lay.FanTop);
            }
            if (hiddenCount > 0)
            {
                // the "+N" tab: one tap shows every note (original moreTabHeight)
                var more = new Border
                {
                    Width = Geom.TabWidth, Height = Geom.MoreTabHeight,
                    Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x2A, 0x2A, 0x2A)),
                    CornerRadius = OnRight ? new CornerRadius(11 * Scale, 0, 0, 11 * Scale) : new CornerRadius(0, 11 * Scale, 11 * Scale, 0),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = $"+{hiddenCount}", FontSize = 12 * Scale, FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                more.MouseLeftButtonUp += (_, _) => { _showAll = true; Refresh(); };
                double my = lay.FanTop + (visible.Count - 1) * lay.Pitch + lay.ItemHeight + Geom.TabGap;
                Canvas.SetLeft(more, x); Canvas.SetTop(more, my);
                _cv.Children.Add(more);
                _more = more;
            }
        }

        _plus = new Border
        {
            Width = Geom.PlusSize, Height = Geom.PlusSize,
            CornerRadius = new CornerRadius(7 * Scale),
            Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "＋", Foreground = Brushes.White, FontSize = 15 * Scale, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -Scale, 0, 0) },
        };
        _plus.MouseLeftButtonUp += (_, _) => App.CreateNote(open: true);
        // centred on the tab column, just below the last visible element
        // (the "+N" tab when folded, else the last tab) — never overlapping it
        double px = x + (Geom.TabWidth - Geom.PlusSize) / 2;
        double py;
        if (hiddenCount > 0)
            py = lay.FanTop + (visible.Count - 1) * lay.Pitch + lay.ItemHeight + Geom.TabGap + Geom.MoreTabHeight + Geom.PlusGap;  // below the "+N" tab
        else
            py = lay.FanTop + (visible.Count == 0 ? 0 : (visible.Count - 1) * lay.Pitch) + lay.ItemHeight + Geom.PlusGap;
        py = Math.Min(py, Work.Height - Geom.PlusSize - 8);   // never run off-screen
        Canvas.SetLeft(_plus, px); Canvas.SetTop(_plus, py);
        _cv.Children.Add(_plus);
        _plusRect = new Rect(px, py, Geom.PlusSize, Geom.PlusSize);
        if (staged) RevealFan();
    }

    // ── colour chips deck (the original's compact style) ────
    // Pure colour blocks, no labels: 30×24 chips with the note's dash colour,
    // stacked with 6px gaps, centred on the screen. Tap to open the note.
    void BuildChips(List<Note> visible, int hiddenCount, double x)
    {
        double chipH = 27 * Scale, chipGap = 7 * Scale, moreH = 25 * Scale;
        int n = Math.Max(1, visible.Count);
        double stackH = (n - 1) * (chipH + chipGap) + chipH
            + (hiddenCount > 0 ? moreH + chipGap : 0) + Geom.PlusGap + Geom.PlusSize;
        double top = Math.Max(12 * Scale, (Work.Height - stackH) / 2);

        for (int i = 0; i < visible.Count; i++)
        {
            var note = visible[i];
            double cy = top + i * (chipH + chipGap);
            var chipShadow = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = 0.22, Direction = OnRight ? 180 : 0 };
            var chip = new Border
            {
                Width = Geom.TabWidth, Height = chipH,
                Background = note.Palette.DashB,
                CornerRadius = new CornerRadius(7 * Scale),
                Cursor = Cursors.Hand,
                Effect = chipShadow,
            };
            var scale = new ScaleTransform(1, 1);
            chip.RenderTransformOrigin = new Point(OnRight ? 1 : 0, 0.5);
            chip.RenderTransform = scale;
            chip.MouseEnter += (_, _) => AnimateFeedback(scale, chipShadow, 1.06, 7, 0.30, 140);
            chip.MouseLeave += (_, _) => AnimateFeedback(scale, chipShadow, 1, 5, 0.22, 140);
            chip.PreviewMouseLeftButtonDown += (_, _) =>
                AnimateFeedback(scale, chipShadow, 0.97, chipShadow.BlurRadius, chipShadow.Opacity, 120);
            chip.PreviewMouseLeftButtonUp += (_, _) =>
                AnimateFeedback(scale, chipShadow, chip.IsMouseOver ? 1.06 : 1,
                    chip.IsMouseOver ? 7 : 5, chip.IsMouseOver ? 0.30 : 0.22, 120);
            chip.LostMouseCapture += (_, _) =>
                AnimateFeedback(scale, chipShadow, chip.IsMouseOver ? 1.06 : 1,
                    chip.IsMouseOver ? 7 : 5, chip.IsMouseOver ? 0.30 : 0.22, 120);
            var doomed = note;
            HookHoverOpen(chip, doomed);
            _dragSlots.Add((chip, doomed, cy));
            HookDrag(chip, doomed, cy, chipH + chipGap, _dragSlots, top);
            Canvas.SetLeft(chip, x); Canvas.SetTop(chip, cy);
            _cv.Children.Add(chip);
            _tabs.Add(chip);
            _tabRects.Add(new Rect(x, cy, Geom.TabWidth, chipH));
        }

        if (hiddenCount > 0)
        {
            var more = new Border
            {
                Width = Geom.TabWidth, Height = moreH,
                Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x2A, 0x2A, 0x2A)),
                CornerRadius = new CornerRadius(7 * Scale),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = $"+{hiddenCount}", FontSize = 11 * Scale, FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            more.MouseLeftButtonUp += (_, _) => { _showAll = true; Refresh(); };
            // Leave the same 7 px rhythm below the final colour chip. The old
            // formula placed +N exactly on the previous chip's bottom edge.
            double my = top + n * (chipH + chipGap);
            Canvas.SetLeft(more, x); Canvas.SetTop(more, my);
            _cv.Children.Add(more);
            _more = more;
        }

        double px = x + (Geom.TabWidth - Geom.PlusSize) / 2;
        double py = top + stackH - Geom.PlusSize;
        var plus = new Border
        {
            Width = Geom.PlusSize, Height = Geom.PlusSize,
            Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x2A, 0x2A, 0x2A)),
            CornerRadius = new CornerRadius(14 * Scale),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "＋", FontSize = 15 * Scale, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        plus.MouseLeftButtonUp += (_, _) => App.CreateNote(open: true);
        Canvas.SetLeft(plus, px); Canvas.SetTop(plus, py);
        _cv.Children.Add(plus);
        _plus = plus;
        _plusRect = new Rect(px, py, Geom.PlusSize, Geom.PlusSize);
    }

    /// <summary>Reveal tabs in a short slide-and-fade cascade. Animation is
    /// time-based, so a busy UI thread skips ahead instead of slowing down.</summary>
    void RevealFan()
    {
        var items = _tabs.ToList();
        if (_more != null) items.Add(_more);
        if (_plus != null) items.Add(_plus);
        if (items.Count == 0) return;

        var goals = items.Select(Canvas.GetLeft).ToArray();
        double direction = OnRight ? 1 : -1;
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Opacity = 0;
            Canvas.SetLeft(items[i], goals[i] + direction * 42 * Scale);
        }
        var started = DateTime.UtcNow;
        const double staggerMs = 40;
        const double revealMs = 180;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            for (int i = 0; i < items.Count; i++)
            {
                double p = Math.Clamp((elapsed - i * staggerMs) / revealMs, 0, 1);
                double eased = 1 - Math.Pow(1 - p, 3);
                items[i].Opacity = Math.Min(1, p * 1.35);
                Canvas.SetLeft(items[i], goals[i] + direction * 42 * Scale * (1 - eased));
            }
            if (elapsed >= (items.Count - 1) * staggerMs + revealMs)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    items[i].Opacity = 1;
                    Canvas.SetLeft(items[i], goals[i]);
                }
                timer.Stop();
                if (_revealAnim == timer) _revealAnim = null;
            }
        };
        _revealAnim = timer;
        timer.Start();
    }

    Border MakeTab(Note? note, Geom.FanLayout lay, double x, double y, string? labelOverride)
    {
        var pal = note?.Palette;
        var paper = pal?.PaperB ?? new SolidColorBrush(Color.FromArgb(0xE0, 0x2A, 0x2A, 0x2A));
        var ink = pal?.InkB ?? Brushes.White;
        var label = labelOverride ?? note!.DisplayTitle.ToUpperInvariant();

        var shadow = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.24, Direction = OnRight ? 180 : 0 };
        var tab = new Border
        {
            Width = Geom.TabWidth, Height = lay.ItemHeight,
            Background = paper,
            // rounded on the screen-centre side; the edge side stays square
            // (the original's edgeTabShape: "docked to the edge")
            CornerRadius = OnRight ? new CornerRadius(11 * Scale, 0, 0, 11 * Scale) : new CornerRadius(0, 11 * Scale, 11 * Scale, 0),
            Effect = shadow,
            Cursor = Cursors.Hand,
        };

        // the original's 3° lean, pivoting on the screen-edge side, plus the
        // hover lift and press squash (all direct transforms — no animation clocks)
        var lean = new RotateTransform(OnRight ? -3 : 3);
        var scale = new ScaleTransform(1, 1);
        tab.RenderTransformOrigin = new Point(OnRight ? 1 : 0, 0.5);
        tab.RenderTransform = new TransformGroup { Children = { lean, scale } };
        tab.MouseEnter += (_, _) =>
        {
            AnimateFeedback(scale, shadow, 1.04, 9, 0.32, 140);
        };
        tab.MouseLeave += (_, _) =>
        {
            AnimateFeedback(scale, shadow, 1, 6, 0.24, 140);
        };
        tab.PreviewMouseLeftButtonDown += (_, _) =>
            AnimateFeedback(scale, shadow, 0.97, shadow.BlurRadius, shadow.Opacity, 120);
        tab.PreviewMouseLeftButtonUp += (_, _) =>
            AnimateFeedback(scale, shadow, tab.IsMouseOver ? 1.04 : 1,
                tab.IsMouseOver ? 9 : 6, tab.IsMouseOver ? 0.32 : 0.24, 120);
        tab.LostMouseCapture += (_, _) =>
            AnimateFeedback(scale, shadow, tab.IsMouseOver ? 1.04 : 1,
                tab.IsMouseOver ? 9 : 6, tab.IsMouseOver ? 0.32 : 0.24, 120);
        if (note != null) HookHoverOpen(tab, note);
        if (note != null) HookPreview(tab, note);

        // The uncovered strip at the top carries the label, turned on its side.
        // Drawn on a Canvas so nothing constrains or clips the rotated text;
        // the original uses a fixed tab font — long titles ellipsise, never shrink
        var fs = TabFontSize;
        double labelAvail = lay.Pitch - Geom.LabelInset;
        var tb = new TextBlock
        {
            Text = label,
            FontFamily = TabFamily,
            FontSize = fs,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(ink.Color) { Opacity = 0.82 },
            MaxWidth = labelAvail,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            // LayoutTransform lets WPF arrange the *rotated* glyph bounds. This is
            // essential for short labels and CJK fallback fonts: estimating the
            // unrotated width separately makes their visual centre drift sideways.
            LayoutTransform = new RotateTransform(OnRight ? -90 : 90),
        };
        var labelHost = new Grid
        {
            Width = Geom.TabWidth,
            Height = lay.Pitch,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };
        labelHost.Children.Add(tb);
        if (note?.Pinned == true)
        {
            labelHost.Children.Add(new Ellipse
            {
                Width = 5 * Scale,
                Height = 5 * Scale,
                Fill = pal!.Value.DashB,
                IsHitTestVisible = false,
                HorizontalAlignment = OnRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = OnRight ? new Thickness(0, 7, 9, 0) : new Thickness(9, 7, 0, 0),
            });
        }
        tab.Child = labelHost;

        Canvas.SetLeft(tab, x); Canvas.SetTop(tab, y);
        _cv.Children.Add(tab);
        _tabs.Add(tab);
        _tabRects.Add(new Rect(x, y, Geom.TabWidth, lay.ItemHeight));
        return tab;
    }

    void HookPreview(FrameworkElement element, Note note)
    {
        element.MouseEnter += (_, _) =>
        {
            if (Settings.OpenOnHover) return;
            _previewTimer?.Stop();
            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
            _previewTimer.Tick += (_, _) =>
            {
                _previewTimer?.Stop();
                if (element.IsMouseOver && !_dragging) ShowPreview(element, note);
            };
            _previewTimer.Start();
        };
        element.MouseLeave += (_, _) => { _previewTimer?.Stop(); HidePreview(); };
        element.Unloaded += (_, _) => { _previewTimer?.Stop(); HidePreview(); };
    }

    void ShowPreview(FrameworkElement target, Note note)
    {
        HidePreview();
        var progress = Tasks.Progress(note.Body);
        var text = note.Body.Replace("\r", "").Replace("\n", " ").Trim();
        if (text.Length > 150) text = text[..150] + "…";
        var pal = note.Palette;
        var paper = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(236, pal.Paper.R, pal.Paper.G, pal.Paper.B), 0),
                new(Color.FromArgb(214, pal.Paper.R, pal.Paper.G, pal.Paper.B), 1),
            }, 90);
        var content = new StackPanel { Width = 278, Margin = new Thickness(15, 13, 15, 13) };
        var titleRow = new DockPanel();
        titleRow.Children.Add(new TextBlock
        {
            Text = note.DisplayTitle,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = pal.InkB,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(titleRow);
        content.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(pal.Ink) { Opacity = 0.12 }, Margin = new Thickness(0, 10, 0, 9) });
        content.Children.Add(new TextBlock
        {
            Text = text.Length == 0 ? Loc.T("Empty note", "空便签") : text,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 58,
            FontSize = 12.5,
            LineHeight = 19,
            Foreground = new SolidColorBrush(pal.Ink) { Opacity = 0.78 },
        });
        if (progress.Total > 0)
        {
            var progressRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(pal.Ink) { Opacity = 0.14 }, VerticalAlignment = VerticalAlignment.Center };
            var fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = pal.DashB, HorizontalAlignment = HorizontalAlignment.Left, Width = 262 * progress.Done / Math.Max(1, progress.Total) };
            track.Child = fill;
            progressRow.Children.Add(track);
            var count = new TextBlock { Text = $"{Loc.T("Tasks", "任务")} {progress.Done}/{progress.Total}", FontSize = 10.5, Foreground = pal.DashB, Margin = new Thickness(9, -3, 0, 0) };
            Grid.SetColumn(count, 1);
            progressRow.Children.Add(count);
            content.Children.Add(progressRow);
        }
        var edgeStrip = new Border
        {
            Width = 5,
            Background = pal.DashB,
            Margin = new Thickness(0),
            CornerRadius = OnRight
                ? new CornerRadius(8, 0, 0, 8)
                : new CornerRadius(0, 8, 8, 0),
            HorizontalAlignment = OnRight ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
        };
        var cardBody = new Grid { ClipToBounds = true, SnapsToDevicePixels = true, UseLayoutRounding = true };
        cardBody.Children.Add(edgeStrip);
        cardBody.Children.Add(content);
        var card = new Border
        {
            Background = paper,
            BorderBrush = new SolidColorBrush(pal.Dash) { Opacity = 0.55 },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(0),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true,
            Child = cardBody,
        };
        _preview = new Popup
        {
            PlacementTarget = target,
            Placement = OnRight ? PlacementMode.Left : PlacementMode.Right,
            HorizontalOffset = OnRight ? -8 : 8,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.None,
            Focusable = false,
            IsHitTestVisible = false,
            Child = card,
        };
        _preview.IsOpen = true;
    }

    void HidePreview()
    {
        if (_preview != null) { _preview.IsOpen = false; _preview = null; }
    }

    public void OpenAllNotes()
    {
        DismissAll();
        if (App.AllNotes == null) App.AllNotes = new AllNotesWindow(); else App.AllNotes.Activate();
    }

    public void OpenArchive()
    {
        DismissAll();
        if (App.ArchiveWin == null) App.ArchiveWin = new AllNotesWindow(archivedOnly: true); else App.ArchiveWin.Activate();
    }

    double LongestLabel(List<Note> notes)
        => notes.Count == 0 ? 0 : notes.Max(n => Geom.LabelWidth(n.DisplayTitle, TabFace, TabFontSize));

    void HookHoverOpen(FrameworkElement element, Note note)
    {
        DispatcherTimer? dwell = null;
        void Stop()
        {
            dwell?.Stop();
            dwell = null;
        }
        element.MouseEnter += (_, _) =>
        {
            if (!Settings.OpenOnHover || App.OpenNote?.NoteId == note.Id) return;
            Stop();
            dwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
            dwell.Tick += (_, _) =>
            {
                Stop();
                if (Settings.OpenOnHover && element.IsMouseOver && !_dragging &&
                    Mouse.LeftButton != MouseButtonState.Pressed)
                    OpenNote(note);
            };
            dwell.Start();
        };
        element.MouseLeave += (_, _) => Stop();
        element.PreviewMouseLeftButtonDown += (_, _) => Stop();
        element.Unloaded += (_, _) => Stop();
    }

    // ── drag to reorder (the original: long-press 0.28 s, then drag; the dragged
    // tab rides on top, neighbours step aside live, and on release everything
    // springs to the new order) ──
    void HookDrag(FrameworkElement el, Note note, double baseY, double pitch,
                  List<(FrameworkElement El, Note Note, double BaseY)> all, double top0)
    {
        Point down = default;
        DateTime downAt = default;
        bool pressed = false;
        bool completingRelease = false;
        el.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressed = true;
            _dragNote = note;
            down = e.GetPosition(this);
            downAt = DateTime.Now;
            _dragging = false;
            Canvas.SetZIndex(el, 100);           // ride over the other tabs while dragging
            el.CaptureMouse();
        };
        el.PreviewMouseMove += (_, e) =>
        {
            if (!pressed || _dragNote != note) return;
            double dy = e.GetPosition(this).Y - down.Y;
            bool hold = (DateTime.Now - downAt).TotalMilliseconds >= 280;   // the original's long press
            if (hold && Math.Abs(dy) > 4)
            {
                // The fan can rebuild while a tab still owns mouse capture
                // (for example after a store notification). In that case the
                // shared slot list has already been cleared; clamping against
                // Count - 1 would produce an invalid [0, -1] range and crash.
                if (all.Count == 0)
                {
                    pressed = false;
                    _dragging = false;
                    _dragNote = null;
                    el.ReleaseMouseCapture();
                    return;
                }
                _dragging = true;
                double safePitch = Math.Max(1, pitch);
                double dragY = Math.Clamp(baseY + dy, top0, top0 + (all.Count - 1) * safePitch);
                Canvas.SetTop(el, dragY);
                el.Opacity = 0.92;
                // neighbours step aside — a live preview of the new order
                // (the slot list is empty outside a freshly built fan — guard it)
                int from = all.Count > 0 ? all.FindIndex(t => t.El == el) : -1;
                if (from >= 0)
                {
                    int target = Math.Clamp((int)Math.Round((dragY - top0) / safePitch), 0, all.Count - 1);
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (i == from) continue;
                        // Moving down pulls intervening neighbours up; moving up
                        // pushes them down. Every move also resets unaffected slots,
                        // so dragging back over the origin cannot leave a gap behind.
                        int shift = from < target ? (i > from && i <= target ? -1 : 0)
                                                  : (i >= target && i < from ? 1 : 0);
                        Canvas.SetTop(all[i].El, all[i].BaseY + shift * safePitch);
                    }
                }
            }
        };
        el.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            double dy = e.GetPosition(this).Y - down.Y;
            bool hold = (DateTime.Now - downAt).TotalMilliseconds >= 280;
            bool wasDrag = _dragging || (hold && Math.Abs(dy) > 4);   // long-press + travel = drag
            int from = all.FindIndex(t => t.El == el);
            int slots = (int)Math.Round(dy / Math.Max(1, pitch));
            int target = from < 0 ? -1 : Math.Clamp(from + slots, 0, all.Count - 1);

            _dragging = false; _dragNote = null;
            completingRelease = true;
            el.ReleaseMouseCapture();
            completingRelease = false;
            el.Opacity = 1;
            Canvas.SetZIndex(el, 0);

            if (wasDrag)
            {
                if (from < 0 || target == from)
                {
                    // snapped back to its own slot — spring everyone home
                    AnimateTabsTo(all, all.Select(t => t.BaseY).ToList());
                }
                else
                {
                    // where every tab must land in the new order
                    var notes = all.Select(t => t.Note).ToList();
                    var moved = notes[from];
                    notes.RemoveAt(from);
                    notes.Insert(target, moved);
                    var goals = new double[all.Count];
                    for (int i = 0; i < all.Count; i++)
                        goals[i] = top0 + notes.FindIndex(n => n.Id == all[i].Note.Id) * pitch;
                    if (Reorder(note, target - from))            // persist (no deck refresh yet)
                        AnimateTabsTo(all, goals.ToList(), NotesStore.I.NotifyChanged);   // spring, then rebuild
                    else
                        AnimateTabsTo(all, all.Select(t => t.BaseY).ToList());
                }
            }
            else OpenNote(note);                 // a plain quick click still opens the note
        };
        el.LostMouseCapture += (_, _) =>
        {
            if (completingRelease) return;
            pressed = false; _dragNote = null; _dragging = false;
            el.Opacity = 1; Canvas.SetZIndex(el, 0);
            AnimateTabsTo(all, all.Select(t => t.BaseY).ToList());
        };
    }

    /// <summary>Spring every tab from where it is to its goal y (ease-out, timer-driven —
    /// no animation clocks on a layered window), then optionally rebuild.</summary>
    void AnimateTabsTo(List<(FrameworkElement El, Note Note, double BaseY)> all, List<double> goals, Action? done = null, int ms = 320)
    {
        var t0 = DateTime.Now;
        var from = all.Select(t => Canvas.GetTop(t.El)).ToArray();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - t0).TotalMilliseconds / ms);
            double e = UiTheme.Spring(t, 0.80);
            for (int i = 0; i < all.Count; i++)
                Canvas.SetTop(all[i].El, from[i] + (goals[i] - from[i]) * e);
            if (t >= 1) { timer.Stop(); done?.Invoke(); }
        };
        timer.Start();
    }

    bool Reorder(Note note, int slots)
    {
        var notes = NotesStore.I.Active.ToList();
        int from = notes.FindIndex(n => n.Id == note.Id);
        if (from < 0 || slots == 0) return false;
        int target = Math.Clamp(from + slots, 0, notes.Count - 1);
        if (target == from) return false;
        var n = notes[from];
        notes.RemoveAt(from);
        notes.Insert(target, n);
        for (int i = 0; i < notes.Count; i++) notes[i].Order = i;   // list order == Order order
        NotesStore.I.Save();
        // no NotifyChanged here — the caller springs the tabs to the new order
        // first and refreshes when the animation lands
        return true;
    }

    // ── per-pixel hit testing: transparent parts pass clicks through ──
    IntPtr HitTest(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_NCHITTEST) return IntPtr.Zero;
        long v = lParam.ToInt64();
        var client = PointFromScreen(new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF)));

        bool hit;
        if (_state == DeckState.Rest)
            hit = client.X >= 0 && client.X <= RestWidth && client.Y >= 0 && client.Y <= Height;
        else
        {
            hit = _tabRects.Any(r => r.Contains(client));
            if (!hit && _plus != null && _plusRect.Contains(client)) hit = true;
        }
        handled = true;
        return (IntPtr)(hit ? Native.HTCLIENT : Native.HTTRANSPARENT);
    }

    // ── notes ───────────────────────────────────────────────
    public void OpenNote(Note note)
    {
        if (App.OpenNote is { } current)
        {
            if (current.NoteId == note.Id)
            {
                NoteActivity();
                current.Activate();
                return;
            }
            // Pinning prevents automatic dismissal; selecting another tab is
            // an explicit switch and therefore closes the current sheet.
            current.SaveAndClose();
        }
        App.OpenNote = null;
        _noteOpen = true;
        NoteActivity();
        SetState(DeckState.Fan);

        var notes = NotesStore.I.Active.ToList();
        int idx = Math.Max(0, notes.FindIndex(n => n.Id == note.Id));
        var lay = Geom.Fan(Work.Height, Math.Max(1, notes.Count), LongestLabel(notes), false);
        var w = Work;
        double tabCenterY = Top + lay.FanTop + idx * lay.Pitch + lay.ItemHeight / 2;
        // the sheet's edge side runs flush to the screen edge (covers its own tab)
        double left = OnRight ? w.Right - Geom.EditorWidth - 8 : w.Left - 8;
        double top = Math.Clamp(tabCenterY - Geom.EditorHeight / 2, w.Top + 10, w.Bottom - Geom.EditorHeight - 10);

        App.OpenNote = new NoteWindow(note, this, new Point(left, top));
        var nw = App.OpenNote;
        nw.Show();
        nw.Activate();   // best effort — foreground lock may refuse on a no-activate click path
        nw.AnimateIn(left);
    }

    public void NoteClosed(NoteWindow nw)
    {
        if (App.OpenNote == nw)
        {
            App.OpenNote = null;
            _noteOpen = false;
        }
    }

    public void NoteActivity() => _lastNoteActivity = DateTime.Now;

    public void DismissAll()
    {
        App.OpenNote?.CloseImmediately();
        App.OpenNote = null;
        _noteOpen = false;
        SetState(DeckState.Rest);
    }

    public void SwitchEdge(bool left)
    {
        if (Settings.EdgeLeft == left) return;
        _geomAnim?.Stop();
        _geomAnim = null;
        _revealAnim?.Stop();
        _revealAnim = null;
        Settings.EdgeLeft = left;
        App.OpenNote?.SaveAndClose();
        NotesStore.I.Save();
        _noteOpen = false;
        if (Settings.KeepDeckOpen)
        {
            _state = DeckState.Fan;
            LayoutFan();
            BuildFan(staged: false);
        }
        else
        {
            _state = DeckState.Rest;         // force relayout even if already rest
            LayoutRest();
            BuildPill();
        }
    }

    public void Refresh()
    {
        if (_state == DeckState.Rest)
        {
            LayoutRest();
            BuildPill();
        }
        else
        {
            LayoutFan();
            BuildFan(staged: false);
        }
    }

    public void ApplySettings()
    {
        _geomAnim?.Stop();
        _geomAnim = null;
        _revealAnim?.Stop();
        _revealAnim = null;
        if (Settings.KeepDeckOpen || _state == DeckState.Fan || _noteOpen)
        {
            _state = DeckState.Fan;
            LayoutFan();
            BuildFan(staged: false);
        }
        else
        {
            LayoutRest();
            BuildPill();
        }
    }

    // ── tray integration ────────────────────────────────────
    /// <summary>Left-click on the tray icon opens the deck (no focus steal).</summary>
    public void ExpandForTray()
    {
        Show();
        SetState(DeckState.Fan);
    }
}
