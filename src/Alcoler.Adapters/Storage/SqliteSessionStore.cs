using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Models;
using Alcoler.Contracts.Permissions;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Storage;
using Microsoft.Data.Sqlite;

namespace Alcoler.Adapters.Storage;

public sealed class SqliteSessionStore : ISessionStore, IAsyncDisposable, IDisposable
{
    private const int SchemaVersion = 3;

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
    ];

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private bool _disposed;

    public SqliteSessionStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(Path.GetFullPath(_databasePath));
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var connection = new SqliteConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                Execute(connection, "PRAGMA journal_mode=WAL;");
                ApplyMigrations(connection);
                _connection = connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask UpsertSessionAsync(SessionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        return RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO session
                    (id, workspace_root, title, permission_mode, model_selection_json,
                     created_at, updated_at, branch_json, is_archived, parent_session_id, title_source)
                VALUES ($id, $workspace_root, $title, $permission_mode, $model_selection_json,
                        $created_at, $updated_at, $branch_json, $is_archived, $parent_session_id, $title_source)
                """;
            command.Parameters.AddWithValue("$id", record.Id.Value);
            command.Parameters.AddWithValue("$workspace_root", record.WorkspaceRoot);
            command.Parameters.AddWithValue("$title", record.Title);
            command.Parameters.AddWithValue("$permission_mode", record.PermissionMode.ToString());
            command.Parameters.AddWithValue("$model_selection_json",
                record.ModelSelection is null ? DBNull.Value : AlcolerJson.Serialize(record.ModelSelection));
            command.Parameters.AddWithValue("$created_at", ToUtcIso(record.CreatedAt));
            command.Parameters.AddWithValue("$updated_at", ToUtcIso(record.UpdatedAt));
            command.Parameters.AddWithValue("$branch_json",
                record.Branch is null ? DBNull.Value : SerializeBranch(record.Branch));
            command.Parameters.AddWithValue("$is_archived", record.IsArchived);
            command.Parameters.AddWithValue("$parent_session_id", (object?)record.ParentSessionId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$title_source", record.TitleSource.ToString());
            command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public ValueTask<bool> TryUpdateSessionTitleAsync(
        SessionId sessionId, string title, SessionTitleSource source, CancellationToken cancellationToken) =>
        QueryAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE session SET title = $title, title_source = $source
                WHERE id = $id AND ($source = 'Custom' OR title_source <> 'Custom')
                """;
            command.Parameters.AddWithValue("$id", sessionId.Value);
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$source", source.ToString());
            return command.ExecuteNonQuery() > 0;
        }, cancellationToken);

    public ValueTask<SessionRecord?> GetSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        QueryAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT workspace_root, title, permission_mode, model_selection_json,
                       created_at, updated_at, branch_json, is_archived, parent_session_id, title_source
                FROM session
                WHERE id = $id
                """;
            command.Parameters.AddWithValue("$id", sessionId.Value);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSessionRecord(reader, sessionId) : null;
        }, cancellationToken);

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string? workspaceRoot,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        return QueryAsync<IReadOnlyList<SessionSummary>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, workspace_root, permission_mode, model_selection_json,
                       created_at, updated_at, is_archived, parent_session_id
                FROM session
                WHERE parent_session_id IS NULL
                  AND ($workspace_root IS NULL OR workspace_root = $workspace_root)
                ORDER BY updated_at DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$workspace_root", (object?)workspaceRoot ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", (long)limit);
            using var reader = command.ExecuteReader();
            var summaries = new List<SessionSummary>();
            while (reader.Read())
            {
                summaries.Add(ReadSummary(reader));
            }

            return summaries;
        }, cancellationToken);
    }

    public ValueTask DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            ExecuteInTransaction(connection, transaction, "DELETE FROM message WHERE session_id = $id", sessionId);
            ExecuteInTransaction(connection, transaction, "DELETE FROM durable_event WHERE session_id = $id", sessionId);
            ExecuteInTransaction(connection, transaction, "DELETE FROM session WHERE id = $id", sessionId);
            transaction.Commit();
        }, cancellationToken);

    public ValueTask AppendMessagesAsync(
        SessionId sessionId,
        IReadOnlyList<ConversationMessage> messages,
        long startingSequence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentOutOfRangeException.ThrowIfNegative(startingSequence);
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var sequence = startingSequence;
            foreach (var message in messages)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO message (session_id, sequence, message_id, payload_json, created_at)
                    VALUES ($session_id, $sequence, $message_id, $payload_json, $created_at)
                    """;
                command.Parameters.AddWithValue("$session_id", sessionId.Value);
                command.Parameters.AddWithValue("$sequence", sequence);
                command.Parameters.AddWithValue("$message_id", message.Id.Value);
                command.Parameters.AddWithValue("$payload_json", AlcolerJson.Serialize(message));
                command.Parameters.AddWithValue("$created_at", ToUtcIso(message.CreatedAt));
                command.ExecuteNonQuery();
                sequence++;
            }

            transaction.Commit();
        }, cancellationToken);
    }

    public ValueTask ReplaceMessagesAsync(
        SessionId sessionId,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            ExecuteInTransaction(connection, transaction, "DELETE FROM message WHERE session_id = $id", sessionId);
            var sequence = 0L;
            foreach (var message in messages)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO message (session_id, sequence, message_id, payload_json, created_at)
                    VALUES ($session_id, $sequence, $message_id, $payload_json, $created_at)
                    """;
                command.Parameters.AddWithValue("$session_id", sessionId.Value);
                command.Parameters.AddWithValue("$sequence", sequence);
                command.Parameters.AddWithValue("$message_id", message.Id.Value);
                command.Parameters.AddWithValue("$payload_json", AlcolerJson.Serialize(message));
                command.Parameters.AddWithValue("$created_at", ToUtcIso(message.CreatedAt));
                command.ExecuteNonQuery();
                sequence++;
            }

            transaction.Commit();
        }, cancellationToken);
    }

    public ValueTask<IReadOnlyList<StoredMessage>> LoadMessagesAsync(
        SessionId sessionId,
        CancellationToken cancellationToken) =>
        QueryAsync<IReadOnlyList<StoredMessage>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, payload_json
                FROM message
                WHERE session_id = $session_id
                ORDER BY sequence
                """;
            command.Parameters.AddWithValue("$session_id", sessionId.Value);
            using var reader = command.ExecuteReader();
            var messages = new List<StoredMessage>();
            while (reader.Read())
            {
                var sequence = reader.GetInt64(reader.GetOrdinal("sequence"));
                var payload = reader.GetString(reader.GetOrdinal("payload_json"));
                var message = AlcolerJson.Deserialize<ConversationMessage>(payload)
                    ?? throw new InvalidOperationException($"Corrupt message payload at sequence {sequence}.");
                messages.Add(new StoredMessage(sequence, message));
            }

            return messages;
        }, cancellationToken);

    public ValueTask AppendDurableEventsAsync(
        IReadOnlyList<SequencedEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        return RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var sequencedEvent in events)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO durable_event (session_id, sequence, occurred_at, payload_json)
                    VALUES ($session_id, $sequence, $occurred_at, $payload_json)
                    """;
                command.Parameters.AddWithValue("$session_id", sequencedEvent.SessionId.Value);
                command.Parameters.AddWithValue("$sequence", sequencedEvent.Sequence);
                command.Parameters.AddWithValue("$occurred_at", ToUtcIso(sequencedEvent.OccurredAt));
                command.Parameters.AddWithValue("$payload_json", AlcolerJson.Serialize(sequencedEvent.Event));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }, cancellationToken);
    }

    public ValueTask<IReadOnlyList<SequencedEvent>> LoadDurableEventsAsync(
        SessionId sessionId,
        CancellationToken cancellationToken) =>
        QueryAsync<IReadOnlyList<SequencedEvent>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, occurred_at, payload_json
                FROM durable_event
                WHERE session_id = $session_id
                ORDER BY sequence
                """;
            command.Parameters.AddWithValue("$session_id", sessionId.Value);
            using var reader = command.ExecuteReader();
            var events = new List<SequencedEvent>();
            while (reader.Read())
            {
                var sequence = reader.GetInt64(reader.GetOrdinal("sequence"));
                var occurredAt = ParseUtcIso(reader.GetString(reader.GetOrdinal("occurred_at")));
                var payload = reader.GetString(reader.GetOrdinal("payload_json"));
                var sessionEvent = AlcolerJson.Deserialize<SessionEvent>(payload)
                    ?? throw new InvalidOperationException($"Corrupt durable event payload at sequence {sequence}.");
                events.Add(new SequencedEvent(sequence, sessionId, occurredAt, sessionEvent));
            }

            return events;
        }, cancellationToken);

    public ValueTask<long> GetNextMessageSequenceAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        QueryAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(MAX(sequence) + 1, 0)
                FROM message
                WHERE session_id = $session_id
                """;
            command.Parameters.AddWithValue("$session_id", sessionId.Value);
            return Convert.ToInt64(command.ExecuteScalar());
        }, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection?.Dispose();
        _connection = null;
        _gate.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        SessionId sessionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", sessionId.Value);
        command.ExecuteNonQuery();
    }

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

    private static SessionRecord ReadSessionRecord(SqliteDataReader reader, SessionId id) => new(
        id,
        reader.GetString(reader.GetOrdinal("workspace_root")),
        reader.GetString(reader.GetOrdinal("title")),
        Enum.Parse<PermissionMode>(reader.GetString(reader.GetOrdinal("permission_mode"))),
        DeserializeOptional<ModelSelection>(reader, "model_selection_json"),
        ParseUtcIso(reader.GetString(reader.GetOrdinal("created_at"))),
        ParseUtcIso(reader.GetString(reader.GetOrdinal("updated_at"))),
        ReadBranch(reader),
        reader.GetBoolean(reader.GetOrdinal("is_archived")),
        ReadParentSessionId(reader),
        ReadTitleSource(reader));

    private static SessionSummary ReadSummary(SqliteDataReader reader) => new(
        new SessionId(reader.GetString(reader.GetOrdinal("id"))),
        reader.GetString(reader.GetOrdinal("title")),
        reader.GetString(reader.GetOrdinal("workspace_root")),
        Enum.Parse<PermissionMode>(reader.GetString(reader.GetOrdinal("permission_mode"))),
        DeserializeOptional<ModelSelection>(reader, "model_selection_json"),
        ParseUtcIso(reader.GetString(reader.GetOrdinal("created_at"))),
        ParseUtcIso(reader.GetString(reader.GetOrdinal("updated_at"))),
        reader.GetBoolean(reader.GetOrdinal("is_archived")),
        ReadParentSessionId(reader));

    private static SessionTitleSource ReadTitleSource(SqliteDataReader reader) =>
        Enum.TryParse<SessionTitleSource>(reader.GetString(reader.GetOrdinal("title_source")), out var source)
            && Enum.IsDefined(source) ? source : SessionTitleSource.Custom;

    private static SessionId? ReadParentSessionId(SqliteDataReader reader)
    {
        var ordinal = reader.GetOrdinal("parent_session_id");
        return reader.IsDBNull(ordinal) ? null : new SessionId(reader.GetString(ordinal));
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
                .ToList());
    }

    // Timestamps must round-trip as fixed-width UTC strings so ORDER BY is chronological.
    private static string ToUtcIso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseUtcIso(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private async ValueTask RunAsync(Action<SqliteConnection> operation, CancellationToken cancellationToken)
    {
        await QueryAsync<object?>(connection =>
        {
            operation(connection);
            return null;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<T> QueryAsync<T>(Func<SqliteConnection, T> operation, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = _connection
            ?? throw new InvalidOperationException($"Call {nameof(InitializeAsync)} before using {nameof(SqliteSessionStore)}.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return operation(connection);
        }
        finally
        {
            _gate.Release();
        }
    }
}
