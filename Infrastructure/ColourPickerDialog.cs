using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace FlankNote;

static class ColourPickerDialog
{
    static int[] _customColours = [];

    public static Color? Show(Window owner, Color initial)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = true,
            Color = System.Drawing.Color.FromArgb(initial.R, initial.G, initial.B),
            CustomColors = _customColours,
        };
        var result = dialog.ShowDialog(new OwnerWindow(new WindowInteropHelper(owner).Handle));
        _customColours = dialog.CustomColors;
        if (result != System.Windows.Forms.DialogResult.OK) return null;
        return Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
    }

    sealed class OwnerWindow(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
