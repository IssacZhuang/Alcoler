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

    public void OpenMarkdownLink(string target, string? workspaceRoot = null)
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
        OpenFileWithDefaultApp(uri is { IsFile: true } ? uri.LocalPath : Uri.UnescapeDataString(target), workspaceRoot);
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
