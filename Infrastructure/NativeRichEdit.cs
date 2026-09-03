using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlankNote;

/// <summary>
/// Small WPF host for the system RichEdit control. WPF's RichTextBox creates a
/// FlowDocument and a large managed text tree; RichEdit keeps the text tree in
/// the native window and exposes only the active selection to managed code.
/// </summary>
sealed class NativeRichEdit : HwndHost
{
    const int WM_COMMAND = 0x0111;
    const int WM_SIZE = 0x0005;
    const int WM_SETFOCUS = 0x0007;
    const int WM_KILLFOCUS = 0x0008;
    const int EN_CHANGE = 0x0300;
    const int EN_SELCHANGE = 0x0700;
    const int WM_SETTEXT = 0x000C;
    const int EM_SETBKGNDCOLOR = 0x0443;
    const int EM_SETMARGINS = 0x00D3;
    const int EM_SETTEXTMODE = 0x0459;
    const int EM_SETUNDOLIMIT = 0x0452;
    const int EM_EXLIMITTEXT = 0x0435;
    const int EM_EXGETSEL = 0x0434;
    const int EM_EXSETSEL = 0x0437;
    const int EM_SETCHARFORMAT = 0x0444;
    const int EM_SETOPTIONS = 0x044D;
    const int EM_HIDESELECTION = 0x043F;
    const int EM_SETMODIFY = 0x00B9;
    const int EM_GETMODIFY = 0x00B8;
    const int EM_SETREADONLY = 0x00CF;
    const int EM_REPLACESEL = 0x00C2;
    const int EM_SCROLLCARET = 0x00B7;
    const int EM_SETLANGOPTIONS = 0x0478;
    const int EM_SETTEXTEX = 0x0461;
    const int EM_GETTEXTEX = 0x045E;
    const int EM_GETTEXTLENGTHEX = 0x045F;
    const int EM_CHARFROMPOS = 0x00D6;
    const int EM_SETCHARFORMAT_SELECTION = 0x0001;
    const int EM_SETOPTIONS_OR = 0x0002;
    const int ECO_AUTOVSCROLL = 0x0040;
    const int ECO_AUTOHSCROLL = 0x0080;
    const int SES_EXTENDBACKCOLOR = 0x00000002;
    const int ST_UNDO = 0x0001;
    const int ST_KEEPUNDO = 0x0004;
    const int ES_MULTILINE = 0x0004;
    const int ES_AUTOVSCROLL = 0x0040;
    const int ES_WANTRETURN = 0x1000;
    const int ES_NOHIDESEL = 0x0100;
    const int WS_CHILD = 0x40000000;
    const int WS_VISIBLE = 0x10000000;
    const int WS_VSCROLL = 0x00200000;
    const int WS_TABSTOP = 0x00010000;
    const int SWP_NOZORDER = 0x0004;
    const int SWP_NOACTIVATE = 0x0010;
    const int VK_RETURN = 0x0D;
    const int VK_ESCAPE = 0x1B;
    const int WM_KEYDOWN = 0x0100;
    const int WM_LBUTTONDOWN = 0x0201;
    const int MK_CONTROL = 0x0008;
    const int MK_SHIFT = 0x0004;

    static readonly IntPtr InvalidHandle = new(-1);
    IntPtr _handle;
    bool _settingText;
    string _pendingText = string.Empty;
    Color _backgroundColor = Colors.White;
    string? _offsetText;
    SubclassProc? _subclassProc;

    public event EventHandler? TextChanged;
    public event EventHandler? SelectionChanged;
    public event Func<NativeKeyEvent, bool>? NativeKeyDown;
    public event Action<Point, bool>? NativeMouseDown;

    Brush _foreground = Brushes.Black;
    Brush _caretBrush = Brushes.Black;
    double _fontSize = 14;

