using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Agents.SessionLogs;

/// <summary>
/// pi's own transcripts:
/// <c>&lt;dir&gt;/sessions/--&lt;slug&gt;--/&lt;stamp&gt;_&lt;sessionId&gt;.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>Measured 2026-09-18 against 0.84.3. The same shape Claude Code uses, with three differences,
/// all of them in the naming and the spelling: the project directory is wrapped in a pair of dashes, the
/// file name carries a timestamp before the id, and the usage object is pi's own
/// (<c>input</c>/<c>output</c>/<c>cacheRead</c>/<c>cacheWrite</c>) with a <c>cost</c> beside it.</para>
/// <para><b>pi is one of the three CLIs that record money</b>, and it records it per turn rather than as
/// a running total, which is why <see cref="JsonlSessionLog"/> adds the figures up over the file rather
/// than taking the last one.</para>
/// </remarks>
public sealed class PiSessionLog : JsonlSessionLog
{
    private readonly Func<AiSignIn?, string> _agentDirectory;

    /// <param name="agentDirectory">Where this sign-in's <c>PI_CODING_AGENT_DIR</c> points, or the
    /// default account's <c>~/.pi/agent</c>.</param>
    public PiSessionLog(Func<AiSignIn?, string> agentDirectory) => _agentDirectory = agentDirectory;

    /// <inheritdoc />
    protected override string SessionsRoot(AiSignIn? signIn) =>
        Path.Combine(_agentDirectory(signIn), "sessions");

    /// <inheritdoc />
    /// <remarks>The dashes round it are pi's, not a separator of ours: the directory for
    /// <c>D:\work\sources\mterminal</c> is literally <c>--D--work-sources-mterminal--</c>.</remarks>
    protected override string DirectoryNameFor(string workspaceDir) => $"--{Slug(workspaceDir)}--";

    /// <inheritdoc />
    /// <remarks>Everything after the first underscore, because the timestamp in front of it contains
    /// dashes of its own and counting them is a rule that breaks the first time pi changes its stamp.
    /// </remarks>
    protected override string? SessionIdIn(string fileName)
    {
        if (!fileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var underscore = name.IndexOf('_');
        return underscore >= 0 && underscore + 1 < name.Length ? name[(underscore + 1)..] : null;
    }

    /// <inheritdoc />
    /// <remarks>pi writes <c>usage.cost.total</c> on every turn.</remarks>
    protected override bool RecordsCost => true;

    /// <inheritdoc />
    protected override LineReading? ReadLine(JsonElement line)
    {
        if (!line.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object)
            return null;

        var used = Tokens(usage, "input") + Tokens(usage, "output")
                   + Tokens(usage, "cacheRead") + Tokens(usage, "cacheWrite");

        decimal? cost = null;
        if (usage.TryGetProperty("cost", out var costs) && costs.ValueKind == JsonValueKind.Object
            && costs.TryGetProperty("total", out var total) && total.TryGetDecimal(out var spent)
            && spent > 0)
            cost = spent;

        // A turn that never reached the model reports zeroes throughout — see ClaudeSessionLog for why
        // that must not be read as a context of nothing. A cost on its own is still worth carrying.
        if (used <= 0 && cost is null) return null;
        return new LineReading(used > 0 ? used : null, cost,
            message.TryGetProperty("model", out var model) ? model.GetString() : null);
    }

    private static long Tokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.TryGetInt64(out var tokens) ? tokens : 0;
}
