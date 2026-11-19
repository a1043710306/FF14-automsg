using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQLite;

namespace DeepSeekAutoReply;

internal readonly record struct LocalChatArea(uint Territory, uint MapId, uint Instance);

internal sealed record ConversationScope(ulong Owner, string Key, GroupContext? Group = null, LocalChatArea? Area = null,
    string ApiProfile = "")
{
    public string OwnerId => Owner.ToString(CultureInfo.InvariantCulture);
    public string StorageKey => Key.ToUpperInvariant();
    public ConversationContext Context => new(Group, Area, ApiProfile);
}

internal sealed record ConversationContext(GroupContext? Group, LocalChatArea? Area, string ApiProfile)
{
    public bool Matches(ConversationContext? current) =>
        current is not null && ApiProfile == current.ApiProfile && Area == current.Area
        && (Group is null ? current.Group is null : Group.Matches(current.Group));
}

internal sealed record ConversationTurn(string User, string Assistant);

internal readonly record struct ConversationWriteResult(int TrimmedTurns, int EvictedConversations, bool ReplacedContext);

internal sealed class ConversationCache(string databasePath)
{
    internal const int MaxTurns = 1000;
    private const int MaxConversationsPerCharacter = 128;
    private readonly object queue = new();
    private Task previousOperation = Task.CompletedTask;
    private SQLiteConnection? database;

    public Task<ConversationTurn[]> ReadAsync(ConversationScope? scope, int maxTurns)
    {
        if (scope is null || maxTurns <= 0)
            return Task.FromResult(Array.Empty<ConversationTurn>());

        var limit = Math.Min(maxTurns, MaxTurns);
        return Enqueue(() =>
        {
            var rows = GetDatabase().Query<TurnRow>(
                """
                SELECT t.user_text AS UserText, t.assistant_text AS AssistantText, c.context AS Context
                FROM conversation_turns t
                INNER JOIN conversations c
                    ON t.owner = c.owner AND t.conversation_key = c.conversation_key
                WHERE t.owner = ? AND t.conversation_key = ?
                ORDER BY t.id DESC
                LIMIT ?
                """, scope.OwnerId, scope.StorageKey, limit);
            if (rows.Count == 0 || !scope.Context.Matches(DeserializeContext(rows[0].Context)))
                return [];

            rows.Reverse();
            return rows.Select(row => new ConversationTurn(row.UserText, row.AssistantText)).ToArray();
        });
    }

    public Task<ConversationWriteResult?> RecordAsync(ConversationScope? scope, int maxTurns, string user, string assistant)
    {
        if (scope is null || maxTurns <= 0)
            return Task.FromResult<ConversationWriteResult?>(null);

        var limit = Math.Min(maxTurns, MaxTurns);
        return Enqueue<ConversationWriteResult?>(() =>
        {
            var owner = scope.OwnerId;
            var key = scope.StorageKey;
            var context = JsonSerializer.Serialize(scope.Context);
            var trimmedTurns = 0;
            var evictedConversations = 0;
            var replacedContext = false;
            GetDatabase().RunInTransaction(connection =>
            {
                connection.Execute("INSERT OR IGNORE INTO conversations (owner, conversation_key, context) VALUES (?, ?, ?)",
                    owner, key, context);
                var previousContext = connection.ExecuteScalar<string>(
                    "SELECT context FROM conversations WHERE owner = ? AND conversation_key = ?", owner, key);
                replacedContext = !scope.Context.Matches(DeserializeContext(previousContext));
                if (replacedContext)
                    connection.Execute("DELETE FROM conversation_turns WHERE owner = ? AND conversation_key = ?", owner, key);

                trimmedTurns = connection.Execute(
                    "INSERT INTO conversation_turns (owner, conversation_key, user_text, assistant_text) VALUES (?, ?, ?, ?)",
                    owner, key, user, assistant);
                evictedConversations = connection.Execute(
                    "UPDATE conversations SET context = ?, last_turn_id = last_insert_rowid() WHERE owner = ? AND conversation_key = ?",
                    context, owner, key);
                connection.Execute(
                    """
                    DELETE FROM conversation_turns
                    WHERE owner = ? AND conversation_key = ?
                        AND id < (SELECT id FROM conversation_turns WHERE owner = ? AND conversation_key = ?
                            ORDER BY id DESC LIMIT 1 OFFSET ?)
                    """, owner, key, owner, key, limit - 1);
                connection.Execute(
                    """
                    DELETE FROM conversations WHERE owner = ? AND conversation_key IN (
                        SELECT conversation_key FROM conversations WHERE owner = ?
                        ORDER BY last_turn_id DESC LIMIT -1 OFFSET ?
                    )
                    """, owner, owner, MaxConversationsPerCharacter);
            });
            return new ConversationWriteResult(trimmedTurns, evictedConversations, replacedContext);
        });
    }