    public NativeRichEdit()
    {
        // HwndHost inherits FrameworkElement's non-focusable default. The
        // native child must be reachable through both a mouse click and Tab.
        Focusable = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    public Brush Foreground
    {
        get => _foreground;
        set { _foreground = value; if (_handle != IntPtr.Zero) ApplyBaseFormat(); }
    }

    public Brush CaretBrush
    {
        get => _caretBrush;
        set => _caretBrush = value;
    }

    public double FontSize
    {
        get => _fontSize;
        set { _fontSize = value; if (_handle != IntPtr.Zero) ApplyBaseFormat(); }
    }

    public string Text
    {
        get
        {
            if (_handle == IntPtr.Zero) return _pendingText;
            int length = GetTextLength();
            if (length <= 0) return string.Empty;
            var buffer = new StringBuilder(length + 1);
            var request = new GETTEXTEX
            {
                cb = buffer.Capacity * 2,
                flags = GT_USECRLF,
                codepage = 1200,
            };
            NativeMethods.SendMessage(_handle, EM_GETTEXTEX, ref request, buffer);
            return buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        set
        {
            _pendingText = value ?? string.Empty;
            if (_handle == IntPtr.Zero) return;
            _settingText = true;
            try
            {
                NativeMethods.SendMessage(_handle, WM_SETTEXT, IntPtr.Zero,
                    _pendingText.Replace("\n", "\r\n", StringComparison.Ordinal));
                NativeMethods.SendMessage(_handle, EM_SETMODIFY, IntPtr.Zero, IntPtr.Zero);
            }
            finally { _settingText = false; }
        }
    }

    public int SelectionStart
    {
        get { GetSelection(out int start, out _); return ToLogicalOffset(start); }
        set { SetSelection(value, SelectionLength); }
    }

    public int SelectionLength
    {
        get
        {
            GetSelection(out int start, out int end);
            return Math.Max(0, ToLogicalOffset(end) - ToLogicalOffset(start));
        }
        set { SetSelection(SelectionStart, value); }
    }

    public int TextOffsetAt(Point point)
    {
        if (_handle == IntPtr.Zero) return 0;
        var nativePoint = new POINTL { x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y) };
        int native = NativeMethods.SendMessageCharFromPos(_handle, EM_CHARFROMPOS,
            IntPtr.Zero, ref nativePoint).ToInt32();
        return ToLogicalOffset(native);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        NativeMethods.LoadLibrary("Msftedit.dll");
        _handle = NativeMethods.CreateWindowEx(
            0, "RICHEDIT50W", string.Empty,
            WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_TABSTOP |
            ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN | ES_NOHIDESEL,
            0, 0, 0, 0, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("Unable to create the native RichEdit control.");

        NativeMethods.SendMessage(_handle, EM_SETOPTIONS, new IntPtr(EM_SETOPTIONS_OR),
            new IntPtr(ECO_AUTOVSCROLL | ECO_AUTOHSCROLL));
        NativeMethods.SendMessage(_handle, EM_SETREADONLY, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.SendMessage(_handle, EM_EXLIMITTEXT, IntPtr.Zero, new IntPtr(4 * 1024 * 1024));
        NativeMethods.SendMessage(_handle, EM_SETUNDOLIMIT, IntPtr.Zero, new IntPtr(64));
        NativeMethods.SendMessage(_handle, EM_SETLANGOPTIONS, IntPtr.Zero, new IntPtr(SES_EXTENDBACKCOLOR));
        NativeMethods.SendMessage(_handle, EM_SETMARGINS, new IntPtr(0x0003), new IntPtr((12 & 0xFFFF) | (10 << 16)));
        NativeMethods.SendMessage(_handle, EM_HIDESELECTION, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.SendMessage(_handle, WM_SETTEXT, IntPtr.Zero,
            _pendingText.Replace("\n", "\r\n", StringComparison.Ordinal));
        NativeMethods.SendMessage(_handle, EM_SETBKGNDCOLOR, IntPtr.Zero,
            new IntPtr(Rgb(_backgroundColor)));

        _subclassProc = SubclassWindow;
        NativeMethods.SetWindowSubclass(_handle, _subclassProc, UIntPtr.Zero, UIntPtr.Zero);
        ApplyBaseFormat();
        return new HandleRef(this, _handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_handle != IntPtr.Zero && _subclassProc != null)
        {
            NativeMethods.RemoveWindowSubclass(_handle, _subclassProc, UIntPtr.Zero);
            NativeMethods.DestroyWindow(_handle);
        }
        _handle = IntPtr.Zero;
        _subclassProc = null;
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_COMMAND)
        {
            int code = unchecked((short)((long)wParam >> 16));
            if (code == EN_CHANGE && !_settingText)
            {
                TextChanged?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
            else if (code == EN_SELCHANGE)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_handle != IntPtr.Zero)
            NativeMethods.SetWindowPos(_handle, IntPtr.Zero, 0, 0,
                Math.Max(1, (int)Math.Ceiling(sizeInfo.NewSize.Width)),
                Math.Max(1, (int)Math.Ceiling(sizeInfo.NewSize.Height)),
                SWP_NOZORDER | SWP_NOACTIVATE);
    }

    protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (_handle != IntPtr.Zero) NativeMethods.SetFocus(_handle);
    }

    public void FocusEditor()
    {
        Focus();
        if (_handle != IntPtr.Zero) NativeMethods.SetFocus(_handle);
    }

    public void Select(int start, int length)
    {
        SetSelection(start, start + Math.Max(0, length));
        NativeMethods.SendMessage(_handle, EM_SCROLLCARET, IntPtr.Zero, IntPtr.Zero);
    }

    public void ReplaceRange(int start, int length, string replacement)
    {
        SetSelection(start, start + Math.Max(0, length));
        NativeMethods.SendMessage(_handle, EM_REPLACESEL, new IntPtr(1), replacement.Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    int GetTextLength()
    {
        var request = new GETTEXTLENGTHEX { flags = GTL_NUMCHARS, codepage = 1200 };
        return Math.Max(0, NativeMethods.SendMessageInt(_handle,
            EM_GETTEXTLENGTHEX, ref request, IntPtr.Zero));
    }

    public void SetTextColour(Color colour)
    {
        Foreground = new SolidColorBrush(colour);
        ApplyBaseFormat();
    }

    public void SetCaretColour(Color colour)
    {
        CaretBrush = new SolidColorBrush(colour);
        // RichEdit follows the text colour for the caret. Reapplying the base
        // format keeps the control visually synchronized without an extra HWND.
        ApplyBaseFormat();
    }

    public void SetBackgroundColour(Color colour)
    {
        _backgroundColor = colour;
        if (_handle != IntPtr.Zero)
            NativeMethods.SendMessage(_handle, EM_SETBKGNDCOLOR, IntPtr.Zero,
                new IntPtr(Rgb(colour)));
    }

    public void ApplyBaseFormat()
    {
        if (_handle == IntPtr.Zero) return;
        int length = Text.Length;
        if (length <= 0) return;
        var format = NativeCharFormat.Default(Rgb(ToColor(Foreground)), FontSize);
        ApplyFormat(0, length, format);
    }

    public void ApplyFormat(int start, int length, NativeCharFormat format)
    {
        if (_handle == IntPtr.Zero || length <= 0) return;
        GetSelection(out int savedStart, out int savedEnd);
        SetSelection(start, start + length);
        format.Size = (int)Math.Round((double)Math.Max(1, format.Size));
        format.Native.cbSize = Marshal.SizeOf<CHARFORMAT2>();
        NativeMethods.SendMessage(_handle, EM_SETCHARFORMAT,
            new IntPtr(EM_SETCHARFORMAT_SELECTION), ref format.Native);
        SetSelectionNative(savedStart, savedEnd);
    }

    public IDisposable BeginOffsetBatch(string logicalText)
    {
        var previous = _offsetText;
        _offsetText = logicalText;
        return new OffsetBatch(this, previous);
    }

    internal void GetSelection(out int start, out int end)
    {
        start = end = 0;
        if (_handle == IntPtr.Zero) return;
        var range = new CHARRANGE();
        NativeMethods.SendMessage(_handle, EM_EXGETSEL, IntPtr.Zero, ref range);
        start = Math.Max(0, range.cpMin);
        end = Math.Max(start, range.cpMax);
    }

    void SetSelection(int start, int end)
    {
        if (_handle == IntPtr.Zero) return;
        int max = GetTextLength();
        var range = new CHARRANGE
        {
            cpMin = Math.Clamp(ToNativeOffset(start), 0, max),
            cpMax = Math.Clamp(ToNativeOffset(end), 0, max),
        };
        NativeMethods.SendMessage(_handle, EM_EXSETSEL, IntPtr.Zero, ref range);
    }

    void SetSelectionNative(int start, int end)
    {
        if (_handle == IntPtr.Zero) return;
        var range = new CHARRANGE { cpMin = Math.Max(0, start), cpMax = Math.Max(0, end) };
        NativeMethods.SendMessage(_handle, EM_EXSETSEL, IntPtr.Zero, ref range);
    }

    int ToNativeOffset(int logicalOffset)
    {
        string text = _offsetText ?? Text;
        logicalOffset = Math.Clamp(logicalOffset, 0, text.Length);
        int native = logicalOffset;
        for (int i = 0; i < logicalOffset; i++)
            if (text[i] == '\n') native++;
        return native;
    }

    int ToLogicalOffset(int nativeOffset)
    {
        string text = RawText();
        nativeOffset = Math.Clamp(nativeOffset, 0, text.Length);
        int logical = 0;
        for (int i = 0; i < nativeOffset; i++)
        {
            if (text[i] == '\r' && i + 1 < nativeOffset && text[i + 1] == '\n')
                continue;
            logical++;
        }
        return logical;
    }

    string RawText()
    {
        if (_handle == IntPtr.Zero) return _pendingText.Replace("\n", "\r\n", StringComparison.Ordinal);
        int length = GetTextLength();
        if (length <= 0) return string.Empty;
        var buffer = new StringBuilder(length + 1);
        var request = new GETTEXTEX
        {
            cb = buffer.Capacity * 2,
            flags = GT_USECRLF,
            codepage = 1200,
        };
        NativeMethods.SendMessage(_handle, EM_GETTEXTEX, ref request, buffer);
        return buffer.ToString();
    }

    sealed class OffsetBatch : IDisposable
    {
        readonly NativeRichEdit _owner;
        readonly string? _previous;
        bool _disposed;

        public OffsetBatch(NativeRichEdit owner, string? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._offsetText = _previous;
        }
    }

    NativeCharFormat ToNative(NativeCharFormat value) => value;

    IntPtr SubclassWindow(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
                          UIntPtr id, UIntPtr data)
    {
        if (msg == WM_KEYDOWN && (wParam.ToInt32() == VK_RETURN || wParam.ToInt32() == VK_ESCAPE))
        {
            var modifiers = (wParam.ToInt32() == VK_RETURN &&
                (NativeMethods.GetKeyState(0x11) & 0x8000) != 0 ? ModifierKeys.Control : ModifierKeys.None)
                | ((NativeMethods.GetKeyState(0x10) & 0x8000) != 0 ? ModifierKeys.Shift : ModifierKeys.None);
            if (NativeKeyDown?.Invoke(new NativeKeyEvent(wParam.ToInt32(), modifiers)) == true)
                return IntPtr.Zero;
        }
        else if (msg == WM_LBUTTONDOWN)
        {
            // HwndHost does not always transfer focus to the child when the
            // parent is a borderless transparent window. Do it before the
            // click reaches RichEdit so typing starts immediately.
            NativeMethods.SetFocus(hwnd);
            int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
            int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
            bool control = (wParam.ToInt32() & MK_CONTROL) != 0;
            NativeMouseDown?.Invoke(new Point(x, y), control);
        }
        return NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    static Color ToColor(Brush brush) => brush is SolidColorBrush solid ? solid.Color : Colors.Black;
    static int Rgb(Color color) => color.R | (color.G << 8) | (color.B << 16);

    internal readonly record struct NativeKeyEvent(int Key, ModifierKeys Modifiers);

    internal struct NativeCharFormat
    {
        internal CHARFORMAT2 Native;
        internal int Size;

        public static NativeCharFormat Default(int colour, double size)
            => new()
            {
                Size = (int)Math.Round(size * 20),
                Native = new CHARFORMAT2
                {
                    dwMask = NativeFormat.CFM_BOLD | NativeFormat.CFM_ITALIC |
                        NativeFormat.CFM_UNDERLINE | NativeFormat.CFM_STRIKEOUT |
                        NativeFormat.CFM_HIDDEN | NativeFormat.CFM_COLOR |
                        NativeFormat.CFM_SIZE | NativeFormat.CFM_FACE,
                    dwEffects = 0,
                    yHeight = (int)Math.Round(size * 20),
                    crTextColor = colour,
                    bCharSet = 1,
                    bPitchAndFamily = 0,
                    szFaceName = "Segoe UI",
                },
            };
    }

    internal static class NativeFormat
    {
        public const uint CFM_BOLD = 0x00000001;
        public const uint CFM_ITALIC = 0x00000002;
        public const uint CFM_UNDERLINE = 0x00000004;
        public const uint CFM_STRIKEOUT = 0x00000008;
        public const uint CFM_COLOR = 0x40000000;
        public const uint CFM_SIZE = 0x80000000;
        public const uint CFM_FACE = 0x20000000;
        public const uint CFM_HIDDEN = 0x00000100;
        public const uint CFE_BOLD = 0x00000001;
        public const uint CFE_ITALIC = 0x00000002;
        public const uint CFE_UNDERLINE = 0x00000004;
        public const uint CFE_STRIKEOUT = 0x00000008;
        public const uint CFE_HIDDEN = 0x00000100;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CHARRANGE { public int cpMin; public int cpMax; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINTL { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CHARFORMAT2
    {
        public int cbSize;
        public uint dwMask;
        public uint dwEffects;
        public int yHeight;
        public int yOffset;
        public int crTextColor;
        public byte bCharSet;
        public byte bPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szFaceName;
        public short wWeight;
        public short sSpacing;
        public int crBackColor;
        public int lcid;
        public int dwReserved;
        public short sStyle;
        public short wKerning;
        public byte bUnderlineType;
        public byte bAnimation;
        public byte bRevAuthor;
        public byte bReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GETTEXTEX { public int cb; public int flags; public int codepage; }

    [StructLayout(LayoutKind.Sequential)]
    struct GETTEXTLENGTHEX { public int flags; public int codepage; }

    const int GT_USECRLF = 1;
    const int GTL_NUMCHARS = 8;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
                                 UIntPtr id, UIntPtr data);

    static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr LoadLibrary(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateWindowEx(int exStyle, string className, string title,
            int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
            IntPtr instance, IntPtr param);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, ref GETTEXTEX wParam, StringBuilder lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Unicode)]
        internal static extern int SendMessageInt(IntPtr hwnd, int msg, ref GETTEXTLENGTHEX wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref CHARRANGE lParam);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref CHARFORMAT2 lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessageCharFromPos(
            IntPtr hwnd, int msg, IntPtr wParam, ref POINTL lParam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y,
            int cx, int cy, int flags);
        [DllImport("comctl32.dll", SetLastError = true)]
        internal static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback,
            UIntPtr id, UIntPtr data);
        [DllImport("comctl32.dll", SetLastError = true)]
        internal static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, UIntPtr id);
        [DllImport("comctl32.dll")] internal static extern IntPtr DefSubclassProc(
            IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
