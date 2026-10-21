using System.Text.Json.Nodes;

namespace Alcoler.Adapters.Models;

/// <summary>
/// Shapes a /v1/responses request body for ChatGPT account (SIWC) access. The account
/// channel rejects server-side state, sampling and attribution fields, and explicit
/// system message items; only developer messages are accepted.
/// </summary>
internal static class OpenAiResponsesAccountShaping
{
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

    internal static string Shape(string body)
    {
        var root = JsonNode.Parse(body) as JsonObject
            ?? throw new ArgumentException("Responses request body must be a JSON object.");
        foreach (var field in OmittedFields)
            root.Remove(field);
        root["store"] = false;
        if (root["input"] is JsonArray input)
        {
            foreach (var item in input)
            {
                if (item is JsonObject message && (string?)message["role"] == "system")
                    message["role"] = "developer";
            }
        }
        return root.ToJsonString();
    }
}
