using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Adapters.Tools;

/// <summary>
/// Shell execution with ZCode-compatible semantics: persistent session cwd, semantic
/// exit-code interpretation, oversized-output persistence, read-state invalidation hints,
/// and foreground-to-background conversion on timeout. The tool manages its own timeout
/// (Metadata.DefaultTimeoutMs stays null) because conversion must not be cut short.
/// </summary>
public sealed class BashTool : ITool, IToolInputPolicy
{
    internal const int DefaultTimeoutMs = 120_000;
    internal const int MaxTimeoutMs = 600_000;

    private readonly ShellSpec _shell;
    private readonly BashTimeoutPolicy _timeouts;
    private readonly Func<string, Stream>? _openOutput;

    public BashTool() : this(ShellResolver.Resolve())
    {
    }

    internal BashTool(ShellSpec shell, BashTimeoutPolicy? timeouts = null, Func<string, Stream>? openOutput = null)
    {
        _shell = shell;
        _openOutput = openOutput;
        _timeouts = timeouts ?? BashTimeoutPolicy.FromEnvironment(Environment.GetEnvironmentVariable);
        Contract = CreateContract(_timeouts);
        Metadata = new(
            ReadOnly: false,
            Destructive: false,
            ConcurrentSafe: false,
            RequiresUserInteraction: false,
            SideEffectScope: SideEffectScope.System,
            RiskLevel: RiskLevel.Medium,
            NeedsApproval: true,
            AllowedInPlanMode: false,
            DefaultTimeoutMs: null,
            MaxTimeoutMs: _timeouts.MaxMs);
    }

    public ToolContract Contract { get; }

    private static ToolContract CreateContract(BashTimeoutPolicy timeouts) => new(
        "Bash",
        $"""
        Executes a bash command and returns its output.

        - Working directory persists between calls, but prefer absolute paths — `cd` in a compound command can trigger a permission prompt. Shell state (env vars, functions) does not persist; the shell is initialized from the user's profile.
        - IMPORTANT: Avoid using this tool to run `find`, `grep`, `cat`, `head`, `tail`, `sed`, `awk`, or `echo` commands, unless explicitly instructed or after you have verified that a dedicated tool cannot accomplish your task. Instead, use the appropriate dedicated tool as this will provide a much better experience for the user.
        - `timeout` is in milliseconds: default {timeouts.DefaultMs}, max {timeouts.MaxMs}.
        - `run_in_background` runs the command detached: it keeps running across turns and re-invokes you when it exits. No `&` needed.

        # Git
        - Interactive flags (`-i`, e.g. `git rebase -i`, `git add -i`) are not supported in this environment.
        - Use the `gh` CLI for GitHub operations (PRs, issues, API).
        - Commit or push only when the user asks.
        """,
        ToolInput.ParseSchema($$"""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "command": { "type": "string", "minLength": 1, "description": "The command to execute" },
            "timeout": { "type": "integer", "maximum": {{timeouts.MaxMs}}, "description": "Optional timeout in milliseconds (max {{timeouts.MaxMs}})" },
            "description": { "type": "string", "description": "{{DescriptionFieldPromptJson}}" },
            "run_in_background": { "type": "boolean", "description": "Set to true to run this command in the background. Use Read to read the output later." },
            "dangerouslyDisableSandbox": { "type": "boolean", "description": "Set this to true to dangerously override sandbox mode and run commands without sandboxing." }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """));

    private const string DescriptionFieldPrompt =
        """
        Clear, concise description of what this command does in active voice. Never use words like "complex" or "risk" in the description - just describe what it does.

        For simple commands (git, npm, standard CLI tools), keep it brief (5-10 words):
        - ls → "List files in current directory"
        - git status → "Show working tree status"
        - npm install → "Install package dependencies"

        For commands that are harder to parse at a glance (piped commands, obscure flags, etc.), add enough context to clarify what it does:
        - find . -name "*.tmp" -exec rm {} \; → "Find and delete all .tmp files recursively"
        - git reset --hard origin/main → "Discard all local changes and match remote main"
        - curl -s url | jq '.data[]' → "Fetch JSON from URL and extract data array elements"
        """;

    private static readonly string DescriptionFieldPromptJson =
        JsonEncodedText.Encode(DescriptionFieldPrompt).ToString();

    public ToolMetadata Metadata { get; }

