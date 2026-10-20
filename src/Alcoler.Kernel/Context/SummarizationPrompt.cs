using System.Text;
using Alcoler.Contracts.Conversation;

namespace Alcoler.Kernel.Context;

public static class SummarizationPrompt
{
    public const string TextOnlyDirective = "CRITICAL: Respond with TEXT ONLY. Do not use any tools.";

    public static string BuildSummaryUserPrompt(
        IReadOnlyList<ConversationMessage> toSummarize,
        string? manualInstructions = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(TextOnlyDirective);
        sb.AppendLine();
        sb.AppendLine(
            "Summarize the coding-agent conversation transcript below to compact the session context window. " +
            "Respond with a markdown summary containing exactly these sections, in this order:");
        sb.AppendLine();
        sb.AppendLine("## Primary Request and Intent");
        sb.AppendLine("The user's primary request, goals, and success criteria for the session.");
        sb.AppendLine("## Key Technical Concepts");
        sb.AppendLine("Key technical concepts, APIs, libraries, languages, and constraints that appeared.");
        sb.AppendLine("## Files and Code Sections");
        sb.AppendLine("Files and code sections that were read, created, or modified. Include small verbatim code snippets with their file paths.");
        sb.AppendLine("## Errors and Fixes");
        sb.AppendLine("Errors encountered and how they were diagnosed and fixed.");
        sb.AppendLine("## Problem Solving");
        sb.AppendLine("Approaches considered and tried, decisions made, and open design questions.");
        sb.AppendLine("## All User Messages");
        sb.AppendLine("Every user text message from the transcript, quoted VERBATIM in the order they occurred. Do not paraphrase, shorten, or omit any.");
        sb.AppendLine("## Pending Tasks");
        sb.AppendLine("Tasks that were requested or discovered but not yet completed.");
        sb.AppendLine("## Current Work");
        sb.AppendLine("What was being worked on at the end of the transcript, including relevant state.");
        sb.AppendLine("## Optional Next Step");
        sb.AppendLine("The single most likely next action, if any.");
        sb.AppendLine();
        sb.AppendLine("<conversation-transcript>");
        AppendTranscript(sb, toSummarize);
        sb.AppendLine("</conversation-transcript>");
        if (!string.IsNullOrWhiteSpace(manualInstructions))
        {
            sb.AppendLine();
            sb.AppendLine("Additional summarization instructions from the user (these take precedence where they conflict):");
            sb.AppendLine(manualInstructions);
        }
        sb.AppendLine();
        sb.Append(TextOnlyDirective);
        return sb.ToString();
    }

    private static void AppendTranscript(StringBuilder sb, IReadOnlyList<ConversationMessage> messages)
    {
        foreach (var message in messages)
        {
            sb.AppendLine(message.Role == MessageRole.User ? "[user]" : "[assistant]");
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case TextPart text:
                        sb.AppendLine(text.Text);
                        break;
                    case ReasoningPart reasoning:
                        sb.AppendLine($"[reasoning] {reasoning.Text}");
                        break;
                    case ToolCallPart call:
                        sb.AppendLine($"[tool call {call.ToolName}] {TokenEstimator.RawJson(call.Input)}");
                        break;
                    case ToolResultPart result:
                        var errorSuffix = result.IsError ? " (error)" : string.Empty;
                        foreach (var block in result.Content)
                            if (block is ToolResultText resultText)
                                sb.AppendLine($"[tool result{errorSuffix}] {resultText.Text}");
                        break;
                    case FilePart file:
                        sb.AppendLine($"[file {file.FileName ?? file.ArtifactUri}]");
                        break;
                    case AttachmentPart attachment:
                        sb.AppendLine($"[attachment {attachment.Kind}] {attachment.Content}");
                        break;
                }
            }
            sb.AppendLine();
        }
    }
}
