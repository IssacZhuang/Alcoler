using System.Collections.Concurrent;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Subagents;
using Microsoft.Extensions.Logging;

namespace Alcoler.Kernel.Subagents;

public sealed class SubagentService : ISubagentPort
{
    private const int SummaryMaxLength = 200;
    private const int DescriptionFallbackMaxLength = 80;

    private readonly SubagentChildSessionFactory _factory;
    private readonly TimeProvider _clock;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<SubagentId, SubagentState> _states = new();

    public SubagentService(SubagentChildSessionFactory factory, TimeProvider? clock = null, ILogger? logger = null)
    {
        _factory = factory;
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
    }

    public IReadOnlyList<SubagentState> ActiveStates => _states.Values.ToArray();

    public async ValueTask<SubagentResult> RunAsync(SubagentRequest request, CancellationToken cancellationToken)
    {
        var id = StrongId.NewSubagentId();
        var child = await _factory(id, request, cancellationToken).ConfigureAwait(false);
        TrackStarted(id, child, request);
        return await RunToResultAsync(id, child, request, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<SubagentHandle> StartBackgroundAsync(
        SubagentRequest request,
        Func<SubagentResult, ValueTask> onCompleted,
        CancellationToken cancellationToken)
    {
        var id = StrongId.NewSubagentId();
        var child = await _factory(id, request, cancellationToken).ConfigureAwait(false);
        var description = DescriptionOf(request);
        TrackStarted(id, child, request);
        _ = NotifyInBackgroundAsync(id, child, request, onCompleted, cancellationToken);
        return new SubagentHandle(
            id, child.ChildSessionId, description, Background: true, OutputFile: child.OutputFile);
    }

    // Runs detached from any caller; nothing thrown here may ever reach an unobserved task.
    private async Task NotifyInBackgroundAsync(
        SubagentId id,
        SubagentChildSession child,
        SubagentRequest request,
        Func<SubagentResult, ValueTask> onCompleted,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunToResultAsync(id, child, request, cancellationToken).ConfigureAwait(false);
            await onCompleted(result).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Subagent {SubagentId} completion sink failed", id.Value);
        }
    }

    private async ValueTask<SubagentResult> RunToResultAsync(
        SubagentId id,
        SubagentChildSession child,
        SubagentRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            await child.SubmitAsync(request.Prompt, cancellationToken).ConfigureAwait(false);
            await child.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            var output = await child.ReadFinalOutputAsync().ConfigureAwait(false) ?? string.Empty;
            var toolUseCount = await ReadToolUseCountQuietlyAsync(child).ConfigureAwait(false);
            return Complete(id, SubagentStatus.Completed, output, toolUseCount);
        }
        catch (OperationCanceledException)
        {
            // The run's own token is already canceled; only CancellationToken.None still stops the child.
            await StopQuietlyAsync(child).ConfigureAwait(false);
            return Complete(id, SubagentStatus.Cancelled, string.Empty);
        }
        catch (Exception ex)
        {
            await StopQuietlyAsync(child).ConfigureAwait(false);
            var output = await ReadOutputQuietlyAsync(child).ConfigureAwait(false) ?? ex.Message;
            return Complete(id, SubagentStatus.Failed, output);
        }
    }

    private async Task StopQuietlyAsync(SubagentChildSession child)
    {
        try
        {
            await child.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Stopping subagent child session {ChildSessionId} failed", child.ChildSessionId.Value);
        }
    }

    private async Task<string?> ReadOutputQuietlyAsync(SubagentChildSession child)
    {
        try
        {
            return await child.ReadFinalOutputAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Reading final output of subagent child session {ChildSessionId} failed", child.ChildSessionId.Value);
            return null;
        }
    }

    private static async Task<int?> ReadToolUseCountQuietlyAsync(SubagentChildSession child)
    {
        try
        {
            return await child.ReadToolUseCountAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The count is advisory; the final output is the load-bearing payload.
            return null;
        }
    }

    private void TrackStarted(SubagentId id, SubagentChildSession child, SubagentRequest request) =>
        _states[id] = new SubagentState(
            id,
            child.ChildSessionId,
            DescriptionOf(request),
            SubagentStatus.Running,
            ResultSummary: null,
            StartedAt: _clock.GetUtcNow(),
            EndedAt: null,
            OutputFile: child.OutputFile,
            ParentToolCallId: request.ParentToolCallId,
            AgentType: request.AgentType ?? SubagentPrompts.GeneralPurposeAgentType);

    private SubagentResult Complete(SubagentId id, SubagentStatus status, string output, int? toolUseCount = null)
    {
        var state = _states[id] with
        {
            Status = status,
            ResultSummary = output.Length == 0 ? null : SingleLineSummary(output, SummaryMaxLength),
            EndedAt = _clock.GetUtcNow(),
        };
        _states[id] = state;
        return new SubagentResult(state, output, toolUseCount);
    }

    private static string DescriptionOf(SubagentRequest request) =>
        string.IsNullOrWhiteSpace(request.Description)
            ? SingleLineSummary(request.Prompt, DescriptionFallbackMaxLength)
            : request.Description;

    private static string SingleLineSummary(string text, int maxLength)
    {
        var singleLine = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= maxLength ? singleLine : singleLine[..maxLength];
    }
}
