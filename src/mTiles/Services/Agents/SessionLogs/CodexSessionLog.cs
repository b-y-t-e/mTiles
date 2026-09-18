using System.Collections.Concurrent;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// codex's rollout files: <c>&lt;home&gt;/sessions/YYYY/MM/DD/rollout-&lt;stamp&gt;-&lt;id&gt;.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>The odd one out in two ways, and both are why this is its own class rather than a
/// <see cref="JsonlSessionLog"/>. codex files by <em>date</em> and not by project, so which conversation
/// belongs to this workspace is a question only the file's own first line answers — the same reading
/// <see cref="SessionCapture.StartedIn"/> already does for the capture, reused here rather than written
/// again. And it is the <b>only one of the six that names the context window it is working in</b>
/// (<c>model_context_window</c>), so it is the only agent whose gauge needs nothing from the provider
/// side at all.</para>
/// <para><b><c>last_token_usage</c>, not <c>total_token_usage</c>.</b> Measured 2026-09-18: the first is
/// the size of the last request, which is what the context currently holds; the second is every token the
/// conversation has ever spent, which grows past the window on any conversation of a few turns and would
/// draw the gauge as permanently full.</para>
/// </remarks>
public sealed class CodexSessionLog : AgentSessionLog
{
    private readonly Func<AiSignIn?, string> _sessionsRoot;
    /// <summary>The last token count each rollout carries, read once and then only from where the
    /// previous read stopped.</summary>
    /// <remarks>A rollout is append-only and runs to megabytes, and the watcher on codex's store is
    /// recursive on the whole <c>sessions</c> root, so every write of every codex conversation on the
    /// machine asks each codex tile to read its own rollout again. Remembered per file, a write elsewhere
    /// costs one <c>stat</c> and a turn here the lines that turn appended. Shared by every codex tile,
    /// because the agent holds one session log.</remarks>
    private readonly JsonlFoldTail<RolloutTokens> _tokens = new(FoldTokenCount, default);

    /// <summary>What a rollout says about its context, as far as it has been read.</summary>
    private readonly record struct RolloutTokens(long? Used, long? Window);

    /// <summary>Where each id's rollout was last found, so asking about a known conversation does not
    /// walk every date directory on the machine again.</summary>
    private readonly ConcurrentDictionary<string, string> _rolloutPaths = new(StringComparer.Ordinal);

    /// <summary>The rollouts already seen to begin in a working directory. Only a yes is remembered: a
    /// rollout whose first line has not been written yet answers no now and yes a moment later, while
    /// the first line, once written, never changes.</summary>
    private readonly ConcurrentDictionary<(string Rollout, string WorkspaceDir), bool> _heldIn = new();

    /// <param name="sessionsRoot">The <c>sessions</c> directory of this sign-in's <c>CODEX_HOME</c>, or
    /// of the default account's <c>~/.codex</c>.</param>
    public CodexSessionLog(Func<AiSignIn?, string> sessionsRoot) => _sessionsRoot = sessionsRoot;

    /// <inheritdoc />
    /// <remarks>The root rather than one project's directory, because codex has no per-project
    /// directory: a new conversation appears under today's date, which is a subdirectory that may not
    /// exist yet. The watcher therefore has to be recursive here, which is what
    /// <c>AgentSessionWatcher</c> reads this answer as.</remarks>
    public override string? WatchDirectory(AiSignIn? signIn, string workspaceDir) => _sessionsRoot(signIn);

    /// <inheritdoc />
    /// <remarks>Every rollout on the machine is a candidate, and nothing here opens one: the name says the
    /// id and the file system says the times. Which of them belong to this workspace is
    /// <see cref="BelongsTo"/>'s question, asked only of the candidates the time filter has kept — this is
    /// read every time any codex session anywhere writes, since the watcher is recursive on the whole
    /// <c>sessions</c> root, and opening every rollout ever written to answer it scaled with the
    /// machine's history.</remarks>
    protected override IEnumerable<SessionEntry> Enumerate(AiSignIn? signIn, string workspaceDir)
    {
        var root = _sessionsRoot(signIn);
        if (!Directory.Exists(root)) yield break;

        foreach (var file in new DirectoryInfo(root).EnumerateFiles("rollout-*.jsonl",
                     SearchOption.AllDirectories))
        {
            if (SessionCapture.SessionIdIn(file.Name) is { Length: > 0 } id) yield return EntryOf(file, id);
        }
    }

    /// <inheritdoc />
    /// <remarks>codex files by date, so only the rollout's own first line says where it was held.</remarks>
    protected override bool BelongsTo(string workspaceDir, SessionEntry entry)
    {
        if (entry.File is not { } file) return false;
        if (_heldIn.ContainsKey((file.FullName, workspaceDir))) return true;
        if (!SessionCapture.StartedIn(file.FullName, workspaceDir)) return false;

        _heldIn[(file.FullName, workspaceDir)] = true;
        return true;
    }

