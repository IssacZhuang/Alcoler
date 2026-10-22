using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Alcoler.ViewModel;

public sealed record DocumentMoveRequest(DocumentViewModel Document, int TargetIndex);

public sealed partial class DocumentWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<DocumentViewModel> _items = [];
    private readonly ObservableCollection<DocumentViewModel> _visibleItems = [];
    private readonly Dictionary<(string Root, string Owner), ScopeState> _scopes = [];
    private readonly ConditionalWeakTable<object, ContentLifetime> _contentLifetimes = new();
    private ScopeState? _scope;
    private string? _workspaceRoot;
    private string? _ownerId;
    private DocumentViewModel? _activeDocument;
    private bool _isVisible;
    private bool _disposed;

    public DocumentWorkspaceViewModel()
    {
        Items = new ReadOnlyObservableCollection<DocumentViewModel>(_items);
        VisibleItems = new ReadOnlyObservableCollection<DocumentViewModel>(_visibleItems);
    }

    public ReadOnlyObservableCollection<DocumentViewModel> Items { get; }

    public ReadOnlyObservableCollection<DocumentViewModel> VisibleItems { get; }

    public DocumentViewModel? ActiveDocument => _activeDocument;

    public bool HasDocuments => _visibleItems.Count > 0;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (SetProperty(ref _isVisible, value) && _scope is not null)
            {
                _scope.IsVisible = value;
            }
        }
    }

    public void SetContext(string workspaceRoot, string ownerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ownerId);
        workspaceRoot = DocumentViewModel.NormalizeWorkspaceRoot(workspaceRoot);
        if (SameRoot(_workspaceRoot, workspaceRoot) && _ownerId == ownerId)
        {
            return;
        }
        _workspaceRoot = workspaceRoot;
        _ownerId = ownerId;
        var key = ScopeKey(workspaceRoot, ownerId);
        if (!_scopes.TryGetValue(key, out _scope))
        {
            _scope = new ScopeState();
            _scopes.Add(key, _scope);
        }
        RefreshVisibleItems();
        IsVisible = _scope.IsVisible;
    }

    public DocumentViewModel Open(DocumentViewModel document)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        if (_workspaceRoot is null)
        {
            SetContext(document.WorkspaceRoot, document.OwnerId ?? string.Empty);
        }
        var existing = _items.FirstOrDefault(item => SameIdentity(item, document));
        if (existing is not null)
        {
            if (!ReferenceEquals(existing, document))
            {
                ReleaseRedundantContent(document);
            }
            Activate(existing);
            return existing;
        }
        var lifetime = _contentLifetimes.GetValue(document.Content, _ => new ContentLifetime());
        if (lifetime.Disposed)
        {
            throw new InvalidOperationException("A closed document's disposed content cannot be reopened.");
        }
        lifetime.References++;
        lifetime.Owned |= document.OwnsContent;
        _items.Add(document);
        Activate(document);
        return document;
    }

    public IRelayCommand<DocumentViewModel> OpenCommand => _openCommand ??= new RelayCommand<DocumentViewModel>(
        document =>
        {
            if (document is not null)
            {
                Open(document);
            }
        });

    private IRelayCommand<DocumentViewModel>? _openCommand;

    [RelayCommand]
    public void Activate(DocumentViewModel? document)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (document is null || !_items.Contains(document))
        {
            return;
        }
        if (!MatchesContext(document))
        {
            return;
        }
        RefreshVisibleItems();
        SetActive(document);
        IsVisible = true;
    }

    [RelayCommand]
    public void Close(DocumentViewModel? document)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (document is null || !_items.Contains(document))
        {
            return;
        }
        var index = _visibleItems.IndexOf(document);
        var wasActive = ReferenceEquals(ActiveDocument, document);
        var next = wasActive
            ? _visibleItems.ElementAtOrDefault(index + 1) ?? _visibleItems.ElementAtOrDefault(index - 1)
            : ActiveDocument;
        _items.Remove(document);
        foreach (var scope in _scopes.Values)
        {
            var scopeIndex = scope.Order.IndexOf(document);
            if (ReferenceEquals(scope.ActiveDocument, document))
            {
                scope.ActiveDocument = scope.Order.ElementAtOrDefault(scopeIndex + 1)
                    ?? scope.Order.ElementAtOrDefault(scopeIndex - 1);
            }
            scope.Order.Remove(document);
            if (scopeIndex >= 0 && scope.Order.Count == 0)
            {
                scope.IsVisible = false;
            }
        }
        document.IsActive = false;
        document.IsVisibleInWorkspace = false;
        RefreshVisibleItems();
        if (wasActive)
        {
            SetActive(next);
        }
        if (index >= 0 && !HasDocuments)
        {
            IsVisible = false;
        }
        ReleaseContent(document);
    }

    [RelayCommand]
    public void CloseOthers(DocumentViewModel? document)
    {
        if (document is null || !_visibleItems.Contains(document))
        {
            return;
        }
        foreach (var other in _visibleItems.Where(item => !ReferenceEquals(item, document)).ToArray())
        {
            Close(other);
        }
        SetActive(document);
    }

    [RelayCommand]
    public void CloseAll()
    {
        foreach (var document in _visibleItems.ToArray())
        {
            Close(document);
        }
        IsVisible = false;
    }

    public void Move(DocumentViewModel document, int targetIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var index = _visibleItems.IndexOf(document);
        if (index < 0)
        {
            return;
        }
        targetIndex = Math.Clamp(targetIndex, 0, _visibleItems.Count - 1);
        if (index == targetIndex)
        {
            return;
        }
        _visibleItems.Move(index, targetIndex);
        _scope!.Order.RemoveAt(index);
        _scope.Order.Insert(targetIndex, document);
    }

    public IRelayCommand<DocumentMoveRequest> MoveCommand => _moveCommand ??= new RelayCommand<DocumentMoveRequest>(
        request =>
        {
            if (request is not null)
            {
                Move(request.Document, request.TargetIndex);
            }
        });

    private IRelayCommand<DocumentMoveRequest>? _moveCommand;

    [RelayCommand]
    public void SelectNext(int delta)
    {
        if (!HasDocuments)
        {
            return;
        }
        var index = ActiveDocument is null ? 0 : _visibleItems.IndexOf(ActiveDocument);
        var next = (int)(((long)index + delta) % _visibleItems.Count);
        Activate(_visibleItems[next < 0 ? next + _visibleItems.Count : next]);
    }

    [RelayCommand]
    public void Collapse() => IsVisible = false;

    public void TransferOwner(string workspaceRoot, string oldOwnerId, string newOwnerId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        workspaceRoot = DocumentViewModel.NormalizeWorkspaceRoot(workspaceRoot);
        if (oldOwnerId == newOwnerId)
        {
            return;
        }
        var oldKey = ScopeKey(workspaceRoot, oldOwnerId);
        var newKey = ScopeKey(workspaceRoot, newOwnerId);
        var isCurrent = SameRoot(_workspaceRoot, workspaceRoot) && _ownerId == oldOwnerId;
        foreach (var document in _items.Where(item => SameRoot(item.WorkspaceRoot, workspaceRoot)
                     && item.OwnerId == oldOwnerId).ToArray())
        {
            var duplicate = _items.FirstOrDefault(item => SameRoot(item.WorkspaceRoot, workspaceRoot)
                && item.OwnerId == newOwnerId && item.Id == document.Id);
            if (duplicate is not null)
            {
                Close(document);
            }
            else
            {
                document.OwnerId = newOwnerId;
            }
        }
        if (_scopes.Remove(oldKey, out var oldScope))
        {
            if (_scopes.TryGetValue(newKey, out var targetScope))
            {
                foreach (var document in oldScope.Order.Where(document => !targetScope.Order.Contains(document)))
                {
                    targetScope.Order.Add(document);
                }
            }
            else
            {
                _scopes[newKey] = oldScope;
            }
        }
        if (isCurrent)
        {
            _ownerId = null;
            SetContext(workspaceRoot, newOwnerId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        foreach (var document in _items.ToArray())
        {
            Close(document);
        }
        _scopes.Clear();
        _scope = null;
        IsVisible = false;
        _disposed = true;
    }

    private void RefreshVisibleItems()
    {
        if (_scope is null)
        {
            return;
        }
        _scope.Order.RemoveAll(document => !_items.Contains(document) || !MatchesContext(document));
        foreach (var document in _items)
        {
            document.IsVisibleInWorkspace = MatchesContext(document);
            if (document.IsVisibleInWorkspace && !_scope.Order.Contains(document))
            {
                _scope.Order.Add(document);
            }
        }
        if (!_visibleItems.SequenceEqual(_scope.Order))
        {
            _visibleItems.Clear();
            foreach (var document in _scope.Order)
            {
                _visibleItems.Add(document);
            }
            OnPropertyChanged(nameof(HasDocuments));
        }
        SetActive(_scope.ActiveDocument is { } active && _visibleItems.Contains(active)
            ? active
            : _visibleItems.FirstOrDefault());
    }

    private void SetActive(DocumentViewModel? document)
    {
        if (_activeDocument is not null)
        {
            _activeDocument.IsActive = false;
        }
        if (document is not null)
        {
            document.IsActive = true;
        }
        SetProperty(ref _activeDocument, document, nameof(ActiveDocument));
        if (_scope is not null)
        {
            _scope.ActiveDocument = document;
        }
    }

    private void ReleaseContent(DocumentViewModel document)
    {
        var lifetime = _contentLifetimes.GetValue(document.Content, _ => new ContentLifetime());
        lifetime.References--;
        DisposeContentIfUnused(document.Content, lifetime);
    }

    private void ReleaseRedundantContent(DocumentViewModel document)
    {
        if (!document.OwnsContent)
        {
            return;
        }
        var lifetime = _contentLifetimes.GetValue(document.Content, _ => new ContentLifetime());
        lifetime.Owned = true;
        DisposeContentIfUnused(document.Content, lifetime);
    }

    private static void DisposeContentIfUnused(object content, ContentLifetime lifetime)
    {
        if (lifetime.References == 0 && lifetime.Owned && !lifetime.Disposed && content is IDisposable disposable)
        {
            lifetime.Disposed = true;
            disposable.Dispose();
        }
    }

    private bool MatchesContext(DocumentViewModel document) => SameRoot(document.WorkspaceRoot, _workspaceRoot)
        && (document.OwnerId is null || document.OwnerId == _ownerId);

    private static bool SameIdentity(DocumentViewModel first, DocumentViewModel second) => first.Id == second.Id
        && first.OwnerId == second.OwnerId && SameRoot(first.WorkspaceRoot, second.WorkspaceRoot);

    private static bool SameRoot(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static (string Root, string Owner) ScopeKey(string root, string owner) => (root.ToUpperInvariant(), owner);

    private sealed class ScopeState
    {
        public List<DocumentViewModel> Order { get; } = [];
        public DocumentViewModel? ActiveDocument { get; set; }
        public bool IsVisible { get; set; }
    }

    private sealed class ContentLifetime
    {
        public int References { get; set; }
        public bool Owned { get; set; }
        public bool Disposed { get; set; }
    }
}
