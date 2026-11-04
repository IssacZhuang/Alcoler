using System.Collections;
using System.Reflection;
using System.Text.Json;
using Alcoler.Adapters.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

/// <summary>
/// The managed engine is the fallback; these tests keep it output-identical to
/// the native ripgrep engine on the core scenarios the tool contract pins.
/// </summary>
public class GrepToolEngineParityTests
{
    [Fact]
    public async Task NativeEngine_MatchesManagedEngine_OnCoreScenarios()
    {
        var native = AlcoGrepEngine.TryCreate();
        if (native is null)
            return;
        var managed = new ManagedGrepEngine();

        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "hello world\r\nnothing here");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "two.txt"), "nope\nhello again\nHELLO shout");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "ctx.txt"), "a\nb\nc\nd\ne\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "o.txt"), "foo bar baz qux\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "m.txt"), "start\nmiddle\nend\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.cs"), "target code\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.py"), "target code\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "skip.log"), "target noise\n");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "sub", "deep.cs"), "target deep\n");
        var binary = new byte[] { 0x68, 0x00, 0x65, 0x6C, 0x6C, 0x6F };
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "blob.bin"), binary);
        File.SetLastWriteTimeUtc(Path.Combine(workspace.Root, "one.txt"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(workspace.Root, "two.txt"), new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var inputs = new[]
        {
            """{"pattern": "hello", "output_mode": "content"}""",
            """{"pattern": "hello", "output_mode": "content", "-i": true}""",
            """{"pattern": "hello", "output_mode": "content", "-n": false}""",
            """{"pattern": "hello"}""",
            """{"pattern": "hello", "output_mode": "count"}""",
            """{"pattern": "c", "output_mode": "content", "-C": 1}""",
            """{"pattern": "c", "output_mode": "content", "-B": 2, "-A": 1}""",
            """{"pattern": "b\\w+", "output_mode": "content", "-o": true}""",
            """{"pattern": "start.*end", "output_mode": "content", "multiline": true}""",
            """{"pattern": "start.*end", "output_mode": "count", "multiline": true}""",
            """{"pattern": "target", "output_mode": "content", "glob": "*.cs"}""",
            """{"pattern": "target", "output_mode": "content", "glob": "*.{cs,py}"}""",
            """{"pattern": "target", "output_mode": "content", "glob": "**/*.cs"}""",
            """{"pattern": "target", "output_mode": "content", "type": "cs"}""",
            """{"pattern": "target", "output_mode": "content", "type": "xyz"}""",
            """{"pattern": "target", "output_mode": "content", "head_limit": 1, "offset": 1}""",
            """{"pattern": "target", "output_mode": "count", "head_limit": 0}""",
            """{"pattern": "hello", "output_mode": "content", "path": "one.txt"}""",
            """{"pattern": "target", "output_mode": "content", "path": "sub"}""",
            """{"pattern": "missing", "output_mode": "content"}""",
            """{"pattern": "hello"}""",
            """{"pattern": "hit", "output_mode": "content", "head_limit": 2}""",
        };

        foreach (var input in inputs)
        {
            var nativeText = await RunAsync(native!, workspace, input);
            var managedText = await RunAsync(managed, workspace, input);
            Assert.Equal(managedText, nativeText);
        }
    }

    [Fact]
    public async Task Engines_ReportIdenticalInvalidPatternErrors()
    {
        var native = AlcoGrepEngine.TryCreate();
        if (native is null)
            return;
        using var workspace = new TempWorkspace();
        var result = await new GrepTool(native).ExecuteAsync(
            workspace.ContextFor("""{"pattern": "("}"""),
            CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal("Invalid grep regular expression: (", result.SingleText());
    }

    [Fact]
    public async Task ManagedEngine_RegexCacheRetentionIsBoundedAcrossUniqueSearches()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "needle");
        var engine = new ManagedGrepEngine();
        var prefix = Guid.NewGuid().ToString("N");
        for (var index = 0; index < 256; index++)
        {
            await RunAsync(engine, workspace, JsonSerializer.Serialize(new { pattern = prefix + index }));
        }

        var cache = (IDictionary)typeof(ManagedGrepEngine)
            .GetField("RegexCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.InRange(cache.Count, 0, 128);
    }

    [Theory]
    [InlineData("content")]
    [InlineData("count")]
    [InlineData("files_with_matches")]
    public void BoundedSelection_MatchesGlobalSortForInterleavedReversePathEvents(string mode)
    {
        using var workspace = new TempWorkspace();
        var paths = Enumerable.Range(0, 32).Select(index => Path.Combine(workspace.Root, $"{31 - index:D2}.txt")).ToArray();
        for (var index = 0; index < paths.Length; index++)
        {
            File.WriteAllText(paths[index], "hit");
            File.SetLastWriteTimeUtc(paths[index], new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(index % 3));
        }
        var limit = mode == "content" ? 45 : 11;
        var full = new GrepResultCollector(Request(workspace.Root, mode, null));
        var limited = new GrepResultCollector(Request(workspace.Root, mode, limit));
        if (mode == "content")
        {
            for (var line = 1; line <= 40; line++)
            {
                foreach (var path in paths)
                {
                    full.AddContent(path, line, $"line {line}");
                    limited.AddContent(path, line, $"line {line}");
                }
            }
        }
        else
        {
            for (var index = 0; index < paths.Length; index++)
            {
                full.AddFile(paths[index], index + 1);
                limited.AddFile(paths[index], index + 1);
            }
        }

        var expected = full.Complete();
        var actual = limited.Complete();
        Assert.Equal(expected.Entries.Take(limit), actual.Entries);
        Assert.Equal(expected.TotalEntries, actual.TotalEntries);
        Assert.Equal(expected.TotalMatches, actual.TotalMatches);
        Assert.Equal(limit, actual.Entries.Count);
        Assert.Equal(mode == "content" ? 1280 : 32, actual.TotalEntries);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FiniteDenseLongMatches_RetainOnlySelectedPrefixesAndBoundAllocations(bool useNative, bool context)
    {
        IGrepEngine? engine = useNative ? AlcoGrepEngine.TryCreate() : new ManagedGrepEngine();
        if (engine is null)
            return;
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "dense.txt");
        var warmPath = Path.Combine(workspace.Root, "warm.txt");
        await File.WriteAllTextAsync(warmPath, "hitwarm\n");
        var padding = new string('x', 16_384);
        var block = $"hit{padding}\ncontext{padding}\ngap{padding}\n";
        var content = string.Concat(Enumerable.Repeat(block, 128));
        await File.WriteAllTextAsync(path, content);
        var request = new GrepEngineRequest
        {
            Pattern = "hit\\w+",
            SearchPath = path,
            FilterRoot = workspace.Root,
            Mode = "content",
            GlobPatterns = [],
            IsSingleFile = true,
            OnlyMatching = true,
            BeforeContext = context ? 1 : 0,
            AfterContext = context ? 1 : 0,
            RetainedEntryLimit = 4,
        };
        engine.Search(Request(warmPath, "content", 4), CancellationToken.None);
        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = engine.Search(request, CancellationToken.None);

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(context ? 383 : 128, result.TotalEntries);
        Assert.Equal(4, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.Equal(501, entry.Text.Length));
        Assert.Equal(context ? new int?[] { 1, 2, 3, 4 } : [1, 4, 7, 10], result.Entries.Select(entry => entry.LineNumber));
        var budget = useNative ? 1024 * 1024 : content.Length * 5L + 2 * 1024 * 1024;
        Assert.True(allocated < budget, $"Allocated {allocated} bytes; budget {budget}, native={useNative}, context={context}.");
    }

    [Fact]
    public async Task Engines_PreservePagingAcrossContextsSubmatchesAndUnlimitedOutput()
    {
        var native = AlcoGrepEngine.TryCreate();
        if (native is null)
            return;
        using var workspace = new TempWorkspace();
        var content = string.Concat(Enumerable.Range(0, 40).Select(index => $"before {index}\nhitAAA hitBBB\nbetween\nhitCCC\nlast\n"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "z.txt"), content);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.txt"), content);
        var managed = new ManagedGrepEngine();
        var inputs = new[]
        {
            """{"pattern":"hit","output_mode":"content","head_limit":7,"offset":77,"-C":1}""",
            """{"pattern":"hit\\w+","output_mode":"content","head_limit":7,"offset":5,"-o":true,"-B":2,"-A":1}""",
            """{"pattern":"hitAAA.*?hitCCC","output_mode":"content","multiline":true,"-o":true,"head_limit":9,"offset":3,"-C":1}""",
            """{"pattern":"hit","output_mode":"content"}""",
            """{"pattern":"hit","output_mode":"content","head_limit":0,"offset":150}""",
            """{"pattern":"hit","output_mode":"content","head_limit":5,"offset":1000}""",
            """{"pattern":"hit","output_mode":"count","head_limit":1,"offset":1}""",
            """{"pattern":"hit","output_mode":"count","head_limit":0,"offset":1}""",
            """{"pattern":"hit","head_limit":1,"offset":1}""",
            """{"pattern":"hit","head_limit":0,"offset":1}""",
        };

        foreach (var input in inputs)
            Assert.Equal(await RunAsync(managed, workspace, input), await RunAsync(native, workspace, input));
    }

    [Theory]
    [InlineData(500, "")]
    [InlineData(500, "tail")]
    [InlineData(499, "\u4E00tail")]
    [InlineData(499, "\U0001F600tail")]
    public async Task Engines_PreserveExactUtf16DisplayBoundary(int prefixLength, string tail)
    {
        using var workspace = new TempWorkspace();
        var content = new string('x', prefixLength) + tail;
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "unicode.txt"), content);
        var input = """{"pattern":"x","output_mode":"content","head_limit":1}""";
        var expected = "unicode.txt:1:" + GrepTool.TruncateLine(content);
        Assert.Equal(expected, await RunAsync(new ManagedGrepEngine(), workspace, input));
        if (AlcoGrepEngine.TryCreate() is { } native)
            Assert.Equal(expected, await RunAsync(native, workspace, input));
    }

    private static GrepEngineRequest Request(string path, string mode, long? limit) => new()
    {
        Pattern = "hit",
        SearchPath = path,
        FilterRoot = Path.GetDirectoryName(path)!,
        IsSingleFile = File.Exists(path),
        Mode = mode,
        GlobPatterns = [],
        RetainedEntryLimit = limit,
    };

    private static async Task<string> RunAsync(IGrepEngine engine, TempWorkspace workspace, string input)
    {
        var tool = new GrepTool(engine);
        var result = await tool.ExecuteAsync(workspace.ContextFor(input), CancellationToken.None);
        return result.SingleText();
    }
}
