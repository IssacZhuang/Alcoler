using System.Text.Json.Nodes;
using Alcoler.Contracts.Models;

namespace Alcoler.Adapters.Models;

/// <summary>
/// Shapes a /v1/responses request for ChatGPT account (SIWC) access. The account
/// channel rejects server-side state, sampling and attribution fields, and explicit
/// system message items; only developer messages are accepted. Prefix-cache routing
/// on this channel derives from the Responses session-id header (mirroring Codex CLI's
/// responses_session_id), so affinity headers and the equivalent prompt_cache_key body
/// field are derived here as well.
/// </summary>
internal static class OpenAiResponsesAccountShaping
{
    internal const string SessionIdHeader = "session-id";
    internal const string ThreadIdHeader = "thread-id";
    // Internal storage prefixes must not leak across the provider network boundary.
    private const string SessionIdInternalPrefix = "sess_";
    private const string SubagentAffinityPrefix = "subagent:";
    // OpenAI's documented prompt_cache_key/session-id length limit.
    private const int CacheKeyMaxLength = 64;

    private static readonly string[] OmittedFields =
    [
        "background",
        "conversation",
        "max_output_tokens",
        "max_tool_calls",
        "metadata",
        "moderation",
        "multi_agent",
        "prompt",
        "prompt_cache_retention",
        "safety_identifier",
        "temperature",
        "top_logprobs",
        "top_p",
        "truncation",
        "user",
        "previous_response_id",
    ];

    /// <summary>
    /// Cache affinity headers for the account channel: a session uses its own normalized
    /// id; a subagent session inherits the parent-derived key so all children of one parent
    /// share a cache route for their common system prefix, and reports the parent as its
    /// thread. Missing affinity yields no headers.
    /// </summary>
    internal static (string? SessionId, string? ThreadId) CacheAffinityHeaders(ModelCacheAffinity? affinity)
    {
        if (affinity is null)
            return (null, null);
        var session = NormalizeSessionId(affinity.SessionId);
        var parent = NormalizeSessionId(affinity.ParentSessionId);
        if (parent is null)
            return (session is null ? null : ClampCacheKey(session), null);
        return (ClampCacheKey(SubagentAffinityPrefix + parent), parent);
    }

    internal static string Shape(string body, string? sessionCacheKey = null)
    {
        var root = JsonNode.Parse(body) as JsonObject
            ?? throw new ArgumentException("Responses request body must be a JSON object.");
        foreach (var field in OmittedFields)
            root.Remove(field);
        root["store"] = false;
        if (sessionCacheKey is not null && root["prompt_cache_key"] is null)
            root["prompt_cache_key"] = sessionCacheKey;
        if (root["input"] is JsonArray input)
        {
            foreach (var item in input)
            {
                if (item is JsonObject message && (string?)message["role"] == "system")
                    message["role"] = "developer";
            }
        }
        return root.ToJsonString(ModelRequestOptions.RequestJsonOptions);
    }

    private static string? NormalizeSessionId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;
        return sessionId.StartsWith(SessionIdInternalPrefix, StringComparison.Ordinal)
            && sessionId.Length > SessionIdInternalPrefix.Length
            ? sessionId[SessionIdInternalPrefix.Length..]
            : sessionId;
    }

    // Clamps by Unicode code points so a surrogate pair is never split.
    private static string ClampCacheKey(string value)
    {
        if (value.Length <= CacheKeyMaxLength)
            return value;
        var end = 0;
        var codePoints = 0;
        while (end < value.Length && codePoints < CacheKeyMaxLength)
        {
            end += char.IsHighSurrogate(value[end]) && end + 1 < value.Length && char.IsLowSurrogate(value[end + 1])
                ? 2
                : 1;
            codePoints++;
        }
        return value[..end];
    }
}
