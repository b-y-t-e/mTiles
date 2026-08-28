namespace mTiles.Services;

/// <summary>What a completed mention leaves in the box, and where the caret goes in it.</summary>
public readonly record struct FileMentionCompletion(string Text, int CaretIndex);

/// <summary>
/// The <c>@…</c> being typed at the caret: where its <c>@</c> is, what has been typed after it, and
/// where the word it sits in ends.
/// </summary>
/// <remarks>
/// <para>Pure, and separate from anything that reads a disk or draws a popup, because every rule here
/// is an opinion about text that can be got wrong silently — an <c>@</c> that fires inside an email
/// address, a completion that eats the word after the caret.</para>
/// </remarks>
public readonly record struct FileMentionToken(int Start, string Query, int End)
{
    /// <summary>
    /// The mention the caret is inside, or null when it is not inside one.
    /// </summary>
    /// <remarks>
    /// <para>The <c>@</c> must open a word — start of text, or after whitespace — so
    /// <c>andrzej@example.com</c> is an address and not a request for suggestions. Nothing between it
    /// and the caret may be whitespace, which is also what closes a mention once one has been inserted:
    /// the caret lands after a trailing space and there is no token here again.</para>
    /// <para><b>The query stops at the caret; the token does not.</b> What is offered is what has been
    /// typed so far — the characters after the caret are not a filter, or moving the caret back into a
    /// finished mention would offer only the file already named. What gets <em>replaced</em>, though, is
    /// the whole word: completing <c>@fi|le.cs</c> replaces <c>@file.cs</c> and not just <c>@fi</c>,
    /// which is what makes fixing a typo in the middle of a path a matter of picking the right row.
    /// This is the rule a completion like this follows everywhere — the token extends past the caret
    /// by the same character class — and it replaced the opposite one here, which left <c>le.cs</c> stranded after
    /// the path it had just inserted.</para>
    /// </remarks>
    public static FileMentionToken? At(string? text, int caret)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (caret < 0 || caret > text.Length) return null;

        // **The nearer of the two wins.** A quoted mention can hold whitespace, so the search for one
        // runs to the last `@"` before the caret however far back that is — and a quote somebody typed
        // and thought better of then swallowed everything after it. `Fix @" the thing and @tests/Foo`
        // asked the matcher for `" the thing and @tests/Foo`, found nothing, and no mention typed later
        // in that message could ever open the list again: the only way out was to go back and delete a
        // quote, with nothing on screen to suggest it. Preferring whichever candidate starts nearer the
        // caret makes an abandoned quote stop mattering as soon as a new mention is begun after it.
        //
        // On a tie the quoted one wins, which is the unquoted scan finding the same `@` and reading the
        // opening quote as the first character of the name.
        var quoted = Quoted(text, caret);
        var plain = Plain(text, caret);

        if (quoted is { } q && (plain is not { } p || q.Start >= p.Start)) return q;

        return plain;
    }

    /// <summary>The unquoted mention the caret is inside, or null.</summary>
    private static FileMentionToken? Plain(string text, int caret)
    {
        for (var i = caret - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) return null;
            if (c != '@') continue;

            if (i != 0 && !char.IsWhiteSpace(text[i - 1])) return null;

            var end = caret;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;

            return new FileMentionToken(i, text[(i + 1)..caret], end);
        }

        return null;
    }

    /// <summary>
    /// The mention the caret is inside when it is a quoted one, or null when it is not.
    /// </summary>
    /// <remarks>
    /// <para><b>Asked before the ordinary scan, because that scan cannot answer it.</b> Walking back
    /// from the caret stops at the first whitespace, which inside <c>@"my folder/</c> is the space in
    /// the name — so a folder with a space in it closed the popup the instant it was stepped into, and
    /// no further keystroke could reopen it. Stepping down a tree and narrowing with Tab both became
    /// impossible for exactly the names that need quoting in the first place.</para>
    /// <para>An opening <c>@"</c> with no closing quote between it and the caret is what makes this a
    /// mention still being written. A closed one is finished, and the caret sitting after it is not
    /// inside anything — the same rule the trailing space gives an unquoted mention.</para>
    /// <para>The <c>@</c> still has to open a word, so an address in quotes is not a mention either.
    /// </para>
    /// </remarks>
    private static FileMentionToken? Quoted(string text, int caret)
    {
        var open = text.LastIndexOf("@\"", caret, StringComparison.Ordinal);
        if (open < 0) return null;
        if (open != 0 && !char.IsWhiteSpace(text[open - 1])) return null;

        // The caret can be *inside* the opener — `LastIndexOf` searches backward from the caret and a
        // match beginning at `caret - 1` extends past it — which is what happens the moment somebody
        // puts the caret between the `@` and the quote, by arrow key or by clicking. Slicing then asks
        // for a range that ends before it starts and throws out of a keystroke handler. Not a mention
        // yet: the ordinary scan below reads it as a bare `@` and offers the top level, which is what
        // the caret is actually sitting in front of.
        if (caret < open + 2) return null;

        var query = text[(open + 2)..caret];
        if (query.Contains('"')) return null;

        // To the closing quote where there is one, and **to the caret where there is not**: an unclosed
        // quote says nothing about where the mention ends, so consuming further would let one
        // completion eat the sentence it was written in. The closed case takes the whole thing, which
        // is what lets a typo be fixed in the middle of a finished path.
        //
        // A quote only *closes* this mention if a token boundary follows it. Taking the next quote
        // anywhere later in the message was the same swallowing by another route: in
        // `Fix @"my fold and then "quoted" here`, with the caret after `fold`, the quote before
        // `quoted` was read as this mention's end and `and then "` was spliced away — silently, and
        // beyond Ctrl+Z, because the box's text is set programmatically.
        var end = caret;

        var close = text.IndexOf('"', caret);
        if (close >= 0 && (close + 1 == text.Length || char.IsWhiteSpace(text[close + 1])))
            end = close + 1;

        return new FileMentionToken(open, query, end);
    }

    /// <summary>
    /// The box with this mention replaced by <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// The trailing space is load-bearing rather than cosmetic: it is what puts the caret somewhere
    /// <see cref="At"/> finds no token, so the popup closes instead of reopening against the whole path
    /// it has just inserted.
    /// </remarks>
    public FileMentionCompletion Complete(string text, string path) =>
        Replace(text, Mention(path) + " ");

    /// <summary>
    /// The box with this mention replaced by <paramref name="prefix"/>, and the popup left open.
    /// </summary>
    /// <remarks>
    /// What Tab does when several rows still share a prefix: it types the part they agree on and stops,
    /// so the next keystroke narrows rather than picks. No trailing space, because the mention is not
    /// finished — a space here would close the popup on a path that names no file yet.
    /// </remarks>
    public FileMentionCompletion Extend(string text, string prefix) =>
        Replace(text, OpenMention(prefix));

    private FileMentionCompletion Replace(string text, string mention) =>
        new(string.Concat(text.AsSpan(0, Start), mention, text.AsSpan(Math.Max(End, Start))),
            Start + mention.Length);

    /// <summary>
    /// How a chosen path is spelled in the text.
    /// </summary>
    /// <remarks>
    /// <para>A path with whitespace in it is quoted, and that is the one thing here that is not a
    /// matter of taste: a mention ends at the first space for the tool reading it exactly as it does
    /// for <see cref="At"/>, so <c>@docs/my notes.md</c> arrives as <c>@docs/my</c> and a loose
    /// <c>notes.md</c> — a mention that names no file, which is its only job. Such names exist and
    /// <c>git ls-files</c> offers them, so the popup can hand one over.</para>
    /// <para><b>A quote inside the name is left alone rather than escaped.</b> It cannot happen on
    /// Windows, where the character is illegal in a file name, and the backslash this used to write was
    /// worse than nothing: the parser on the other side reads <c>@"([^"]+)"</c>, so an escaped quote
    /// ends the mention early there just as a bare one does, and the backslash is then delivered as
    /// part of the file name.</para>
    /// </remarks>
    public static string Mention(string path) =>
        path.Any(char.IsWhiteSpace) ? $"@\"{path}\"" : $"@{path}";

    /// <summary>
    /// The same, for a mention that is not finished — so the quote is opened and not closed.
    /// </summary>
    /// <remarks>
    /// <para><b>The closing quote is what a finished mention has</b>, and writing one here put the
    /// caret behind it: <see cref="At"/> then found a quoted token whose query held a <c>"</c> and
    /// refused it, and the unquoted scan behind that stopped at the space in the name. The token
    /// vanished, the popup closed, and stepping into a folder with a space in it — the case the whole
    /// of this quoting exists for — ended the completion instead of continuing it. Folders without a
    /// space were unaffected, which is why it survived a test.</para>
    /// <para>An unclosed quote is also what the text honestly says: the user is inside a name they have
    /// not finished. <see cref="Complete"/> writes the closed form, with the trailing space that ends
    /// the mention for everything downstream.</para>
    /// </remarks>
    private static string OpenMention(string path) =>
        path.Any(char.IsWhiteSpace) ? $"@\"{path}" : $"@{path}";

    /// <summary>
    /// The longest start every one of <paramref name="paths"/> shares, or an empty string when they
    /// share nothing.
    /// </summary>
    /// <remarks>
    /// <para>What Tab types. Compared without case — the paths come from one tree and two that differ
    /// only in case are one file on Windows — but the prefix is cut from the first path, so what lands
    /// in the box is spelled the way the file is.</para>
    /// </remarks>
    public static string CommonPrefix(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return "";
        if (paths.Count == 1) return paths[0];

        var length = paths[0].Length;
        foreach (var path in paths)
        {
            length = Math.Min(length, path.Length);

            var i = 0;
            while (i < length && char.ToLowerInvariant(paths[0][i]) == char.ToLowerInvariant(path[i])) i++;
            length = i;
        }

        return paths[0][..length];
    }
}