    public Task ClearAsync() => Enqueue(() =>
    {
        if (database is not null || File.Exists(databasePath))
            GetDatabase().Execute("DELETE FROM conversations");
    });

    public Task SetLimitAsync(int maxTurns)
    {
        if (maxTurns <= 0)
            return ClearAsync();

        return Enqueue(() =>
        {
            if (database is null && !File.Exists(databasePath))
                return;

            GetDatabase().Execute(
                """
                DELETE FROM conversation_turns WHERE id IN (
                    SELECT id FROM (
                        SELECT id, ROW_NUMBER() OVER (PARTITION BY owner, conversation_key ORDER BY id DESC) AS turn_number
                        FROM conversation_turns
                    ) WHERE turn_number > ?
                )
                """, Math.Min(maxTurns, MaxTurns));
        });
    }

    public Task CloseAsync() => Enqueue(() =>
    {
        database?.Dispose();
        database = null;
    });

    private Task Enqueue(Action action) => Enqueue(() =>
    {
        action();
        return true;
    });

    private Task<T> Enqueue<T>(Func<T> action)
    {
        lock (queue)
        {
            // Serialize disk work in submission order without running it on the framework thread.
            var operation = previousOperation.ContinueWith(_ => action(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            previousOperation = operation;
            return operation;
        }
    }

    private SQLiteConnection GetDatabase()
    {
        if (database is not null)
            return database;

        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SQLiteConnection(databasePath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex)
        {
            BusyTimeout = TimeSpan.FromSeconds(3),
        };
        try
        {
            connection.Execute("PRAGMA foreign_keys = ON");
            connection.ExecuteScalar<string>("PRAGMA journal_mode = WAL");
            connection.Execute(
                """
                CREATE TABLE IF NOT EXISTS conversations (
                    owner TEXT NOT NULL,
                    conversation_key TEXT NOT NULL,
                    context TEXT NOT NULL,
                    last_turn_id INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (owner, conversation_key)
                )
                """);
            connection.Execute(
                """
                CREATE TABLE IF NOT EXISTS conversation_turns (
                    id INTEGER PRIMARY KEY,
                    owner TEXT NOT NULL,
                    conversation_key TEXT NOT NULL,
                    user_text TEXT NOT NULL,
                    assistant_text TEXT NOT NULL,
                    FOREIGN KEY (owner, conversation_key) REFERENCES conversations(owner, conversation_key) ON DELETE CASCADE
                )
                """);
            connection.Execute(
                "CREATE INDEX IF NOT EXISTS turn_lookup ON conversation_turns (owner, conversation_key, id DESC)");
            database = connection;
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static ConversationContext? DeserializeContext(string? value) =>
        value is null ? null : JsonSerializer.Deserialize<ConversationContext>(value);

    private sealed class TurnRow
    {
        public TurnRow() { }
        public string UserText { get; set; } = string.Empty;
        public string AssistantText { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
    }
}
