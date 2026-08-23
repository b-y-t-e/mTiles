namespace mTiles.Services;

/// <summary>
/// What the working tree looks like, as it goes into a prompt.
/// <para>Pure, and separate from the git calls that gather it, because the assembly is where the bug
/// was: the untracked list was appended and then the whole thing truncated, so the moment the diff grew
/// past the cap the list vanished — and a large diff is exactly the resumed run the list exists for.
/// Each part is now bounded on its own, and the order says which one gives way first.</para>
/// </summary>
internal static class GoalDiffContext
{
    /// <summary>
    /// How much of a prompt the working tree may take up.
    /// <para><b>This is a transport limit, not a token budget.</b> Every AI tool here is handed its
    /// prompt as a command-line argument (<c>AiProcessRunner</c>, and each <c>IAiToolRunner</c>), and
    /// Windows caps a command line at 32 767 characters — or at <b>8 191</b> when the executable is a
    /// <c>.cmd</c> shim, which is exactly what npm installs and what <c>AiToolDetector</c> looks for
    /// first. Past that, <c>Process.Start</c> throws, the run is judged <c>Failed</c>, the tile pauses
    /// and Resume reproduces the same failure for ever — in the one scenario this whole feature exists
    /// for, a resume after a large implementation.</para>
    /// <para>The earlier 100 000 was chosen against token cost, which is the wrong reason and the wrong
    /// order of magnitude. This number is not a guarantee either: with the other blocks and the quality
    /// rules the prompt can still pass 8 191, and a cap low enough to promise otherwise would be a diff
    /// too small to be worth sending. It bounds the largest variable part, and the guarantee comes from
    /// elsewhere — <c>AiProcessRunner.GuardPromptLength</c> refuses what will not fit with a message
    /// saying why, and a tool that reads its prompt on stdin has no such limit at all.</para>
    /// </summary>
    public const int MaxDiffChars = 6_000;

    /// <summary>The names cost a line each and share the same budget as everything else in the
    /// prompt.</summary>
    public const int MaxUntrackedChars = 1_000;

    /// <summary>
    /// Assembles the diff and the untracked names into one block, or <c>null</c> when there is nothing
    /// to say.
    /// </summary>
    /// <param name="diff">Output of <c>git diff HEAD</c> — staged and unstaged alike, because a tool
    /// that stages its work as it goes leaves a plain <c>git diff</c> empty and a resumed run would be
    /// told the tree was clean.</param>
    /// <param name="untracked">Output of <c>git ls-files --others --exclude-standard</c>: one path per
    /// line. Names only. A new file is most of what an implementation produces and no form of diff
    /// shows one, so this is what stops a resumed run creating them all a second time.</param>
    /// <param name="problem">Why one of the commands could not be run, if either could not. It goes
    /// into the prompt rather than only into the log: silence here is indistinguishable from a clean
    /// tree, and a tool told the tree is clean when nobody knows what is in it will happily write over
    /// work it cannot see. Saying so lets it be careful instead.</param>
    public static string? Compose(string? diff, string? untracked, string? problem = null)
    {
        // Truncated before they are joined, never after: appending the list and then cutting the whole
        // thing to length threw the list away in precisely the case it was added for.
        var body = Clip(diff, MaxDiffChars, "diff");
        var names = Clip(untracked, MaxUntrackedChars, "file list");

        var parts = new List<string>(3);
        if (body.Length > 0) parts.Add(body);
        if (names.Length > 0) parts.Add($"Untracked files (contents not shown):\n{names}");
        if (problem is { Length: > 0 }) parts.Add($"Note: the working tree could not be read in full — {problem}");

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>Cuts to length on a line boundary, so nothing is handed on half a line. A path cut in
    /// two is a filename that does not exist, which is worse than one name fewer.</summary>
    private static string Clip(string? text, int max, string what)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length <= max) return trimmed;

        var cut = trimmed[..max];
        var lastBreak = cut.LastIndexOf('\n');
        if (lastBreak > 0) cut = cut[..lastBreak];

        return $"{cut}\n… {what} truncated at {max} characters.";
    }
}
