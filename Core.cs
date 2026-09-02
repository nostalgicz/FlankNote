using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace FlankNote;

// ────────────────────────────────────────────────────────────
//  Palette — faithful port of the original macOS palette.
// ────────────────────────────────────────────────────────────
record struct NoteColor(string Name, Color Paper, Color Dash, Color Ink)
{
    static Color Hex(uint v) => Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);

    static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * amount),
            (byte)Math.Round(from.G + (to.G - from.G) * amount),
            (byte)Math.Round(from.B + (to.B - from.B) * amount));
    }

    public static readonly NoteColor[] All =
    [
        new("Lemon", Hex(0xFCE795), Hex(0xE0AD08), Hex(0x3A3008)),
        new("Peach", Hex(0xFBCFA6), Hex(0xE2762A), Hex(0x422413)),
        new("Rose",  Hex(0xFAC4D1), Hex(0xDC4570), Hex(0x40161F)),
        new("Lilac", Hex(0xD9C7FA), Hex(0x7C4DEE), Hex(0x2A1B44)),
        new("Sky",   Hex(0xBEDDFA), Hex(0x2280D6), Hex(0x13293A)),
        new("Mint",  Hex(0xB4E8D0), Hex(0x0E9B6E), Hex(0x0F2E23)),
        new("Sand",  Hex(0xE3D3B4), Hex(0xA37B3C), Hex(0x372C18)),
        new("Slate", Hex(0xCBD6E2), Hex(0x4E6579), Hex(0x1A242E)),
        new("White", Hex(0xFFFFFF), Hex(0xD8D8D8), Hex(0x333333)),
    ];

    public static NoteColor At(int i) => All[((i % All.Length) + All.Length) % All.Length];

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (value is not { Length: 7 } || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out uint rgb)) return false;
        color = Hex(rgb);
        return true;
    }

    public static bool TryCustom(string? value, out NoteColor palette)
    {
        palette = default;
        if (!TryParse(value, out var accent)) return false;

        double luminance = (0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B) / 255;
        var paper = Blend(accent, Colors.White, luminance < 0.25 ? 0.65 : 0.55);
        var dash = luminance > 0.82 ? Blend(accent, Colors.Black, 0.16) : accent;
        var ink = Blend(dash, Colors.Black, 0.80);
        palette = new NoteColor("Custom", paper, dash, ink);
        return true;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public SolidColorBrush PaperB => new(Paper);
    public SolidColorBrush DashB => new(Dash);
    public SolidColorBrush InkB => new(Ink);
}

// ────────────────────────────────────────────────────────────
//  Note model.  Bodies are plain text; tasks live inline as
//  ☐ / ☑ prefixes, exactly like the original.
// ────────────────────────────────────────────────────────────
class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public bool HasCustomTitle { get; set; }
    public string Body { get; set; } = "";
    public int Color { get; set; }
    public string? CustomColor { get; set; }
    public double WindowWidth { get; set; } = Geom.EditorWidth + Geom.WindowInset;
    public double WindowHeight { get; set; } = Geom.EditorHeight + Geom.WindowInset;
    public bool Pinned { get; set; }
    public bool Archived { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Updated { get; set; } = DateTime.Now;
    public int Order { get; set; }              // ascending; smallest = newest, top of the deck

    // Computed — brushes and derived text must not be serialized
    [System.Text.Json.Serialization.JsonIgnore]
    public NoteColor Palette => NoteColor.TryCustom(CustomColor, out var custom)
        ? custom
        : NoteColor.At(Color);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasCustomColor => NoteColor.TryCustom(CustomColor, out _);
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayTitle => Title.Length == 0 ? "New note" : Title;

    public void DeriveTitle()
    {
        var line = Body.Split('\n').FirstOrDefault()?.Trim() ?? "";
        line = Regex.Replace(line, @"^#{1,6}\s*", "");
        line = Tasks.Strip(line);
        Title = line.Length > 60 ? line[..60] + "…" : line;
    }
}

