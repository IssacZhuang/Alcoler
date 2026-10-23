using Avalonia.Markup.Xaml;

namespace Alcoler.Localization;

// Resolves once at XAML load time; switching languages requires a restart by design.
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
