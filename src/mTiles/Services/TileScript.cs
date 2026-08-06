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
    /// Expands <see cref="TileIdToken"/> in a profile script.
    /// </summary>
    /// <param name="tileId">
    /// Must be a GUID, and is checked rather than trusted. It is read from the workspace layout on disk
    /// and substituted into a string that is then handed to <c>shell -c</c>, so anything that reaches
    /// here is executed by a shell: an id of <c>x; rm -rf ~</c> in a hand-edited or restored layout file
    /// is a command, not an identifier. Every id the application creates is
    /// <c>Guid.NewGuid().ToString()</c>, so demanding exactly that costs nothing and closes the path.
    /// <para>Blank is refused for a different reason: expanding the token to nothing turns
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

        if (!script.Contains(TileIdToken))
            return true;

        if (string.IsNullOrWhiteSpace(tileId))
        {
            why = $"The script uses {TileIdToken} but the tile has no id.";
            return false;
        }

        // Exactly the "D" form — `Guid.NewGuid().ToString()` and nothing else. Plain `TryParse` also
        // accepts `{…}`, `(…)`, the unhyphenated form and the `X` hex form, and the first three of those
        // carry braces, parentheses and commas: characters a shell reads as grouping and subshells,
        // which is precisely what this check exists to keep out of a `shell -c` argument.
        if (!Guid.TryParseExact(tileId, "D", out _))
        {
            why = $"A tile id must be a GUID in the plain hyphenated form; '{tileId}' is not, and it "
                + "would be substituted into a shell command.";
            return false;
        }

        resolved = script.Replace(TileIdToken, tileId);
        return true;
    }
}
