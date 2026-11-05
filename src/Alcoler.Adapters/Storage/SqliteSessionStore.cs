using System.Globalization;
using Alcoler.Contracts.Conversation;
using Alcoler.Contracts.Events;
using Alcoler.Contracts.Identifiers;
using Alcoler.Contracts.Sessions;
using Alcoler.Contracts.Serialization;
using Alcoler.Contracts.Storage;
using Microsoft.Data.Sqlite;

namespace Alcoler.Adapters.Storage;

public sealed partial class SqliteSessionStore : ISessionStore, IAsyncDisposable, IDisposable
{
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
                     created_at, updated_at, branch_json, is_archived, parent_session_id, title_source, shell_json,
                     forked_from_session_id, forked_from_message_id, active_workflows_json, plan_enabled)
                VALUES ($id, $workspace_root, $title, $permission_mode, $model_selection_json,
                        $created_at, $updated_at, $branch_json, $is_archived, $parent_session_id, $title_source, $shell_json,
                        $forked_from_session_id, $forked_from_message_id, $active_workflows_json, $plan_enabled)
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
            command.Parameters.AddWithValue("$shell_json", record.Shell is null ? DBNull.Value : AlcolerJson.Serialize(record.Shell));
            command.Parameters.AddWithValue("$forked_from_session_id",
                (object?)record.ForkedFromSessionId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$forked_from_message_id",
                (object?)record.ForkedFromMessageId?.Value ?? DBNull.Value);
            command.Parameters.AddWithValue("$active_workflows_json",
                record.ActiveWorkflows is { Count: > 0 } workflows
                    ? SerializeWorkflows(workflows)
                    : DBNull.Value);
            command.Parameters.AddWithValue("$plan_enabled", record.PlanEnabled);
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
                       created_at, updated_at, branch_json, is_archived, parent_session_id, title_source, shell_json,
                       forked_from_session_id, forked_from_message_id, active_workflows_json, plan_enabled
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
        CancellationToken cancellationToken) =>
        ListSessionsAsync(workspaceRoot, limit, SessionListKind.Active, cancellationToken);

    public ValueTask<IReadOnlyList<SessionSummary>> ListSessionsAsync(
        string? workspaceRoot,
        int limit,
        SessionListKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var archived = kind == SessionListKind.Archived ? 1 : 0;
        return QueryAsync<IReadOnlyList<SessionSummary>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, title, workspace_root, permission_mode, model_selection_json, plan_enabled,
                       created_at, updated_at, is_archived, parent_session_id
                FROM session
                WHERE parent_session_id IS NULL
                  AND is_archived = $is_archived
                  AND ($workspace_root IS NULL OR workspace_root = $workspace_root)
                ORDER BY updated_at DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$is_archived", archived);
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

    public ValueTask SetSessionArchivedAsync(
        SessionId sessionId,
        bool archived,
        CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE session SET is_archived = $is_archived WHERE id = $id";
            command.Parameters.AddWithValue("$id", sessionId.Value);
            command.Parameters.AddWithValue("$is_archived", archived);
            command.ExecuteNonQuery();
        }, cancellationToken);

    public ValueTask<int> ArchiveSessionsUpdatedBeforeAsync(
        DateTimeOffset cutoff,
        IReadOnlyCollection<SessionId> excludedSessionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(excludedSessionIds);
        var excluded = excludedSessionIds.Distinct().ToArray();
        return QueryAsync(connection =>
        {
            using var command = connection.CreateCommand();
            var exclusionClause = excluded.Length == 0
                ? string.Empty
                : $" AND id NOT IN ({string.Join(", ", Enumerable.Range(0, excluded.Length).Select(index => $"$excluded{index}"))})";
            command.CommandText = $"""
                UPDATE session
                SET is_archived = 1
                WHERE parent_session_id IS NULL
                  AND is_archived = 0
                  AND updated_at < $cutoff{exclusionClause}
                """;
            command.Parameters.AddWithValue("$cutoff", ToUtcIso(cutoff));
            for (var index = 0; index < excluded.Length; index++)
            {
                command.Parameters.AddWithValue($"$excluded{index}", excluded[index].Value);
            }

            return command.ExecuteNonQuery();
        }, cancellationToken);
    }

    public ValueTask<int> CountArchivedSessionsAsync(CancellationToken cancellationToken) =>
        QueryAsync(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM session
                WHERE parent_session_id IS NULL
                  AND is_archived = 1
                """;
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);

    public ValueTask<ArchivedSessionDeletion> DeleteArchivedSessionsAsync(CancellationToken cancellationToken) =>
        QueryAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            int taskCount;
            using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText = """
                    SELECT COUNT(*)
                    FROM session
                    WHERE parent_session_id IS NULL
                      AND is_archived = 1
                    """;
                taskCount = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            using (var collect = connection.CreateCommand())
            {
                collect.Transaction = transaction;
                collect.CommandText = """
                    DROP TABLE IF EXISTS archived_cleanup_ids;
                    CREATE TEMP TABLE archived_cleanup_ids (id TEXT PRIMARY KEY);
                    INSERT INTO archived_cleanup_ids (id)
                    WITH RECURSIVE doomed(id) AS (
                        SELECT id FROM session WHERE is_archived = 1
                        UNION
                        SELECT child.id
                        FROM session child
                        JOIN doomed ON child.parent_session_id = doomed.id
                    )
                    SELECT id FROM doomed;
                    """;
                collect.ExecuteNonQuery();
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM message WHERE session_id IN (SELECT id FROM archived_cleanup_ids);
                    DELETE FROM durable_event WHERE session_id IN (SELECT id FROM archived_cleanup_ids);
                    DELETE FROM session WHERE id IN (SELECT id FROM archived_cleanup_ids);
                    """;
                delete.ExecuteNonQuery();
            }

            var deletedSessionIds = new List<SessionId>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT id FROM archived_cleanup_ids";
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    deletedSessionIds.Add(new SessionId(reader.GetString(0)));
                }
            }

            using (var drop = connection.CreateCommand())
            {
                drop.Transaction = transaction;
                drop.CommandText = "DROP TABLE archived_cleanup_ids";
                drop.ExecuteNonQuery();
            }

            transaction.Commit();

            // VACUUM cannot run inside a transaction, and a locked database must not undo the deletion.
            try
            {
                Execute(connection, "VACUUM;");
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (SqliteException)
            {
            }

            return new ArchivedSessionDeletion(taskCount, deletedSessionIds);
        }, cancellationToken);

    public ValueTask DeleteSessionAsync(SessionId sessionId, CancellationToken cancellationToken) =>
        RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.Add("$id", SqliteType.Text).Value = sessionId.Value;
            command.CommandText = "DELETE FROM message WHERE session_id = $id";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM durable_event WHERE session_id = $id";
            command.ExecuteNonQuery();
            command.CommandText = "DELETE FROM session WHERE id = $id";
            command.ExecuteNonQuery();
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
            InsertMessages(connection, transaction, sessionId, messages, startingSequence);
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
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM message WHERE session_id = $id";
                delete.Parameters.AddWithValue("$id", sessionId.Value);
                delete.ExecuteNonQuery();
            }

            InsertMessages(connection, transaction, sessionId, messages, 0L);
            transaction.Commit();
        }, cancellationToken);
    }

    private static void InsertMessages(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        IReadOnlyList<ConversationMessage> messages,
        long startingSequence)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message (session_id, sequence, message_id, payload_json, created_at)
            VALUES ($session_id, $sequence, $message_id, $payload_json, $created_at)
            """;
        var sessionParameter = command.Parameters.Add("$session_id", SqliteType.Text);
        var sequenceParameter = command.Parameters.Add("$sequence", SqliteType.Integer);
        var messageParameter = command.Parameters.Add("$message_id", SqliteType.Text);
        var payloadParameter = command.Parameters.Add("$payload_json", SqliteType.Text);
        var createdParameter = command.Parameters.Add("$created_at", SqliteType.Text);
        command.Prepare();
        var sequence = startingSequence;
        foreach (var message in messages)
        {
            sessionParameter.Value = sessionId.Value;
            sequenceParameter.Value = sequence;
            messageParameter.Value = message.Id.Value;
            payloadParameter.Value = AlcolerJson.Serialize(message);
            createdParameter.Value = ToUtcIso(message.CreatedAt);
            command.ExecuteNonQuery();
            sequence++;
        }
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
            var sequenceOrdinal = reader.GetOrdinal("sequence");
            var payloadOrdinal = reader.GetOrdinal("payload_json");
            var messages = new List<StoredMessage>();
            while (reader.Read())
            {
                var sequence = reader.GetInt64(sequenceOrdinal);
                var payload = reader.GetString(payloadOrdinal);
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
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO durable_event (session_id, sequence, occurred_at, payload_json)
                VALUES ($session_id, $sequence, $occurred_at, $payload_json)
                """;
            var sessionParameter = command.Parameters.Add("$session_id", SqliteType.Text);
            var sequenceParameter = command.Parameters.Add("$sequence", SqliteType.Integer);
            var occurredParameter = command.Parameters.Add("$occurred_at", SqliteType.Text);
            var payloadParameter = command.Parameters.Add("$payload_json", SqliteType.Text);
            command.Prepare();
            foreach (var sequencedEvent in events)
            {
                sessionParameter.Value = sequencedEvent.SessionId.Value;
                sequenceParameter.Value = sequencedEvent.Sequence;
                occurredParameter.Value = ToUtcIso(sequencedEvent.OccurredAt);
                payloadParameter.Value = AlcolerJson.Serialize(sequencedEvent.Event);
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
            var sequenceOrdinal = reader.GetOrdinal("sequence");
            var occurredOrdinal = reader.GetOrdinal("occurred_at");
            var payloadOrdinal = reader.GetOrdinal("payload_json");
            var events = new List<SequencedEvent>();
            while (reader.Read())
            {
                var sequence = reader.GetInt64(sequenceOrdinal);
                var occurredAt = ParseUtcIso(reader.GetString(occurredOrdinal));
                var payload = reader.GetString(payloadOrdinal);
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
            // The work must stay inline on the caller's context: offloading it to the thread pool
            // widens the turn-start window that phase-polling end-to-end tests race against.
            return operation(connection);
        }
        finally
        {
            _gate.Release();
        }
    }
}
