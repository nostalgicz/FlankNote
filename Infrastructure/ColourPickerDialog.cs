using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace FlankNote;

/// <summary>Application-owned HSV colour picker used by every note surface.</summary>
static class ColourPickerDialog
{
    public static Color? Show(Window owner, Color initial)
    {
        var picker = new PickerWindow(initial)
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Topmost = owner.Topmost,
        };
        var ownerBounds = new Rect(owner.Left, owner.Top,
            owner.ActualWidth > 0 ? owner.ActualWidth : owner.Width,
            owner.ActualHeight > 0 ? owner.ActualHeight : owner.Height);
        var bounds = CalculateBounds(ownerBounds, DisplayService.WorkArea(),
            new Size(picker.Width, picker.Height));
        picker.Left = bounds.Left;
        picker.Top = bounds.Top;
        return picker.ShowDialog() == true ? picker.SelectedColor : null;
    }

    internal static Rect CalculateBounds(Rect owner, Rect workArea, Size picker, double gap = 12)
    {
        double left = owner.Left - picker.Width - gap;
        if (left < workArea.Left)
            left = owner.Right + gap;
        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - picker.Width));
        double top = owner.Top + (owner.Height - picker.Height) / 2;
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - picker.Height));
        return new Rect(left, top, picker.Width, picker.Height);
    }

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        double chroma = value * saturation;
        double section = hue / 60;
        double x = chroma * (1 - Math.Abs(section % 2 - 1));
        (double r, double g, double b) = section switch
        {
            < 1 => (chroma, x, 0d),
            < 2 => (x, chroma, 0d),
            < 3 => (0d, chroma, x),
            < 4 => (0d, x, chroma),
            < 5 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        double match = value - chroma;
        return Color.FromRgb(
            (byte)Math.Round((r + match) * 255),
            (byte)Math.Round((g + match) * 255),
            (byte)Math.Round((b + match) * 255));
    }

    internal static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        if (hue < 0) hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    sealed class PickerWindow : Window
    {
        readonly Border _svField = new();
        readonly Canvas _svOverlay = new();
        readonly Ellipse _svMarker = new();
        readonly Border _hueField = new();
        readonly Canvas _hueOverlay = new();
        readonly Border _hueMarker = new();
        readonly Border _preview = new();
        readonly Border _hexShell = new();
        readonly TextBox _hex = new();
        readonly SolidColorBrush _hueBrush = new(Colors.Red);
        double _hue;
        double _saturation;
        double _value;
        bool _syncing;

        public Color SelectedColor => FromHsv(_hue, _saturation, _value);

        public PickerWindow(Color initial)
        {
            (_hue, _saturation, _value) = ToHsv(initial);
            Title = Loc.T("Choose colour", "选择颜色");
            Width = 400;
            Height = 430;
            MinWidth = MaxWidth = Width;
            MinHeight = MaxHeight = Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            FontFamily = UiTheme.Font;
            Foreground = UiTheme.Text;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var content = new StackPanel { Margin = new Thickness(24, 19, 24, 20) };
            content.Children.Add(new TextBlock
            {
                Text = Loc.T("Custom colour", "自定义颜色"),
                FontSize = 19,
                FontWeight = FontWeights.SemiBold,
                Foreground = UiTheme.Text,
                Margin = new Thickness(0, 0, 0, 16),
            });

            BuildSaturationValueField();
            content.Children.Add(_svField);
            BuildHueField();
            content.Children.Add(_hueField);
            content.Children.Add(BuildValueRow());
            content.Children.Add(BuildButtons());

            Content = UiTheme.WithWindowChrome(this, Title, content, dialog: true);
            Loaded += (_, _) => Render();
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Close();
                else if (e.Key == Key.Enter) Accept();
            };
        }

        void BuildSaturationValueField()
        {
            var layers = new Grid();
            layers.Children.Add(new Border { Background = _hueBrush });
            layers.Children.Add(new Border
            {
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new(Colors.White, 0),
                        new(Color.FromArgb(0, 255, 255, 255), 1),
                    }, new Point(0, 0.5), new Point(1, 0.5)),
            });
            layers.Children.Add(new Border
            {
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new(Color.FromArgb(0, 0, 0, 0), 0),
                        new(Colors.Black, 1),
                    }, new Point(0.5, 0), new Point(0.5, 1)),
            });
            _svMarker.Width = _svMarker.Height = 14;
            _svMarker.Fill = Brushes.Transparent;
            _svMarker.Stroke = Brushes.White;
            _svMarker.StrokeThickness = 2;
            _svMarker.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.65,
            };
            _svOverlay.IsHitTestVisible = false;
            _svOverlay.Children.Add(_svMarker);
            layers.Children.Add(_svOverlay);
            _svField.Height = 174;
            _svField.Background = UiTheme.Surface;
            _svField.BorderBrush = UiTheme.Hairline;
            _svField.BorderThickness = new Thickness(1);
            _svField.CornerRadius = UiTheme.ControlRadius;
            _svField.SizeChanged += (_, _) => _svField.Clip = new RectangleGeometry(
                new Rect(0, 0, _svField.ActualWidth, _svField.ActualHeight), 7, 7);
            _svField.Cursor = Cursors.Cross;
            _svField.Child = layers;
            _svField.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _svField.CaptureMouse();
                SetSaturationValue(e.GetPosition(_svField));
            };
            _svField.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && _svField.IsMouseCaptured)
                    SetSaturationValue(e.GetPosition(_svField));
            };
            _svField.PreviewMouseLeftButtonUp += (_, _) => _svField.ReleaseMouseCapture();
        }

        void BuildHueField()
        {
            var spectrum = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Colors.Red, 0),
                    new(Colors.Yellow, 1.0 / 6),
                    new(Colors.Lime, 2.0 / 6),
                    new(Colors.Cyan, 3.0 / 6),
                    new(Colors.Blue, 4.0 / 6),
                    new(Colors.Magenta, 5.0 / 6),
                    new(Colors.Red, 1),
                }, new Point(0, 0.5), new Point(1, 0.5));
            var layers = new Grid();
            layers.Children.Add(new Border { Background = spectrum });
            _hueMarker.Width = 8;
            _hueMarker.Height = 22;
            _hueMarker.CornerRadius = new CornerRadius(4);
            _hueMarker.Background = Brushes.Transparent;
            _hueMarker.BorderBrush = Brushes.White;
            _hueMarker.BorderThickness = new Thickness(2);
            _hueMarker.Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 3,
                ShadowDepth = 0,
                Opacity = 0.55,
            };
            _hueOverlay.IsHitTestVisible = false;
            _hueOverlay.Children.Add(_hueMarker);
            layers.Children.Add(_hueOverlay);
            _hueField.Height = 16;
            _hueField.Margin = new Thickness(0, 13, 0, 15);
            _hueField.CornerRadius = new CornerRadius(8);
            _hueField.Cursor = Cursors.Hand;
            _hueField.SizeChanged += (_, _) => _hueField.Clip = new RectangleGeometry(
                new Rect(0, 0, _hueField.ActualWidth, _hueField.ActualHeight), 8, 8);
            _hueField.Child = layers;
            _hueField.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _hueField.CaptureMouse();
                SetHue(e.GetPosition(_hueField).X);
            };
            _hueField.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed && _hueField.IsMouseCaptured)
                    SetHue(e.GetPosition(_hueField).X);
            };
            _hueField.PreviewMouseLeftButtonUp += (_, _) => _hueField.ReleaseMouseCapture();
        }

        UIElement BuildValueRow()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(126) });

            var previewRow = new StackPanel { Orientation = Orientation.Horizontal };
            _preview.Width = 34;
            _preview.Height = 34;
            _preview.CornerRadius = UiTheme.ControlRadius;
            _preview.BorderBrush = UiTheme.Hairline;
            _preview.BorderThickness = new Thickness(1);
            previewRow.Children.Add(_preview);
            previewRow.Children.Add(new TextBlock
            {
                Text = Loc.T("Selected colour", "已选颜色"),
                FontSize = 12.5,
                Foreground = UiTheme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
            });
            grid.Children.Add(previewRow);

            _hex.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            _hex.FontSize = 12.5;
            _hex.Foreground = UiTheme.Text;
            _hex.CaretBrush = UiTheme.Text;
            _hex.Background = Brushes.Transparent;
            _hex.BorderThickness = new Thickness(0);
            _hex.Padding = new Thickness(10, 7, 10, 7);
            _hex.MaxLength = 7;
            _hex.TextChanged += (_, _) => ReadHex();
            _hex.GotKeyboardFocus += (_, _) => _hex.SelectAll();
            _hexShell.Background = UiTheme.Surface;
            _hexShell.BorderBrush = UiTheme.Hairline;
            _hexShell.BorderThickness = new Thickness(1);
            _hexShell.CornerRadius = UiTheme.ControlRadius;
            _hexShell.Child = _hex;
            Grid.SetColumn(_hexShell, 1);
            grid.Children.Add(_hexShell);
            return grid;
        }

        UIElement BuildButtons()
        {
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            buttons.Children.Add(Button(Loc.T("Cancel", "取消"), primary: false, Close));
            buttons.Children.Add(Button(Loc.T("Apply", "应用"), primary: true, Accept));
            return buttons;
        }

        static Border Button(string text, bool primary, Action click)
        {
            var button = new Border
            {
                Background = primary ? UiTheme.Text : Brushes.Transparent,
                BorderBrush = primary ? UiTheme.Text : UiTheme.Hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = UiTheme.ControlRadius,
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(8, 0, 0, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 12.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = primary ? UiTheme.Surface : UiTheme.Text,
                },
            };
            button.MouseEnter += (_, _) => button.Opacity = 0.82;
            button.MouseLeave += (_, _) => button.Opacity = 1;
            button.MouseLeftButtonUp += (_, _) => click();
            return button;
        }

        void SetSaturationValue(Point point)
        {
            _saturation = Math.Clamp(point.X / Math.Max(1, _svField.ActualWidth), 0, 1);
            _value = 1 - Math.Clamp(point.Y / Math.Max(1, _svField.ActualHeight), 0, 1);
            Render();
        }

        void SetHue(double x)
        {
            _hue = 360 * Math.Clamp(x / Math.Max(1, _hueField.ActualWidth), 0, 1);
            Render();
        }

        void ReadHex()
        {
            if (_syncing) return;
            string value = _hex.Text.Trim();
            if (!value.StartsWith('#')) value = "#" + value;
            if (!NoteColor.TryParse(value, out var color))
            {
                _hexShell.BorderBrush = UiTheme.Danger;
                return;
            }
            _hexShell.BorderBrush = UiTheme.Hairline;
            (_hue, _saturation, _value) = ToHsv(color);
            Render(updateHex: false);
        }

        void Render(bool updateHex = true)
        {
            var color = SelectedColor;
            _hueBrush.Color = FromHsv(_hue, 1, 1);
            _preview.Background = new SolidColorBrush(color);
            Canvas.SetLeft(_svMarker, _saturation * Math.Max(1, _svField.ActualWidth) - 7);
            Canvas.SetTop(_svMarker, (1 - _value) * Math.Max(1, _svField.ActualHeight) - 7);
            Canvas.SetLeft(_hueMarker, _hue / 360 * Math.Max(1, _hueField.ActualWidth) - 4);
            Canvas.SetTop(_hueMarker, -3);
            if (!updateHex) return;
            _syncing = true;
            _hex.Text = NoteColor.ToHex(color);
            _hex.CaretIndex = _hex.Text.Length;
            _syncing = false;
            _hexShell.BorderBrush = UiTheme.Hairline;
        }

        void Accept()
        {
            string value = _hex.Text.Trim();
            if (!value.StartsWith('#')) value = "#" + value;
            if (!NoteColor.TryParse(value, out _))
            {
                _hexShell.BorderBrush = UiTheme.Danger;
                _hex.Focus();
                return;
            }
            DialogResult = true;
        }
    }
}