    public bool IsReadOnlyInput(JsonElement input) =>
        ToolInput.StringOrNull(input, "command") is { } command && BashSemantics.IsReadOnlyCommand(command);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var command = ToolInput.String(context.Input, "command");
        if (command.Trim().Length == 0)
            return ToolExecutionResult.Text(string.Empty);

        var timeoutMs = _timeouts.Resolve(ToolInput.IntOrNull(context.Input, "timeout"));
        var description = ToolInput.StringOrNull(context.Input, "description");
        var runInBackground = ToolInput.BoolOrDefault(context.Input, "run_in_background", false);
        var workingDirectory = context.Shell?.CurrentDirectory ?? context.WorkspaceRoot;
        var environment = context.Shell?.Environment;
        var shell = environment is null ? _shell : new ShellSpec(environment.FileName, environment.Arguments);
        var isPosix = environment?.IsPosix ?? BashProcessSession.IsPosixShell(shell);

        if (runInBackground)
            return StartExplicitBackground(context, shell, isPosix, command, description, workingDirectory);

        var trackedReads = context.FileReads?.TrackedReads() ?? [];
        using var session = BashProcessSession.Start(
            shell,
            command,
            workingDirectory,
            captureCwd: true,
            outputFilePath: CreateBackgroundOutputPath(context),
            openOutput: _openOutput,
            isPosix: isPosix);

