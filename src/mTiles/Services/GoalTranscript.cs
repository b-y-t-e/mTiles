using System.Text;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// How a structured answer is written into a transcript that is a terminal, not a chat window.
/// <para>Pure, and separate from both the parser and the view model, because it is the one part of this
/// that is a judgement about presentation and has to be readable as one. The rule it follows: a
/// finding is a line you can scan down a column of, and its detail is indented under it, so the shape
/// of a review is visible before a word of it is read.</para>
/// </summary>
internal static class GoalTranscript
{
    /// <summary>
    /// A review, as it appears in the transcript.
    /// <para>The tool's own prose is not reprinted under a list of findings. It said the same things at
    /// greater length, and printing both makes the transcript a place where every review is read
    /// twice.</para>
    /// <para><b>Unless there are no findings</b>, in which case the prose is the only account of why —
    /// and dropping it left "Goal not met · nothing found" standing alone as the entire explanation of
    /// a failed attempt. The argument against duplication only holds where there is something to
    /// duplicate.</para>
    /// </summary>
    /// <param name="goalMetMatters">Whether the run is actually gated on <c>goalMet</c>. The note about
    /// a review that never mentioned it is advice about a criterion, so it must not be given where that
    /// criterion is switched off — it told the user their goal had failed a check nothing was
    /// making.</param>
    public static string Review(GoalReviewResult review, VerifyOutcome? verify = null,
        bool goalMetMatters = true)
    {
        if (!review.WasStructured)
            return review.RawText.Trim();

        var sb = new StringBuilder();
        sb.Append(review.GoalMet ? "Goal met" : "Goal not met");

        var counts = new[]
            {
                GoalSeverity.Blocker, GoalSeverity.Error, GoalSeverity.Warning, GoalSeverity.Suggestion,
            }
            .Select(s => (Severity: s, Count: review.Count(s)))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {x.Severity.ToString().ToLowerInvariant()}{(x.Count == 1 ? "" : "s")}")
            .ToList();

        sb.Append(counts.Count > 0 ? $" · {string.Join(" · ", counts)}" : " · nothing found");

        if (verify is { Ran: true } v)
            sb.Append(v.Succeeded ? " · verify passed" : $" · verify exited {v.ExitCode}");

        // Not when the goal is being counted as met anyway. A structured review that omits goalMet
        // still falls back to the prose verdict, so it can perfectly well end up met — and the note
        // then sat under the words "Goal met" telling the user it counted as not met.
        if (review.SaidNothingAboutTheGoal && goalMetMatters && !review.GoalMet)
            sb.Append('\n').Append('\n')
              .Append("The review did not say whether the goal is met, so it counts as not met. Turn " +
                      "that requirement off under the tune button if this tool never answers it.");

        if (review.Findings.Count == 0 && Prose(review.RawText) is { Length: > 0 } why)
            sb.Append('\n').Append('\n').Append(why);

        foreach (var f in Ordered(review.Findings))
        {
            sb.Append('\n').Append('\n').Append(Label(f.Severity));

            var where = Where(f);
            if (where.Length > 0) sb.Append("  ").Append(where);

            sb.Append('\n').Append("  ").Append(f.Title);
            if (f.Detail.Length > 0)
                sb.Append('\n').Append("  ").Append(f.Detail.ReplaceLineEndings("\n  "));
        }

        return sb.ToString();
    }


    /// <summary>How wide a set of options may be before it stops being one line.</summary>
    /// <remarks>
    /// Not a terminal width — the transcript wraps and nobody here knows how wide it is. It is the
    /// width at which " / " stops reading as a separator: short answers scan across a line, and the
    /// moment the options are clauses the slashes disappear into the prose and the reader has to
    /// reconstruct where each one began.
    /// </remarks>
    private const int InlineOptionsWidth = 60;

    /// <summary>
    /// The answers a question offers, under it.
    /// </summary>
    /// <remarks>
    /// <para>Two shapes, because one does not fit both cases. <c>appsettings.json / launchSettings.json</c>
    /// is a line worth keeping as a line. Three clause-long options joined the same way is a paragraph
    /// with slashes in it, which is what the tool actually produces whenever the decision is about
    /// behaviour rather than a name.</para>
    /// <para>Lettered rather than bulleted, so an answer can name one: "1a" is a reply, "the first one"
    /// is a guess about what the first one was. The letters are not parsed anywhere — they go back to
    /// the tool as part of the conversation, which reads them perfectly well.</para>
    /// <para><c>e.g.</c> survives both shapes and is load-bearing: these are suggestions, not a closed
    /// list, and a lettered list with no label reads as a form to be filled in.</para>
    /// </remarks>
    private static void AppendOptions(StringBuilder sb, IReadOnlyList<string> options)
    {
        if (options.Count == 0) return;

        // A newline inside an option would break either shape — it is a json string, so nothing stops
        // one being there.
        var flat = options.Select(o => o.ReplaceLineEndings(" ").Trim()).ToList();
        var inline = string.Join(" / ", flat);

        if (inline.Length <= InlineOptionsWidth)
        {
            sb.Append('\n').Append("   e.g. ").Append(inline);
            return;
        }

        sb.Append('\n').Append("   e.g.");
        for (var i = 0; i < flat.Count; i++)
            sb.Append('\n').Append("   ").Append(Marker(i)).Append(flat[i]);
    }

