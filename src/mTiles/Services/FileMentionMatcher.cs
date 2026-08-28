namespace mTiles.Services;

/// <summary>
/// Which files a half-typed mention offers, and in what order.
/// </summary>
/// <remarks>
/// <para>The fzf-style scorer the popular tools of this kind use for the same job. It replaced a
/// three-tier substring rank whose top rule was that a hit in the file's own name beats a hit in a
/// directory above it — a rule that is wrong for any tree organised by folder rather than by file
/// name. Measured against a 3443-file TypeScript repository where 121 files are
/// called <c>index.ts</c> and 45 <c>prompt.ts</c>: <c>@bash</c> put <c>tools/BashTool/prompt.ts</c> at
/// position 32 of 66 and filled all twelve rows with files merely spelling "bash" in their name, so
/// nothing in <c>BashTool/</c> was reachable at all. Here it is tenth, and the rows above it are the
/// rest of that folder.</para>
/// <para>Pure, so the order can be argued about in a table test rather than by typing into a tile: what
/// a list like this is judged on is entirely its first three rows.</para>
/// </remarks>
public static class FileMentionMatcher
{
    /// <summary>
    /// How many rows the popup offers.
    /// <para>The limit these popups usually carry. Enough that a name typed to three letters is usually
    /// on screen, and few enough that the list stays a glance rather than something to scroll — a popup
    /// taller than the composer it hangs over covers the sentence being written.</para>
    /// </summary>
    public const int DefaultLimit = 15;

    // The scoring constants, on the scale an fzf-style scorer uses. They are only meaningful against
    // each other, so they move together or not at all.

    /// <summary>What each matched character is worth before any bonus.</summary>
    private const int ScoreMatch = 16;

    /// <summary>A match on the first character of a path segment, or after any of <c>- _ . space</c>.
    /// This is the whole of what the scorer knows about structure, and it is why a folder name is as
    /// good a thing to type as a file name.</summary>
    private const int BonusBoundary = 8;

    /// <summary>A match on the capital in <c>bashTool</c> — the other way a reader sees a word start.
    /// </summary>
    private const int BonusCamel = 6;

    /// <summary>Each character matched immediately after the one before it. Runs beat scatterings.
    /// </summary>
    private const int BonusConsecutive = 4;

    /// <summary>The query's first character landing at the very start of the path.</summary>
    private const int BonusFirstChar = 8;

    /// <summary>What opening a gap costs, before its length is counted.</summary>
    private const int PenaltyGapStart = 3;

    /// <summary>What each further character of a gap costs.</summary>
    private const int PenaltyGapExtension = 1;

    /// <summary>The longest query that is scored. Past this the extra characters cannot change an order
    /// that a hundred points of bonus has already settled, and the cost is per candidate.</summary>
    private const int MaxQueryLength = 64;

    /// <summary>
    /// The best <paramref name="limit"/> paths for what has been typed so far, best first.
    /// </summary>
    /// <remarks>
    /// <para>An empty query — a bare <c>@</c>, before anything has been typed — offers the tree's
    /// <see cref="TopLevel">top level</see> rather than the front of the corpus. Handing over the first
    /// fifteen paths as they come fills the list with whatever sorts earliest, which here is folders
    /// four levels down: <c>src/mTiles/Services/Database/</c> before <c>tests/</c>. The question a bare
    /// <c>@</c> asks is "what is in here", and the answer to that is the top level.</para>
    /// <para>Ties are broken by the order the paths arrive in, and only by that: the sort is stable and
    /// nothing else is compared. The length bonus inside <see cref="Score"/> has already said what the
    /// old <c>ThenBy(path.Length)</c> was for, and said it on the same scale as everything else rather
    /// than as an override of it.</para>
    /// </remarks>
    public static IReadOnlyList<string> Match(
        IReadOnlyList<string> paths, string query, int limit = DefaultLimit) =>
        Match(new FileMentionCorpus(paths), query, limit);

