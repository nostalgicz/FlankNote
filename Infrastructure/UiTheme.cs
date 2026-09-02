using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;

namespace FlankNote;

/// <summary>Shared visual tokens for application-owned windows.</summary>
static class UiTheme
{
    static SolidColorBrush Make(uint rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb(
            (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }

    public static readonly SolidColorBrush Window = Make(0xF5F4F0);
    public static readonly SolidColorBrush Surface = Make(0xFFFFFF);
    public static readonly SolidColorBrush Text = Make(0x262626);
    public static readonly SolidColorBrush Muted = Make(0x77746E);
    public static readonly SolidColorBrush Hairline = Make(0xDAD7CF);
    public static readonly SolidColorBrush Selection = Make(0xDDD9CF);
    public static readonly SolidColorBrush Accent = Make(0x3E6258);
    public static readonly SolidColorBrush Danger = Make(0xB8473F);

    public static readonly FontFamily Font = new("Segoe UI, Microsoft YaHei UI");
    public static readonly FontFamily Symbols = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly CornerRadius WindowRadius = new(12);
    public static readonly CornerRadius ControlRadius = new(7);

    /// <summary>
    /// ScrollViewer templates assign the platform scrollbar width as a local
    /// value, which outranks an implicit Style setter. Apply the compact metric
    /// after each real ScrollBar is created so the requested width is effective.
    /// </summary>
    public static void ApplySlimMetric(ScrollBar bar)
    {
        if (bar.Orientation == System.Windows.Controls.Orientation.Vertical)
        {
            bar.MinWidth = 3;
            bar.Width = 3;
            bar.MaxWidth = 3;
        }
        else
        {
            bar.MinHeight = 3;
            bar.Height = 3;
            bar.MaxHeight = 3;
        }
    }

    /// <summary>Application-owned title bar shared by every ordinary window.</summary>
    public static UIElement WithWindowChrome(Window window, string title, UIElement body)
    {
        window.WindowStyle = WindowStyle.None;
        // A rounded child alone is insufficient: an opaque HWND still paints a
        // rectangular background behind the clipped shell. Make the client
        // surface genuinely transparent so the desktop is visible at all four
        // corners and only the rounded shell remains opaque.
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 40,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = WindowRadius,
            UseAeroCaptionButtons = false,
        });

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var bar = new Grid { Background = Window };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Font,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = Text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 8, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 7, 5),
        };
        var minimize = ChromeButton("\uE921", false);
        var maximize = ChromeButton("\uE922", false);
        var close = ChromeButton("\uE8BB", true);
        minimize.MouseLeftButtonUp += (_, _) => window.WindowState = WindowState.Minimized;
        maximize.MouseLeftButtonUp += (_, _) =>
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
        close.MouseLeftButtonUp += (_, _) => window.Close();
        controls.Children.Add(minimize);
        controls.Children.Add(maximize);
        controls.Children.Add(close);
        Grid.SetColumn(controls, 1);
        bar.Children.Add(controls);

        var lineBrush = new SolidColorBrush(Hairline.Color) { Opacity = 0.72 };
        var line = new Border
        {
            Height = 1,
            Background = lineBrush,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumnSpan(line, 2);
        bar.Children.Add(line);
        Grid.SetRow(bar, 0);
        root.Children.Add(bar);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var shell = new Border
        {
            Background = Window,
            BorderBrush = Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = WindowRadius,
            SnapsToDevicePixels = true,
            Child = root,
        };

        void RefreshOutline()
        {
            bool maximized = window.WindowState == WindowState.Maximized;
            shell.CornerRadius = maximized ? new CornerRadius(0) : WindowRadius;
            shell.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
            shell.Clip = maximized || shell.ActualWidth <= 0 || shell.ActualHeight <= 0
                ? null
                : new RectangleGeometry(
                    new Rect(0, 0, shell.ActualWidth, shell.ActualHeight),
                    WindowRadius.TopLeft,
                    WindowRadius.TopLeft);
            if (maximize.Child is TextBlock glyph)
                glyph.Text = maximized ? "\uE923" : "\uE922";
        }
        shell.SizeChanged += (_, _) => RefreshOutline();
        window.StateChanged += (_, _) =>
        {
            RefreshOutline();
        };
        return shell;
    }

    static Border ChromeButton(string glyph, bool danger)
    {
        var button = new Border
        {
            Width = 32,
            Height = 28,
            Margin = new Thickness(2, 0, 0, 0),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = Symbols,
                FontSize = 10,
                Foreground = danger ? Danger : Muted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        button.MouseEnter += (_, _) => button.Background = danger
            ? new SolidColorBrush(Danger.Color) { Opacity = 0.12 }
            : Selection;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        return button;
    }

    public static void StyleComboBox(ComboBox combo)
    {
        combo.Background = Brushes.Transparent;
        combo.BorderThickness = new Thickness(0);
        combo.Padding = new Thickness(0);
        combo.MinHeight = 38;
        combo.VerticalContentAlignment = VerticalAlignment.Center;
        combo.ItemContainerStyle = ComboItemStyle();

        var template = new ControlTemplate(typeof(ComboBox));
        var root = new FrameworkElementFactory(typeof(Grid));

        var shell = new FrameworkElementFactory(typeof(ToggleButton));
        shell.Name = "PART_ToggleButton";
        shell.SetValue(ToggleButton.BackgroundProperty, Brushes.Transparent);
        shell.SetValue(ToggleButton.BorderThicknessProperty, new Thickness(0));
        shell.SetValue(ToggleButton.PaddingProperty, new Thickness(0));
        shell.SetValue(ToggleButton.FocusableProperty, false);
        shell.SetValue(ToggleButton.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch);
        shell.SetValue(ToggleButton.VerticalContentAlignmentProperty, VerticalAlignment.Stretch);
        shell.SetBinding(ToggleButton.IsCheckedProperty,
            new Binding("IsDropDownOpen")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                Mode = BindingMode.TwoWay,
            });

        var shellTemplate = new ControlTemplate(typeof(ToggleButton));
        var shellSurface = new FrameworkElementFactory(typeof(Border));
        shellSurface.SetValue(Border.BackgroundProperty, Surface);
        shellSurface.SetValue(Border.BorderBrushProperty, Hairline);
        shellSurface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        shellSurface.SetValue(Border.CornerRadiusProperty, ControlRadius);
        shellSurface.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var shellContent = new FrameworkElementFactory(typeof(ContentPresenter));
        shellContent.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        shellContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        shellContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        shellSurface.AppendChild(shellContent);

        var layout = new FrameworkElementFactory(typeof(Grid));
        layout.SetValue(FrameworkElement.MarginProperty, new Thickness(11, 0, 9, 0));

        var selected = new FrameworkElementFactory(typeof(ContentPresenter));
        selected.SetValue(ContentPresenter.ContentSourceProperty, "SelectionBoxItem");
        selected.SetValue(ContentPresenter.ContentTemplateProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemTemplateProperty));
        selected.SetValue(ContentPresenter.ContentStringFormatProperty,
            new TemplateBindingExtension(ComboBox.SelectionBoxItemStringFormatProperty));
        selected.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        selected.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        selected.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        selected.SetValue(TextElement.FontFamilyProperty,
            new TemplateBindingExtension(Control.FontFamilyProperty));
        selected.SetValue(TextElement.FontSizeProperty,
            new TemplateBindingExtension(Control.FontSizeProperty));
        selected.SetValue(TextElement.ForegroundProperty,
            new TemplateBindingExtension(Control.ForegroundProperty));
        layout.AppendChild(selected);

        var arrow = new FrameworkElementFactory(typeof(TextBlock));
        arrow.SetValue(TextBlock.TextProperty, "\uE70D");
        arrow.SetValue(TextBlock.FontFamilyProperty, Symbols);
        arrow.SetValue(TextBlock.FontSizeProperty, 11.5);
        arrow.SetValue(TextBlock.ForegroundProperty, Muted);
        arrow.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        arrow.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Right);
        layout.AppendChild(arrow);
        shell.AppendChild(layout);
        shellTemplate.VisualTree = shellSurface;
        shell.SetValue(ToggleButton.TemplateProperty, shellTemplate);
        root.AppendChild(shell);

        var popup = new FrameworkElementFactory(typeof(Popup));
        popup.Name = "PART_Popup";
        popup.SetValue(Popup.PlacementProperty, PlacementMode.Bottom);
        popup.SetValue(Popup.AllowsTransparencyProperty, true);
        popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
        popup.SetValue(Popup.StaysOpenProperty, false);
        popup.SetBinding(Popup.PlacementTargetProperty,
            new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        popup.SetBinding(Popup.IsOpenProperty,
            new Binding("IsDropDownOpen") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        popup.SetBinding(FrameworkElement.MinWidthProperty,
            new Binding("ActualWidth") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

        var menu = new FrameworkElementFactory(typeof(Border));
        menu.SetValue(Border.BackgroundProperty, Surface);
        menu.SetValue(Border.BorderBrushProperty, Hairline);
        menu.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        menu.SetValue(Border.CornerRadiusProperty, ControlRadius);
        menu.SetValue(Border.PaddingProperty, new Thickness(4));
        menu.SetValue(Border.MarginProperty, new Thickness(0, 5, 0, 0));
        menu.SetValue(Border.EffectProperty, new DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 3,
            Opacity = 0.16,
        });
        var scroll = new FrameworkElementFactory(typeof(ScrollViewer));
        scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        scroll.SetValue(ScrollViewer.CanContentScrollProperty, true);
        scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        menu.AppendChild(scroll);
        popup.AppendChild(menu);
        root.AppendChild(popup);

        template.VisualTree = root;
        combo.Template = template;
    }

    static Style ComboItemStyle()
    {
        var style = new Style(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Text));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(9, 8, 9, 8)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.MinHeightProperty, 34.0));

        var template = new ControlTemplate(typeof(ComboBoxItem));
        var item = new FrameworkElementFactory(typeof(Border));
        item.SetValue(Border.CornerRadiusProperty, ControlRadius);
        item.SetBinding(Border.BackgroundProperty,
            new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        item.SetBinding(Border.BorderBrushProperty,
            new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        item.SetBinding(Border.BorderThicknessProperty,
            new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentSourceProperty, "Content");
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        content.SetBinding(ContentPresenter.ContentTemplateProperty,
            new Binding("ContentTemplate") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        content.SetBinding(ContentPresenter.ContentStringFormatProperty,
            new Binding("ContentStringFormat") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        content.SetBinding(FrameworkElement.MarginProperty,
            new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        item.AppendChild(content);
        template.VisualTree = item;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));

        var hover = new Trigger { Property = ComboBoxItem.IsHighlightedProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, Selection));
        style.Triggers.Add(hover);
        var selected = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Accent.Color) { Opacity = 0.12 }));
        style.Triggers.Add(selected);
        return style;
    }

    public static double EaseOut(double progress)
    {
        double p = Math.Clamp(progress, 0, 1);
        return 1 - Math.Pow(1 - p, 3);
    }

    /// <summary>Unit spring with zero initial velocity and configurable damping.</summary>
    public static double Spring(double progress, double damping = 0.82)
    {
        double p = Math.Clamp(progress, 0, 1);
        double zeta = Math.Clamp(damping, 0.05, 0.99);
        const double omega = 10;
        double root = Math.Sqrt(1 - zeta * zeta);
        double damped = omega * root;
        return 1 - Math.Exp(-zeta * omega * p)
            * (Math.Cos(damped * p) + zeta / root * Math.Sin(damped * p));
    }
}