static class Tasks
{
    public const char Open = '☐', Done = '☑';
    public static bool IsOpen(string line) => line.StartsWith(Open + " ");
    public static bool IsDone(string line) => line.StartsWith(Done + " ");
    public static bool IsTask(string line) => IsOpen(line) || IsDone(line);
    public static string Strip(string line) => IsTask(line) ? line[2..] : line;
    public static (int Done, int Total) Progress(string body)
    {
        int done = 0, total = 0;
        foreach (var line in body.Split('\n'))
        {
            if (IsDone(line)) { total++; done++; }
            else if (IsOpen(line)) total++;
        }
        return (done, total);
    }
    public static string Toggle(string line)
        => IsDone(line) ? Open + line[1..] : (IsOpen(line) ? Done + line[1..] : Done + " " + line);
}

// ────────────────────────────────────────────────────────────
//  Store — one JSON file in the compatibility data directory.
//  (SQLite + AES-GCM from the original dropped for the minimal port.)
// ────────────────────────────────────────────────────────────
class NotesStore
{
    public static readonly NotesStore I = new();
    public List<Note> Notes = [];               // sorted, newest first (ascending Order)
    public event Action? Changed;
    public string? LoadError { get; private set; }
    bool _storageNeedsRecovery;
    readonly string _dir;
    readonly string _filePath;

