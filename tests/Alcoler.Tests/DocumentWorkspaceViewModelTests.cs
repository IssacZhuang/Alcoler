using Alcoler.ViewModel;
using Xunit;

namespace Alcoler.Tests;

public sealed class DocumentWorkspaceViewModelTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "alcoler-documents");

    [Fact]
    public void Open_AcceptsArbitraryContentAndMutableTitle_WithoutFixedDocumentKinds()
    {
        using var workspace = new DocumentWorkspaceViewModel();
        var content = new CustomContent();
        var document = new DocumentViewModel("custom", "extension.custom", "Custom", "M0 0h24v24z",
            content, Path.Combine(Root, "nested", ".."), "draft:first");

        Assert.Same(document, workspace.Open(document));
        document.Title = "Renamed custom document";

        Assert.Same(content, document.Content);
        Assert.Equal("extension.custom", document.Kind);
        Assert.Equal("Renamed custom document", document.Title);
        Assert.Equal(Root, document.WorkspaceRoot);
        Assert.Equal("M0 0h24v24z", document.IconPath);
        Assert.Same(document, workspace.ActiveDocument);
        Assert.True(document.IsActive);
        Assert.True(document.IsVisibleInWorkspace);
        Assert.True(workspace.HasDocuments);
        Assert.True(workspace.IsVisible);
    }

    [Fact]
    public void Open_DeduplicatesWorkspaceOwnerAndId_ReactivatesAndDisposesRedundantOwnedPayload()
    {
        using var workspace = Create();
        var original = Document("same");
        workspace.Open(original);
        workspace.Open(Document("other"));
        workspace.Collapse();
        var incoming = new CustomContent();
        var duplicate = new DocumentViewModel("same", "another-kind", "Incoming", "", incoming,
            Root.ToUpperInvariant() + Path.DirectorySeparatorChar, "session:first");

        Assert.Same(original, workspace.Open(duplicate));

        Assert.Equal(2, workspace.Items.Count);
        Assert.Same(original, workspace.ActiveDocument);
        Assert.True(workspace.IsVisible);
        Assert.Equal(1, incoming.DisposeCount);
        Assert.Equal(0, ((CustomContent)original.Content).DisposeCount);
    }

    [Fact]
    public void ActivateMoveAndSelectNext_KeepStableTabOrderAndWrapAround()
    {
        using var workspace = Create();
        var first = workspace.Open(Document("first"));
        var second = workspace.Open(Document("second"));
        var third = workspace.Open(Document("third"));
        workspace.ActivateCommand.Execute(first);
        Assert.Equal([first, second, third], workspace.VisibleItems);
        Assert.True(first.IsActive);
        Assert.False(third.IsActive);

        workspace.MoveCommand.Execute(new DocumentMoveRequest(third, 0));
        Assert.Equal([third, first, second], workspace.VisibleItems);
        Assert.Same(first, workspace.ActiveDocument);
        workspace.SelectNextCommand.Execute(1);
        Assert.Same(second, workspace.ActiveDocument);
        workspace.SelectNext(1);
        Assert.Same(third, workspace.ActiveDocument);
        workspace.SelectNext(-1);
        Assert.Same(second, workspace.ActiveDocument);
        workspace.Move(second, int.MaxValue);
        Assert.Equal([third, first, second], workspace.VisibleItems);
    }

    [Fact]
    public void CloseActive_SelectsRightNeighborThenLeft_AndClosingLastCollapses()
    {
        using var workspace = Create();
        var first = workspace.Open(Document("first"));
        var second = workspace.Open(Document("second"));
        var third = workspace.Open(Document("third"));
        workspace.Activate(second);

        workspace.CloseCommand.Execute(second);
        Assert.Same(third, workspace.ActiveDocument);
        Assert.False(second.IsActive);
        Assert.False(second.IsVisibleInWorkspace);
        workspace.Close(third);
        Assert.Same(first, workspace.ActiveDocument);
        workspace.Close(first);

        Assert.Null(workspace.ActiveDocument);
        Assert.Empty(workspace.Items);
        Assert.Empty(workspace.VisibleItems);
        Assert.False(workspace.HasDocuments);
        Assert.False(workspace.IsVisible);
    }

    [Fact]
    public void CloseInactive_PreservesActive_AndCollapseRetainsContentAndSelection()
    {
        using var workspace = Create();
        var first = workspace.Open(Document("first"));
        var second = workspace.Open(Document("second"));
        workspace.Close(first);
        Assert.Same(second, workspace.ActiveDocument);

        workspace.CollapseCommand.Execute(null);

        Assert.False(workspace.IsVisible);
        Assert.Same(second, workspace.ActiveDocument);
        Assert.Same(second, Assert.Single(workspace.Items));
        Assert.True(workspace.HasDocuments);
        Assert.Equal(0, ((CustomContent)second.Content).DisposeCount);
        workspace.IsVisible = true;
        Assert.Same(second, workspace.ActiveDocument);
    }

    [Fact]
    public void ContentOwnership_DisposesOnlyOwnedPayloadsOnce_AfterLastSharedRecordCloses()
    {
        var workspace = Create();
        var shared = new CustomContent();
        var borrowed = new CustomContent();
        var first = workspace.Open(Document("first", shared));
        var second = workspace.Open(Document("second", shared));
        workspace.Open(Document("borrowed", borrowed, ownsContent: false));
        workspace.Open(Document("first", shared));
        Assert.Equal(0, shared.DisposeCount);
        workspace.Close(first);
        Assert.Equal(0, shared.DisposeCount);
        workspace.Close(second);
        workspace.Close(second);
        Assert.Equal(1, shared.DisposeCount);
        Assert.Throws<InvalidOperationException>(() => workspace.Open(Document("reused", shared)));

        workspace.Dispose();
        workspace.Dispose();

        Assert.Equal(1, shared.DisposeCount);
        Assert.Equal(0, borrowed.DisposeCount);
    }

    [Fact]
    public void RedundantIncomingContent_IsNotDisposedTwice_AndBorrowedPayloadIsNotDisposed()
    {
        using var workspace = Create();
        workspace.Open(Document("original"));
        var redundant = new CustomContent();
        var borrowed = new CustomContent();
        var duplicate = Document("original", redundant);

        workspace.Open(duplicate);
        workspace.Open(duplicate);
        workspace.Open(Document("original", borrowed, ownsContent: false));

        Assert.Equal(1, redundant.DisposeCount);
        Assert.Equal(0, borrowed.DisposeCount);
        Assert.Single(workspace.Items);
    }

    [Fact]
    public void SetContext_RestoresPerOwnerSelectionVisibilityAndOrder_WithWorkspaceGlobalDocuments()
    {
        using var workspace = Create();
        var global = workspace.Open(Document("global", ownerId: null));
        var first = workspace.Open(Document("first"));
        var second = workspace.Open(Document("second"));
        workspace.Move(second, 0);
        workspace.Activate(first);
        workspace.IsVisible = false;

        workspace.SetContext(Root, "session:second");
        Assert.Same(global, Assert.Single(workspace.VisibleItems));
        Assert.False(first.IsVisibleInWorkspace);
        Assert.False(first.IsActive);
        Assert.False(workspace.IsVisible);
        var other = workspace.Open(Document("first", ownerId: "session:second"));
        Assert.Equal([global, other], workspace.VisibleItems);
        workspace.SetContext(Root, "draft:new");
        var draft = workspace.Open(Document("first", ownerId: "draft:new"));
        Assert.Equal([global, draft], workspace.VisibleItems);

        workspace.SetContext(Root.ToUpperInvariant(), "session:first");
        Assert.Equal([second, global, first], workspace.VisibleItems);
        Assert.Same(first, workspace.ActiveDocument);
        Assert.False(workspace.IsVisible);
        workspace.SetContext(Root, "session:second");
        Assert.Same(other, workspace.ActiveDocument);
        Assert.True(workspace.IsVisible);
        Assert.Equal(5, workspace.Items.Count);
    }

    [Fact]
    public void BackgroundOpenAndActivate_OldScopeCannotStealCurrentContext()
    {
        using var workspace = Create();
        var old = workspace.Open(Document("old"));
        var otherRoot = Path.Combine(Root, "other");
        workspace.SetContext(otherRoot, "session:other");
        var current = workspace.Open(new DocumentViewModel("current", "custom", "Current", "",
            new CustomContent(), otherRoot, "session:other"));
        workspace.Collapse();

        var background = workspace.Open(Document("background"));
        workspace.Activate(old);
        workspace.Open(Document("old"));

        Assert.Same(current, workspace.ActiveDocument);
        Assert.Same(current, Assert.Single(workspace.VisibleItems));
        Assert.False(workspace.IsVisible);
        Assert.False(background.IsVisibleInWorkspace);
        Assert.Equal(3, workspace.Items.Count);
        workspace.SetContext(Root, "session:first");
        Assert.Equal([old, background], workspace.VisibleItems);
    }

    [Fact]
    public void CloseOthersAndCloseAll_OnlyAffectVisibleScope()
    {
        using var workspace = Create();
        var global = workspace.Open(Document("global", ownerId: null));
        var first = workspace.Open(Document("first"));
        workspace.SetContext(Root, "session:second");
        var second = workspace.Open(Document("second", ownerId: "session:second"));
        workspace.CloseOthersCommand.Execute(second);
        Assert.DoesNotContain(global, workspace.Items);
        Assert.Contains(first, workspace.Items);
        Assert.Same(second, workspace.ActiveDocument);

        workspace.CloseAllCommand.Execute(null);

        Assert.Same(first, Assert.Single(workspace.Items));
        Assert.Empty(workspace.VisibleItems);
        Assert.False(workspace.IsVisible);
        Assert.Equal(0, ((CustomContent)first.Content).DisposeCount);
        workspace.SetContext(Root, "session:first");
        Assert.Same(first, workspace.ActiveDocument);
    }

    [Fact]
    public void TransferOwner_MovesDraftDocumentsAndScopeState_WithoutDisposingContent()
    {
        using var workspace = new DocumentWorkspaceViewModel();
        workspace.SetContext(Root, "draft:first");
        var global = workspace.Open(Document("global", ownerId: null));
        var draft = workspace.Open(Document("draft", ownerId: "draft:first"));
        workspace.Move(draft, 0);
        workspace.Collapse();

        workspace.TransferOwner(Root, "draft:first", "session:first");

        Assert.Equal("session:first", draft.OwnerId);
        Assert.Same(draft, workspace.ActiveDocument);
        Assert.Equal([draft, global], workspace.VisibleItems);
        Assert.False(workspace.IsVisible);
        Assert.Equal(0, ((CustomContent)draft.Content).DisposeCount);
        workspace.SetContext(Root, "draft:first");
        Assert.Same(global, Assert.Single(workspace.VisibleItems));
        workspace.SetContext(Root, "session:first");
        Assert.Same(draft, workspace.ActiveDocument);
    }

    [Fact]
    public void EmptyWorkspace_CanShowLauncherWithoutCreatingAnyDocument()
    {
        using var workspace = Create();
        workspace.IsVisible = true;

        Assert.True(workspace.IsVisible);
        Assert.False(workspace.HasDocuments);
        Assert.Empty(workspace.Items);
        Assert.Null(workspace.ActiveDocument);
        workspace.SetContext(Root, "other");
        Assert.False(workspace.IsVisible);
        workspace.SetContext(Root, "session:first");
        Assert.True(workspace.IsVisible);
    }

    private static DocumentWorkspaceViewModel Create()
    {
        var workspace = new DocumentWorkspaceViewModel();
        workspace.SetContext(Root, "session:first");
        return workspace;
    }

    private static DocumentViewModel Document(string id, CustomContent? content = null,
        string? ownerId = "session:first", bool ownsContent = true) =>
        new(id, "custom", id, "", content ?? new CustomContent(), Root, ownerId, ownsContent);

    private sealed class CustomContent : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
