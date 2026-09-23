namespace mTiles.Services.Shells;

/// <summary>
/// Which arguments no shell here — and no batch file behind one — reads anything into.
/// </summary>
public static class ShellArgument
{
    /// <summary>Whether <paramref name="argument"/> is made only of characters every shell in the
    /// catalog, and <c>cmd.exe</c> re-reading a batch file's <c>%*</c>, leaves alone.</summary>
    /// <remarks>An allow-list rather than a list of what to escape: the next shell added brings its own
    /// metacharacters, and a rule stated the other way round would already be wrong for it. An empty
    /// argument is not quote-free — it only survives a command line inside quotes.</remarks>
    public static bool IsQuoteFree(string argument) =>
        argument.Length > 0 && argument.All(IsQuoteFree);

    /// <summary>Whether <paramref name="argument"/> survives a batch file: PowerShell quotes it for
    /// itself, and <c>cmd.exe</c> re-reading <c>%*</c> finds nothing in it to act on.</summary>
    /// <remarks>Wider than <see cref="IsQuoteFree(string)"/> on purpose — a space, a backslash, a
    /// <c>$</c> or a <c>'</c> is taken care of by PowerShell's own quoting and means nothing to
    /// <c>cmd.exe</c> inside the double quotes PowerShell passes it on in. That is what lets a path
    /// under a profile such as <c>C:\Users\Jan Kowalski</c> reach a <c>.cmd</c> shim at all. What is
    /// refused is what <c>cmd.exe</c> reads whatever the quoting said: a command separator, a
    /// redirection, its escape, a variable, a quote and a group.</remarks>
    public static bool IsBatchSafe(string argument) =>
        argument.Length > 0 && !argument.Any(character =>
            char.IsControl(character) || BatchMetacharacters.Contains(character));

    private const string BatchMetacharacters = "&|<>^%!\"()";

    private static bool IsQuoteFree(char character) =>
        char.IsAsciiLetterOrDigit(character) || "._-/:=@+,".Contains(character);
}
