namespace mTiles.Services;

/// <summary>
/// What the working tree looks like, as it goes into a prompt.
/// <para>Pure, and separate from the git calls that gather it, because the assembly is where the bug
/// was: the untracked list was appended and then the whole thing truncated, so the moment the diff grew
/// past the cap the list vanished — and a large diff is exactly the resumed run the list exists for.
/// Each part is now bounded on its own, and the order they are joined in decides which one
/// survives when something cuts the assembled block again — least replaceable first, diff last.</para>
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

    /// <summary>
    /// The same cap where there is no command line to fit — a tool that reads its prompt on stdin, or
    /// any tool at all off Windows, where a command line runs to about two megabytes.
    /// </summary>
    /// <remarks>
    /// <para>Six thousand characters is a <em>transport</em> limit, and it was being charged on a
    /// channel that does not have one. Measured on this repository mid-change: a 140 000-character
    /// <c>git diff HEAD</c> across twenty-one files, of which the tool was shown the first 6 000 — four
    /// per cent, and by path order that four per cent was two markdown files. "Detect goal" then named
    /// a goal drawn from the only evidence it had, correctly and about the wrong change, and there was
    /// nothing on screen to say it had been shown a twenty-fifth of the work.</para>
    /// <para>Not unlimited, because this is now a token bill rather than a crash: the diff is the
    /// largest and most compressible thing in any of these prompts. Forty thousand characters is
    /// roughly ten thousand tokens — the size of a working tree somebody is actually holding in their
    /// head, and still an order of magnitude more than the old cap.</para>
    /// </remarks>
    public const int MaxDiffCharsOffCommandLine = 40_000;

    /// <summary>
    /// How much of each part this run can afford, given what <c>AiProcessRunner.PromptBudget</c> said
    /// about the tool that will receive it. A null budget means the prompt is not going on a command
    /// line.
    /// </summary>
    /// <remarks>
    /// <para>Both caps together, and not because it is tidier. The file summary was added without
    /// taking its 3 000 characters from anywhere, which on a command line is not a free addition: the
    /// worktree block went from at most 7 000 characters to at most 10 000 against the <b>8 191</b> a
    /// <c>.cmd</c> shim allows, so <c>GoalPromptBuilder.Fit</c> would have started cutting the diff
    /// harder than before the summary existed — silently, and for three of the four supported tools.
    /// A block that grows has to say where the room came from.</para>
    /// <para>So on that path the summary is 800 characters and the diff drops to 5 200, keeping the
    /// same 7 000 total. That is the trade the summary is worth: about ten files named, bought with
    /// roughly thirteen per cent of a diff that was already a fragment. Off the command line nothing
    /// competes and both are generous.</para>
    /// </remarks>
    public static WorktreeCaps CapsFor(int? promptBudget) =>
        promptBudget is null ? OffCommandLine : OnCommandLine;

    /// <summary>The tighter pair, for a prompt that has to fit a Windows command line. The answer a
    /// caller gets when it does not know which tool will receive the prompt: costing some context in a
    /// case that may not arise is cheaper than overflowing one that does.</summary>
    public static WorktreeCaps OnCommandLine { get; } =
        new(MaxDiffCharsOnCommandLine, MaxSummaryCharsOnCommandLine);

    /// <summary>The generous pair, for a prompt handed over on stdin or on a system whose command line
    /// runs to megabytes.</summary>
    public static WorktreeCaps OffCommandLine { get; } =
        new(MaxDiffCharsOffCommandLine, MaxSummaryChars);

    /// <summary>What the diff and the file summary may take, for one run.</summary>
    public readonly record struct WorktreeCaps(int Diff, int Summary);

    /// <summary>The diff's share once the summary has taken its own on the command-line path. Lower
    /// than <see cref="MaxDiffChars"/> by exactly what the summary is given, so the block as a whole is
    /// the size it was before the summary existed.</summary>
    public const int MaxDiffCharsOnCommandLine = 5_200;

    /// <summary>The names cost a line each and share the same budget as everything else in the
    /// prompt.</summary>
    public const int MaxUntrackedChars = 1_000;

    /// <summary>
    /// What <c>git diff --stat</c> may take. One line per file plus a total, so it is bounded by the
    /// number of files rather than by the size of the change — which is the whole reason it is here.
    /// <para>Three thousand characters is about forty files at the widths this is asked for, which is a
    /// large change rather than an unusual one. Past that it is cut like everything else, and the note
    /// saying so is itself the information: a change touching more files than this is one nobody should
    /// be told the full extent of in a sentence.</para>
    /// </summary>
    public const int MaxSummaryChars = 3_000;

    /// <summary>Its share where the prompt goes on a command line: about ten files named. Enough to say
    /// that the change is wider than the fragment below it, which is the whole job.</summary>
    public const int MaxSummaryCharsOnCommandLine = 800;

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
    /// <param name="summary">Output of <c>git diff HEAD --stat</c>: one line per changed file with its
    /// counts. It is what a person reads first, and the one part that says how big the change is
    /// <em>everywhere</em> rather than in the fragment that fitted. Optional, because it is derived
    /// from the same command as the diff and its absence costs nothing but breadth.</param>
    /// <param name="caps">How much of the diff and the summary to keep — see
    /// <see cref="CapsFor"/>. Defaults to the command-line figures, so a caller that does not know the
    /// transport gets the safe ones.</param>
    public static string? Compose(string? diff, string? untracked, string? problem = null,
        string? summary = null, WorktreeCaps? caps = null)
    {
        var limits = caps ?? OnCommandLine;

        // Truncated before they are joined, never after: appending the list and then cutting the whole
        // thing to length threw the list away in precisely the case it was added for.
        var body = Clip(diff, limits.Diff, "diff");
        var names = Clip(untracked, MaxUntrackedChars, "file list");
        var stat = Clip(summary, limits.Summary, "summary");

        // Smallest and least replaceable first, the diff last, because whatever cuts this block again
        // will cut it from the end. Something does: GoalPromptBuilder.Fit shrinks the borrowed blocks
        // to fit a command line, and it sees one assembled string rather than these parts — so with the
        // diff on top, the very first re-cut threw away the file list. That is the same loss this class
        // was written to prevent, arriving one layer up.
        //
        // The order is also the order of value under pressure: the note is one line and says nobody
        // could look, a new file's name is a line and no form of diff will ever show it, and the diff
        // is bulk that degrades gracefully.
        var parts = new List<string>(4);
        if (problem is { Length: > 0 }) parts.Add($"Note: the working tree could not be read in full — {problem}");
        if (names.Length > 0) parts.Add($"Untracked files (contents not shown):\n{names}");
        // Above the diff, for the reason the untracked names are: it is bounded by the file count
        // rather than by the size of the change, so it survives a cut that takes most of the body —
        // and it is the only part that says the change reaches files the body never got to. Without it
        // a tool handed the first fragment of a large diff has no way to know it is a fragment, and
        // "Detect goal" confidently named a goal covering the two files that happened to sort first.
        if (stat.Length > 0) parts.Add($"Changed files:\n{stat}");
        if (body.Length > 0) parts.Add(body);

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
