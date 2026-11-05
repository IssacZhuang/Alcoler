using System.Text;
using System.Text.Json;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Serialization;
using Microsoft.Data.Sqlite;

namespace Alcoler.Adapters.Storage;

public sealed partial class SqliteSessionStore
{
    private const int SchemaVersion = 8;

    private static readonly (int Version, string Sql)[] Migrations =
    [
        (
            1,
            """
            CREATE TABLE IF NOT EXISTS session (
                id TEXT PRIMARY KEY,
                workspace_root TEXT NOT NULL,
                title TEXT NOT NULL,
                permission_mode TEXT NOT NULL,
                model_selection_json TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                branch_json TEXT NULL,
                is_archived INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_session_updated_at ON session (updated_at);

            CREATE TABLE IF NOT EXISTS message (
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                message_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (session_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS ix_message_message_id ON message (message_id);

            CREATE TABLE IF NOT EXISTS durable_event (
                session_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                PRIMARY KEY (session_id, sequence)
            );
            """
        ),
        (2, "ALTER TABLE session ADD COLUMN parent_session_id TEXT NULL;"),
        (3, "ALTER TABLE session ADD COLUMN title_source TEXT NOT NULL DEFAULT 'legacy';"),
        (4, "ALTER TABLE session ADD COLUMN shell_json TEXT NULL;"),
        (
            5,
            """
            ALTER TABLE session ADD COLUMN forked_from_session_id TEXT NULL;
            ALTER TABLE session ADD COLUMN forked_from_message_id TEXT NULL;
            """
        ),
        (6, "ALTER TABLE session ADD COLUMN previous_permission_mode TEXT NULL;"),
        (7, "ALTER TABLE session ADD COLUMN active_workflows_json TEXT NULL;"),
        (8, "ALTER TABLE session ADD COLUMN plan_enabled INTEGER NOT NULL DEFAULT 0;"),
    ];

    private static void ApplyMigrations(SqliteConnection connection)
    {
        Execute(connection, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");

        long current;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
            current = Convert.ToInt64(command.ExecuteScalar());
        }

        if (current >= SchemaVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (version, sql) in Migrations)
        {
            if (version <= current)
            {
                continue;
            }

            using (var migrate = connection.CreateCommand())
            {
                migrate.Transaction = transaction;
                migrate.CommandText = sql;
                migrate.ExecuteNonQuery();
            }

            if (version == 2)
            {
                BackfillSubagentParents(connection, transaction);
            }
            else if (version == 3)
            {
                BackfillSessionTitles(connection, transaction);
            }

            using (var stamp = connection.CreateCommand())
            {
                stamp.Transaction = transaction;
                stamp.CommandText = "INSERT INTO schema_version (version) VALUES ($version)";
                stamp.Parameters.AddWithValue("$version", (long)version);
                stamp.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    private static void BackfillSubagentParents(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT session_id, payload_json
            FROM durable_event
            WHERE json_extract(CASE WHEN json_valid(payload_json) THEN payload_json ELSE '{}' END, '$."$type"') = 'subagent_started'
            ORDER BY occurred_at, session_id, sequence
            """;
        using var reader = query.ExecuteReader();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE session SET parent_session_id = $parent
            WHERE id = $child AND parent_session_id IS NULL AND id <> $parent
            """;
        var parent = update.Parameters.Add("$parent", SqliteType.Text);
        var child = update.Parameters.Add("$child", SqliteType.Text);
        while (reader.Read())
        {
            SubagentStartedEvent? started;
            try
            {
                started = AlcolerJson.Deserialize<SessionEvent>(reader.GetString(1)) as SubagentStartedEvent;
            }
            catch (JsonException)
            {
                continue;
            }
            if (started?.State is not { } state || string.IsNullOrWhiteSpace(state.ChildSessionId.Value))
            {
                continue;
            }
            parent.Value = reader.GetString(0);
            child.Value = state.ChildSessionId.Value;
            update.ExecuteNonQuery();
        }
    }

    private static void BackfillSessionTitles(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT id, workspace_root, title, parent_session_id FROM session ORDER BY id";
        using var reader = query.ExecuteReader();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE session SET title = $title, title_source = $source WHERE id = $id";
        var id = update.Parameters.Add("$id", SqliteType.Text);
        var title = update.Parameters.Add("$title", SqliteType.Text);
        var source = update.Parameters.Add("$source", SqliteType.Text);
        while (reader.Read())
        {
            var sessionId = reader.GetString(0);
            var currentTitle = reader.GetString(2);
            var titleSource = SessionTitleSource.Custom;
            var workspaceName = Path.GetFileName(Path.TrimEndingDirectorySeparator(reader.GetString(1)));
            if (reader.IsDBNull(3)
                && (currentTitle == workspaceName || currentTitle == "New session")
                && !HasDurableTitleChange(connection, transaction, sessionId))
            {
                var firstInputTitle = ReadFirstInputTitle(connection, transaction, sessionId);
                titleSource = firstInputTitle is null ? SessionTitleSource.Default : SessionTitleSource.FirstInput;
                currentTitle = firstInputTitle ?? currentTitle;
            }

            id.Value = sessionId;
            title.Value = currentTitle;
            source.Value = titleSource.ToString();
            update.ExecuteNonQuery();
        }
    }

    private static bool HasDurableTitleChange(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_json FROM durable_event WHERE session_id = $id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                if (AlcolerJson.Deserialize<SessionEvent>(reader.GetString(0)) is SessionTitleChangedEvent)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    private static string? ReadFirstInputTitle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload_json FROM message WHERE session_id = $id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ConversationMessage? message;
            try
            {
                message = AlcolerJson.Deserialize<ConversationMessage>(reader.GetString(0));
            }
            catch (JsonException)
            {
                continue;
            }

            if (message is null || message.Role != MessageRole.User || message.Parts is null
                || !(message.Source == MessageSource.RealUser
                    || message.Source is null && !message.Parts.Any(part => part is ToolResultPart or AttachmentPart)))
            {
                continue;
            }

            if (message.Parts.OfType<TextPart>().Any(part =>
                part.Text is not null && part.Text.AsSpan().TrimStart().StartsWith("<system-reminder>", StringComparison.Ordinal)))
            {
                continue;
            }

            if (CompactTitle(message.Parts) is { } title)
            {
                return title;
            }
        }

        return null;
    }

    private static string? CompactTitle(IReadOnlyList<MessagePart> parts)
    {
        var title = new StringBuilder(61);
        var pendingSpace = false;
        foreach (var part in parts.OfType<TextPart>())
        {
            foreach (var character in part.Text.AsSpan())
            {
                if (char.IsWhiteSpace(character))
                {
                    pendingSpace = title.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    title.Append(' ');
                    pendingSpace = false;
                }

                title.Append(character);
                if (title.Length > 60)
                {
                    return title.ToString(0, 57) + "...";
                }
            }

            pendingSpace = title.Length > 0;
        }

        return title.Length == 0 ? null : title.ToString();
    }
}
