using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace FlankNote;

/// <summary>The ten-second undo after a delete, bottom-centre of the screen.</summary>
class UndoToast : Window
{
    readonly Button _undo;
    readonly bool _restoreArchived;
    int _left = 10;

    public UndoToast(Note note, bool archived = false, bool restoreArchived = false)
    {
        _restoreArchived = restoreArchived;
        Width = 320; Height = 56;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        var w = DisplayService.WorkArea();
        Left = w.Left + (w.Width - 320) / 2;
        Top = w.Bottom - 56 - 48;

        _undo = new Button
        {
            Content = $"{Loc.T("Undo", "撤销")} (10)",
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 3, 10, 3),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _undo.Click += (_, _) =>
        {
            NotesStore.I.Restore(note, _restoreArchived);
            Close();
        };

        var bar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x24, 0x24, 0x28)),
            CornerRadius = new CornerRadius(9),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 },
            Padding = new Thickness(14, 0, 10, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = archived ? Loc.T("Note archived", "便签已归档") : Loc.T("Note deleted", "便签已删除"), Foreground = Brushes.White, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                    _undo,
                },
            },
        };
        Content = bar;

        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) =>
        {
            if (--_left <= 0) { tick.Stop(); Close(); }
            else _undo.Content = $"{Loc.T("Undo", "撤销")} ({_left})";
        };
        Closed += (_, _) => tick.Stop();
        tick.Start();

        SourceInitialized += (_, _) => Native.NoActivate(this);
        Show();
    }
}