    internal NotesStore(string? storageDirectory = null)
    {
        _dir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.StorageDirectoryName);
        _filePath = Path.Combine(_dir, "notes.json");
    }

    class Root
    {
        public List<Note> notes { get; set; } = [];
        public string edge { get; set; } = "right";
        public string display { get; set; } = "";
        public double fontSize { get; set; } = 14;
        public double? noteTransparency { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? noteOpacity { get; set; }
        public string deckStyle { get; set; } = "tabs";
        public double deckScale { get; set; } = 1;
        public bool keepDeckOpen { get; set; }
        public bool openOnHover { get; set; }
        public bool autoCollapseNote { get; set; }
        public double wakeDistance { get; set; } = 40;
        public bool markdown { get; set; } = true;
        public bool overlayFullscreen { get; set; } = true;
        public string language { get; set; } = "en";
        public bool? firstRunCompleted { get; set; }
        public int? firstRunVersion { get; set; }
    }

    public void Load()
    {
        LoadError = null;
        _storageNeedsRecovery = false;
        Notes = [];
        try
        {
            if (File.Exists(_filePath))
            {
                var root = JsonSerializer.Deserialize<Root>(File.ReadAllText(_filePath));
                if (root?.notes is { } ns) Notes = ns;
                Settings.EdgeLeft = root?.edge == "left";
                Settings.DisplayName = root?.display ?? "";
                Settings.NoteFontSize = root?.fontSize is > 0 ? root.fontSize : 14;
                Settings.NoteTransparency = root?.noteTransparency is { } transparency
                    ? Settings.ClampNoteTransparency(transparency)
                    : root?.noteOpacity is { } legacyOpacity
                        ? Settings.ClampNoteTransparency(1 - Math.Clamp(legacyOpacity, 0, 1))
                        : 0;
                if (root?.deckStyle is "tabs" or "chips") Settings.DeckStyle = root.deckStyle;
                Settings.DeckScale = root?.deckScale is >= 0.7 and <= 1.8 ? root.deckScale : 1;
                Settings.KeepDeckOpen = root?.keepDeckOpen ?? false;
                Settings.OpenOnHover = root?.openOnHover ?? false;
                Settings.AutoCollapseNote = root?.autoCollapseNote ?? false;
                Settings.WakeDistance = root?.wakeDistance is > 0 ? root.wakeDistance : 40;
                Settings.Markdown = root?.markdown ?? true;
                Settings.OverlayFullscreen = root?.overlayFullscreen ?? true;
                Settings.Language = root?.language is "zh" or "en" ? root.language : "en";
                // Existing data predating the welcome page belongs to an upgrade,
                // not a fresh installation, so it must not trigger onboarding.
                Settings.FirstRunCompleted = root?.firstRunVersion >= 1
                    || root?.firstRunCompleted == true
                    || root is { firstRunVersion: null, firstRunCompleted: null };
            }
        }
        catch (Exception ex)
        {
            Notes = [];
            _storageNeedsRecovery = true;
            LoadError = Loc.T(
                "FlankNote could not read its notes file. Your original file was kept; changes will be saved after you create or edit a note.",
                "FlankNote 无法读取便签文件。原文件已保留；创建或编辑便签后才会保存新的数据文件。");
            App.ReportError($"Storage load failed: {ex}");
            try
            {
                if (File.Exists(_filePath))
                {
                    var backup = _filePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
                    File.Copy(_filePath, backup, overwrite: false);
                    LoadError += Loc.T($" Backup: {Path.GetFileName(backup)}", $" 备份：{Path.GetFileName(backup)}");
                }
            }
            catch (Exception backupError) { App.ReportError($"Storage backup failed: {backupError}"); }
        }
        Notes.Sort((a, b) => a.Order.CompareTo(b.Order));
    }

    public void Save()
    {
        if (_storageNeedsRecovery) return;
        var temporary = _filePath + ".tmp";
        try
        {
            Directory.CreateDirectory(_dir);
            var root = new Root
            {
                notes = Notes,
                edge = Settings.EdgeLeft ? "left" : "right",
                display = Settings.DisplayName,
                fontSize = Settings.NoteFontSize,
                noteTransparency = Settings.ClampNoteTransparency(Settings.NoteTransparency),
                deckStyle = Settings.DeckStyle,
                deckScale = Settings.DeckScale,
                keepDeckOpen = Settings.KeepDeckOpen,
                openOnHover = Settings.OpenOnHover,
                autoCollapseNote = Settings.AutoCollapseNote,
                wakeDistance = Settings.WakeDistance,
                markdown = Settings.Markdown,
                overlayFullscreen = Settings.OverlayFullscreen,
                language = Settings.Language,
                firstRunCompleted = Settings.FirstRunCompleted,
                firstRunVersion = Settings.FirstRunCompleted ? 1 : 0,
            };
            var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporary, json);
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanupError) { App.ReportError($"Storage temp cleanup failed: {cleanupError}"); }
            App.ReportError($"Storage save failed: {ex}");
        }
    }

    void PrepareWrite() => _storageNeedsRecovery = false;

    // Always derive deck order from the persisted rank. Reordering changes the
    // Order values in place, so relying on List insertion order would make the
    // next deck refresh visually snap back to the old arrangement.
    public IEnumerable<Note> Active => Notes.Where(n => !n.Archived).OrderBy(n => n.Order);
    public Note? ById(string id) => Notes.FirstOrDefault(n => n.Id == id);

    public Note Create(string body = "", int? color = null)
    {
        PrepareWrite();
        var n = new Note
        {
            Body = body,
            Color = color ?? (Notes.Count % NoteColor.All.Length),
            Order = (Notes.Count == 0 ? 0 : Notes.Min(x => x.Order)) - 1,   // newest at the top
        };
        n.DeriveTitle();
        Notes.Add(n);
        Notes.Sort((a, b) => a.Order.CompareTo(b.Order));
        Save();
        Changed?.Invoke();
        return n;
    }

    public void Update(Note n)
    {
        n.Updated = DateTime.Now;
        if (!n.HasCustomTitle) n.DeriveTitle();
        // Detached Note instances are useful for previews and unit tests. They
        // must never cause the singleton store to overwrite the real data file.
        if (!Notes.Contains(n)) return;
        PrepareWrite();
        Save(); Changed?.Invoke();
    }
    public void Delete(string id) { PrepareWrite(); Notes.RemoveAll(x => x.Id == id); Save(); Changed?.Invoke(); }
    public void Archive(string id)
    {
        PrepareWrite();
        var n = ById(id);
        if (n == null) return;
        n.Archived = true;
        n.Updated = DateTime.Now;
        Save(); Changed?.Invoke();
    }
    public void Restore(Note n, bool archived = false)
    {
        PrepareWrite();
        n.Archived = archived;
        if (!Notes.Contains(n)) Notes.Add(n);   // deleted → re-add; archived → already in the list
        Notes.Sort((a, b) => a.Order.CompareTo(b.Order));
        Save(); Changed?.Invoke();
    }

    /// <summary>Raise Changed for subscribers outside the store (e.g. reorder).</summary>
    public void NotifyChanged() => Changed?.Invoke();

    // ── Markdown export ─────────────────────────────────────
    /// <summary>Export active and archived notes as one portable UTF-8 Markdown document.</summary>
    public void ExportMarkdown(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var output = new StringBuilder();
        output.Append("# ").AppendLine(AppIdentity.DisplayName).AppendLine();

        AppendSection(Loc.T("Active notes", "当前便签"), Active);
        AppendSection(Loc.T("Archived notes", "已归档便签"),
            Notes.Where(n => n.Archived).OrderBy(n => n.Order));

        File.WriteAllText(path, output.ToString().TrimEnd() + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        void AppendSection(string heading, IEnumerable<Note> notes)
        {
            var items = notes.ToArray();
            if (items.Length == 0) return;

            output.Append("## ").AppendLine(heading).AppendLine();
            foreach (var note in items)
            {
                output.Append("### ").AppendLine(EscapeMarkdownHeading(note.DisplayTitle)).AppendLine();
                var body = ToPortableMarkdown(note.Body).TrimEnd();
                if (body.Length > 0) output.AppendLine(body).AppendLine();
                output.AppendLine("---").AppendLine();
            }
        }
    }

    static string ToPortableMarkdown(string body) => string.Join("\n",
        body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line =>
            Tasks.IsOpen(line) ? "- [ ] " + Tasks.Strip(line) :
            Tasks.IsDone(line) ? "- [x] " + Tasks.Strip(line) : line));

    static string EscapeMarkdownHeading(string text)
    {
        const string syntax = @"\`*_{}[]<>()#+-.!|";
        var escaped = new StringBuilder(text.Length);
        foreach (char c in text.Replace('\r', ' ').Replace('\n', ' '))
        {
            if (syntax.Contains(c)) escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }
}

