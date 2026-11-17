using Alcoler.Adapters.Tools;
using Alcoler.Kernel.Tests.Storage;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class PathResolutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveSymbolicLinks_ResolvesTargetParentsAndRemainingSegments(bool relativeTargets)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var directory = new TempDirectory();
        var workspace = Path.Combine(directory.Root, "workspace");
        var subdirectory = Path.Combine(workspace, "sub");
        Directory.CreateDirectory(subdirectory);
        File.WriteAllText(Path.Combine(subdirectory, "file.txt"), "content");
        Directory.CreateSymbolicLink(Path.Combine(directory.Root, "alias"),
            relativeTargets ? "workspace" : workspace);
        var shortcut = Path.Combine(directory.Root, "shortcut");
        Directory.CreateSymbolicLink(shortcut,
            relativeTargets ? "alias/sub" : Path.Combine(directory.Root, "alias", "sub"));

        Assert.Equal(Path.Combine(PathResolution.ResolveSymbolicLinks(directory.Root), "workspace", "sub", "file.txt"),
            PathResolution.ResolveSymbolicLinks(Path.Combine(shortcut, "file.txt")));
    }

    [Fact]
    public void ResolveSymbolicLinks_ResolvesTargetSymlinksBeforeParentTraversal()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Root, "physical", "deep"));
        Directory.CreateDirectory(Path.Combine(directory.Root, "physical", "sub"));
        Directory.CreateSymbolicLink(Path.Combine(directory.Root, "alias"), "physical/deep");
        var shortcut = Path.Combine(directory.Root, "shortcut");
        Directory.CreateSymbolicLink(shortcut, "alias/../sub");

        Assert.Equal(Path.Combine(PathResolution.ResolveSymbolicLinks(directory.Root), "physical", "sub"),
            PathResolution.ResolveSymbolicLinks(shortcut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveSymbolicLinks_LoopsIncludingTargetParents_ThrowIOException(bool targetParent)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var directory = new TempDirectory();
        var first = Path.Combine(directory.Root, "first");
        Directory.CreateSymbolicLink(first, targetParent ? "second/sub" : "second");
        Directory.CreateSymbolicLink(Path.Combine(directory.Root, "second"), "first");

        Assert.ThrowsAny<IOException>(() => PathResolution.ResolveSymbolicLinks(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveSymbolicLinks_ResolvesParentTraversalInInput(bool relative)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var directory = new TempDirectory();
        var target = Directory.CreateDirectory(Path.Combine(directory.Root, "target", "nested")).FullName;
        var expected = Directory.CreateDirectory(Path.Combine(directory.Root, "target", "sibling")).FullName;
        Directory.CreateDirectory(Path.Combine(directory.Root, "sibling"));
        var shortcut = Path.Combine(directory.Root, "shortcut");
        Directory.CreateSymbolicLink(shortcut, target);
        if (relative)
            shortcut = Path.GetRelativePath(Environment.CurrentDirectory, shortcut);

        var resolved = PathResolution.ResolveSymbolicLinks(Path.Combine(shortcut, "..", "sibling"));

        Assert.Equal(PathResolution.ResolveSymbolicLinks(expected), resolved);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void ResolveSymbolicLinks_TraversalThroughRegularFile_ThrowsIOException(string segment)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var directory = new TempDirectory();
        var file = Path.Combine(directory.Root, "file");
        File.WriteAllText(file, "contents");

        Assert.ThrowsAny<IOException>(() => PathResolution.ResolveSymbolicLinks(Path.Combine(file, segment)));
    }

    [Fact]
    public void ResolveSymbolicLinks_NonexistentPath_PreservesWindowsLexicalBehavior()
    {
        using var directory = new TempDirectory();
        var missing = Path.Combine(directory.Root, "missing");

        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.GetFullPath(missing), PathResolution.ResolveSymbolicLinks(missing));
        else
            Assert.ThrowsAny<IOException>(() => PathResolution.ResolveSymbolicLinks(missing));
    }
}
