using System.Diagnostics;
using System.Text;
using Alcoler.Adapters.Grep;
using Alcoler.Adapters.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class GrepNativeTests
{
    [Fact]
    public void NativeLibrary_IsAvailableWithCompatibleAbi()
    {
        // win-x64 is the delivered RID and must always load; other RIDs without
        // a source build legitimately fall back to the managed engine.
        if (OperatingSystem.IsWindows() && Environment.Is64BitProcess)
            Assert.True(AlcoGrepNative.IsAvailable);
        else if (!AlcoGrepNative.IsAvailable)
            return;
        Assert.Equal(AlcoGrepAbi.AbiMajor, AlcoGrepRaw.GrepAbiVersion() >> 16);
    }

    [Fact]
    public async Task NativeEngine_RespectsGitignore()
    {
        var native = AlcoGrepEngine.TryCreate();
        if (native is null)
            return;
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, ".gitignore"), "ignored.txt\nsub/\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "ignored.txt"), "target\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "keep.txt"), "target\n");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "sub"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "sub", "deep.txt"), "target\n");

        var result = await new GrepTool(native).ExecuteAsync(
            workspace.ContextFor("""{"pattern": "target"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Found 1 file\nkeep.txt", result.SingleText());
    }

    [Fact]
    public async Task NativeEngine_RespectsIgnoreFile()
    {
        var native = AlcoGrepEngine.TryCreate();
        if (native is null)
            return;
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, ".ignore"), "*.log\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "noise.log"), "target\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "keep.txt"), "target\n");

        var result = await new GrepTool(native).ExecuteAsync(
            workspace.ContextFor("""{"pattern": "target"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("Found 1 file\nkeep.txt", result.SingleText());
    }

    [Fact]
    public async Task CancelledToken_ThrowsOperationCancelled()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "one.txt"), "hello world");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new GrepTool().ExecuteAsync(
                workspace.ContextFor("""{"pattern": "hello"}"""),
                cancelled.Token));
    }

    [Fact]
    public async Task LongContentLines_AreTruncatedInContentMode()
    {
        using var workspace = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "long.txt"), "hit" + new string('x', 800));
        var result = await new GrepTool().ExecuteAsync(
            workspace.ContextFor("""{"pattern": "hit", "output_mode": "content"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var firstLine = result.SingleText().Split('\n')[0];
        Assert.EndsWith(" [...truncated]", firstLine);
        Assert.True(firstLine.Length <= "long.txt:1:".Length + GrepTool.MaxColumnChars + " [...truncated]".Length,
            $"line was {firstLine.Length} chars");
    }

    [Fact]
    public void BundledRipgrep_RunsAndReportsAlignedVersion()
    {
        var rg = Path.Combine(AppContext.BaseDirectory, "tools", "ripgrep", "rg.exe");
        if (!File.Exists(rg))
            return;
        using var process = Process.Start(new ProcessStartInfo(rg, "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        var output = process!.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.StartsWith("ripgrep 14.1.1", output);
    }

    [Fact]
    public void BashShim_PreambleIsEmittedOnlyWithoutSystemRg()
    {
        var root = Path.Combine(Path.GetTempPath(), "alcoler-shim-" + Guid.NewGuid().ToString("N"));
        var rgPath = Path.Combine(root, "tools", "ripgrep", "rg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(rgPath)!);
        try
        {
            File.WriteAllText(rgPath, "placeholder");

            Assert.Equal("", BashRipgrepShim.BuildForTests(
                name => name == "PATH" ? @"C:/some-dir" : null,
                _ => true,
                root));

            Assert.Equal($"rg() {{ {Escape(rgPath)} \"$@\"; }}; ", BashRipgrepShim.BuildForTests(
                name => name == "PATH" ? @"C:/some-dir" : null,
                path => path == rgPath,
                root));

            Assert.Equal("", BashRipgrepShim.BuildForTests(
                _ => null,
                _ => false,
                root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static string Escape(string value)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in value)
            {
                if (c is '\\' or '"' or '$' or '`')
                    builder.Append('\\');
                builder.Append(c);
            }
            return builder.Append('"').ToString();
        }
    }

    [Fact]
    public async Task BashSession_CanInvokeRg()
    {
        if (!PosixShellAvailable() || (RipgrepBinary.FindOnPath() is null && RipgrepBinary.LocateBundled() is null))
            return;
        using var workspace = new TempWorkspace();
        var result = await new BashTool().ExecuteAsync(
            workspace.ContextFor("""{"command": "rg --version"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("ripgrep", result.SingleText());
    }

    internal static bool PosixShellAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        var spec = ShellResolver.Resolve();
        return Path.GetFileNameWithoutExtension(spec.FileName) is "bash" or "sh" or "zsh" or "dash";
    }
}
