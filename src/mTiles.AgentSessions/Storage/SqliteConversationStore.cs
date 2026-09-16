using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using mTiles.AgentSessions.Events;

namespace mTiles.AgentSessions.Storage;

/// <summary>
/// The conversation store as one SQLite file.
/// </summary>
/// <remarks>
/// <para><b>SQLite rather than a file per conversation</b> because a browser view will ask questions
/// across conversations — every conversation in a workspace, the newest ones — and a directory of JSON
/// lines answers those by reading all of it.</para>
/// <para><b>One writer at a time, by lock.</b> The file is in WAL mode so a read never waits on a
/// write, and every write goes through <see cref="_write"/> so two tiles appending at once cannot meet
/// the "database is locked" answer SQLite gives a second concurrent writer.</para>
/// <para><b>No shared cache</b>, because it takes that guarantee back: measured, with
/// <c>Cache=Shared</c> a read on a second connection while a write transaction is open answers
/// <c>SQLite Error 6: database table is locked</c> — shared-cache table locks are not WAL's snapshot
/// isolation — and <see cref="Find"/>/<see cref="ReadEvents"/> run outside <see cref="_write"/> on
/// purpose, so a tile opening while another appends would fail to open its conversation at all. The
/// busy timeout is what covers the one contention WAL leaves, two writers at the file level.</para>
/// <para>An event this build cannot read — written by a newer one and read after a rollback — is skipped
/// with a log line rather than failing the conversation: the rest of it is still worth showing.</para>
/// </remarks>
public sealed class SqliteConversationStore : IConversationStore
{
    /// <summary>2 added the index a conversation list is read through. Nothing reads this number yet: every
    /// statement in <see cref="EnsureSchema"/> is <c>IF NOT EXISTS</c>, so an older file is brought up to date
    /// by opening it, and an older build opening a newer file simply does not use what it does not know.</summary>
    private const int SchemaVersion = 2;
    private const int BusyTimeoutSeconds = 30;

    private readonly string _connectionString;
    private readonly Lock _write = new();

    public SqliteConversationStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();

        EnsureSchema();
    }

    public ConversationRecord? Find(string conversationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, agent_id, working_directory, resume_token, created_at, updated_at " +
            "FROM conversations WHERE id = $id";
        command.Parameters.AddWithValue("$id", conversationId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new ConversationRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ParseTime(reader.GetString(4)),
            ParseTime(reader.GetString(5)));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>One statement rather than a query per row: the opening comes from a correlated subquery over
    /// <c>events</c>, whose primary key is <c>(conversation_id, sequence)</c>, so each one walks that
    /// conversation's own rows in order and stops at the first that is a user message. A conversation nobody
    /// spoke in is the one that costs a scan of all of its rows, and it is also the one thrown away here.</para>
    /// <para><b>Ordering by the timestamp as text</b> is safe only because every writer formats UTC with the
    /// round-trip specifier, so each one ends <c>+00:00</c> and sorts the same as the instant. An event carrying
    /// a local-time <c>At</c> would sort into the wrong place rather than fail.</para>
    /// <para>A payload this build cannot read as JSON costs the row its title, not its place in the list;
    /// anything else is left to the caller, as in <see cref="ReadEvents"/>.</para>
    /// </remarks>
    public IReadOnlyList<ConversationSummary> List(string workingDirectory)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, agent_id, updated_at, opening FROM (" +
            " SELECT c.id AS id, c.agent_id AS agent_id, c.updated_at AS updated_at, " +
            " (SELECT e.payload FROM events e WHERE e.conversation_id = c.id AND e.type = $opening " +
            "  ORDER BY e.sequence LIMIT 1) AS opening " +
            " FROM conversations c WHERE c.working_directory = $cwd) " +
            "WHERE opening IS NOT NULL ORDER BY updated_at DESC";
        command.Parameters.AddWithValue("$cwd", workingDirectory);
        command.Parameters.AddWithValue("$opening", nameof(UserMessageAdded));

        var conversations = new List<ConversationSummary>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            conversations.Add(new ConversationSummary(
                reader.GetString(0),
                reader.GetString(1),
                ParseTime(reader.GetString(2)),
                OpeningOf(reader.GetString(3))));
        }

        return conversations;
    }

    private static string? OpeningOf(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentEvent>(payload, AgentSessionJson.Options) is UserMessageAdded m
                ? m.Text
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(ConversationRecord record)
    {
        lock (_write)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO conversations (id, agent_id, working_directory, resume_token, created_at, updated_at) " +
                "VALUES ($id, $agent, $cwd, $token, $created, $updated) " +
                "ON CONFLICT(id) DO UPDATE SET agent_id = $agent, working_directory = $cwd, " +
                "resume_token = $token, updated_at = $updated";
            command.Parameters.AddWithValue("$id", record.Id);
            command.Parameters.AddWithValue("$agent", record.AgentId);
            command.Parameters.AddWithValue("$cwd", record.WorkingDirectory);
            command.Parameters.AddWithValue("$token", (object?)record.ResumeToken ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", FormatTime(record.CreatedAt));
            command.Parameters.AddWithValue("$updated", FormatTime(record.UpdatedAt));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AgentEvent> ReadEvents(string conversationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sequence, payload FROM events WHERE conversation_id = $id ORDER BY sequence";
        command.Parameters.AddWithValue("$id", conversationId);

        var events = new List<AgentEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<AgentEvent>(reader.GetString(1), AgentSessionJson.Options) is { } e)
                    events.Add(e);
            }
            catch (JsonException ex)
            {
                Trace.TraceWarning(
                    $"[AgentSessions] Skipped event {reader.GetInt64(0)} of {conversationId}: {ex.Message}");
            }
        }

        return events;
    }

    public long LastSequence(string conversationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM events WHERE conversation_id = $id";
        command.Parameters.AddWithValue("$id", conversationId);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Append(string conversationId, IReadOnlyList<AgentEvent> events)
    {
        if (events.Count == 0) return;

        lock (_write)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR REPLACE INTO events (conversation_id, sequence, type, at, payload) " +
                "VALUES ($id, $sequence, $type, $at, $payload)";
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var sequence = command.Parameters.Add("$sequence", SqliteType.Integer);
            var type = command.Parameters.Add("$type", SqliteType.Text);
            var at = command.Parameters.Add("$at", SqliteType.Text);
            var payload = command.Parameters.Add("$payload", SqliteType.Text);

            foreach (var e in events)
            {
                id.Value = conversationId;
                sequence.Value = e.Sequence;
                type.Value = e.GetType().Name;
                at.Value = FormatTime(e.At);
                payload.Value = JsonSerializer.Serialize(e, AgentSessionJson.Options);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public void Delete(string conversationId)
    {
        lock (_write)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "DELETE FROM events WHERE conversation_id = $id; DELETE FROM conversations WHERE id = $id";
            command.Parameters.AddWithValue("$id", conversationId);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        lock (_write)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    working_directory TEXT NOT NULL,
                    resume_token TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events (
                    conversation_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    type TEXT NOT NULL,
                    at TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    PRIMARY KEY (conversation_id, sequence)) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS conversations_by_directory
                    ON conversations (working_directory, updated_at DESC);
                PRAGMA user_version = {SchemaVersion};
                """;
            command.ExecuteNonQuery();
        }
    }

    private static string FormatTime(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
