using Alcoler.Localization;
using Alcoler.Ui;

namespace Alcoler.ViewModel;

public sealed partial class WorkbenchViewModel
{
    public IReadOnlyList<ExternalEditor> ExternalEditors => _externalEditorLauncher.Editors;

    public void OpenFileExternally(string path, ExternalEditor editor, string? workspaceRoot = null)
    {
        if (!TryResolveExternalPath(path, workspaceRoot, out var fullPath))
        {
            return;
        }

        try
        {
            _externalEditorLauncher.Open(editor, fullPath);
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("timeline_open_external_failed",
                Path.GetFileName(fullPath), editor.Name, exception.Message);
        }
    }

    public async Task OpenMarkdownLinkAsync(string target, string? workspaceRoot = null, string? ownerId = null,
        Func<string, string, Task<bool>>? openImage = null)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            try
            {
                _externalEditorLauncher.OpenDefault(target);
            }
            catch (Exception exception)
            {
                Banner = Loc.Get("timeline_open_failed", target, exception.Message);
            }
            return;
        }
        workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot) ? CurrentWorkspaceRoot : workspaceRoot;
        var path = uri is { IsFile: true } ? uri.LocalPath : Uri.UnescapeDataString(target);
        if (!TryResolveExternalPath(path, workspaceRoot, out var fullPath))
            return;
        ownerId ??= CurrentDocumentOwnerId;
        try
        {
            if (ComposerClipboardImages.GetFileMimeType(fullPath) is { } mimeType)
            {
                if (openImage is not null && await openImage(fullPath, mimeType))
                    return;
            }
            else
            {
                using var preview = TextDocumentViewModel.ForFile(fullPath, InlineUiScheduler.Instance);
                await preview.ReloadAsync();
                if (preview.Error is null)
                {
                    await OpenFileDocumentAsync(fullPath, workspaceRoot, ownerId);
                    return;
                }
            }
            OpenFileWithDefaultApp(fullPath, workspaceRoot);
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("timeline_open_failed", Path.GetFileName(fullPath), exception.Message);
        }
    }

    public void OpenFileWithDefaultApp(string path, string? workspaceRoot = null)
    {
        if (!TryResolveExternalPath(path, workspaceRoot, out var fullPath))
        {
            return;
        }

        try
        {
            _externalEditorLauncher.OpenDefault(fullPath);
        }
        catch (Exception exception)
        {
            Banner = Loc.Get("timeline_open_failed", Path.GetFileName(fullPath), exception.Message);
        }
    }

    private bool TryResolveExternalPath(string path, string? workspaceRoot, out string fullPath)
    {
        var root = string.IsNullOrWhiteSpace(workspaceRoot) ? CurrentWorkspaceRoot : workspaceRoot;
        try
        {
            fullPath = Path.GetFullPath(path, root);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            fullPath = path;
            Banner = Loc.Get("workbench_file_path_error", exception.Message);
            return false;
        }
    }
}
