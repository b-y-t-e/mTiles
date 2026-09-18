using System.Collections.Concurrent;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// opencode's own store under <c>&lt;data&gt;/storage/</c>: a project index, a session file per
/// conversation, and a message file per turn.
/// </summary>
/// <remarks>
/// <para>Measured 2026-09-18 against 1.18.18. <c>project/&lt;id&gt;.json</c> names the
/// <c>worktree</c>, <c>session/&lt;projectId&gt;/ses_*.json</c> is one file per conversation, and
/// <c>message/&lt;sessionId&gt;/msg_*.json</c> holds the assistant's turns with a
/// <c>tokens</c> object and a <c>cost</c>.</para>
/// <para><b>The project id is read, never computed.</b> It looks like a hash of the path and is not one
/// — measured, SHA-1 of the worktree in every plausible spelling misses it — so the index is what
/// answers, which is both correct and cheaper to keep correct: when opencode changes how it derives the
/// id, nothing here has to change at all.</para>
/// <para><b>The data directory is <c>XDG_DATA_HOME</c>'s, which is the variable a sign-in relocates.</b>
/// That is the same fact <c>OpenCodeAgent.SignInEnv</c> carries, and it is handed in rather than
/// restated for the reason Claude Code's config directory is.</para>
/// </remarks>
public sealed class OpenCodeSessionLog : AgentSessionLog
{
    private readonly Func<AiSignIn?, string> _dataDirectory;

    /// <param name="dataDirectory">Where this sign-in's <c>XDG_DATA_HOME</c> points, or the default
    /// account's <c>~/.local/share</c>.</param>
    public OpenCodeSessionLog(Func<AiSignIn?, string> dataDirectory) => _dataDirectory = dataDirectory;

    private string StorageRoot(AiSignIn? signIn) =>
        Path.Combine(_dataDirectory(signIn), "opencode", "storage");

    /// <inheritdoc />
    /// <remarks>The session directory rather than the message one: a new conversation appears there,
    /// and a turn inside an existing one changes the session file's own summary as well.</remarks>
    public override string? WatchDirectory(AiSignIn? signIn, string workspaceDir) =>
        ProjectIdFor(signIn, workspaceDir) is { Length: > 0 } project
            ? Path.Combine(StorageRoot(signIn), "session", project)
            : null;

    /// <inheritdoc />
    protected override IEnumerable<SessionEntry> Enumerate(AiSignIn? signIn, string workspaceDir) =>
        FilesIn(WatchDirectory(signIn, workspaceDir), "ses_*.json")
            .Select(file => new SessionEntry(Path.GetFileNameWithoutExtension(file.Name),
                StartedAt(file), file.LastWriteTimeUtc));

    /// <inheritdoc />
    protected override AgentSessionReading? Read(AiSignIn? signIn, string workspaceDir, SessionEntry entry)
    {
        var messages = Path.Combine(StorageRoot(signIn), "message", entry.Id);

        // Newest first for the tokens — the last assistant turn is what the context currently holds —
        // and every file for the cost, which opencode records per turn rather than as a running total.
        var turns = FilesIn(messages, "msg_*.json")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => _turns.Get(file, ReadTurn))
            .OfType<Turn>()
            .ToList();

