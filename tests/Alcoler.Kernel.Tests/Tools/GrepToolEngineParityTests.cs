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
    public void ForcedManagedEngine_StillPassesDefaultConstruction()
    {
        var tool = new GrepTool(new ManagedGrepEngine());
        Assert.Equal("Grep", tool.Contract.Name);
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

    private static async Task<string> RunAsync(IGrepEngine engine, TempWorkspace workspace, string input)
    {
        var tool = new GrepTool(engine);
        var result = await tool.ExecuteAsync(workspace.ContextFor(input), CancellationToken.None);
        return result.SingleText();
    }
}
