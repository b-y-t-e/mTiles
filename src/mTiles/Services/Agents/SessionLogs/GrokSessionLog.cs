using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// Grok's own store: <c>~/.grok/sessions/&lt;url-encoded cwd&gt;/&lt;sessionId&gt;/</c>, a directory per
/// conversation with a ready-made <c>usage.json</c> in it.
/// </summary>
/// <remarks>
/// <para>Measured 2026-09-18 against 1.0.34. The friendliest of the six by a distance: the session id is
/// the directory's own name, and <c>usage.json</c> is an aggregate the CLI keeps up to date —
/// <c>costUsdTicks</c> for the session, and a <c>turns</c> list whose last entry is what the context
/// holds now. Nothing has to be walked and no transcript has to be parsed.</para>
/// <para><b>A tick is 1e-10 of a dollar.</b> Measured against a live session: 352 220 000 ticks for
/// 16 291 input and 472 output tokens on grok-4.6, which is $0.035 and not $3.52 or $0.0000035. It is
/// stated here as a named constant rather than inlined because it is somebody else's unit and the only
/// evidence for it is that one reading.</para>
/// <para><b>The directory name is the path URL-encoded, not slugged</b> —
/// <c>C%3A%5CUsers%5Candrz</c> — so <c>C:</c> and every backslash survive as themselves. That means the
/// encoding has to match Grok's byte for byte, which is why it is pure and pinned by a table test.</para>
/// <para><b>Not yet reached by any tile.</b> <see cref="GrokAgent.SessionLog"/> answers <c>null</c>: a
/// store is read by the id of the conversation a tile is in, and a terminal Grok tile has none until
/// the CLI's terminal resume is measured. The reader is kept, measured and tested, for that day.</para>
/// </remarks>
public sealed class GrokSessionLog : AgentSessionLog
{
    /// <summary>What one unit of <c>costUsdTicks</c> is worth in dollars.</summary>
    private const decimal DollarsPerTick = 0.0000000001m;

    private const string UsageFile = "usage.json";

    private readonly string _home;

    /// <param name="home">The directory <c>.grok</c> lives in; the user's profile unless a test says
    /// otherwise.</param>
    public GrokSessionLog(string? home = null) => _home = home ?? Home;

    /// <inheritdoc />
    public override string? WatchDirectory(AiSignIn? signIn, string workspaceDir) =>
        Path.Combine(_home, ".grok", "sessions", DirectoryNameFor(workspaceDir));

    /// <inheritdoc />
    /// <remarks>A conversation is a directory, so what is enumerated is directories — and the times come
    /// from <c>usage.json</c> where there is one, since the directory's own write time moves for every
    /// lock file Grok drops in it.</remarks>
    protected override IEnumerable<SessionEntry> Enumerate(AiSignIn? signIn, string workspaceDir)
    {
        var root = WatchDirectory(signIn, workspaceDir);
        if (root is not { Length: > 0 } || !Directory.Exists(root)) yield break;

        foreach (var directory in new DirectoryInfo(root).EnumerateDirectories())
        {
            var usage = new FileInfo(Path.Combine(directory.FullName, UsageFile));
            var updated = usage.Exists ? usage.LastWriteTimeUtc : directory.LastWriteTimeUtc;
            yield return new SessionEntry(directory.Name, StartedAt(directory), updated);
        }
    }

    /// <inheritdoc />
    protected override AgentSessionReading? Read(AiSignIn? signIn, string workspaceDir, SessionEntry entry)
    {
        var root = WatchDirectory(signIn, workspaceDir);
        if (root is not { Length: > 0 }) return null;

        var path = Path.Combine(root, entry.Id, UsageFile);
        // A conversation that has been opened and not yet answered has no usage.json at all. That is a
        // session to resume with nothing to draw, not a session that is not there.
        if (!File.Exists(path)) return new AgentSessionReading(entry.Id, entry.UpdatedAt);

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty("session", out var session)
            || session.ValueKind != JsonValueKind.Object)
            return new AgentSessionReading(entry.Id, entry.UpdatedAt);

        var used = LastTurnTokens(document.RootElement);

        decimal? cost = session.TryGetProperty("costUsdTicks", out var ticks)
                        && ticks.TryGetInt64(out var value) && value > 0
            ? value * DollarsPerTick
            : null;

        var model = session.TryGetProperty("primaryModelId", out var id) ? id.GetString() : null;

        return new AgentSessionReading(entry.Id, entry.UpdatedAt, used, CostUsd: cost, Model: model);
    }

    /// <summary>How many tokens the conversation's last turn carried — what the context holds now.</summary>
    /// <remarks><b>Never <c>session.totalTokens</c></b>: that is a running total, the sum of every turn —
    /// measured, 33 526 = 16 719 + 16 807 after two turns of a conversation holding some 16.8k — so it
    /// doubles after the second turn and pins the gauge full soon after, the mistake codex's
    /// <c>total_token_usage</c> invites. Each turn's own <c>totalTokens</c> is its input, the whole
    /// conversation resent, plus its output. Every turn measured so far made one model call; a turn of
    /// several would sum them and read high.</remarks>
    private static long? LastTurnTokens(JsonElement root)
    {
        if (!root.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array
            || turns.GetArrayLength() == 0)
            return null;

        var last = turns[turns.GetArrayLength() - 1];
        return last.ValueKind == JsonValueKind.Object
               && last.TryGetProperty("totalTokens", out var total)
               && total.TryGetInt64(out var tokens) && tokens > 0
            ? tokens
            : null;
    }

    /// <summary>The path as Grok files it: percent-encoded, with the drive's colon and every separator
    /// preserved.</summary>
    /// <remarks>Uppercase hex, and every character outside the unreserved set encoded — measured against
    /// <c>C%3A%5CUsers%5Candrz</c>. <see cref="Uri.EscapeDataString"/> leaves <c>-._~</c> and the
    /// alphanumerics alone, which is exactly what that name shows.</remarks>
    private static string DirectoryNameFor(string workspaceDir) => Uri.EscapeDataString(workspaceDir);
}
