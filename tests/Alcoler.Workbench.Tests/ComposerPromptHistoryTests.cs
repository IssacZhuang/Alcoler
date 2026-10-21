using System.Text.Json;
using Xunit;

namespace Alcoler.Workbench.Tests;

public sealed class ComposerPromptHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "alcoler-prompt-history-" + Guid.NewGuid().ToString("N"));

    public ComposerPromptHistoryTests() => Directory.CreateDirectory(_directory);

    private string HistoryPath => Path.Combine(_directory, "data", "prompt-history.json");

    private string Workspace(string name = "workspace") => Path.Combine(_directory, name);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void MissingHistory_IsEmptyAndDoesNotCreateStorage()
    {
        var history = new ComposerPromptHistory(HistoryPath);

        Assert.Empty(history.GetEntries(Workspace()));
        history.Record(Workspace(), " \r\n\t ");
        Assert.False(Directory.Exists(Path.GetDirectoryName(HistoryPath)));
    }

    [Fact]
    public void DefaultPath_UsesExistingWorkbenchDataRoot()
    {
        Assert.Equal(Path.Combine(WorkbenchDataPaths.Root, "prompt-history.json"), ComposerPromptHistory.DefaultPath);
    }

    [Fact]
    public void Record_TrimsTextAndDeduplicatesOnlyConsecutiveIdenticalEntries()
    {
        var history = new ComposerPromptHistory(HistoryPath);

        history.Record(Workspace(), "  first\r\nline  ");
        history.Record(Workspace(), "first\r\nline");
        history.Record(Workspace(), "  ");
        history.Record(Workspace(), "First\r\nline");
        history.Record(Workspace(), "first\r\nline");

        Assert.Equal(["first\r\nline", "First\r\nline", "first\r\nline"], history.GetEntries(Workspace()));
        Assert.Equal(history.GetEntries(Workspace()), new ComposerPromptHistory(HistoryPath).GetEntries(Workspace()));
    }

    [Fact]
    public void Record_PersistsSeparateWorkspaceHistoriesAcrossRestarts()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        history.Record(Workspace("one"), "Hello, 世界");
        history.Record(Workspace("two"), "/report compare models");
        history.Record(Workspace("one"), "second");

        var restarted = new ComposerPromptHistory(HistoryPath);
        Assert.Equal(["Hello, 世界", "second"], restarted.GetEntries(Workspace("one")));
        Assert.Equal(["/report compare models"], restarted.GetEntries(Workspace("two")));
        Assert.Empty(restarted.GetEntries(Workspace("three")));
    }

    [Fact]
    public void Record_KeepsNewestThirtyPerWorkspaceInOldestToNewestOrder()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        history.Record(Workspace("other"), "unrelated");
        for (var index = 0; index < 40; index++)
        {
            history.Record(Workspace(), "prompt " + index);
        }

        var expected = Enumerable.Range(10, ComposerPromptHistory.MaxEntries).Select(index => "prompt " + index).ToArray();
        Assert.Equal(30, ComposerPromptHistory.MaxEntries);
        Assert.Equal(expected, history.GetEntries(Workspace()));
        Assert.Equal(expected, new ComposerPromptHistory(HistoryPath).GetEntries(Workspace()));
        Assert.Equal(["unrelated"], history.GetEntries(Workspace("other")));
    }

    [Fact]
    public void WorkspaceKeys_NormalizeRelativePathsDotSegmentsAndTrailingSeparators()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        var root = Workspace();
        history.Record(root, "one");
        history.Record(Path.Combine(root, "nested", "..") + Path.DirectorySeparatorChar, "two");
        history.Record(Path.GetRelativePath(Environment.CurrentDirectory, root), "three");

        Assert.Equal(["one", "two", "three"], history.GetEntries(root + Path.DirectorySeparatorChar));
        Assert.Equal(["one", "two", "three"], new ComposerPromptHistory(HistoryPath).GetEntries(root));
        var stored = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(HistoryPath))!;
        Assert.Equal(Path.GetFullPath(root), Assert.Single(stored).Key);
    }

    [Fact]
    public void WorkspaceKeys_RespectPlatformPathCaseSensitivity()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        history.Record(Workspace("Case"), "one");
        history.Record(Workspace("case"), "two");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(["one", "two"], history.GetEntries(Workspace("Case")));
            Assert.Equal(["one", "two"], new ComposerPromptHistory(HistoryPath).GetEntries(Workspace("case")));
        }
        else
        {
            Assert.Equal(["one"], history.GetEntries(Workspace("Case")));
            Assert.Equal(["two"], history.GetEntries(Workspace("case")));
        }
    }

    [Fact]
    public void GetEntries_ReturnsIndependentSnapshots()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        history.Record(Workspace(), "one");
        var snapshot = history.GetEntries(Workspace());
        history.Record(Workspace(), "two");

        Assert.Equal(["one"], snapshot);
        Assert.Equal(["one", "two"], history.GetEntries(Workspace()));
        if (snapshot is string[] entries)
        {
            entries[0] = "changed outside service";
        }
        Assert.Equal(["one", "two"], history.GetEntries(Workspace()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"workspace\": 123}")]
    public void InvalidHistory_StartsEmptyAndCanBeReplacedByNewRecords(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, content);

        var history = new ComposerPromptHistory(HistoryPath);
        Assert.Empty(history.GetEntries(Workspace()));
        history.Record(Workspace(), "recovered");

        Assert.Equal(["recovered"], new ComposerPromptHistory(HistoryPath).GetEntries(Workspace()));
    }

    [Fact]
    public void Load_ReappliesTrimmingDedupeAndLimitAndSkipsInvalidEntries()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        var prompts = Enumerable.Range(0, 35).SelectMany(index => new[] { " prompt " + index + " ", "prompt " + index }).ToArray();
        var stored = new Dictionary<string, string?[]?>
        {
            [Workspace() + Path.DirectorySeparatorChar] = [null, "", " ", .. prompts],
            [Workspace("missing")] = null,
            [""] = ["invalid root"],
            ["\0invalid"] = ["invalid path"],
        };
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(stored));

        var history = new ComposerPromptHistory(HistoryPath);

        Assert.Equal(Enumerable.Range(5, 30).Select(index => "prompt " + index), history.GetEntries(Workspace()));
        Assert.Empty(history.GetEntries(Workspace("missing")));
    }

    [Fact]
    public void DuplicateRecord_DoesNotRewritePersistedHistory()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        history.Record(Workspace(), "one");
        File.SetLastWriteTimeUtc(HistoryPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var lastWrite = File.GetLastWriteTimeUtc(HistoryPath);

        history.Record(Workspace(), " one ");

        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(HistoryPath));
    }

    [Fact]
    public void UnwritableStorage_DoesNotRejectAnAcceptedPromptOrLeaveTemporaryFiles()
    {
        Directory.CreateDirectory(HistoryPath);
        var history = new ComposerPromptHistory(HistoryPath);

        history.Record(Workspace(), "one");
        history.Record(Workspace(), "two");

        Assert.Equal(["one", "two"], history.GetEntries(Workspace()));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(HistoryPath)!));
    }

    [Fact]
    public void SuccessfulRecords_LeaveOnlyTheHistoryFileInStorage()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        history.Record(Workspace(), "one");
        history.Record(Workspace(), "two");

        Assert.Equal(HistoryPath, Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(HistoryPath)!)));
    }

    [Fact]
    public void WorkspaceKeys_PreserveFilesystemRoots()
    {
        var history = new ComposerPromptHistory(HistoryPath);
        var root = Path.GetPathRoot(_directory)!;
        history.Record(root, "root prompt");

        Assert.Equal(["root prompt"], new ComposerPromptHistory(HistoryPath).GetEntries(root));
    }
}
