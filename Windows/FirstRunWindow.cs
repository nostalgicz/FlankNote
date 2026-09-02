using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlankNote;

/// <summary>Small first-run setup for the settings needed before the deck is useful.</summary>
class FirstRunWindow : Window
{
    readonly ComboBox _display = new();
    readonly Border _leftChoice;
    readonly Border _rightChoice;
    readonly TextBlock _leftMark;
    readonly TextBlock _rightMark;
    string _language;
    bool _edgeLeft;

    public FirstRunWindow()
    {
        _language = Settings.Language is "zh" or "en" ? Settings.Language : "en";
        _edgeLeft = Settings.EdgeLeft;

        Title = Loc.T("Welcome to FlankNote", "欢迎使用 FlankNote");
        Width = 470;
        Height = 580;
        MinWidth = 430;
        MinHeight = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = UiTheme.Window;
        Foreground = UiTheme.Text;
        FontFamily = UiTheme.Font;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var content = new StackPanel { Margin = new Thickness(28, 24, 28, 24) };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Welcome to FlankNote", "欢迎使用 FlankNote"),
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Set up the deck before you start writing.", "先设置纸签栏，然后开始记录。"),
            FontSize = 12.5,
            Foreground = UiTheme.Muted,
            Margin = new Thickness(0, 4, 0, 23),
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.T(
                "FlankNote stays quietly at the edge of your screen, ready when an idea arrives. Choose the language, display and edge that feel most natural to you; you can change them later in Settings.",
                "FlankNote 会吸附在屏幕边缘，随时记录突然出现的想法。请选择你习惯的语言、显示器和停靠位置，之后也可以在设置中修改和更精细的调整。"
                ),
            FontSize = 12.5,
            Foreground = UiTheme.Muted,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, -10, 0, 24),
        });

        var languageRows = new StackPanel { Orientation = Orientation.Horizontal };
        var zh = LanguageChoice("简体中文", "zh");
        var en = LanguageChoice("English", "en");
        languageRows.Children.Add(zh);
        languageRows.Children.Add(en);
        content.Children.Add(Section(Loc.T("LANGUAGE", "语言"), Card(languageRows, padding: 4)));

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
        content.Children.Add(Section(Loc.T("DISPLAY", "显示器"), Card(_display)));

        _leftChoice = EdgeChoice(Loc.T("Left edge", "左侧"), true, out _leftMark);
        _rightChoice = EdgeChoice(Loc.T("Right edge", "右侧"), false, out _rightMark);
        var edgeRows = new StackPanel { Orientation = Orientation.Horizontal };
        edgeRows.Children.Add(_leftChoice);
        edgeRows.Children.Add(_rightChoice);
        content.Children.Add(Section(Loc.T("DOCK SIDE", "停靠位置"), Card(edgeRows, padding: 4)));
        RefreshEdgeChoice();

        var finish = new Border
        {
            Background = UiTheme.Text,
            CornerRadius = UiTheme.ControlRadius,
            Padding = new Thickness(17, 9, 17, 9),
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = Loc.T("Finish setup", "完成设置"),
                FontSize = 12.5,
                FontWeight = FontWeights.Medium,
                Foreground = UiTheme.Surface,
            },
        };
        finish.MouseLeftButtonUp += (_, _) => Finish();
        content.Children.Add(finish);

        Content = UiTheme.WithWindowChrome(this, Title, content);
        DisplayService.CenterOnSelected(this);

        Show();

        // Show() returning means the one-time welcome opened successfully. Save
        // immediately, before the dispatcher can process a user close action.
        Settings.FirstRunCompleted = true;
        NotesStore.I.Save();
        Activate();
    }

    Border LanguageChoice(string label, string language)
    {
        var mark = new TextBlock
        {
            Text = _language == language ? "●" : "",
            FontSize = 12,
            Foreground = UiTheme.Accent,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
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
            Padding = new Thickness(10, 9, 12, 9),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            CornerRadius = UiTheme.ControlRadius,
            Cursor = Cursors.Hand,
            Child = row,
            Tag = (language, mark),
        };
        shell.MouseEnter += (_, _) => shell.Background = UiTheme.Window;
        shell.MouseLeave += (_, _) => shell.Background = Brushes.Transparent;
        shell.MouseLeftButtonUp += (_, _) =>
        {
            _language = language;
            if (shell.Parent is Panel parent)
                foreach (var candidate in parent.Children.OfType<Border>())
                    if (candidate.Tag is ValueTuple<string, TextBlock> choice)
                        choice.Item2.Text = _language == choice.Item1 ? "●" : "";
        };
        return shell;
    }

    Border EdgeChoice(string label, bool left, out TextBlock mark)
    {
        mark = new TextBlock
        {
            FontSize = 12,
            Width = 18,
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
            Padding = new Thickness(10, 9, 12, 9),
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            CornerRadius = UiTheme.ControlRadius,
            Cursor = Cursors.Hand,
            Child = row,
        };
        shell.MouseEnter += (_, _) => shell.Background = UiTheme.Window;
        shell.MouseLeave += (_, _) => shell.Background = Brushes.Transparent;
        shell.MouseLeftButtonUp += (_, _) =>
        {
            _edgeLeft = left;
            RefreshEdgeChoice();
        };
        return shell;
    }

    void RefreshEdgeChoice()
    {
        _leftMark.Text = _edgeLeft ? "●" : "";
        _rightMark.Text = _edgeLeft ? "" : "●";
    }

    void Finish()
    {
        Settings.Language = _language;
        Settings.EdgeLeft = _edgeLeft;
        if (_display.SelectedItem is DisplayOption option)
            Settings.DisplayName = option.DeviceName;
        NotesStore.I.Save();
        App.Tray?.RefreshMenu();
        App.Deck.ApplySettings();
        Close();
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

    static DataTemplate DisplayItemTemplate()
    {
        var template = new DataTemplate(typeof(DisplayOption));
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(DisplayOption.Label)));
        label.SetValue(TextBlock.FontFamilyProperty, UiTheme.Font);
        label.SetValue(TextBlock.FontSizeProperty, 13.0);
        label.SetValue(TextBlock.ForegroundProperty, UiTheme.Text);
        label.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = label;
        return template;
    }
}
