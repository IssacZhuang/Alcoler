namespace Alcoler.Workbench.ViewModel;

public sealed partial class WorkbenchViewModel
{
    private const string TextDocumentIcon = "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M8 13h8M8 17h6";

    public DocumentViewModel OpenTextDocument(string id, string title, string content, string? language = null)
    {
        var workspaceRoot = CurrentWorkspaceRoot;
        var ownerId = CurrentDocumentOwnerId;
        var text = new TextDocumentViewModel(title, content, language);
        var document = OpenDocument(new DocumentViewModel(
            $"text:{id}", "text", title, TextDocumentIcon, text, workspaceRoot, ownerId));
        if (document.Content is TextDocumentViewModel retained)
        {
            retained.UpdateText(content);
        }
        return document;
    }

    public async Task<DocumentViewModel> OpenFileDocumentAsync(
        string path, string? workspaceRoot = null, string? ownerId = null)
    {
        workspaceRoot ??= CurrentWorkspaceRoot;
        ownerId ??= CurrentDocumentOwnerId;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, workspaceRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            var invalid = new TextDocumentViewModel(path, string.Empty)
            {
                Error = $"This file path cannot be opened. Check the path and try again. {exception.Message}",
            };
            return OpenDocument(new DocumentViewModel(
                $"file:{path}", "file", "Invalid file path", TextDocumentIcon, invalid, workspaceRoot, ownerId));
        }
        var canonicalId = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
        var preview = TextDocumentViewModel.ForFile(fullPath, _scheduler);
        var document = OpenDocument(new DocumentViewModel(
            $"file:{canonicalId}", "file", Path.GetFileName(fullPath), TextDocumentIcon, preview, workspaceRoot, ownerId));
        if (document.Content is TextDocumentViewModel retained && !retained.IsDisposed)
        {
            if (ReferenceEquals(retained, preview))
            {
                await retained.ReloadAsync();
            }
            else
            {
                await retained.LoadingTask;
            }
        }
        return document;
    }
}
