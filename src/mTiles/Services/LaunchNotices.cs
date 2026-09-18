namespace mTiles.Services;

/// <summary>
/// The tile's notice bar as a set of independent lines, rather than one string whoever writes last owns.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> Two unrelated facts want that bar: what the tile had to be launched
/// <em>as</em> (<c>AgentSubstitution</c>, written once and never again, because it is a fact about the
/// layout) and that this workspace's skills have moved under a running agent
/// (<see cref="SkillChangePolicy.Notice"/>). Written straight over each other, the second silently took
/// the first away — a tile running a different program in somebody's repository than the one they chose,
/// with the only sentence saying so gone for good, since the substitution is reported at construction and
/// nothing reinstates it.</para>
/// <para>Pure, and by whole lines: a notice is one sentence, so a line is the unit a writer puts up and
/// takes down, and neither writer has to know what the other one said.</para>
/// </remarks>
public static class LaunchNotices
{
    private const char Separator = '\n';

    /// <summary>The bar with <paramref name="notice"/> standing on it, added once.</summary>
    public static string With(string standing, string notice)
    {
        if (notice.Length == 0) return standing;
        if (Lines(standing).Contains(notice, StringComparer.Ordinal)) return standing;

        return standing.Length == 0 ? notice : $"{standing}{Separator}{notice}";
    }

    /// <summary>The bar with <paramref name="notice"/> taken down, leaving whatever else was on it.
    /// </summary>
    /// <remarks>Answers the bar unchanged when that line is not there, which is the user having dismissed
    /// it: a writer withdrawing its own notice must not be able to put back one somebody else put away.
    /// </remarks>
    public static string Without(string standing, string notice) =>
        string.Join(Separator, Lines(standing).Where(line => !string.Equals(line, notice, StringComparison.Ordinal)));

    private static IEnumerable<string> Lines(string standing) =>
        standing.Length == 0 ? [] : standing.Split(Separator);
}