static class Settings
{
    public static bool EdgeLeft = false;
    public static string DisplayName = "";
    public static double NoteFontSize = 14;
    // Signed adjustment around the original material: negative values make
    // the paper/gutter more opaque, positive values fade the whole window.
    public static double NoteTransparency;
    public static string DeckStyle = "tabs";        // "tabs" | "chips" (colour chips, no labels)
    public static double DeckScale = 1;              // 0.7 ... 1.8
    public static bool KeepDeckOpen = false;         // fan is the resting state
    public static bool OpenOnHover = false;          // dwell on a tab to open it
    public static bool AutoCollapseNote = false;     // close an unpinned note after leaving its interaction area
    public static double WakeDistance = 40;         // how close to the edge the pointer wakes the deck (DIP)
    public static bool Markdown = true;             // markdown-as-you-type styling
    public static bool OverlayFullscreen = true;    // stay on top of full-screen apps
    public static string Language = "en";           // "en" | "zh"
    public static bool FirstRunCompleted;

    public static double ClampNoteTransparency(double value)
        => double.IsFinite(value) ? Math.Clamp(value, -0.70, 0.70) : 0;

    public static double TransparencyControlValue(double transparency)
    {
        double value = ClampNoteTransparency(transparency);
        return (value + 0.70) / 1.40;
    }

    public static double NoteTransparencyFromControl(double control)
    {
        if (!double.IsFinite(control)) return 0;
        return ClampNoteTransparency(Math.Clamp(control, 0, 1) * 1.40 - 0.70);
    }
}

// ────────────────────────────────────────────────────────────
//  Deck geometry — ported 1:1 from the original DeckGeom.
// ────────────────────────────────────────────────────────────
static class Geom
{
    static double S => Math.Clamp(Settings.DeckScale, 0.7, 1.8);
    public static double PillWidth => 14 * S;
    public static double PillTouchWidth => 18 * S;
    public static double DashHeight => 15 * S;
    public static double DashWidth => 8 * S;
    public static double DashGap => 5 * S;
    public static double PillPad => 7 * S;
    public const int MaxDashes = 14;
    public static double TabWidth => 36 * S;
    public static double TabLap => 42 * S;
    public static double PitchMin => 62 * S;
    public static double PitchMax => 116 * S;
    public static double LabelPad => 22 * S;
    public static double LabelInset => 13 * S;
    public static double MoreTabHeight => 36 * S;
    public static double TabGap => 7 * S;
    public static double Bleed => 14 * S;
    public static double FanWidth => 58 * S;
    public static double PlusSize => 30 * S;
    public static double PlusGap => 13 * S;
    public const double HeightBudget = 0.68;   // share of the deck window's height the fan may fill
    public const double EditorWidth = 460, EditorHeight = 380, GutterWidth = 36;
    public const double WindowInset = 16;
    public const double WindowMinWidth = 400, WindowMaxWidth = 776;
    public const double WindowMinHeight = 150, WindowMaxHeight = 776;
    public static double TabHeightMax => 118 * S;   // original value (tabHeightMax)

