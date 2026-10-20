using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Alcoler.Workbench.Ui;

internal static class WindowChrome
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

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
        var caption = dark ? 0x00161616 : 0x00F8F8F8;
        DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
        var text = dark ? 0x00D4D4D4 : 0x00262626;
        DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
        var border = dark ? 0x00161616 : 0x00F8F8F8;
        DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
