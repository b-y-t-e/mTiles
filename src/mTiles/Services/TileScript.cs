namespace mTiles.Services;

/// <summary>
/// The one place that knows what a profile script's placeholders mean.
/// <para>A whole type for one substitution because the token is a promise to users who write their
/// profiles by hand, and it was being expanded by two independent implementations — the command chain's
/// and the interactive shell's. Two copies of a literal string is how <c>${tileId}</c> comes to work on
/// one launch path and not the other.</para>
/// </summary>
internal static class TileScript
{
    /// <summary>Resolved to the tile's own <c>TileId</c>, so a tool can key its state to the tile it is
    /// running in and find it again after a restart.</summary>
    public const string TileIdToken = "${tileId}";

    /// <summary>
    /// Resolved to the path of the document that <c>opencode import</c> takes to create this tile's
    /// session — see <see cref="OpenCodeSession"/> for why that indirection exists at all.
    /// <para>A token rather than a path a profile could spell out, because the path is under
    /// <c>%APPDATA%</c> and differs per machine, and the file is written by the application rather than
    /// by the script. Still a <em>pure</em> function of the tile id, which is what lets the launcher ask
    /// whether a profile resolves without writing anything.</para>
    /// </summary>
    public const string OpenCodeSessionFileToken = "${opencodeSessionFile}";

    /// <summary>
    /// Expands every token in a profile script — <see cref="TileIdToken"/> and
    /// <see cref="OpenCodeSessionFileToken"/>. Both are substituted from the tile id, so both carry the
    /// same requirement on it.
    /// </summary>
    /// <param name="tileId">
    /// Must be a GUID, and is checked rather than trusted. It is read from the workspace layout on disk
    /// and substituted into a string that is then handed to <c>shell -c</c>, so anything that reaches
    /// here is executed by a shell: an id of <c>x; rm -rf ~</c> in a hand-edited or restored layout file
    /// is a command, not an identifier — and by way of <see cref="OpenCodeSessionFileToken"/> it also
    /// becomes a file name. Every id the application creates is <c>Guid.NewGuid().ToString()</c>, so
    /// demanding exactly that costs nothing and closes both paths.
    /// <para>Blank is refused for a different reason: expanding a token to nothing turns
    /// <c>claude -r ${tileId}</c> into <c>claude -r</c> — a different command, which may well run.</para>
    /// </param>
    public static string Resolve(string script, string tileId)
    {
        if (TryResolve(script, tileId, out var resolved, out var why))
            return resolved;

        throw new ArgumentException(why, nameof(tileId));
    }

    /// <summary>
    /// <see cref="Resolve"/> without the exception, for callers whose job is to ask rather than to do —
    /// a launcher deciding whether a profile can run at all. One implementation, so the check and the
    /// substitution cannot come to different conclusions.
    /// </summary>
    public static bool TryResolve(string script, string tileId, out string resolved, out string? why)
    {
        resolved = script;
        why = null;

        // Both tokens are substituted from the tile id — one is the id, the other a path built out of
        // it — so either one present is the same requirement, and neither may be expanded without it.
        var usedToken = script.Contains(TileIdToken) ? TileIdToken
            : script.Contains(OpenCodeSessionFileToken) ? OpenCodeSessionFileToken
            : null;
        if (usedToken is null)
            return true;

        if (string.IsNullOrWhiteSpace(tileId))
        {
            why = $"The script uses {usedToken} but the tile has no id.";
            return false;
        }

        if (!IsUsableId(tileId))
        {
            why = $"A tile id must be a GUID in the plain hyphenated form; '{tileId}' is not, and it "
                + "would be substituted into a shell command.";
            return false;
        }

        resolved = script.Replace(TileIdToken, tileId);

        // Only when it is actually there. Building the path validates the id again and asks the OS where
        // the application data lives, and every profile in the app that is not OpenCode's would pay for
        // it on every launch — for a value it then throws away.
        if (resolved.Contains(OpenCodeSessionFileToken))
            resolved = resolved.Replace(OpenCodeSessionFileToken, OpenCodeSession.DocumentPath(tileId));

        return true;
    }

    /// <summary>
    /// Whether a tile id may be put into something that will be executed or opened.
    /// <para>Exactly the "D" form — <c>Guid.NewGuid().ToString()</c> and nothing else. Plain
    /// <c>TryParse</c> also accepts <c>{…}</c>, <c>(…)</c>, the unhyphenated form and the <c>X</c> hex
    /// form, and the first three of those carry braces, parentheses and commas: characters a shell reads
    /// as grouping and subshells, which is precisely what this check exists to keep out of a
    /// <c>shell -c</c> argument.</para>
    /// <para>Public within the assembly because the same value also becomes a file name
    /// (<see cref="OpenCodeSession.DocumentPath"/>), and two copies of "what an id may look like" is how
    /// one of the two ends up accepting <c>..\..\</c>.</para>
    /// </summary>
    internal static bool IsUsableId(string tileId) => Guid.TryParseExact(tileId, "D", out _);
}
