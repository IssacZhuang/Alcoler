using CommunityToolkit.Mvvm.ComponentModel;

namespace Alcoler.ViewModel;

public sealed partial class DocumentViewModel : ObservableObject
{
    public DocumentViewModel(
        string id,
        string kind,
        string title,
        string iconPath,
        object content,
        string workspaceRoot,
        string? ownerId = null,
        bool ownsContent = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(iconPath);
        ArgumentNullException.ThrowIfNull(content);
        Id = id;
        Kind = kind;
        _title = title;
        IconPath = iconPath;
        Content = content;
        WorkspaceRoot = NormalizeWorkspaceRoot(workspaceRoot);
        _ownerId = ownerId;
        OwnsContent = ownsContent;
    }

    public string Id { get; }

    public string Kind { get; }

    public string IconPath { get; }

    public object Content { get; }

    public string WorkspaceRoot { get; }

    public bool OwnsContent { get; }

    [ObservableProperty]
    private string _title;

    private string? _ownerId;

    public string? OwnerId
    {
        get => _ownerId;
        internal set => SetProperty(ref _ownerId, value);
    }

    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    private bool _isVisibleInWorkspace;

    public bool IsVisibleInWorkspace
    {
        get => _isVisibleInWorkspace;
        internal set => SetProperty(ref _isVisibleInWorkspace, value);
    }

    internal static string NormalizeWorkspaceRoot(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
    }
}