        var latest = turns.FirstOrDefault(turn => turn.UsedTokens is not null);
        var costs = turns.Where(turn => turn.CostUsd is not null).Select(turn => turn.CostUsd!.Value).ToList();
        return new AgentSessionReading(entry.Id, entry.UpdatedAt, latest?.UsedTokens,
            CostUsd: costs.Count > 0 ? costs.Sum() : null, Model: latest?.Model);
    }

    /// <summary>What one message file says: the context after it, and what it cost.</summary>
    private sealed record Turn(long? UsedTokens, string? Model, decimal? CostUsd);

    /// <summary>Every message file parsed once and then only when it changes, so a turn costs the file
    /// that turn wrote rather than the whole conversation again.</summary>
    private readonly FileParseCache<Turn> _turns = new();

    /// <summary>One message file, or null while it is being written and does not parse yet.</summary>
    private static Turn? ReadTurn(FileInfo file)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (document)
        {
            var message = document.RootElement;
            decimal? cost = message.TryGetProperty("cost", out var spent) && spent.TryGetDecimal(out var amount)
                            && amount > 0
                ? amount
                : null;

            if (!message.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return new Turn(null, null, cost);

            var total = Tokens(tokens, "input") + Tokens(tokens, "output")
                        + Cache(tokens, "read") + Cache(tokens, "write");
            return total <= 0
                ? new Turn(null, null, cost)
                : new Turn(total, message.TryGetProperty("modelID", out var id) ? id.GetString() : null, cost);
        }
    }

    /// <summary>What the project index said, and the shape it was in when it said it.</summary>
    /// <param name="WrittenUtc">The index directory's own last write, which a new project file bumps.</param>
    /// <param name="Files">How many files were in it, because a directory's write time is the file
    /// system's to round and a count is not.</param>
    private readonly record struct IndexState(DateTime WrittenUtc, int Files);

    /// <summary>The last answer for one index and one working directory, kept while both hold.</summary>
    private readonly ConcurrentDictionary<string, (IndexState State, string? ProjectId)> _projects =
        new(StringComparer.Ordinal);

    /// <summary>The id opencode has filed this working directory under, or null when it has not.</summary>
    /// <remarks>
    /// <para>A linear walk of the project index, which is one small file per project the user has ever
    /// opened. Compared as paths rather than as strings, because the index stores whatever spelling
    /// opencode was started with and a tile's working directory is whatever the workspace holds.</para>
    /// <para><b>Walked only when the index has moved.</b> This is asked on every attempt to attach a
    /// watcher, and a workspace opencode has never run in has no project — so a tile there asks again
    /// every few seconds for as long as it is open, and each miss read and parsed every project the user
    /// has ever had. The answer only changes when opencode files a new project, which adds a file to
    /// this directory, so the walk is spent on that and a repeat costs one listing with nothing opened.
    /// </para>
    /// </remarks>
    private string? ProjectIdFor(AiSignIn? signIn, string workspaceDir)
    {
        var index = Path.Combine(StorageRoot(signIn), "project");
        var directory = new DirectoryInfo(index);
        if (!directory.Exists) return null;

        var files = directory.EnumerateFiles("*.json", SearchOption.TopDirectoryOnly).ToList();
        var state = new IndexState(directory.LastWriteTimeUtc, files.Count);
        var question = string.Concat(index, "|", workspaceDir);
        if (_projects.TryGetValue(question, out var known) && known.State == state) return known.ProjectId;

        var project = ProjectIn(files, workspaceDir);
        _projects[question] = (state, project);
        return project;
    }

    /// <summary>The project among those files whose worktree is that working directory.</summary>
    private static string? ProjectIn(IEnumerable<FileInfo> index, string workspaceDir)
    {
        foreach (var file in index)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.TryGetProperty("worktree", out var worktree)
                    && worktree.GetString() is { Length: > 0 } path
                    && SamePath(path, workspaceDir))
                    return Path.GetFileNameWithoutExtension(file.Name);
            }
        }

        return null;
    }

    private static long Tokens(JsonElement tokens, string name) =>
        tokens.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) ? count : 0;

    private static long Cache(JsonElement tokens, string name) =>
        tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object
            ? Tokens(cache, name)
            : 0;

    /// <summary>Whether two spellings name one directory.</summary>
    /// <remarks>Case-insensitively on Windows, where <c>D:\Work</c> and <c>d:\work</c> are the same
    /// place and opencode records whichever the shell was started in.</remarks>
    private static bool SamePath(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Normalise(left), Normalise(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
