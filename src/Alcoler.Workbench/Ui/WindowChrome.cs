using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Alcoler.Workbench.Ui;

internal static class WindowChrome
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int NoBorderColor = unchecked((int)0xFFFFFFFE);

    public static void Attach(Window window)
    {
        window.Opened += (_, _) => Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dark = window.ActualThemeVariant == ThemeVariant.Dark;
        var useDark = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref useDark, sizeof(int));
        if (NativeColor(window, "AlcolerChromeColor") is { } caption)
        {
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
        }
        if (NativeColor(window, "AlcolerTextPrimaryColor") is { } text)
        {
            DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
        }
        var border = NoBorderColor;
        DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
    }

    private static int? NativeColor(Window window, string key)
    {
        return window.TryFindResource(key, window.ActualThemeVariant, out var value) && value is Color color
            ? color.R | (color.G << 8) | (color.B << 16)
            : null;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
