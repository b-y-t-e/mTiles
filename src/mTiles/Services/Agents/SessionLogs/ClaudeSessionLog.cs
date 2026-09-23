using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// Claude Code's own transcripts: <c>&lt;config&gt;/projects/&lt;slug&gt;/&lt;sessionId&gt;.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>Measured 2026-09-18 against 2.1.274. The session id is the file's own name and is repeated on
/// every line, the working directory is recorded on the message lines, and each assistant message
/// carries the Anthropic <c>usage</c> object.</para>
/// <para><b>It names no context window and no price.</b> The window is filled in by the caller from
/// <c>ModelContextWindow</c> — the same figure arrived at from the provider's side, which is also the
/// figure this CLI is told to assume through <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c>, so the gauge and the
/// agent are working from one number rather than two. The price is simply absent; a subscription has no
/// per-turn price to record and a third-party provider's is the provider's business.</para>
/// <para><b>The cache counts towards the context and the reasoning does not.</b> What occupies the
/// window on the next turn is what will be sent again: the input, everything read out of or written into
/// the prompt cache, and the output that is now part of the conversation. Measured on a live transcript,
/// a turn reporting 6 669 input and 116 608 cache-read is a conversation of some 124 000 tokens, and
/// counting the input alone would have drawn it as almost empty.</para>
/// </remarks>
public sealed class ClaudeSessionLog : JsonlSessionLog
{
    private readonly Func<AiSignIn?, string> _configDirectory;

    /// <param name="configDirectory">Where this sign-in's <c>CLAUDE_CONFIG_DIR</c> points, or the
    /// default account's <c>~/.claude</c>. Handed in rather than worked out here, because the rule for
    /// it is <c>ClaudeAgent</c>'s and stating it twice is how the two come to disagree.</param>
    public ClaudeSessionLog(Func<AiSignIn?, string> configDirectory) => _configDirectory = configDirectory;

    /// <inheritdoc />
    protected override string SessionsRoot(AiSignIn? signIn) =>
        Path.Combine(_configDirectory(signIn), "projects");

    /// <inheritdoc />
    protected override string DirectoryNameFor(string workspaceDir) => Slug(workspaceDir);

    /// <inheritdoc />
    /// <remarks>The whole name less the extension: Claude Code names the file after the session, so
    /// there is nothing to parse out of it.</remarks>
    protected override string? SessionIdIn(string fileName) =>
        fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : null;

    /// <inheritdoc />
    protected override LineReading? ReadLine(JsonElement line)
    {
        if (!line.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return null;

        var used = Tokens(usage, "input_tokens")
                   + Tokens(usage, "cache_read_input_tokens")
                   + Tokens(usage, "cache_creation_input_tokens")
                   + Tokens(usage, "output_tokens");

        // A usage object of all zeroes is a turn that failed before it reached the model — an auth
        // error, a refused model — and reporting it as a context of nothing would empty the gauge
        // behind a conversation that is still there.
        return used <= 0
            ? null
            // The model the turn actually ran on, which the instance very often does not name: a tile on
            // a subscription is configured with no model at all and the CLI picks its own.
            : new LineReading(used, null,
                message.TryGetProperty("model", out var model) ? model.GetString() : null);
    }

    /// <summary>How far into a transcript its entry point is looked for.</summary>
    /// <remarks>Measured 2026-09-18: it is on the third to sixth line, after the bookkeeping lines a
    /// session opens with. Bounded, because a file without one is otherwise read to its end.</remarks>
    private const int EntrypointSearchLines = 20;

    /// <inheritdoc />
    public override bool TellsHeadlessRunsApart => true;

    /// <inheritdoc />
    /// <remarks>Measured 2026-09-18: every message line carries <c>entrypoint</c> — <c>cli</c> for the
    /// interactive interface, <c>sdk-cli</c> for <c>claude -p</c>, which is what a Goal tile's run and an
    /// Agent tile's <c>stream-json</c> session both are. Any <c>sdk-</c> spelling is headless; a file
    /// that has not said yet is taken as interactive, since a conversation just opened in the TUI is
    /// exactly the one a tile must not miss.</remarks>
    protected override bool IsInteractive(AiSignIn? signIn, string workspaceDir, SessionEntry entry)
    {
        if (FileOf(entry) is not { } file) return false;

        return ReadFirstLines(file.FullName, EntrypointSearchLines)
            .Select(EntrypointOf)
            .FirstOrDefault(entrypoint => entrypoint is not null)
            is not { } found || !found.StartsWith("sdk-", StringComparison.Ordinal);
    }

    private static string? EntrypointOf(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("entrypoint", out var entrypoint)
                   && entrypoint.ValueKind == JsonValueKind.String
                ? entrypoint.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long Tokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var tokens) ? tokens : 0;
}
