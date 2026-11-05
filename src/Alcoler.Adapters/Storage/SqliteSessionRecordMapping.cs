using System.Text.Json.Nodes;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Alcoler.Contracts.Tools;
using Microsoft.Data.Sqlite;

namespace Alcoler.Adapters.Storage;

public sealed partial class SqliteSessionStore
{
    private static SessionRecord ReadSessionRecord(SqliteDataReader reader, SessionId id)
    {
        var mode = ReadPermissionMode(reader);
        var planEnabled = reader.GetBoolean(reader.GetOrdinal("plan_enabled"));

        return new SessionRecord(
            id,
            reader.GetString(reader.GetOrdinal("workspace_root")),
            reader.GetString(reader.GetOrdinal("title")),
            mode,
            DeserializeOptional<ModelSelection>(reader, "model_selection_json"),
            ParseUtcIso(reader.GetString(reader.GetOrdinal("created_at"))),
            ParseUtcIso(reader.GetString(reader.GetOrdinal("updated_at"))),
            ReadBranch(reader),
            reader.GetBoolean(reader.GetOrdinal("is_archived")),
            ReadParentSessionId(reader),
            ReadTitleSource(reader),
            DeserializeOptional<ShellEnvironment>(reader, "shell_json"),
            ReadForkedFromSessionId(reader),
            ReadForkedFromMessageId(reader),
            ReadActiveWorkflows(reader),
            planEnabled);
    }

    private static SessionSummary ReadSummary(SqliteDataReader reader)
    {
        var mode = ReadPermissionMode(reader);
        var planEnabled = reader.GetBoolean(reader.GetOrdinal("plan_enabled"));

        return new SessionSummary(
            new SessionId(reader.GetString(reader.GetOrdinal("id"))),
            reader.GetString(reader.GetOrdinal("title")),
            reader.GetString(reader.GetOrdinal("workspace_root")),
            mode,
            DeserializeOptional<ModelSelection>(reader, "model_selection_json"),
            ParseUtcIso(reader.GetString(reader.GetOrdinal("created_at"))),
            ParseUtcIso(reader.GetString(reader.GetOrdinal("updated_at"))),
            reader.GetBoolean(reader.GetOrdinal("is_archived")),
            ReadParentSessionId(reader),
            planEnabled);
    }

    private static SessionTitleSource ReadTitleSource(SqliteDataReader reader) =>
        Enum.TryParse<SessionTitleSource>(reader.GetString(reader.GetOrdinal("title_source")), out var source)
            && Enum.IsDefined(source) ? source : SessionTitleSource.Custom;

    private static SessionId? ReadParentSessionId(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("parent_session_id");
        return reader.IsDBNull(ordinal) ? null : new SessionId(reader.GetString(ordinal));
    }

    private static SessionId? ReadForkedFromSessionId(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("forked_from_session_id");
        return reader.IsDBNull(ordinal) ? null : new SessionId(reader.GetString(ordinal));
    }

    private static MessageId? ReadForkedFromMessageId(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("forked_from_message_id");
        return reader.IsDBNull(ordinal) ? null : new MessageId(reader.GetString(ordinal));
    }

    private static PermissionMode ReadPermissionMode(SqliteDataReader reader) =>
        Enum.TryParse<PermissionMode>(reader.GetString(reader.GetOrdinal("permission_mode")), out var mode)
            && Enum.IsDefined(mode)
                ? mode
                : PermissionMode.Edit;

    // Kept as hand-built JSON like branch_json: IReadOnlyList<string> has no source-gen metadata.
    private static string SerializeWorkflows(IReadOnlyList<string> workflows)
    {
        var array = new JsonArray();
        foreach (var workflow in workflows)
        {
            array.Add(workflow);
        }
        return array.ToJsonString();
    }

    private static IReadOnlyList<string>? ReadActiveWorkflows(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("active_workflows_json");
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }
        if (JsonNode.Parse(reader.GetString(ordinal)) is not JsonArray array)
        {
            return null;
        }
        List<string>? workflows = null;
        foreach (var node in array)
        {
            var id = node?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            (workflows ??= []).Add(id);
        }
        return workflows is { Count: > 0 } active ? active : null;
    }

    private static T? DeserializeOptional<T>(SqliteDataReader reader, string column)
        where T : class
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : AlcolerJson.Deserialize<T>(reader.GetString(ordinal));
    }

    // AlcolerJson cannot read SessionBranch: IReadOnlySet<MessageId> has no constructible default, so this column keeps its own JSON shape.
    private static string SerializeBranch(SessionBranch branch)
    {
        var kept = new JsonArray();
        foreach (var messageId in branch.KeptMessageIds)
        {
            kept.Add(messageId.Value);
        }

        return new JsonObject
        {
            ["cutAfterMessageId"] = branch.CutAfterMessageId.Value,
            ["keptMessageIds"] = kept,
            ["cutEventSequence"] = branch.CutEventSequence,
        }.ToJsonString();
    }

    private static SessionBranch? ReadBranch(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("branch_json");
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var root = JsonNode.Parse(reader.GetString(ordinal)) as JsonObject
            ?? throw new InvalidOperationException("Corrupt session branch payload.");
        var cutAfter = root["cutAfterMessageId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Corrupt session branch payload.");
        var kept = root["keptMessageIds"]?.AsArray()
            ?? throw new InvalidOperationException("Corrupt session branch payload.");
        return new SessionBranch(
            new MessageId(cutAfter),
            kept.Select(node => new MessageId(
                node?.GetValue<string>() ?? throw new InvalidOperationException("Corrupt session branch payload.")))
                .ToList(),
            root["cutEventSequence"] is JsonValue sequence ? sequence.GetValue<long>() : null);
    }
}
