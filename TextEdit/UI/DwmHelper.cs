using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TextEdit;

/// <summary>
/// Applies DWM window attributes (dark mode, caption/border colour) to match the active theme.
/// </summary>
internal static class DwmHelper
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    public static void ApplyTheme(Visual visual, ThemeManager themeManager)
    {
        if (PresentationSource.FromVisual(visual) is not HwndSource source) return;
        var hwnd = source.Handle;

        var bg = themeManager.EditorBg as SolidColorBrush;
        if (bg == null) return;
        var c = bg.Color;
        bool isDark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;

        int darkMode = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        var chromeBrush = Application.Current.Resources["ThemeChromeBrush"] as SolidColorBrush;
        if (chromeBrush != null)
        {
            var cc = chromeBrush.Color;
            int colorRef = cc.R | (cc.G << 8) | (cc.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        }
    }
}
