using Alcoler.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public sealed partial class PlanDocumentViewModel : ObservableObject
{
    public PlanDocumentViewModel(string title, string markdown)
    {
        _title = title;
        _markdown = markdown;
    }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string _markdown;

    public void Update(string title, string markdown)
    {
        Title = title;
        Markdown = markdown;
    }

    internal static string ResolveTitle(string? summary, string? markdown)
    {
        if (!string.IsNullOrWhiteSpace(summary))
            return summary.Trim();
        return FirstHeading(markdown) ?? Loc.Get("workbench_plan");
    }

    private static string? FirstHeading(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return null;
        string? fence = null;
        foreach (var line in markdown.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith("```", StringComparison.Ordinal) || text.StartsWith("~~~", StringComparison.Ordinal))
            {
                var marker = text[..3];
                fence = fence is null ? marker : fence == marker ? null : fence;
                continue;
            }
            if (fence is not null)
                continue;
            var heading = text.TakeWhile(character => character == '#').Count();
            if (heading is >= 1 and <= 6 && text.Length > heading && char.IsWhiteSpace(text[heading]))
            {
                var title = text[heading..].Trim().TrimEnd('#').TrimEnd();
                if (title.Length > 0)
                    return title;
            }
        }
        return null;
    }
}
