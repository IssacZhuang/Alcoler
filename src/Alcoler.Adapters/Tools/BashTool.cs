using System.Text.Json;
using System.Diagnostics;
using System.Text;
using Alcoler.Contracts.Tools;
using Alcoler.Kernel.Tools;

namespace Alcoler.Adapters.Tools;

public sealed class BashTool : ITool
{
    internal const int DefaultTimeoutMs = 30_000;
    internal const int MaxTimeoutMs = 600_000;

    private readonly ShellSpec _shell;

    public BashTool() : this(ShellResolver.Resolve())
    {
    }

    internal BashTool(ShellSpec shell)
    {
        _shell = shell;
    }

    public ToolContract Contract { get; } = new(
        "Bash",
        "Runs a shell command in the workspace directory and returns the combined stdout/stderr output with the exit code. Commands have a 30s default timeout (max 600s, configurable via timeout_ms); the whole process tree is killed on timeout.",
        ToolInput.ParseSchema("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "properties": {
            "command": { "type": "string", "minLength": 1, "description": "Shell command line to execute." },
            "timeout_ms": { "type": "integer", "minimum": 1, "maximum": 600000, "description": "Execution timeout in milliseconds; defaults to 30000, capped at 600000." },
            "description": { "type": "string", "description": "Short user-facing description of what the command does." }
          },
          "required": ["command"],
          "additionalProperties": false
        }
        """));

    public ToolMetadata Metadata { get; } = new(
        ReadOnly: false,
        Destructive: false,
        ConcurrentSafe: false,
        RequiresUserInteraction: false,
        SideEffectScope: SideEffectScope.System,
        RiskLevel: RiskLevel.Medium,
        NeedsApproval: false,
        AllowedInPlanMode: false,
        DefaultTimeoutMs: null,
        MaxTimeoutMs: MaxTimeoutMs);

    public ValueTask<ToolInputIssue?> ValidateInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolInputIssue?>(null);

    public ValueTask<JsonElement> ResolveInputAsync(JsonElement input, CancellationToken cancellationToken) =>
        ValueTask.FromResult(input);

    public async ValueTask<ToolExecutionResult> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken)
    {
        var command = ToolInput.String(context.Input, "command");
        var timeoutMs = Math.Clamp(ToolInput.IntOrNull(context.Input, "timeout_ms") ?? DefaultTimeoutMs, 1, MaxTimeoutMs);

        var startInfo = new ProcessStartInfo(_shell.FileName)
        {
            WorkingDirectory = context.WorkspaceRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(_shell.Arguments);
        startInfo.ArgumentList.Add(command);

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var outputLock = new object();
        process.OutputDataReceived += (_, e) => AppendLine(output, outputLock, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(output, outputLock, e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            try
            {
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }
        }
        process.WaitForExit();

        if (cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);

        string text;
        lock (outputLock)
        {
            text = output.ToString();
        }
        if (timedOut)
            text += $"\n[command timed out after {timeoutMs / 1000.0:0.##}s; the process tree was killed]";
        text += $"\nExit code: {process.ExitCode}";

        var result = ToolExecutionResult.Text(text.TrimStart('\n'), isError: process.ExitCode != 0);
        return ToolResultBudget.ApplyBudget(result, ToolResultLimits.DefaultMaxInlineChars);
    }

    private static void AppendLine(StringBuilder builder, object lockObject, string? data)
    {
        if (data is null)
            return;
        lock (lockObject)
        {
            builder.AppendLine(data);
        }
    }
}
