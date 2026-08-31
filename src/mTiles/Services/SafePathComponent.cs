namespace mTiles.Services;

/// <summary>
/// Turns a stored identifier into something that is safe to put in a path.
/// </summary>
/// <remarks>
/// <para><b>One rule, because two copies of it is a directory traversal waiting for one of them to
/// drift.</b> It was written twice — once for a sign-in's directory, once for the generated opencode
/// config — with the same character set, the same fallback, and a comment in each pointing at the
/// other, which is as clear a sign as there is that it wanted to be one function. Only one of the two
/// had a test.</para>
/// <para>The values it is given are generated ids, so in practice nothing is ever replaced. It exists
/// for the case that is not practice: <c>settings.json</c> is hand-editable and these values become
/// paths, so a separator in one is a file written somewhere nobody intended.</para>
/// <para><b>Allow-list, not a deny-list.</b> Letters, digits, <c>-</c> and <c>_</c> pass; everything
/// else becomes <c>-</c>, which covers separators, drive letters, <c>..</c> and whatever a future
/// platform decides is special, without anybody having to think of it first.</para>
/// </remarks>
public static class SafePathComponent
{
    /// <summary>The value as a single path component. Never empty, never a separator.</summary>
    public static string Of(string component)
    {
        var safe = new string([.. component.Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-')]);

        // An id made only of punctuation would otherwise become a run of dashes, and an empty one a
        // path that silently addresses the parent directory.
        if (safe.Trim('-').Length == 0) return "unnamed";

        // Windows' reserved device names pass every character test and are still not file names: CON
        // and NUL open a device, and they are reserved with any extension and in any case. The one
        // hole in the allow-list's own argument, closed by suffixing rather than replacing so that two
        // ids cannot collide on the same fallback.
        return Reserved.Contains(safe, StringComparer.OrdinalIgnoreCase) ? safe + "-" : safe;
    }

    /// <summary>The device names Windows refuses, whatever extension they are given.</summary>
    private static readonly string[] Reserved =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
}
