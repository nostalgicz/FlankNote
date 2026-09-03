using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace FlankNote;

/// <summary>All Notes (⌥⌘A): search every note, edit the detail panel,
/// archive/restore.  The original's LibraryWindow.</summary>
class AllNotesWindow : Window
{
    static readonly SolidColorBrush WindowBg = UiTheme.Window;
    static readonly SolidColorBrush Surface = UiTheme.Surface;
    static readonly SolidColorBrush TextInk = UiTheme.Text;
    static readonly SolidColorBrush MutedInk = UiTheme.Muted;
    static readonly SolidColorBrush Hairline = UiTheme.Hairline;
    static readonly SolidColorBrush Selection = UiTheme.Selection;
    static readonly SolidColorBrush Danger = UiTheme.Danger;

    readonly TextBox _search = new();
    readonly ListBox _list = new();
    readonly TextBlock _viewToggle;
    readonly TextBox _titleBox = new();
    readonly RichTextBox _body;
    readonly Border _modeBtn, _colorBtn, _archiveBtn, _deleteBtn;
    readonly Ellipse _colorSwatch = new() { Width = 11, Height = 11, StrokeThickness = 1 };
    readonly Popup _colourPopup = new() { AllowsTransparency = true, StaysOpen = false };
    readonly Grid _detailEditor = new();
    readonly TextBlock _emptyState = new();
    readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(250) };

    Note? _current;
    bool _showArchived;
    readonly bool _archivedOnly;
    bool _loading;
    bool _styling;
    bool _titleEdited;
    Paragraph? _editingMarkdownParagraph;

    public AllNotesWindow(bool archivedOnly = false)
    {
        _archivedOnly = archivedOnly;
        if (archivedOnly)
        {
            _showArchived = true;
            Title = Loc.T("Archive", "归档");
        }
        else
        {
            Title = Loc.T("All Notes", "全部便签");
        }
        Width = 760; Height = 560;
        MinWidth = 660; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = WindowBg;
        FontFamily = UiTheme.Font;
        Foreground = TextInk;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // ── left: search + list ─────────────────────────────
        _search.FontSize = 13;
        _search.Padding = new Thickness(10, 7, 10, 7);
        _search.Background = Brushes.Transparent;
        _search.Foreground = TextInk;
        _search.CaretBrush = TextInk;
        _search.BorderThickness = new Thickness(0);
        _search.ToolTip = Loc.T("Search titles and note text", "搜索标题和便签内容");
        _search.TextChanged += (_, _) => RefreshList();

        var listPanel = new Grid { Margin = new Thickness(16, 16, 12, 14) };
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        listPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var sectionTitle = new TextBlock
        {
            Text = archivedOnly ? Loc.T("ARCHIVE", "归档") : Loc.T("ALL NOTES", "全部便签"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = MutedInk,
            Margin = new Thickness(2, 0, 0, 10),
        };
        Grid.SetRow(sectionTitle, 0);
        listPanel.Children.Add(sectionTitle);

        var searchShell = new Border
        {
            Background = Surface,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = _search,
        };
        Grid.SetRow(searchShell, 1);
        listPanel.Children.Add(searchShell);

        _list.Background = Brushes.Transparent;
        _list.BorderThickness = new Thickness(0);
        _list.Padding = new Thickness(0);
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem it && it.Tag is Note n) Select(n);
        };
        Grid.SetRow(_list, 2);
        listPanel.Children.Add(_list);

        _viewToggle = new TextBlock
        {
            Text = Loc.T("Archive (0)", "归档 (0)"), FontSize = 12, Cursor = Cursors.Hand,
            Foreground = MutedInk,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 10, 0, 2),
        };
        _viewToggle.MouseLeftButtonUp += (_, _) => { Save(); _showArchived = !_showArchived; ClearDetail(); RefreshList(); };
        if (_archivedOnly) _viewToggle.Visibility = Visibility.Collapsed;   // the Archive window is pinned to archived
        Grid.SetRow(_viewToggle, 3);
        listPanel.Children.Add(_viewToggle);

        // ── right: detail panel ─────────────────────────────
        _titleBox.FontSize = 21;
        _titleBox.FontWeight = FontWeights.SemiBold;
        _titleBox.Foreground = TextInk;
        _titleBox.Background = Brushes.Transparent;
        _titleBox.BorderThickness = new Thickness(0);
        _titleBox.Padding = new Thickness(0);
        _titleBox.Margin = new Thickness(0, 0, 0, 14);
        _titleBox.IsReadOnly = false;
        _titleBox.Focusable = true;
        _titleBox.ToolTip = Loc.T("Edit note title", "编辑便签标题");

        _body = new RichTextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 14,
            AcceptsReturn = true,
            AcceptsTab = true,
            Foreground = TextInk,
            CaretBrush = TextInk,
            Padding = new Thickness(0, 2, 4, 4),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _body.Document = new FlowDocument { PagePadding = new Thickness(0) };
        _body.TextChanged += (_, _) =>
        {
            if (_loading || _styling || _current == null) return;
            StyleBodyIfLineChanged();
            _autosave.Stop(); _autosave.Start();
        };
        _body.SelectionChanged += (_, _) =>
        {
            if (!_loading && !_styling && _current != null) StyleBodyIfLineChanged();
        };
        _body.GotKeyboardFocus += (_, _) => StyleBody();
        _body.LostKeyboardFocus += (_, _) =>
            Dispatcher.BeginInvoke(new Action(StyleBody));
        _titleBox.TextChanged += (_, _) =>
        {
            if (_loading || _current == null) return;
            _titleEdited = true;
            _autosave.Stop(); _autosave.Start();
        };
        _body.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Markdown.TryOpenLink(_body, e, _current?.UsesMarkdown == true)) return;
            Markdown.TryToggleTaskAtPoint(_body, e, _current?.UsesMarkdown == true);
        };
        _body.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Return
                && Markdown.HandleTaskReturn(_body, _current?.UsesMarkdown == true)) e.Handled = true;
        };
        _autosave.Tick += (_, _) => { _autosave.Stop(); Save(); };

        _modeBtn = ActionButton("", (_, _) => ToggleTextMode());
        _colorBtn = ActionButton(Loc.T("Colour", "颜色"), (_, _) => ShowColourMenu());
        var colourText = ActionLabel(_colorBtn);
        _colorBtn.Child = null;
        var colourContent = new StackPanel { Orientation = Orientation.Horizontal };
        _colorSwatch.VerticalAlignment = VerticalAlignment.Center;
        _colorSwatch.Margin = new Thickness(0, 0, 7, 0);
        colourContent.Children.Add(_colorSwatch);
        colourContent.Children.Add(colourText);
        _colorBtn.Child = colourContent;
        _colorBtn.ToolTip = Loc.T("Choose note colour", "选择便签颜色");
        _archiveBtn = ActionButton(Loc.T("Archive", "归档"), (_, _) =>
        {
            if (_current == null) return;
            _current.Archived = !_current.Archived;
            Save(); ClearDetail(); RefreshList();
        });
        _deleteBtn = ActionButton(Loc.T("Delete", "删除"), (_, _) =>
        {
            if (_current == null) return;
            var doomed = _current;
            NotesStore.I.Delete(_current.Id);
            _current = null;
            RefreshList(); ClearDetail();
            new UndoToast(doomed, restoreArchived: doomed.Archived);
        }, danger: true);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        btns.Children.Add(_modeBtn);
        btns.Children.Add(_colorBtn);
        btns.Children.Add(_archiveBtn);
        btns.Children.Add(_deleteBtn);

        _detailEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailEditor.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _detailEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_titleBox, 0); _detailEditor.Children.Add(_titleBox);
        Grid.SetRow(_body, 1); _detailEditor.Children.Add(_body);
        Grid.SetRow(btns, 2); _detailEditor.Children.Add(btns);

        _emptyState.Text = Loc.T("Select a note", "选择一张便签");
        _emptyState.FontSize = 14;
        _emptyState.Foreground = MutedInk;
        _emptyState.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyState.VerticalAlignment = VerticalAlignment.Center;

        var detailHost = new Grid();
        detailHost.Children.Add(_detailEditor);
        detailHost.Children.Add(_emptyState);
        var detail = new Border
        {
            Background = Surface,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(12, 14, 16, 14),
            Padding = new Thickness(20, 18, 18, 14),
            Child = detailHost,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(listPanel, 0);
        var divider = new Border { Background = Hairline };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(listPanel);
        grid.Children.Add(divider);
        grid.Children.Add(detail);
        Content = UiTheme.WithWindowChrome(this, Title, grid);
        ClearDetail();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Save(); Close(); }
        };
        Closed += (_, _) => { Save(); if (_archivedOnly) App.ArchiveWin = null; else App.AllNotes = null; };

        Loaded += (_, _) => RefreshList();
        DisplayService.CenterOnSelected(this);
        Show();
        _search.Focus();
    }

    Border ActionButton(string text, MouseButtonEventHandler onClick, bool danger = false)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = danger ? Danger : MutedInk,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var b = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            Child = label,
        };
        b.MouseEnter += (_, _) => b.Background = WindowBg;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += onClick;
        return b;
    }

    static TextBlock ActionLabel(Border button) => (TextBlock)button.Child;

    void ShowColourMenu()
    {
        if (_current == null) return;

        // Commit pending text before changing the palette so both edits are
        // represented by the same note instance in the refreshed list.
        Save();
        var note = _current;
        if (note == null) return;

        var paletteRow = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < NoteColor.All.Length; i++)
        {
            int colorIndex = i;
            var palette = NoteColor.All[colorIndex];
            var dot = new Ellipse
            {
                Width = 15,
                Height = 15,
                Fill = palette.DashB,
                Stroke = new SolidColorBrush(palette.Ink) { Opacity = 0.25 },
                StrokeThickness = 1,
            };
            var choice = new Border
            {
                Width = 29,
                Height = 29,
                Margin = new Thickness(colorIndex == 0 ? 0 : 2, 0, 0, 0),
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1.5),
                BorderBrush = !note.HasCustomColor && colorIndex == note.Color
                    ? new SolidColorBrush(palette.Ink) { Opacity = 0.55 }
                    : Brushes.Transparent,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = Loc.ColourName(palette.Name),
                Child = dot,
            };
            dot.HorizontalAlignment = HorizontalAlignment.Center;
            dot.VerticalAlignment = VerticalAlignment.Center;
            choice.MouseEnter += (_, _) => choice.Background = WindowBg;
            choice.MouseLeave += (_, _) => choice.Background = Brushes.Transparent;
            choice.MouseLeftButtonUp += (_, e) =>
            {
                note.Color = colorIndex;
                note.CustomColor = null;
                NotesStore.I.Update(note);
                UpdateColourSwatch(note.Palette);
                StyleBody();
                RefreshList();
                _colourPopup.IsOpen = false;
                e.Handled = true;
            };
            paletteRow.Children.Add(choice);
        }

        paletteRow.Children.Add(new Border
        {
            Width = 1,
            Height = 16,
            Margin = new Thickness(7, 0, 7, 0),
            Background = Hairline,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var customSwatch = new Ellipse
        {
            Width = 15,
            Height = 15,
            Fill = UiTheme.ColourSpectrum,
            Stroke = new SolidColorBrush(Colors.White) { Opacity = 0.8 },
            StrokeThickness = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var customChoice = new Border
        {
            Width = 29,
            Height = 29,
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(1.5),
            BorderBrush = note.HasCustomColor
                ? new SolidColorBrush(note.Palette.Ink) { Opacity = 0.55 }
                : Brushes.Transparent,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("Custom colour…", "自定义颜色…"),
            Child = customSwatch,
        };
        customChoice.MouseEnter += (_, _) => customChoice.Background = WindowBg;
        customChoice.MouseLeave += (_, _) => customChoice.Background = Brushes.Transparent;
        customChoice.MouseLeftButtonUp += (_, e) =>
        {
            _colourPopup.IsOpen = false;
            try
            {
                var initial = NoteColor.TryParse(note.CustomColor, out var custom)
                    ? custom
                    : note.Palette.Dash;
                var selected = ColourPickerDialog.Show(this, initial);
                if (selected is { } color)
                {
                    note.CustomColor = NoteColor.ToHex(color);
                    NotesStore.I.Update(note);
                    UpdateColourSwatch(note.Palette);
                    StyleBody();
                    RefreshList();
                }
            }
            catch (Exception ex) { Log($"Custom colour EX {ex}"); }
            e.Handled = true;
        };
        paletteRow.Children.Add(customChoice);

        _colourPopup.Child = new Border
        {
            Background = Surface,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9),
            Margin = new Thickness(0, 0, 0, 7),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.18 },
            Child = paletteRow,
        };
        _colourPopup.PlacementTarget = _colorBtn;
        _colourPopup.Placement = PlacementMode.Top;
        _colourPopup.HorizontalOffset = -210;
        _colourPopup.IsOpen = true;
    }

    void UpdateColourSwatch(NoteColor palette)
    {
        _colorSwatch.Fill = palette.DashB;
        _colorSwatch.Stroke = new SolidColorBrush(palette.Ink) { Opacity = 0.25 };
    }

    static void Log(string s)
    {
        App.ReportError($"[AllNotes] {s}");
    }

    internal void RefreshList()
    {
        _list.Items.Clear();
        string q = _search.Text.Trim();
        var notes = _showArchived
            ? NotesStore.I.Notes.Where(n => n.Archived)
            : NotesStore.I.Active;
        int archived = NotesStore.I.Notes.Count(n => n.Archived);
        _viewToggle.Text = _showArchived
            ? Loc.T("← Active notes", "← 当前便签")
            : $"{Loc.T("Archive", "归档")}  {archived}";

        foreach (var n in notes.OrderBy(n => n.Order))
        {
            if (q.Length > 0 && !(n.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                               || n.Body.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                continue;
            var pal = n.Palette;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2),
                Background = pal.DashB,
            });
            var progress = Tasks.Progress(n.Body, n.UsesMarkdown);
            var title = new TextBlock
            {
                Text = n.DisplayTitle,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = TextInk,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 8, 0),
            };
            Grid.SetColumn(title, 1);
            row.Children.Add(title);
            if (progress is (int done, int total) && total > 0)
            {
                var count = new TextBlock
                {
                    Text = $"{done}/{total}",
                    FontSize = 11,
                    Foreground = MutedInk,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(count, 2);
                row.Children.Add(count);
            }

            var card = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 9, 10, 9),
                Margin = new Thickness(0, 2, 3, 2),
                Child = row,
            };
            var listItem = new ListBoxItem
            {
                Content = card,
                Tag = n,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                FocusVisualStyle = null,
            };
            listItem.Resources[SystemColors.HighlightBrushKey] = Brushes.Transparent;
            listItem.Resources[SystemColors.HighlightTextBrushKey] = TextInk;
            listItem.Selected += (_, _) => card.Background = Selection;
            listItem.Unselected += (_, _) => card.Background = Brushes.Transparent;
            _list.Items.Add(listItem);
        }
        // keep selection on the current note
        if (_current != null)
            foreach (ListBoxItem it in _list.Items)
                if (ReferenceEquals(it.Tag, _current)) { it.IsSelected = true; break; }
    }

    void Select(Note n)
    {
        try
        {
            Save();
            _current = n;
            _titleEdited = false;
            _loading = true;
            _titleBox.Text = n.DisplayTitle;
            _body.Document.Blocks.Clear();
            foreach (var line in n.Body.Split('\n'))
                _body.Document.Blocks.Add(new Paragraph(new Run(line.TrimEnd('\r'))) { Margin = new Thickness(0) });
            StyleBody();
            _loading = false;
            UpdateModeButton();
            ActionLabel(_archiveBtn).Text = n.Archived
                ? Loc.T("Restore", "恢复") : Loc.T("Archive", "归档");
            UpdateColourSwatch(n.Palette);
            _detailEditor.Visibility = Visibility.Visible;
            _emptyState.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Log($"Select EX {ex}"); }
    }

    void ClearDetail()
    {
        _current = null;
        _titleBox.Text = "";
        _body.Document.Blocks.Clear();
        _editingMarkdownParagraph = null;
        ActionLabel(_modeBtn).Text = "";
        ActionLabel(_archiveBtn).Text = Loc.T("Archive", "归档");
        _colorSwatch.Fill = Brushes.Transparent;
        _colorSwatch.Stroke = Hairline;
        _detailEditor.Visibility = Visibility.Collapsed;
        _emptyState.Visibility = Visibility.Visible;
    }

    void StyleBody()
    {
        if (_styling) return;   // re-entrancy guard (formatting can re-fire TextChanged)
        _styling = true;
        try
        {
            var doc = _body.Document;
            var all = new TextRange(doc.ContentStart, doc.ContentEnd);
            all.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
            all.ApplyPropertyValue(TextElement.FontStyleProperty, FontStyles.Normal);
            all.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Segoe UI, Microsoft YaHei UI"));
            all.ApplyPropertyValue(TextElement.ForegroundProperty, TextInk);
            all.ApplyPropertyValue(TextElement.FontSizeProperty, 14.0);
            all.ApplyPropertyValue(Inline.TextDecorationsProperty, null);
            all.ApplyPropertyValue(TextElement.BackgroundProperty, Brushes.Transparent);
            var editingParagraph = CurrentEditingParagraph();
            _editingMarkdownParagraph = editingParagraph;
            if (_current?.UsesMarkdown == true)
                Markdown.StyleDocument(doc, _current?.Palette ?? NoteColor.At(0), 14.0,
                    editingParagraph);
            else
                Markdown.RestoreSourceMarkers(doc);
        }
        catch (Exception ex)
        {
            Log($"StyleBody EX {ex}");
        }
        finally
        {
            _styling = false;
        }
    }

    Paragraph? CurrentEditingParagraph()
        => _body.IsKeyboardFocusWithin
            ? Markdown.ParagraphAt(_body.CaretPosition)
            : null;

    void StyleBodyIfLineChanged()
    {
        if (!ReferenceEquals(CurrentEditingParagraph(), _editingMarkdownParagraph))
            StyleBody();
    }

    void ToggleTextMode()
    {
        if (_current == null) return;
        Save();
        _current.MarkdownEnabled = !_current.UsesMarkdown;
        _body.Focus();
        StyleBody();
        NotesStore.I.Update(_current);
        _loading = true;
        _titleBox.Text = _current.DisplayTitle;
        _loading = false;
        UpdateModeButton();
    }

    void UpdateModeButton()
    {
        if (_current == null) return;
        bool markdown = _current.UsesMarkdown;
        ActionLabel(_modeBtn).Text = markdown ? "MD" : "TXT";
        _modeBtn.ToolTip = markdown
            ? Loc.T("Markdown mode. Click for plain text.", "Markdown 模式，点击切换到纯文本。")
            : Loc.T("Plain text mode. Click for Markdown.", "纯文本模式，点击切换到 Markdown。");
    }


    void Save()
    {
        if (_current == null) return;
        if (_titleEdited)
        {
            var title = _titleBox.Text.Trim();
            if (title.Length == 0)
            {
                _current.HasCustomTitle = false;
                _current.DeriveTitle();
            }
            else
            {
                _current.Title = title;
                _current.HasCustomTitle = true;
            }
        }
        var sb = new System.Text.StringBuilder();
        foreach (Block b in _body.Document.Blocks)
            if (b is Paragraph p)
                sb.Append(Markdown.SourceText(p)).Append('\n');
        _current.Body = sb.ToString().TrimEnd('\n');
        NotesStore.I.Update(_current);
        _loading = true;
        _titleBox.Text = _current.DisplayTitle;
        _loading = false;
    }
}
