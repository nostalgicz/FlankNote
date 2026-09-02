using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Data;

namespace FlankNote;

/// <summary>Settings presented with the same restrained visual system as the library.</summary>
class SettingsWindow : Window
{
    const double HeaderActionWidth = 85;
    const double HeaderActionHeight = 30;
    readonly Slider _font = new() { Minimum = 10, Maximum = 20, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly Slider _wake = new() { Minimum = 16, Maximum = 160, TickFrequency = 8, IsSnapToTickEnabled = true };
    readonly Slider _deckSize = new() { Minimum = 70, Maximum = 180, TickFrequency = 10, IsSnapToTickEnabled = true };
    readonly ComboBox _display = new();
    bool _markdown;
    bool _overlay;
    bool _launchAtLogin;
    bool _keepDeckOpen;
    bool _openOnHover;
    TextBlock? _tabsMark;
    TextBlock? _chipsMark;
    TextBlock? _leftMark;
    TextBlock? _rightMark;
    TextBlock? _englishMark;
    TextBlock? _chineseMark;
    readonly Border _updateBadge = new();
    readonly TextBlock _updateLabel = new();

    public SettingsWindow()
    {
        Title = Loc.T("Settings", "设置");
        Width = 450;
        Height = 560;
        MinWidth = 420;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = UiTheme.Window;
        Foreground = UiTheme.Text;
        FontFamily = UiTheme.Font;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _font.Value = Settings.NoteFontSize;
        _wake.Value = Settings.WakeDistance;
        _deckSize.Value = Settings.DeckScale * 100;
        _markdown = Settings.Markdown;
        _overlay = Settings.OverlayFullscreen;
        _launchAtLogin = StartupRegistration.IsEnabled;
        _keepDeckOpen = Settings.KeepDeckOpen;
        _openOnHover = Settings.OpenOnHover;

        var content = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        content.Children.Add(BuildHeading());

        var fontValue = ValueText($"{Settings.NoteFontSize:0} pt");
        _font.ValueChanged += (_, _) => fontValue.Text = $"{_font.Value:0} pt";
        content.Children.Add(Section(Loc.T("NOTES", "便签"), Card(SettingSlider(Loc.T("Text size", "文字大小"), fontValue, _font))));

        var wakeValue = ValueText($"{Settings.WakeDistance:0} px");
        _wake.ValueChanged += (_, _) => wakeValue.Text = $"{_wake.Value:0} px";
        content.Children.Add(Section(Loc.T("EDGE DETECTION", "边缘检测"), Card(SettingSlider(Loc.T("Wake distance", "唤醒距离"), wakeValue, _wake))));

        var deckRows = new StackPanel();
        deckRows.Children.Add(ChoiceRow(Loc.T("Labelled tabs", "便签卡"), false, out _tabsMark));
        deckRows.Children.Add(Divider());
        deckRows.Children.Add(ChoiceRow(Loc.T("Colour chips", "标签条"), true, out _chipsMark));
        content.Children.Add(Section(Loc.T("DECK STYLE", "纸签栏样式"), Card(deckRows, padding: 3)));

        var deckSizeValue = ValueText($"{Settings.DeckScale * 100:0}%");
        _deckSize.ValueChanged += (_, _) =>
        {
            deckSizeValue.Text = $"{_deckSize.Value:0}%";
            Settings.DeckScale = _deckSize.Value / 100;
            App.Deck.ApplySettings();
        };
        content.Children.Add(Section(Loc.T("DECK SIZE", "纸签栏尺寸"),
            Card(SettingSlider(Loc.T("Scale", "缩放"), deckSizeValue, _deckSize))));

        var edgeRows = new StackPanel();
        edgeRows.Children.Add(EdgeRow(Loc.T("Left edge", "左侧"), true, out _leftMark));
        edgeRows.Children.Add(Divider());
        edgeRows.Children.Add(EdgeRow(Loc.T("Right edge", "右侧"), false, out _rightMark));
        content.Children.Add(Section(Loc.T("DOCK SIDE", "停靠位置"), Card(edgeRows, padding: 3)));

        var displays = DisplayService.Options();
        _display.ItemsSource = displays;
        _display.ItemTemplate = DisplayItemTemplate();
        _display.SelectedItem = displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, Settings.DisplayName, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, System.Windows.Forms.Screen.PrimaryScreen?.DeviceName, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault();
        _display.FontFamily = UiTheme.Font;
        _display.FontSize = 13;
        _display.Foreground = UiTheme.Text;
        UiTheme.StyleComboBox(_display);
        _display.SelectionChanged += (_, _) =>
        {
            if (_display.SelectedItem is not DisplayOption option) return;
            if (!string.Equals(Settings.DisplayName, option.DeviceName, StringComparison.OrdinalIgnoreCase)
                && App.OpenNote is { } openNote)
                openNote.CloseImmediately();
            Settings.DisplayName = option.DeviceName;
            NotesStore.I.Save();
            App.Deck.ApplySettings();
        };
        content.Children.Add(Section(Loc.T("DISPLAY", "显示器"), Card(_display)));

        var options = new StackPanel();
        options.Children.Add(ToggleRow(Loc.T("Keep deck open", "始终展开纸签栏"), () => _keepDeckOpen, value =>
        {
            _keepDeckOpen = value;
            Settings.KeepDeckOpen = value;
            App.Deck.ApplySettings();
        }));
        options.Children.Add(Divider());
        options.Children.Add(ToggleRow(Loc.T("Open notes on hover", "鼠标悬停打开便签"), () => _openOnHover, value =>
        {
            _openOnHover = value;
            Settings.OpenOnHover = value;
        }));
        options.Children.Add(Divider());
        options.Children.Add(ToggleRow(Loc.T("Markdown styling", "Markdown 样式"), () => _markdown, value => _markdown = value));
        options.Children.Add(Divider());
        options.Children.Add(ToggleRow(Loc.T("Show over full-screen apps", "覆盖全屏应用"), () => _overlay, value => _overlay = value));
        options.Children.Add(Divider());
        options.Children.Add(ToggleRow(Loc.T("Launch at sign-in", "开机自启"), () => _launchAtLogin, value => _launchAtLogin = value));
        content.Children.Add(Section(Loc.T("BEHAVIOUR", "行为"), Card(options, padding: 3)));

        var languageRows = new StackPanel();
        languageRows.Children.Add(LanguageRow("简体中文", "zh", out _chineseMark));
        languageRows.Children.Add(Divider());
        languageRows.Children.Add(LanguageRow("English", "en", out _englishMark));
        content.Children.Add(Section(Loc.T("LANGUAGE", "语言"), Card(languageRows, padding: 3)));

        var footer = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var updates = SecondaryActionButton(Loc.T("Check for Updates…", "检查更新…"));
        updates.HorizontalAlignment = HorizontalAlignment.Left;
        updates.MouseLeftButtonUp += (_, _) => App.OpenUpdates();
        footer.Children.Add(updates);

        var done = ActionButton(Loc.T("Done", "完成"));
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.MouseLeftButtonUp += (_, _) => SaveAndClose();
        Grid.SetColumn(done, 1);
        footer.Children.Add(done);
        content.Children.Add(footer);

        Content = UiTheme.WithWindowChrome(this, Title, ScrollHost(content));

        Closed += (_, _) =>
        {
            App.UpdateStateChanged -= OnUpdateStateChanged;
            PersistSettings();
            App.SettingsWin = null;
        };
        App.UpdateStateChanged += OnUpdateStateChanged;
        RefreshDeckChoice();
        RefreshEdgeChoice();
        RefreshLanguageChoice();
        RefreshUpdateHeader();
        DisplayService.CenterOnSelected(this);
        Show();
        Activate();
    }

    UIElement BuildHeading()
    {
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 22) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var copy = new StackPanel();
        copy.Children.Add(new TextBlock
        {
            Text = Loc.T("Settings", "设置"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.Text,
        });
        copy.Children.Add(new TextBlock
        {
            Text = Loc.T("Deck behaviour and note appearance", "纸签栏行为与便签外观"),
            FontSize = 12,
            Foreground = UiTheme.Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        });
        heading.Children.Add(copy);

        var links = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 0, 0, 0),
        };
        BuildUpdateBadge();
        links.Children.Add(_updateBadge);
        links.Children.Add(BuildGitHubLink());
        Grid.SetColumn(links, 1);
        heading.Children.Add(links);
        return heading;
    }

    void BuildUpdateBadge()
    {
        _updateLabel.FontSize = 11.5;
        _updateLabel.FontWeight = FontWeights.Medium;
        _updateLabel.Foreground = UiTheme.Accent;
        _updateLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _updateLabel.VerticalAlignment = VerticalAlignment.Center;

        _updateBadge.Width = HeaderActionWidth;
        _updateBadge.Height = HeaderActionHeight;
        _updateBadge.Background = AccentWash(0.1);
        _updateBadge.BorderBrush = AccentWash(0.3);
        _updateBadge.BorderThickness = new Thickness(1);
        _updateBadge.CornerRadius = UiTheme.ControlRadius;
        _updateBadge.Padding = new Thickness(8, 0, 8, 0);
        _updateBadge.Margin = new Thickness(0, 0, 0, 5);
        _updateBadge.HorizontalAlignment = HorizontalAlignment.Right;
        _updateBadge.SnapsToDevicePixels = true;
        _updateBadge.Cursor = Cursors.Hand;
        _updateBadge.Visibility = Visibility.Collapsed;
        _updateBadge.ToolTip = Loc.T("Open available update", "打开可用更新");
        _updateBadge.Child = _updateLabel;
        _updateBadge.MouseEnter += (_, _) => _updateBadge.Background = AccentWash(0.17);
        _updateBadge.MouseLeave += (_, _) => _updateBadge.Background = AccentWash(0.1);
        _updateBadge.MouseLeftButtonUp += (_, _) => App.OpenUpdates();
    }

    static Border BuildGitHubLink()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new Path
        {
            Data = Geometry.Parse("M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.03.08-2.13 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27s1.36.09 2 .27c1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.93.08 2.13.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0 0 16 8c0-4.42-3.58-8-8-8Z"),
            Fill = UiTheme.Text,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = "GitHub",
            FontSize = 11.5,
            Foreground = UiTheme.Text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 2, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = "↗",
            FontSize = 12,
            Foreground = UiTheme.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var link = new Border
        {
            Width = HeaderActionWidth,
            Height = HeaderActionHeight,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            CornerRadius = UiTheme.ControlRadius,
            Background = UiTheme.Surface,
            BorderBrush = UiTheme.Hairline,
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Cursor = Cursors.Hand,
            ToolTip = Loc.T("Open GitHub repository", "打开 GitHub 仓库"),
            Child = row,
        };
        link.MouseEnter += (_, _) => link.Background = UiTheme.Selection;
        link.MouseLeave += (_, _) => link.Background = UiTheme.Surface;
        link.MouseLeftButtonUp += (_, _) => App.OpenRepository();
        return link;
    }

    static SolidColorBrush AccentWash(double opacity) =>
        new(UiTheme.Accent.Color) { Opacity = opacity };

    void OnUpdateStateChanged() => Dispatcher.BeginInvoke(RefreshUpdateHeader);

    void RefreshUpdateHeader()
    {
        var release = App.LatestRelease;
        bool available = release != null && GitHubUpdateService.IsNewer(release.TagName);
        _updateBadge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (available)
            _updateLabel.Text = Loc.T($"New {release!.TagName}", $"新版本 {release.TagName}");
    }

    static FrameworkElement ScrollHost(UIElement content)
    {
        return new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        };
    }

    static Border Divider() => new()
    {
        Height = 1,
        Background = UiTheme.Hairline,
        Margin = new Thickness(10, 0, 10, 0),
    };

    static TextBlock ValueText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = UiTheme.Muted,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    static DataTemplate DisplayItemTemplate()
    {
        var template = new DataTemplate(typeof(DisplayOption));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(DisplayOption.Label)));
        label.SetValue(TextBlock.FontFamilyProperty, UiTheme.Font);
        label.SetValue(TextBlock.FontSizeProperty, 13.0);
        label.SetValue(TextBlock.ForegroundProperty, UiTheme.Text);
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = label;
        return template;
    }

    static FrameworkElement Section(string title, UIElement body)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 17) };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = UiTheme.Muted,
            Margin = new Thickness(2, 0, 0, 7),
        });
        stack.Children.Add(body);
        return stack;
    }

    static Border Card(UIElement child, double padding = 12) => new()
    {
        Background = UiTheme.Surface,
        BorderBrush = UiTheme.Hairline,
        BorderThickness = new Thickness(1),
        CornerRadius = UiTheme.WindowRadius,
        Padding = new Thickness(padding),
        Child = child,
    };

    static UIElement SettingSlider(string label, TextBlock value, Slider slider)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { Text = label, FontSize = 13, Foreground = UiTheme.Text };
        Grid.SetColumn(value, 1);
        slider.Margin = new Thickness(0, 9, 0, 0);
        slider.Foreground = UiTheme.Accent;
        Grid.SetRow(slider, 1);
        Grid.SetColumnSpan(slider, 2);
        grid.Children.Add(name);
        grid.Children.Add(value);
        grid.Children.Add(slider);
        return grid;
    }

    Border ChoiceRow(string label, bool chips, out TextBlock mark)
    {
        mark = new TextBlock
        {
            FontSize = 13,
            Width = 22,
            Foreground = UiTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mark);
        row.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = UiTheme.Text, VerticalAlignment = VerticalAlignment.Center });
        var shell = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brushes.Transparent,
            CornerRadius = UiTheme.ControlRadius,
            Cursor = Cursors.Hand,
            Child = row,
        };
        shell.MouseEnter += (_, _) => shell.Background = UiTheme.Window;
        shell.MouseLeave += (_, _) => shell.Background = Brushes.Transparent;
        shell.MouseLeftButtonUp += (_, _) =>
        {
            Settings.DeckStyle = chips ? "chips" : "tabs";
            RefreshDeckChoice();
            App.Deck.ApplySettings();
        };
        return shell;
    }

    Border LanguageRow(string label, string language, out TextBlock mark)
    {
        mark = new TextBlock
        {
            FontSize = 13,
            Width = 22,
            Foreground = UiTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mark);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = UiTheme.Text,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var shell = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brushes.Transparent,
            CornerRadius = UiTheme.ControlRadius,
            Cursor = Cursors.Hand,
            Child = row,
        };
        shell.MouseEnter += (_, _) => shell.Background = UiTheme.Window;
        shell.MouseLeave += (_, _) => shell.Background = Brushes.Transparent;
        shell.MouseLeftButtonUp += (_, _) => ChangeLanguage(language);
        return shell;
    }

    Border EdgeRow(string label, bool left, out TextBlock mark)
    {
        mark = new TextBlock
        {
            FontSize = 13,
            Width = 22,
            Foreground = UiTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mark);
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = UiTheme.Text,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var shell = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brushes.Transparent,
            CornerRadius = UiTheme.ControlRadius,
            Cursor = Cursors.Hand,
            Child = row,
        };
        shell.MouseEnter += (_, _) => shell.Background = UiTheme.Window;
        shell.MouseLeave += (_, _) => shell.Background = Brushes.Transparent;
        shell.MouseLeftButtonUp += (_, _) =>
        {
            App.Deck.SwitchEdge(left);
            RefreshEdgeChoice();
        };
        return shell;
    }

    static Border ToggleRow(string label, Func<bool> get, Action<bool> set)
    {
        var knob = new Ellipse { Width = 13, Height = 13, Fill = UiTheme.Surface };
        var track = new Border
        {
            Width = 32,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(2),
            Child = knob,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void Render()
        {
            bool on = get();
            track.Background = on ? UiTheme.Accent : UiTheme.Hairline;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = UiTheme.Text, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        var shell = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brushes.Transparent,
            CornerRadius = UiTheme.ControlRadius,
            Cursor = Cursors.Hand,
            Child = grid,
        };
        shell.MouseEnter += (_, _) => shell.Background = UiTheme.Window;
        shell.MouseLeave += (_, _) => shell.Background = Brushes.Transparent;
        shell.MouseLeftButtonUp += (_, _) => { set(!get()); Render(); };
        Render();
        return shell;
    }

    static Border ActionButton(string label) => new()
    {
        Background = UiTheme.Text,
        CornerRadius = UiTheme.ControlRadius,
        Padding = new Thickness(17, 7, 17, 7),
        Cursor = Cursors.Hand,
        Child = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = UiTheme.Surface,
        },
    };

    static Border SecondaryActionButton(string label)
    {
        var button = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = UiTheme.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = UiTheme.ControlRadius,
            Padding = new Thickness(15, 6, 15, 6),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                Foreground = UiTheme.Text,
            },
        };
        button.MouseEnter += (_, _) => button.Background = UiTheme.Surface;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    void RefreshDeckChoice()
    {
        bool chips = Settings.DeckStyle == "chips";
        if (_tabsMark != null) _tabsMark.Text = chips ? "" : "●";
        if (_chipsMark != null) _chipsMark.Text = chips ? "●" : "";
    }

    void RefreshEdgeChoice()
    {
        if (_leftMark != null) _leftMark.Text = Settings.EdgeLeft ? "●" : "";
        if (_rightMark != null) _rightMark.Text = Settings.EdgeLeft ? "" : "●";
    }

    void RefreshLanguageChoice()
    {
        if (_englishMark != null) _englishMark.Text = Settings.Language == "en" ? "●" : "";
        if (_chineseMark != null) _chineseMark.Text = Settings.Language == "zh" ? "●" : "";
    }

    void PersistSettings()
    {
        Settings.NoteFontSize = _font.Value;
        Settings.WakeDistance = _wake.Value;
        Settings.DeckScale = _deckSize.Value / 100;
        Settings.KeepDeckOpen = _keepDeckOpen;
        Settings.OpenOnHover = _openOnHover;
        Settings.Markdown = _markdown;
        Settings.OverlayFullscreen = _overlay;
        StartupRegistration.SetEnabled(_launchAtLogin);
        NotesStore.I.Save();
        App.Deck.ApplySettings();
        App.Deck.ApplyOverlay();
        App.OpenNote?.ApplySettings();
    }

    void ChangeLanguage(string language)
    {
        if (Settings.Language == language) return;
        PersistSettings();
        Settings.Language = language;
        NotesStore.I.Save();
        App.Tray?.RefreshMenu();
        Close();
        App.SettingsWin = new SettingsWindow();
    }

    void SaveAndClose()
    {
        PersistSettings();
        Close();
    }
}