    /// <summary>Beyond the alphabet a dash, which is not a case any tool asked to offer "few and
    /// knowable" answers will reach — and is still better than the punctuation past 'z'.</summary>
    private static string Marker(int index) =>
        index < 26 ? $"{(char)('a' + index)}) " : "- ";

    /// <summary>
    /// What goes back to the tool on the next attempt: the defects, without the nits and without the
    /// prose around them.
    /// <para>The whole review used to go back. Suggestions competed with errors for the tool's
    /// attention and for the prompt's own size budget, and a run could spend an attempt renaming a
    /// variable while the null dereference above it stayed exactly where it was.</para>
    /// </summary>
    public static string Feedback(GoalReviewResult review)
    {
        if (!review.WasStructured)
            return review.RawText.Trim();

        var blocking = Ordered(review.Findings.Where(f => f.Severity != GoalSeverity.Suggestion)).ToList();
        if (blocking.Count == 0)
            return review.GoalMet
                ? "The review found nothing blocking."
                : "The review found no specific defects but says the goal is not met yet.";

        var sb = new StringBuilder();
        foreach (var f in blocking)
        {
            var where = Where(f);
            sb.Append(Label(f.Severity))
              .Append(where.Length > 0 ? $" {where}" : "")
              .Append(": ")
              .Append(f.Title);
            if (f.Detail.Length > 0) sb.Append(" — ").Append(f.Detail);
            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The clarifying questions, numbered, so an answer can be filed against the question it answers.
    /// </summary>
    public static string Questions(GoalClarifyResult clarify)
    {
        // Prose, from a tool that ignored the schema — shown as it was written, minus any block it
        // ended with. A stray fenced object is machinery, and printing it as the question put JSON in
        // front of the user and into the prompt that reads this back on the next round.
        //
        // With no prose at all the answer *is* the block, and it is unreadable — malformed JSON, or a
        // question somebody fenced for no reason. Falling back to the raw text there put back exactly
        // the fences this is here to remove, so the fence markers are stripped instead: what is left is
        // at worst ugly, and at best the question, but it is never three backticks in a prompt.
        if (!clarify.WasStructured || clarify.Questions.Count == 0)
            return Prose(clarify.RawText) is { Length: > 0 } prose ? prose : Unfenced(clarify.RawText);

        var sb = new StringBuilder();
        for (var i = 0; i < clarify.Questions.Count; i++)
        {
            var q = clarify.Questions[i];
            if (i > 0) sb.Append('\n').Append('\n');

            // "1." and not "1)", because the skeleton the composer is filled with uses "1." and an
            // answer is matched to its question by eye. Two spellings of the same number is one more
            // thing for the reader to reconcile.
            sb.Append(i + 1).Append(". ").Append(q.Question);
            if (q.Why.Length > 0) sb.Append('\n').Append("   ").Append(q.Why);
            AppendOptions(sb, q.Options);
        }

        return sb.ToString();
    }

    /// <summary>
    /// What the tool wrote around its block when it had no questions to ask — an aside, not a question.
    /// <para>Worth keeping. A clarification round that decides the goal is clear often says something
    /// about <em>why</em>, or what it is assuming, and that is the last chance to disagree before a
    /// plan is written against it. Dropping it left the transcript reading "No questions to answer",
    /// with the reasoning that produced it thrown away.</para>
    /// </summary>
    public static string Aside(GoalClarifyResult clarify) =>
        clarify.WasStructured ? Prose(clarify.RawText) : "";

    /// <summary>
    /// Whether an answer says nothing — bare numbering, or a line or two of it with the numbers
    /// still empty.
    /// <para>It guards the <em>prose</em> path, which is the only one that still answers in the
    /// composer: structured questions have a box each and a command that refuses an empty set. What
    /// arrives here is free text, and it used to cost a clarification round out of three whenever it
    /// was nothing but numbering. The check strips the markers this class writes and asks whether
    /// anything is left, so an answer to one question out of three still counts and only a genuinely
    /// empty one does not.</para>
    /// </summary>
    public static bool IsBlankAnswer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;

        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        // Several lines, every one of them nothing but a number and its separator. The rule below
        // requires the number to match the line's position, which a skeleton with a line deleted out of
        // the middle no longer does — "1.\n3." was read as an answer and spent one of three rounds.
        // Position cannot be relaxed for a single line, because a lone "3." is a perfectly good answer
        // to "how many retries?"; a *list* of bare numbers is not an answer to anything.
        if (lines.Count > 1 && lines.All(IsBareMarker)) return true;

        var position = 0;
        foreach (var line in lines)
        {
            var rest = line;
            position++;

            // The marker is dropped only where it is the marker this class would have written: the
            // number has to match the line's own position. Stripping any leading number made "3." — a
            // perfectly good answer to "how many retries?" — look like the third line of an untouched
            // skeleton, and refused it.
            var digits = 0;
            while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;

            if (digits > 0 && digits < rest.Length && rest[digits] is '.' or ')'
                && int.TryParse(rest[..digits], out var number) && number == position)
            {
                rest = rest[(digits + 1)..].Trim();
            }

            if (rest.Length > 0) return false;
        }

        return true;
    }

    /// <summary>A line that is a number and its separator and nothing else — "2." or "7)".</summary>
    private static bool IsBareMarker(string line)
    {
        var digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits])) digits++;

        return digits > 0
               && digits + 1 == line.Length
               && line[digits] is '.' or ')';
    }

    /// <summary>The same text with the fence lines taken out — the last resort, for an answer that is
    /// nothing but a block nobody could parse.</summary>
    private static string Unfenced(string raw) =>
        string.Join('\n', raw.Split('\n').Where(line => !IsFence(line))).Trim();

    /// <summary>A line that is only backticks and an optional info string, which is what opens and
    /// closes a fence.</summary>
    private static bool IsFence(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return false;

        // Trimmed again after the backticks: "``` json" is a fence, and leaving the space in had All
        // reject it, so the line stayed in text that is shown as prose.
        var rest = trimmed.TrimStart('`').Trim();

        // Letters and digits are not the whole of an info string: c#, c++, objective-c and
        // typescript.jsx are all things a tool writes there, and every one of them made the line stop
        // counting as a fence — so the backticks were printed as prose.
        return rest.All(c => char.IsLetterOrDigit(c) || c is '#' or '+' or '-' or '.' or '_');
    }

    /// <summary>
    /// What the tool wrote outside its JSON block: everything before the first fence, or — when there
    /// is nothing there — everything after the last one.
    /// <para>The prompt asks for the block at the end, so text before it is the ordinary case. A tool
    /// that puts its explanation after the block instead is unusual rather than exotic, and reading
    /// there too costs two lines. It matters in exactly the case this is called for: a review with no
    /// findings, where the prose is the only account of why the goal was not met.</para>
    /// </summary>
    private static string Prose(string raw)
    {
        var fence = raw.IndexOf("```", StringComparison.Ordinal);

        // No fence does not mean no machinery. GoalResponseParser reads an unfenced object too — by its
        // outermost braces, because a tool told to "answer with JSON only" tends to send exactly that —
        // so a reply of prose followed by a bare {…} parsed perfectly well and then had the whole thing,
        // JSON included, printed as the tool's own words. Aside and the Questions fallback both go
        // through here, so it appeared in the transcript and in the next round's prompt.
        if (fence < 0) return Outside(raw, '{', '}');

        var before = raw[..fence].Trim();
        if (before.Length > 0) return before;

        // Trimmed of backticks as well as whitespace: a fence may be longer than three, and
        // LastIndexOf finds the *last* three of them, so a ```` fence left one behind at the front of
        // the prose it was supposed to have removed.
        var last = raw.LastIndexOf("```", StringComparison.Ordinal);
        return raw[(last + 3)..].TrimStart('`').Trim();
    }

    /// <summary>What is written either side of the outermost <paramref name="open"/>…<paramref
    /// name="close"/> span: before it where there is anything, otherwise after it.</summary>
    private static string Outside(string raw, char open, char close)
    {
        var start = raw.IndexOf(open);
        var end = raw.LastIndexOf(close);
        if (start < 0 || end <= start) return raw.Trim();

        var before = raw[..start].Trim();
        return before.Length > 0 ? before : raw[(end + 1)..].Trim();
    }

    /// <summary>Blockers first, then errors, warnings and suggestions — declaration order, which is
    /// the order they have to be dealt with in and the order that puts the thing worth reading at the
    /// top of a long review.</summary>
    private static IEnumerable<GoalFinding> Ordered(IEnumerable<GoalFinding> findings) =>
        findings.OrderBy(f => (int)f.Severity);

    /// <summary>Padded to one width, so the severities line up as a column that can be read down.</summary>
    private static string Label(GoalSeverity severity) => severity switch
    {
        GoalSeverity.Blocker => "BLOCKER",
        GoalSeverity.Error => "error  ",
        GoalSeverity.Warning => "warning",
        _ => "suggest",
    };

    private static string Where(GoalFinding f)
    {
        var parts = new List<string>(2);
        if (f.File.Length > 0) parts.Add(f.Line is { } line ? $"{f.File}:{line}" : f.File);
        if (f.Category.Length > 0) parts.Add($"[{f.Category}]");
        return string.Join(" ", parts);
    }
}