    /// <inheritdoc cref="Match(IReadOnlyList{string}, string, int)"/>
    /// <remarks>
    /// The overload the tile actually calls. The corpus carries the folded paths and the letter maps,
    /// built once when the tree was read rather than once per keystroke — see
    /// <see cref="FileMentionCorpus"/> for what that is worth. The list overload builds one and is for
    /// tests and for anything holding a plain list.
    /// </remarks>
    public static IReadOnlyList<string> Match(
        FileMentionCorpus corpus, string query, int limit = DefaultLimit)
    {
        if (limit <= 0) return [];
        if (query.Length == 0) return TopLevel(corpus.Paths, limit);

        // Smart case, the usual convention: a query typed in lower case ignores case, and one carrying a
        // capital means it. Someone typing `UI` in a tree of `ui/` directories has said something, and
        // it is the only way to say it without a switch nobody would find.
        var caseSensitive = query != query.ToLowerInvariant();
        var needle = caseSensitive ? query : query.ToLowerInvariant();
        if (needle.Length > MaxQueryLength) needle = needle[..MaxQueryLength];

        // Every letter of the query, so a candidate missing one can be dropped without being read.
        // Taken from the folded query either way: a case-sensitive search still cannot match a path
        // whose lowered form lacks the letter outright.
        var wanted = FileMentionCorpus.LettersIn(needle.ToLowerInvariant());

        var scored = new List<(int Score, int Order, string Path)>();

        for (var i = 0; i < corpus.Count; i++)
        {
            if ((corpus.Letters[i] & wanted) != wanted) continue;

            var haystack = caseSensitive ? corpus.Paths[i] : corpus.Lowered[i];
            if (Score(corpus.Paths[i], haystack, needle) is { } score)
                scored.Add((score, i, corpus.Paths[i]));
        }

        return scored
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Order)
            .Take(limit)
            .Select(candidate => candidate.Path)
            .ToList();
    }

    /// <summary>
    /// What a bare <c>@</c> offers: each path's first segment, once.
    /// </summary>
    /// <remarks>
    /// <para>A folder keeps its separator, so <c>src/mTiles/…</c> and the folder row <c>src/</c> both
    /// come out as <c>src/</c> and are offered once. That is what makes the row a folder to everything
    /// downstream — Enter steps into it rather than finishing the mention — and it is the whole point:
    /// this list is a way in, not a set of paths to take. A file in the root has no separator to keep
    /// and is offered as itself.</para>
    /// <para>Shortest first and then alphabetically, which reads as the
    /// tree's shape: the names at the top of a repository are short, and the sort puts them where the
    /// eye starts.</para>
    /// </remarks>
    private static IReadOnlyList<string> TopLevel(IReadOnlyList<string> paths, int limit)
    {
        var segments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            // The separator is kept, and that is the difference between a way in and an answer. A row
            // ending in `/` is a folder everywhere else in this tile: Enter steps into it and leaves
            // the list up. Trimmed to a bare `src` the row stopped being a folder to everything that
            // reads one, so the first Enter after a bare `@` finished the mention as `@src ` instead of
            // opening what is inside — against the tile's own rule that a folder is a step.
            var cut = path.IndexOfAny(['/', '\\']);
            var segment = cut < 0 ? path : path[..(cut + 1)];

            if (segment.Length > 0) segments.Add(segment);
        }

        return segments
            .OrderBy(segment => segment.Length)
            .ThenBy(segment => segment, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// What one path scores for one query, or null when the query is not a subsequence of it.
    /// </summary>
    /// <remarks>
    /// <para><b>A subsequence, not a substring.</b> Every character of the query has to appear in the
    /// path in order, but not adjacently — which is what lets <c>@btp</c> find
    /// <c>tools/BashTool/prompt.ts</c>. Runs of adjacent characters are what the bonuses reward, so an
    /// acronym scores well without a substring ever matching.</para>
    /// <para><b>Greedy and earliest, deliberately.</b> Each character is taken at its first position
    /// after the one before it, with no backtracking to find a better alignment further along. It can
    /// therefore miss the highest-scoring alignment — scorers of this kind accept that too, and it is
    /// keeps the whole thing one pass per candidate over a list that is read on a keystroke.</para>
    /// <para>The path is matched whole: there is no term for the file's own name, and none is wanted.
    /// A folder name is a word the user typed for a reason, and <see cref="BonusBoundary"/> pays for
    /// hitting the start of one exactly as it pays for hitting the start of a file name.</para>
    /// </remarks>
    /// <param name="path">As spelled — the bonuses read camel case out of it, so it must not be folded.</param>
    /// <param name="haystack">The same path, folded when the search ignores case. Prepared once by
    /// <see cref="FileMentionCorpus"/> so that the vectorised <c>IndexOf</c> can be used without
    /// allocating a copy per candidate per keystroke.</param>
    /// <param name="needle">The query, folded to match <paramref name="haystack"/>.</param>
    private static int? Score(string path, string haystack, string needle)
    {
        var at = haystack.IndexOf(needle[0]);
        if (at < 0) return null;

        var score = needle.Length * ScoreMatch + BonusAt(path, at, first: true);
        var previous = at;

        for (var j = 1; j < needle.Length; j++)
        {
            at = haystack.IndexOf(needle[j], previous + 1);
            if (at < 0) return null;

            var gap = at - previous - 1;
            score += gap == 0
                ? BonusConsecutive
                : -(PenaltyGapStart + gap * PenaltyGapExtension);

            score += BonusAt(path, at, first: false);
            previous = at;
        }

        // The shallow file when several match as well as each other, worth up to a full character's
        // match at the top and nothing at all past 128 characters. This is the only thing that puts
        // `README.md` above `deep/nested/dir/README.md`, and it is a bonus rather than a tiebreak so
        // that a genuinely better match down the tree can still outrank a mediocre one at the root.
        return score + Math.Max(0, 32 - (path.Length >> 2));
    }

    /// <summary>What a match at <paramref name="position"/> earns for where it landed.</summary>
    private static int BonusAt(string path, int position, bool first)
    {
        if (position == 0) return first ? BonusFirstChar : 0;


        var before = path[position - 1];
        if (IsBoundary(before)) return BonusBoundary;

        return char.IsLower(before) && char.IsUpper(path[position]) ? BonusCamel : 0;
    }

    /// <summary>The characters a reader sees a new word after. Both separators, because a path typed by
    /// a user on Windows carries backslashes whatever the source produced.</summary>
    private static bool IsBoundary(char c) =>
        c is '/' or '\\' or '-' or '_' or '.' or ' ';
}