    public static double PillHeight(int noteCount)
    {
        int shown = Math.Min(noteCount, MaxDashes);
        int n = Math.Max(1, shown + (noteCount > MaxDashes ? 1 : 0));
        return PillPad * 2 + n * DashHeight + (n - 1) * DashGap;
    }

    public static (Size Min, Size Max) WindowSizeLimits(Rect workArea)
    {
        double availableWidth = Math.Max(280, workArea.Width - 24);
        double availableHeight = Math.Max(220, workArea.Height - 24);
        double minWidth = Math.Min(WindowMinWidth, availableWidth);
        double minHeight = Math.Min(WindowMinHeight, availableHeight);
        double maxWidth = Math.Max(minWidth, Math.Min(WindowMaxWidth, availableWidth));
        double maxHeight = Math.Max(minHeight, Math.Min(WindowMaxHeight, availableHeight));
        return (new Size(minWidth, minHeight), new Size(maxWidth, maxHeight));
    }

    public static Size DefaultWindowSize(Rect workArea)
    {
        var limits = WindowSizeLimits(workArea);
        return new Size(
            Math.Clamp(EditorWidth + WindowInset, limits.Min.Width, limits.Max.Width),
            Math.Clamp(EditorHeight + WindowInset, limits.Min.Height, limits.Max.Height));
    }

    public static Size WindowSize(Note note, Rect workArea)
    {
        var limits = WindowSizeLimits(workArea);
        var defaults = DefaultWindowSize(workArea);
        double requestedWidth = double.IsFinite(note.WindowWidth) && note.WindowWidth > 0
            ? note.WindowWidth : defaults.Width;
        double requestedHeight = double.IsFinite(note.WindowHeight) && note.WindowHeight > 0
            ? note.WindowHeight : defaults.Height;
        return new Size(
            Math.Clamp(requestedWidth, limits.Min.Width, limits.Max.Width),
            Math.Clamp(requestedHeight, limits.Min.Height, limits.Max.Height));
    }

    public record struct FanLayout(double Pitch, double ItemHeight, double FanTop);

    /// <summary>Shingled tabs, exactly like the original (DeckPanel.layout):
    /// each tab is `pitch` below the last and laps `TabLap` over it; the strip is
    /// sized to the longest label (56…106), the deck may claim heightBudget of the
    /// screen before tabs shrink, and the whole stack sits centred.</summary>
    public static FanLayout Fan(double workH, int count, double longestLabel, bool hasMore)
    {
        int n = Math.Max(1, count);
        double pitch = Math.Clamp(longestLabel + LabelPad, PitchMin, PitchMax);
        double reserved = hasMore ? MoreTabHeight + TabGap : 0;
        double budget = workH * HeightBudget - reserved;
        if (n * pitch + TabLap > budget)
            pitch = Math.Max(36 * S, (budget - TabLap) / n);
        double itemH = pitch + TabLap;
        double stackH = (n - 1) * pitch + itemH
            + (hasMore ? MoreTabHeight + TabGap : 0) + PlusGap + PlusSize;
        double top = Math.Max(12 * S, (workH - stackH) / 2);   // centred, like the original
        return new FanLayout(pitch, itemH, top);
    }

    public static double LabelWidth(string title, Typeface face, double size)
    {
        var t = title.ToUpperInvariant();
        if (t.Length == 0) return 0;
        // PixelsPerDip must match the real DPI or measured widths are wrong on
        // scaled displays — long labels then drift off-centre (CJK worst).
        double ppd = 1.0;
        try { ppd = VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip; } catch { }
        return new FormattedText(t, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, size, Brushes.Black, ppd).Width;
    }
}
