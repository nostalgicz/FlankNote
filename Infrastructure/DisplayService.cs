using DrawingRectangle = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;
using System.Windows;

namespace FlankNote;

sealed record DisplayOption(string DeviceName, string Label)
{
    public override string ToString() => Label;
}

static class DisplayService
{
    public static IReadOnlyList<DisplayOption> Options()
    {
        var screens = FormsScreen.AllScreens;
        var result = new List<DisplayOption>(screens.Length);
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var label = screen.Primary
                ? Loc.T("Primary display", "主显示器")
                : Loc.T($"Display {i + 1}", $"显示器 {i + 1}");
            result.Add(new DisplayOption(screen.DeviceName, label));
        }
        return result;
    }

    public static Rect WorkArea()
    {
        var screens = FormsScreen.AllScreens;
        var selected = screens.FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(Settings.DisplayName) &&
            string.Equals(s.DeviceName, Settings.DisplayName, StringComparison.OrdinalIgnoreCase))
            ?? FormsScreen.PrimaryScreen
            ?? screens.FirstOrDefault();

        if (selected == null) return SystemParameters.WorkArea;
        var area = selected.WorkingArea;
        // WPF window coordinates are logical pixels. Use the system scale for
        // virtual-screen coordinates so a high-DPI primary monitor does not
        // place the deck far outside the selected work area.
        double scale = Math.Max(1, Native.GetDpiForSystem() / 96.0);
        return new Rect(area.Left / scale, area.Top / scale,
                        area.Width / scale, area.Height / scale);
    }

    public static void CenterOnSelected(Window window)
    {
        var area = WorkArea();
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = Math.Max(area.Left, area.Left + (area.Width - window.Width) / 2);
        window.Top = Math.Max(area.Top, area.Top + (area.Height - window.Height) / 2);
    }
}