    /// <inheritdoc />
    /// <remarks>By the file's name rather than through <see cref="Enumerate"/>: that opens the first line
    /// of every rollout on the machine, and this is asked every time any codex session anywhere writes —
    /// the watcher is recursive on the whole <c>sessions</c> root. Only the one file named after the id is
    /// opened, to ask whether it belongs to this workspace.</remarks>
    protected override SessionEntry? Find(AiSignIn? signIn, string workspaceDir, string sessionId,
        CancellationToken ct)
    {
        if (!IsPlainId(sessionId) || RolloutNamed(signIn, sessionId) is not { } file) return null;
        var entry = EntryOf(file, sessionId);
        return BelongsTo(workspaceDir, entry) ? entry : null;
    }

    private static SessionEntry EntryOf(FileInfo file, string id) =>
        new(id, StartedAt(file), file.LastWriteTimeUtc, file);

    /// <summary>Whether an id can go into a file-name pattern as itself.</summary>
    /// <remarks>The id comes out of a hand-editable layout, and a <c>*</c> in it would match somebody
    /// else's rollout.</remarks>
    private static bool IsPlainId(string sessionId) =>
        sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <inheritdoc />
    public override bool TellsHeadlessRunsApart => true;

    /// <inheritdoc />
    /// <remarks>Measured 2026-09-18 across this machine's rollouts: the metadata line's
    /// <c>payload.source</c> is <c>cli</c> for the TUI, <c>exec</c> for <c>codex exec</c> — a Goal tile's
    /// run — and <c>vscode</c> for an app-server client, which is what an Agent tile's session is. A
    /// rollout that names no source is taken as interactive, the rule Claude Code's reader follows for a
    /// file that has not said yet.</remarks>
    protected override bool IsInteractive(AiSignIn? signIn, string workspaceDir, SessionEntry entry) =>
        RolloutOf(signIn, entry) is { } file
        && SourceIn(FirstLineOf(file.FullName)) is null or InteractiveSource;

    /// <summary>What codex's own interface writes as a rollout's source.</summary>
    private const string InteractiveSource = "cli";

    private static string? FirstLineOf(string path)
    {
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete));
        return reader.ReadLine();
    }

    private static string? SourceIn(string? metadataLine)
    {
        if (string.IsNullOrWhiteSpace(metadataLine)) return null;

        try
        {
            using var document = JsonDocument.Parse(metadataLine);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("payload", out var payload)
                   && payload.ValueKind == JsonValueKind.Object
                   && payload.TryGetProperty("source", out var source)
                   && source.ValueKind == JsonValueKind.String
                ? source.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The rollout file a conversation is kept in, found by its id wherever its date put it.
    /// </summary>
    private FileInfo? RolloutOf(AiSignIn? signIn, SessionEntry entry) =>
        entry.File is { Exists: true } file ? file : RolloutNamed(signIn, entry.Id);

    private FileInfo? RolloutNamed(AiSignIn? signIn, string sessionId)
    {
        var root = _sessionsRoot(signIn);
        var key = Path.Combine(root, sessionId);
        if (_rolloutPaths.TryGetValue(key, out var remembered) && new FileInfo(remembered) is { Exists: true } known)
            return known;

        if (!Directory.Exists(root)) return null;
        var found = new DirectoryInfo(root)
            .EnumerateFiles($"rollout-*-{sessionId}.jsonl", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (found is not null) _rolloutPaths[key] = found.FullName;
        return found;
    }

    /// <inheritdoc />
    /// <remarks>Only what was appended since the last read is read (<see cref="JsonlFoldTail{TState}"/>): this
    /// is asked by every codex tile whenever any codex session anywhere writes, and a rollout that has not
    /// moved costs a <c>stat</c>.</remarks>
    protected override AgentSessionReading? Read(AiSignIn? signIn, string workspaceDir, SessionEntry entry)
    {
        if (RolloutOf(signIn, entry) is not { } file) return null;

        var tokens = _tokens.Read(file);
        return new AgentSessionReading(entry.Id, file.LastWriteTimeUtc, tokens.Used, tokens.Window);
    }

    /// <summary>One rollout line folded in: the last <c>token_count</c> that <em>parses</em> wins.</summary>
    /// <remarks>The rule is the last one that parses because the substring also turns up inside messages
    /// about token counts; the cheap <c>Contains</c> keeps the parse off the thousands of lines that are
    /// the conversation.</remarks>
    private static RolloutTokens FoldTokenCount(RolloutTokens tokens, string line) =>
        line.Contains("token_count", StringComparison.Ordinal) && TokenCountIn(line) is { } found
            ? new RolloutTokens(found.Used ?? tokens.Used, found.Window ?? tokens.Window)
            : tokens;

    private static RolloutTokens? TokenCountIn(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("info", out var info)
                || info.ValueKind != JsonValueKind.Object)
                return null;

            long? used = info.TryGetProperty("last_token_usage", out var last)
                         && last.ValueKind == JsonValueKind.Object
                         && last.TryGetProperty("total_tokens", out var total)
                         && total.TryGetInt64(out var count) && count > 0
                ? count
                : null;

            long? window = info.TryGetProperty("model_context_window", out var size)
                           && size.TryGetInt64(out var limit) && limit > 0
                ? limit
                : null;

            return used is null && window is null ? null : new RolloutTokens(used, window);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
