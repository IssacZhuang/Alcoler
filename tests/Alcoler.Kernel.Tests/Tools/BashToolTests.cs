using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Alcoler.Adapters.Tools;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tests.Storage;
using Alcoler.Kernel.Tools;
using Xunit;

namespace Alcoler.Kernel.Tests.Tools;

public class BashToolTests
{
    [Fact]
    public async Task EchoCommand_RoundTripsOutput()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo hello"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("hello", result.SingleText());
    }

    [Theory]
    [InlineData(ShellDialect.Cmd)]
    [InlineData(ShellDialect.GitBash)]
    public async Task SessionShell_OverridesConstructor_ForBuiltinsPipelinesAndErrors(ShellDialect dialect)
    {
        var environment = InstalledWindowsShell(dialect);
        if (environment is null)
            return;
        using var workspace = new TempWorkspace();
        var shell = new ShellSessionState(workspace.Root, environment);
        var tool = new BashTool(new ShellSpec("unused-fallback-shell", "-c"));
        var builtin = dialect == ShellDialect.Cmd ? "echo selected-cmd" : "printf 'selected-posix'";
        var pipeline = dialect == ShellDialect.Cmd ? "echo pipeline | findstr pipeline" : "printf 'pipeline' | cat";
        var failure = dialect == ShellDialect.Cmd ? "cmd /d /c exit 7" : "false";

        var result = await tool.ExecuteAsync(workspace.ContextFor(JsonSerializer.Serialize(new { command = builtin }), shell), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(dialect == ShellDialect.Cmd ? "selected-cmd" : "selected-posix", result.SingleText());
        result = await tool.ExecuteAsync(workspace.ContextFor(JsonSerializer.Serialize(new { command = pipeline }), shell), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("pipeline", result.SingleText());
        result = await tool.ExecuteAsync(workspace.ContextFor(JsonSerializer.Serialize(new { command = failure }), shell), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.StartsWith(dialect == ShellDialect.Cmd ? "Exit code 7" : "Exit code 1", result.SingleText());
    }

    [Theory]
    [InlineData(ShellDialect.Cmd)]
    [InlineData(ShellDialect.GitBash)]
    public async Task SessionShell_CapturesSpacesCwd_AndStartsExplicitBackgroundInIt(ShellDialect dialect)
    {
        var environment = InstalledWindowsShell(dialect);
        if (environment is null)
            return;
        using var workspace = new TempWorkspace();
        var subdirectory = Path.Combine(workspace.Root, "space ! and & directory");
        Directory.CreateDirectory(subdirectory);
        var shell = new ShellSessionState(workspace.Root, environment);
        var tool = new BashTool(new ShellSpec("unused-fallback-shell", "-c"));
        var command = dialect == ShellDialect.Cmd ? "cd /d \"space ! and & directory\"" : "cd 'space ! and & directory'";
        var result = await tool.ExecuteAsync(workspace.ContextFor(JsonSerializer.Serialize(new { command }), shell), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(subdirectory, shell.CurrentDirectory, ignoreCase: true);
        Assert.Equal("", result.SingleText());

        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        command = dialect == ShellDialect.Cmd
            ? "echo cmd-background>\"background proof.txt\" & cd"
            : "printf 'posix-background' > 'background proof.txt'; pwd";
        result = await tool.ExecuteAsync(workspace.ContextFor(JsonSerializer.Serialize(new { command, run_in_background = true }), shell, jobs: jobs), CancellationToken.None);
        Assert.False(result.IsError);
        var job = Assert.Single(jobs.List());
        await WaitForJobCompletionAsync(jobs, job.Id);
        Assert.Equal(0, jobs.Find(job.Id)!.ExitCode);
        Assert.Equal(dialect == ShellDialect.Cmd ? "cmd-background" : "posix-background",
            (await File.ReadAllTextAsync(Path.Combine(subdirectory, "background proof.txt"))).Trim());
        var output = await File.ReadAllTextAsync(job.OutputPath!);
        Assert.Contains("space ! and & directory", output);
        Assert.DoesNotContain(BashProcessSession.CwdMarker, output);
        Assert.Equal(subdirectory, shell.CurrentDirectory, ignoreCase: true);

        result = await tool.ExecuteAsync(workspace.ContextFor("""{"command":"cd .."}""", shell), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(workspace.Root, shell.CurrentDirectory, ignoreCase: true);
        result = await tool.ExecuteAsync(workspace.ContextFor("""{"command":"cd .."}""", shell), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(workspace.Root, shell.CurrentDirectory, ignoreCase: true);
        Assert.Contains($"Shell cwd was reset to {workspace.Root}", result.SingleText());
    }

    [Fact]
    public async Task CmdFallback_PreservesQuotedExecutableAndForLoopSyntax()
    {
        var environment = InstalledWindowsShell(ShellDialect.Cmd);
        if (environment is null)
            return;
        using var workspace = new TempWorkspace();
        var tool = new BashTool(new ShellSpec(environment.FileName, "/c"));
        var command = $"\"{environment.FileName}\" /d /c echo quoted-executable";
        var result = await tool.ExecuteAsync(workspace.ContextFor(JsonSerializer.Serialize(new { command })), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("quoted-executable", result.SingleText());
        result = await tool.ExecuteAsync(workspace.ContextFor("""{"command":"for %i in (one two) do @echo %i"}"""), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal("one\ntwo", result.SingleText().Replace("\r\n", "\n"));
        Assert.DoesNotContain(BashProcessSession.CwdMarker, result.SingleText());
    }

    [Fact]
    public async Task SameTool_ConcurrentCmdAndGitBashSessions_UseIndependentForegroundAndBackgroundShells()
    {
        var cmd = InstalledWindowsShell(ShellDialect.Cmd);
        var bash = InstalledWindowsShell(ShellDialect.GitBash);
        if (cmd is null || bash is null)
            return;
        using var workspace = new TempWorkspace();
        var cmdDirectory = Path.Combine(workspace.Root, "cmd session");
        var bashDirectory = Path.Combine(workspace.Root, "bash session");
        Directory.CreateDirectory(cmdDirectory);
        Directory.CreateDirectory(bashDirectory);
        var cmdState = new ShellSessionState(cmdDirectory, cmd);
        var bashState = new ShellSessionState(bashDirectory, bash);
        var tool = new BashTool(new ShellSpec("unused-fallback-shell", "-c"));
        var results = await Task.WhenAll(
            tool.ExecuteAsync(workspace.ContextFor("""{"command":"echo %COMSPEC%"}""", cmdState), CancellationToken.None).AsTask(),
            tool.ExecuteAsync(workspace.ContextFor("""{"command":"printf '%s' \"$BASH_VERSION\""}""", bashState), CancellationToken.None).AsTask());
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.EndsWith("cmd.exe", results[0].SingleText(), StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"^\d+\.\d+", results[1].SingleText());
        Assert.Equal(cmdDirectory, cmdState.CurrentDirectory, ignoreCase: true);
        Assert.Equal(bashDirectory, bashState.CurrentDirectory, ignoreCase: true);

        var cmdJobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var bashJobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        results = await Task.WhenAll(
            tool.ExecuteAsync(workspace.ContextFor("""{"command":"echo %COMSPEC%","run_in_background":true}""", cmdState, jobs: cmdJobs), CancellationToken.None).AsTask(),
            tool.ExecuteAsync(workspace.ContextFor("""{"command":"printf '%s' \"$BASH_VERSION\"","run_in_background":true}""", bashState, jobs: bashJobs), CancellationToken.None).AsTask());
        Assert.All(results, result => Assert.False(result.IsError));
        var cmdJob = Assert.Single(cmdJobs.List());
        var bashJob = Assert.Single(bashJobs.List());
        await Task.WhenAll(WaitForJobCompletionAsync(cmdJobs, cmdJob.Id), WaitForJobCompletionAsync(bashJobs, bashJob.Id));
        Assert.Equal(0, cmdJobs.Find(cmdJob.Id)!.ExitCode);
        Assert.Equal(0, bashJobs.Find(bashJob.Id)!.ExitCode);
        Assert.EndsWith("cmd.exe", (await File.ReadAllTextAsync(cmdJob.OutputPath!)).Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"^\d+\.\d+", await File.ReadAllTextAsync(bashJob.OutputPath!));
    }

    private static ShellEnvironment? InstalledWindowsShell(ShellDialect dialect) => OperatingSystem.IsWindows()
        ? ShellResolver.AvailableShells().FirstOrDefault(shell => shell.Dialect == dialect)
        : null;

    [Fact]
    public async Task NonZeroExitCode_MarksErrorResult_WithExitCodeHeader()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "exit 3"}"""),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.StartsWith("Exit code 3", result.SingleText());
    }

    [Fact]
    public async Task SemanticExitCode_GrepNoMatch_IsNotError()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        File.WriteAllText(Path.Combine(workspace.Root, "file.txt"), "line one\nline two\n");
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "grep needle file.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(BashSemantics.NoMatchesFound, result.SingleText());
    }

    [Fact]
    public async Task WorkingDirectory_IsWorkspaceRoot()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo wd123 > wd-probe.txt"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.True(File.Exists(Path.Combine(workspace.Root, "wd-probe.txt")));
    }

    [Fact]
    public async Task Timeout_KillsProcessTree_AndReportsAbort()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "ping -n 20 127.0.0.1", "timeout": 1000}"""),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.IsError);
        Assert.Contains("<error>Command was aborted before completion</error>", result.SingleText());
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TimeoutParam_IsAccepted_DangerouslyDisableSandbox_IsNoOp()
    {
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo sandboxed", "timeout": 30000, "dangerouslyDisableSandbox": true}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("sandboxed", result.SingleText());
    }

    [Fact]
    public void Metadata_RequiresApproval_AndClassifiesReadOnlyInput()
    {
        var tool = new BashTool();
        Assert.True(tool.Metadata.NeedsApproval);

        Assert.True(tool.IsReadOnlyInput(Json("""{"command": "git status"}""")));
        Assert.True(tool.IsReadOnlyInput(Json("""{"command": "cat notes.txt"}""")));
        Assert.False(tool.IsReadOnlyInput(Json("""{"command": "rm -rf build"}""")));
        Assert.False(tool.IsReadOnlyInput(Json("""{"command": "echo nope"}""")));
        Assert.False(tool.IsReadOnlyInput(Json("""{"command": "grep x f > out.txt"}""")));
    }

    [Fact]
    public void ContractSchema_ExposesTimeoutAndDangerouslyDisableSandbox()
    {
        var contract = new BashTool().Contract;

        Assert.Contains($"- `timeout` is in milliseconds: default {BashTool.DefaultTimeoutMs}, max {BashTool.MaxTimeoutMs}.", contract.Description);
        var properties = contract.InputSchema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("timeout_ms", out _));
        var timeout = properties.GetProperty("timeout");
        Assert.Equal($"Optional timeout in milliseconds (max {BashTool.MaxTimeoutMs})", timeout.GetProperty("description").GetString());
        var sandbox = properties.GetProperty("dangerouslyDisableSandbox");
        Assert.Equal("boolean", sandbox.GetProperty("type").GetString());
        Assert.Equal(
            "Set this to true to dangerously override sandbox mode and run commands without sandboxing.",
            sandbox.GetProperty("description").GetString());
    }

    [Fact]
    public async Task PersistentCwd_CapturedWithinWorkspace()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "sub"));
        var shell = new ShellSessionState(workspace.Root);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "cd sub"}""", shell),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(
            Path.Combine(workspace.Root, "sub"),
            shell.CurrentDirectory,
            ignoreCase: true);

        var followUp = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "pwd"}""", shell),
            CancellationToken.None);
        Assert.EndsWith("/sub", followUp.SingleText().Trim());
    }

    [Fact]
    public async Task CwdLeavingWorkspace_ResetsToRoot_AndAppendsStderrHint()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var shell = new ShellSessionState(workspace.Root);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "cd .."}""", shell),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(workspace.Root, shell.CurrentDirectory, ignoreCase: true);
        Assert.Contains($"Shell cwd was reset to {workspace.Root}", result.SingleText());
    }

    [Fact]
    public async Task OversizedOutput_PersistedWithEnvelope()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "yes 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx' | head -n 2000"}"""),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.StartsWith("<persisted-output>", text);
        Assert.Contains("Output too large (", text);
        Assert.Contains("). Full output saved to: ", text);
        Assert.Contains("Preview (first 2KB):", text);
        Assert.EndsWith("</persisted-output>", text);
    }

    [Fact]
    public async Task ModifiedPreviouslyReadFile_AppendsStaleReadHint()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "tracked.txt");
        File.WriteAllText(path, "before");
        var fileReads = new FileReadState();
        fileReads.RecordRead(path, completeView: true);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo after > tracked.txt"}""", fileReads: fileReads),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains(
            "[This command modified 1 file you've previously read: tracked.txt. Call Read before editing.]",
            result.SingleText());
    }

    [Fact]
    public async Task UnchangedReadFiles_DoNotProduceHint()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var path = Path.Combine(workspace.Root, "tracked.txt");
        File.WriteAllText(path, "same");
        var fileReads = new FileReadState();
        fileReads.RecordRead(path, completeView: true);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "cat tracked.txt"}""", fileReads: fileReads),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.DoesNotContain("previously read", result.SingleText());
    }

    [Fact]
    public async Task RunInBackground_StartsJob_AndStreamsOutputToFile()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "echo bg-output-line", "run_in_background": true}""", jobs: jobs),
            CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.StartsWith("Command running in background with ID: ", text);
        Assert.Contains("Output is being written to: ", text);
        Assert.Contains("You will be notified when it completes.", text);
        Assert.Contains("To check interim output, use Read on that file path.", text);

        var job = Assert.Single(jobs.List());
        Assert.Equal(BackgroundJobStatus.Running, job.Status);
        await WaitForJobCompletionAsync(jobs, job.Id);
        Assert.Equal(BackgroundJobStatus.Completed, jobs.Find(job.Id)!.Status);
        Assert.Equal(0, jobs.Find(job.Id)!.ExitCode);
        Assert.Contains("bg-output-line", await File.ReadAllTextAsync(jobs.Find(job.Id)!.OutputPath!));
    }

    [Fact]
    public async Task RunInBackground_WritesOutputUnderToolOutputRoot()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        using var outputRoot = new TempDirectory();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor(
                """{"command": "echo routed-output", "run_in_background": true}""",
                jobs: jobs,
                toolOutputRoot: outputRoot.Root),
            CancellationToken.None);

        Assert.False(result.IsError);
        var job = Assert.Single(jobs.List());
        Assert.StartsWith(Path.Combine(outputRoot.Root, "sess_test"), job.OutputPath, StringComparison.OrdinalIgnoreCase);
        await WaitForJobCompletionAsync(jobs, job.Id);
        Assert.Contains("routed-output", await File.ReadAllTextAsync(job.OutputPath!));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, ".alcoler")));
    }

    [Fact]
    public async Task Timeout_OnEligibleCommand_ConvertsToBackground()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new BashTool();
        var stopwatch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor(
                """{"command": "ping -n 20 127.0.0.1", "timeout": 1000}""",
                jobs: jobs),
            CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.IsError);
        Assert.StartsWith("Command exceeded the blocking budget (1s) and was moved to the background with ID: ", result.SingleText());
        var job = Assert.Single(jobs.List());
        Assert.True(File.Exists(job.OutputPath));

        jobs.Stop(job.Id);
        await WaitForJobCompletionAsync(jobs, job.Id);
        Assert.Equal(BackgroundJobStatus.Stopped, jobs.Find(job.Id)!.Status);
    }

    [Fact]
    public async Task Timeout_OnSleepCommand_IsNotConvertedToBackground()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var jobs = new BackgroundJobService(TimeProvider.System, static _ => ValueTask.CompletedTask);
        var tool = new BashTool();

        var result = await tool.ExecuteAsync(
            workspace.ContextFor("""{"command": "sleep 5", "timeout": 500}""", jobs: jobs),
            CancellationToken.None);

        Assert.Contains("<error>Command was aborted before completion</error>", result.SingleText());
        Assert.Empty(jobs.List());
    }

    [Theory]
    [InlineData("ping -n 20 127.0.0.1", true)]
    [InlineData("sleep 5", false)]
    [InlineData("  sleep 5", false)]
    [InlineData("sleep", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsAutoBackgroundEligible_ExcludesSleep(string command, bool expected)
    {
        Assert.Equal(expected, BashTool.IsAutoBackgroundEligible(command));
    }

    [Theory]
    [InlineData(null, null, 120_000, 600_000)]
    [InlineData("200000", "1000", 200_000, 200_000)]
    [InlineData("700000junk", null, 700_000, 700_000)]
    [InlineData("0", "-100", 120_000, 600_000)]
    public void TimeoutPolicy_UsesPositiveEnvironmentOverrides(string? configuredDefault, string? configuredMax, int expectedDefault, int expectedMax)
    {
        var policy = BashTimeoutPolicy.FromEnvironment(key => key == "BASH_DEFAULT_TIMEOUT_MS" ? configuredDefault : configuredMax);
        Assert.Equal(expectedDefault, policy.DefaultMs);
        Assert.Equal(expectedMax, policy.MaxMs);
        Assert.Equal(expectedDefault, policy.Resolve(0));
        Assert.Equal(-20, policy.Resolve(-20));
        Assert.Equal(expectedMax, policy.Resolve(int.MaxValue));
        var tool = new BashTool(ShellResolver.Resolve(), policy);
        Assert.Contains($"default {expectedDefault}, max {expectedMax}", tool.Contract.Description);
        Assert.False(tool.Contract.InputSchema.GetProperty("properties").GetProperty("timeout").TryGetProperty("minimum", out _));
    }

    [Fact]
    public async Task ProcessCapture_PreservesUnterminatedOutputAndBoundsMemory()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var output = Path.Combine(workspace.Root, "output.log");
        using var session = BashProcessSession.Start(ShellResolver.Resolve(),
            "printf 'start'; printf '%080000d' 0; printf 'end'", workspace.Root, captureCwd: true, output);
        Assert.Equal(0, await session.WaitForExitAsync(CancellationToken.None));
        Assert.True(session.SnapshotStdout().Length <= session.InlineLimit);
        var persisted = await File.ReadAllTextAsync(output);
        Assert.StartsWith("start", persisted);
        Assert.EndsWith("end", persisted);
        Assert.DoesNotContain(BashProcessSession.CwdMarker, persisted);
        Assert.Equal(80_008, session.OutputBytes);
        Assert.True(session.TryExtractCapturedCwd(out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task PersistenceWriterFailure_ReturnsBoundedTailInsteadOfFakeArtifact(bool failOpen, bool unicode)
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var writes = 0;
        var tool = new BashTool(ShellResolver.Resolve(), openOutput: path => failOpen
            ? throw new IOException("output create failed")
            : new FailingOutputStream(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), () => writes++));
        var command = unicode
            ? "printf 'discarded-head'; for ((i=0;i<12000;i++)); do printf '\\344\\270\\255\\360\\237\\230\\200'; done; printf 'retained-tail'"
            : "printf 'discarded-head'; printf '%080000d' 0; printf 'retained-tail'";
        var result = await tool.ExecuteAsync(workspace.ContextFor(JsonSerializer.Serialize(new { command })), CancellationToken.None);

        Assert.False(result.IsError);
        var text = result.SingleText();
        Assert.DoesNotContain("<persisted-output>", text);
        Assert.DoesNotContain("discarded-head", text);
        Assert.Contains("retained-tail\n\n[Tool output truncated by resultBudget:", text);
        Assert.EndsWith("maxModelBytes=30000, strategy=artifact]", text);
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 29_990, 30_000);
        Assert.DoesNotContain('\ufffd', text);
        if (!failOpen)
            Assert.Equal(1, writes);
    }

    [Fact]
    public async Task PosixOutput_MergesStdoutAndStderrInWriteOrder()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var result = await new BashTool().ExecuteAsync(workspace.ContextFor(
            """{"command":"printf 'out-one\\n'; printf 'err-one\\n' >&2; printf 'out-two\\n'; printf 'err-two\\n' >&2"}"""), CancellationToken.None);
        Assert.Equal("out-one\nerr-one\nout-two\nerr-two", result.SingleText());
    }

    [Fact]
    public void OutputDecoder_PrefersUtf8AndCarriesSplitMultibyteCharacters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var decoder = new BashOutputDecoder(Encoding.GetEncoding(936));
        var utf8 = Encoding.UTF8.GetBytes("中");
        Assert.Equal("ascii ", decoder.Decode("ascii "u8));
        Assert.Equal("", decoder.Decode(utf8.AsSpan(0, 1)));
        Assert.Equal("中", decoder.Decode(utf8.AsSpan(1)));
        Assert.Equal("", decoder.Decode([], flush: true));
        var asciiPrefix = new string('a', 4095);
        var split = new BashOutputDecoder(Encoding.GetEncoding(936));
        Assert.Equal(asciiPrefix, split.Decode([.. Encoding.ASCII.GetBytes(asciiPrefix), utf8[0]]));
        Assert.Equal("中", split.Decode(utf8.AsSpan(1)));
    }

    [Fact]
    public void OutputDecoder_FallsBackToLegacyAndCarriesSplitCharacters()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var decoder = new BashOutputDecoder(Encoding.GetEncoding(936));
        Assert.Equal("ascii ", decoder.Decode("ascii "u8));
        Assert.Equal("", decoder.Decode([0xd6]));
        Assert.Equal("中文", decoder.Decode([0xd0, 0xce, 0xc4]));
        Assert.Equal("", decoder.Decode([], flush: true));
    }

    [Fact]
    public async Task ProcessCapture_DecodesLegacyBytesWithoutCorruptingUtf8Output()
    {
        if (!PosixShellAvailable())
            return;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var workspace = new TempWorkspace();
        var output = Path.Combine(workspace.Root, "legacy.log");
        using var session = BashProcessSession.Start(ShellResolver.Resolve(), "printf '\\326\\320\\316\\304'", workspace.Root,
            captureCwd: false, output, legacyEncoding: Encoding.GetEncoding(936));
        Assert.Equal(0, await session.WaitForExitAsync(CancellationToken.None));
        Assert.Equal("中文", session.SnapshotStdout());
        Assert.Equal("中文", await File.ReadAllTextAsync(output));
    }

    private sealed class FailingOutputStream(Stream inner, Action attempted) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            attempted();
            inner.Write(buffer, offset, Math.Min(count, 16));
            throw new IOException("output write failed");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task ProcessCapture_PersistedLimitStopsRunawayOutput()
    {
        if (!PosixShellAvailable())
            return;
        using var workspace = new TempWorkspace();
        var output = Path.Combine(workspace.Root, "limited.log");
        using var session = BashProcessSession.Start(ShellResolver.Resolve(), "yes runaway", workspace.Root,
            captureCwd: false, output, persistedLimit: 64_000);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, await session.WaitForExitAsync(timeout.Token));
        Assert.True(session.OutputBytes > 64_000);
        Assert.Equal(64_000, new FileInfo(output).Length);
        Assert.True(session.SnapshotStdout().Length <= session.InlineLimit);
    }

    [Theory]
    [InlineData(null, 30_000)]
    [InlineData("", 30_000)]
    [InlineData("5000junk", 5_000)]
    [InlineData("999999", 150_000)]
    public void OutputEnvironmentOverride_IsBounded(string? value, int expected) =>
        Assert.Equal(expected, BashProcessSession.ResolveInlineLimit(value));

    internal static bool PosixShellAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return true;
        var spec = ShellResolver.Resolve();
        return Path.GetFileNameWithoutExtension(spec.FileName) is "bash" or "sh" or "zsh" or "dash";
    }

    private static async Task WaitForJobCompletionAsync(BackgroundJobService jobs, BackgroundJobId id)
    {
        var deadline = Task.Delay(TimeSpan.FromSeconds(30));
        while (jobs.Find(id) is not { } job || job.Status == BackgroundJobStatus.Running)
        {
            if (deadline.IsCompleted)
                throw new TimeoutException("background job never completed");
            await Task.Delay(20);
        }
    }

    private static JsonElement Json(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
}

internal static class BashToolTestContext
{
    public static ToolExecutionContext ContextFor(
        this TempWorkspace workspace,
        string json,
        ShellSessionState? shell = null,
        FileReadState? fileReads = null,
        BackgroundJobService? jobs = null,
        string? toolOutputRoot = null) =>
        new(
            new SessionId("sess_test"),
            workspace.Root,
            System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(),
            workspace.Workspace,
            TimeProvider.System,
            FileReads: fileReads,
            Shell: shell,
            BackgroundJobs: jobs,
            ToolOutputRoot: toolOutputRoot);
}
