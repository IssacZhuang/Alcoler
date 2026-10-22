using Avalonia;
using Avalonia.Styling;

namespace Alcoler.Themes;

/// <summary>
/// Both palettes ship as ThemeDictionaries inside AlcolerTheme.axaml, so switching
/// the application-wide requested variant is what swaps the active dictionary for
/// every DynamicResource lookup; dictionaries themselves are never replaced.
/// </summary>
public static class ThemeSelector
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static IReadOnlyList<string> Choices { get; } = [Dark, Light];

    public static string Normalize(string? theme) => theme == Light ? Light : Dark;

    public static void Apply(string? theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }
        application.RequestedThemeVariant = Normalize(theme) == Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }
}