        var exitCode = 0;
        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeoutMs > 0)
            timeoutCts.CancelAfter(timeoutMs);
        try
        {
            exitCode = await session.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (IsAutoBackgroundEligible(command) && context.BackgroundJobs is { } jobs)
            {
                var outputPath = session.OutputPath!;
                var info = jobs.Start(
                    description ?? command,
                    outputPath,
                    cancellationToken => RunBackgroundAsync(session, cancellationToken),
                    context.ToolCallId);
                session.TransferOwnership();
                return ToolExecutionResult.Text(BashModelContent.FormatAutoBackgrounded(
                    info.Id.Value,
                    outputPath,
                    timeoutMs));
            }

            timedOut = true;
            session.KillTree();
            try
            {
                await session.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        return CompleteForeground(context, session, command, workingDirectory, trackedReads, exitCode, timedOut);
    }

    private ToolExecutionResult StartExplicitBackground(
        ToolExecutionContext context,
        ShellSpec shell,
        bool isPosix,
        string command,
        string? description,
        string workingDirectory)
    {
        if (context.BackgroundJobs is not { } jobs)
            return ToolExecutionResult.Text(
                "Background job service is not configured for Bash tool",
                isError: true);

        var outputPath = CreateBackgroundOutputPath(context);
        var session = BashProcessSession.Start(shell, command, workingDirectory, captureCwd: false, outputPath,
            openOutput: _openOutput, isPosix: isPosix);
        BackgroundJobInfo info;
        try
        {
            info = jobs.Start(
                description ?? command,
                outputPath,
                cancellationToken => RunBackgroundAsync(session, cancellationToken),
                context.ToolCallId);
        }
        catch
        {
            session.Dispose();
            throw;
        }

        session.TransferOwnership();
        return ToolExecutionResult.Text(BashModelContent.FormatBackgroundStart(info.Id.Value, outputPath));
    }

    private static async Task<BackgroundJobResult> RunBackgroundAsync(
        BashProcessSession session,
        CancellationToken cancellationToken)
    {
        var exitCode = await session.RunToCompletionAsync(cancellationToken).ConfigureAwait(false);
        return new BackgroundJobResult(exitCode);
    }

    private ToolExecutionResult CompleteForeground(
        ToolExecutionContext context,
        BashProcessSession session,
        string command,
        string workingDirectory,
        IReadOnlyList<(string Path, FileReadSnapshot Snapshot)> trackedReads,
        int exitCode,
        bool timedOut)
    {
        var stdout = session.SnapshotStdout();
        var stderr = session.SnapshotStderr();

        var stderrSuffix = DecideCwd(context, session, exitCode, timedOut);
        var stderrWithSuffix = BashModelContent.AppendCwdStderrSuffix(stderr, stderrSuffix);

        var semantic = timedOut ? null : BashSemantics.SemanticNonErrorExit(command, exitCode);
        var providerError = !timedOut && exitCode != 0 && !BashSemantics.IsSemanticNonError(semantic);
        var modelStdout = BashModelContent.FormatStdout(stdout);
        var modelStderr = BashModelContent.FormatStderr(stderrWithSuffix, interrupted: timedOut);

        var parts = new List<string>();
        if (providerError)
            parts.Add($"Exit code {exitCode}");
        if (modelStdout.Length > 0)
            parts.Add(modelStdout);
        if (modelStderr.Length > 0)
            parts.Add(modelStderr);
        // A semantic non-error exit (grep miss, diff, test) reads as its message when the
        // command itself produced nothing to show.
        if (parts.Count == 0 && semantic is not null)
            parts.Add(semantic);
        if (!timedOut && exitCode == 0)
        {
            var changedPaths = DetectChangedReads(trackedReads);
            if (changedPaths.Count > 0)
                parts.Add(BashModelContent.FormatStaleReadHint(changedPaths, workingDirectory));
        }

        var content = string.Join("\n", parts);
        if (session.PersistenceFailed && session.OutputBytes > BashModelContent.MaxInlineOutputBytes)
        {
            content = BashModelContent.FormatPersistenceFailure(session.SnapshotTail(), session.OutputBytes);
        }
        else if (!session.PersistenceFailed && session.OutputBytes > session.InlineLimit)
        {
            content = BashModelContent.FormatPersistedOutputEnvelope(content, session.OutputBytes, session.OutputPath!);
        }
        else if (!session.PersistenceFailed)
        {
            session.DeleteOutputFile();
        }

        return ToolExecutionResult.Text(content, isError: providerError);
    }

    private static string? DecideCwd(
        ToolExecutionContext context,
        BashProcessSession session,
        int exitCode,
        bool timedOut)
    {
        if (timedOut || exitCode != 0 || context.Shell is null)
            return null;
        if (!session.TryExtractCapturedCwd(out var captured))
            return null;

        var windowsCwd = ResolveCapturedPath(captured);
        if (windowsCwd is not null && IsInsideOrSamePath(windowsCwd, context.WorkspaceRoot))
        {
            context.Shell.SetCurrentDirectory(windowsCwd);
            return null;
        }

        // Leaving the workspace resets the session cwd; the reset itself is announced on
        // stderr so the model does not assume the new directory stuck.
        context.Shell.SetCurrentDirectory(context.WorkspaceRoot);
        return $"Shell cwd was reset to {context.WorkspaceRoot}";
    }

    // cygpath already yields a drive-letter path on git-bash; drive-style drives only exist
    // on Windows, and non-Windows shells report the native path directly.
    internal static string? ResolveCapturedPath(string captured)
    {
        if (!OperatingSystem.IsWindows())
            return captured;
        if (captured.Length >= 3
            && char.IsAsciiLetter(captured[0])
            && captured[1] == ':'
            && (captured[2] == '\\' || captured[2] == '/'))
            return captured;
        return TryPosixToWindowsPath(captured);
    }

    internal static bool IsAutoBackgroundEligible(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.Length == 0)
            return false;
        var firstToken = trimmed.Split(' ', '\t', '\n')[0];
        return firstToken != "sleep";
    }

    internal static string? TryPosixToWindowsPath(string posixPath)
    {
        if (posixPath.Length < 2 || posixPath[0] != '/')
            return null;
        if (!char.IsAsciiLetter(posixPath[1]))
            return null;
        var drive = char.ToUpperInvariant(posixPath[1]);
        if (posixPath.Length == 2)
            return $"{drive}:\\";
        if (posixPath[2] != '/')
            return null;
        return $"{drive}:\\" + posixPath[3..].Replace('/', '\\');
    }

    internal static bool IsInsideOrSamePath(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(full, normalizedRoot, comparison)
            || full.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    internal static List<string> DetectChangedReads(
        IReadOnlyList<(string Path, FileReadSnapshot Snapshot)> trackedReads)
    {
        var changed = new List<string>();
        foreach (var (path, snapshot) in trackedReads)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || snapshot.MatchesCurrent(info.Length, info.LastWriteTimeUtc))
                    continue;
                changed.Add(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return changed;
    }

    private static string CreateBackgroundOutputPath(ToolExecutionContext context)
    {
        var directory = context.ToolOutputRoot is { Length: > 0 } outputRoot
            ? Path.Combine(outputRoot, context.SessionId.Value)
            : Path.Combine(context.WorkspaceRoot, ".alcoler", "background", context.SessionId.Value);
        return Path.Combine(directory, $"bash-{Guid.NewGuid():N}.log");
    }
}
