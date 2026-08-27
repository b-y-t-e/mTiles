using System.Text.Json;
using System.Text.RegularExpressions;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Turns what an AI tool wrote into the shapes the workflow asks questions of.
/// <para>Pure, and separate from both the prompt that asked for the shape and the loop that acts on it,
/// because the interesting half is what happens when the shape is not there. A JSON block is a request,
/// not a protocol: a tool may ignore it, wrap it in commentary, emit it twice, or fence it with four
/// backticks because the block contains three. <b>None of those may cost the user a goal</b>, so every
/// path here ends in a usable answer — at worst the prose itself, judged by the rule this tile used
/// before any structure existed.</para>
/// </summary>
internal static partial class GoalResponseParser
{
    /// <summary>
    /// The <b>last</b> fenced block, not the first.
    /// <para>A tool asked for JSON at the end of its answer routinely writes an example, a diff or a
    /// snippet of the code it is discussing first. Taking the first block read one of those as the
    /// verdict.</para>
    /// <para>The closing fence is allowed to be longer than the opening one because that is what
    /// CommonMark says, and because <c>GoalPromptBuilder.Block</c> emits exactly such fences in the
    /// prompt this is answering.</para>
    /// <para>The <c>\r</c> in the trailing class is load-bearing on Windows. <c>$</c> in multiline mode
    /// matches before the <c>\n</c> of a line break but <em>after</em> its <c>\r</c>, so a tool
    /// answering with CRLF and one more word after the block matched nothing at all — and the review
    /// then fell back to the substring rule this whole class exists to replace, silently.</para>
    /// </summary>
    [GeneratedRegex(@"^(?<tick>`{3,})[^\n]*\n(?<body>.*?)^\k<tick>`*[ \t\r]*$",
        RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencedBlock();

    internal static JsonElement? ExtractJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        foreach (Match m in FencedBlock().Matches(text).Reverse())
            if (TryParseObject(m.Groups["body"].Value) is { } fenced)
                return fenced;

        // Unfenced, which is what a tool told to answer "with JSON only" tends to do. The braces are
        // taken at their outermost, so prose either side of the object does not stop it being read.
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        return open >= 0 && close > open ? TryParseObject(text[open..(close + 1)]) : null;
    }

    private static JsonElement? TryParseObject(string candidate)
    {
        var trimmed = candidate.Trim();
        if (!trimmed.StartsWith('{')) return null;

        try
        {
            // Cloned: the JsonDocument is disposed on the way out and its elements die with it.
            using var doc = JsonDocument.Parse(trimmed,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Review ──────────────────────────────────────────

    public static GoalReviewResult ParseReview(string? response)
    {
        var raw = response ?? "";
        var json = ExtractJson(raw);

        if (json is not { } root)
            return Unstructured(raw);

        var findings = new List<GoalFinding>();
        var array = Property(root, "findings") is { ValueKind: JsonValueKind.Array } found ? found : (JsonElement?)null;
        var hasFindings = array is not null;
        if (array is { } list)
            findings.AddRange(list.EnumerateArray().Select(ReadFinding).OfType<GoalFinding>());

        // A block with neither key in it is not a review — it is a snippet of configuration the tool
        // happened to end its answer with. Falling back is right there: the prose above it is the
        // review, and the old rule can still read a verdict out of it.
        //
        // The question is whether the *key* is there, not whether the list has anything in it. Asking
        // about the count read `{"findings": []}` — a clean review, and the shape of every successful
        // one — as no review at all, dropped it into prose parsing, found no "VERDICT: PASS" in a reply
        // that was asked to be JSON, and marked the goal unmet. Every attempt, for ever.
        // An explicit null said nothing, exactly as a missing key does — "goalMet": null is what a
        // model emits when it has not decided, and reading it as "no" cost the note that tells the user
        // the question went unanswered.
        var met = Nulled(Property(root, "goalMet")) ?? Nulled(Property(root, "goal_met"));
        if (met == null && !hasFindings)
            return Unstructured(raw);

        return new GoalReviewResult
        {
            SaidNothingAboutTheGoal = met == null,
            GoalMet = IsTrue(met) ||
                      // Only when the tool said nothing about it: a review that lists findings but
                      // forgets the flag is answered by the same substring rule as one with no JSON at
                      // all, rather than by assuming the goal was missed.
                      (met == null && GoalWorkflowEngine.IsVerdictPass(raw)),
            Findings = findings,
            WasStructured = true,
            RawText = raw,
        };
    }

    private static GoalReviewResult Unstructured(string raw) => new()
    {
        GoalMet = GoalWorkflowEngine.IsVerdictPass(raw),
        WasStructured = false,
        RawText = raw,
    };

    private static GoalFinding? ReadFinding(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        var title = Text(e, "title");
        var detail = Text(e, "detail");

        // A finding with nothing to say is not a finding. Dropped rather than kept as a blank row,
        // because it would still be counted against a completion criterion.
        if (title.Length == 0 && detail.Length == 0) return null;

        return new GoalFinding
        {
            Severity = Severity(Text(e, "severity")),
            Category = Text(e, "category"),
            File = Text(e, "file"),
            Line = Property(e, "line") is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out var line)
                ? line
                : null,
            Title = title.Length > 0 ? title : detail,
            Detail = title.Length > 0 ? detail : "",
        };
    }

    /// <summary>
    /// A severity word from a tool that was shown three of them and may still write a fourth.
    /// <para>An unrecognised word becomes a <see cref="GoalSeverity.Warning"/>, not a suggestion:
    /// guessing downwards lets an unknown label through a "no errors, no warnings" gate, and the whole
    /// point of the gate is that nothing unexamined gets through it.</para>
    /// </summary>
    private static GoalSeverity Severity(string word) => word.Trim().ToLowerInvariant() switch
    {
        // Only words that mean what *this* Blocker means: it works and it must not stand. A tool with
        // its own five-level scale writes "critical" for something very broken, which is an Error here
        // — and Error is the level with a threshold the user can raise. Mapping it to Blocker handed
        // those tools the one severity nobody can tune, for a judgement they never made.
        "blocker" or "blocking" or "unacceptable" => GoalSeverity.Blocker,
        "error" or "errors" or "bug" or "high" or "critical" or "fatal" => GoalSeverity.Error,
        "warning" or "warn" or "warnings" or "major" or "medium" => GoalSeverity.Warning,
        "suggestion" or "suggestions" or "suggest" or "nit" or "nitpick" or "info" or "note"
            or "minor" or "low" => GoalSeverity.Suggestion,
        _ => GoalSeverity.Warning,
    };

    // ── Clarify ─────────────────────────────────────────

    public static GoalClarifyResult ParseClarify(string? response)
    {
        var raw = response ?? "";
        var json = ExtractJson(raw);

        if (json is not { } root)
            return new GoalClarifyResult { NeedsClarification = true, WasStructured = false, RawText = raw };

        var questions = new List<GoalQuestion>();
        if (Property(root, "questions") is { ValueKind: JsonValueKind.Array } array)
            questions.AddRange(array.EnumerateArray().Select(ReadQuestion).OfType<GoalQuestion>());

        // Deliberately not through Nulled, unlike goalMet, because the two keys are read for different
        // purposes. goalMet is read as a *value* that drives a gate, where an explicit null means "did
        // not say" and must not become "no". This one is read only as a *marker of shape* — does this
        // object look like a clarification answer at all — and "needsClarification": null answers that
        // perfectly well: it is a clarify block from a tool that had not decided. Treating it as absent
        // would drop the whole answer into the prose path and print the JSON as the question.
        var needs = Property(root, "needsClarification") ?? Property(root, "needs_clarification");
        if (needs == null && questions.Count == 0)
            return new GoalClarifyResult { NeedsClarification = true, WasStructured = false, RawText = raw };

        return new GoalClarifyResult
        {
            // The questions decide, and the flag does not get a vote. A tool that says it needs nothing
            // and then asks three has asked three; a tool that says it needs clarification and asks
            // nothing has asked nothing, and there is no way to answer that — the tile used to print the
            // raw JSON as the question, file it in the clarification history, hand it to the planner,
            // and then wait for a reply to it. Planning instead gives the user a plan they can reject,
            // which is a better place to argue from than a question that was never asked.
            NeedsClarification = questions.Count > 0,
            Questions = questions,
            // Read on every round, including the one that decides no questions are needed: a tool that
            // can plan the goal as it stands is exactly the one that already knows how to check it.
            // Newlines out here rather than at the far end — this string is bound for a command line,
            // and a shell handed two lines runs the first one and calls that the verification.
            WasStructured = true,
            RawText = raw,
        };
    }

    private static GoalQuestion? ReadQuestion(JsonElement e)
    {
        // A bare string is a question too, and a tool that answers with a list of them has answered
        // the question that was asked.
        if (e.ValueKind == JsonValueKind.String)
        {
            var only = e.GetString() ?? "";
            return only.Trim().Length == 0 ? null : new GoalQuestion { Question = only.Trim() };
        }

        if (e.ValueKind != JsonValueKind.Object) return null;

        var text = Text(e, "question");
        if (text.Length == 0) return null;

        var options = new List<string>();
        if (Property(e, "options") is { ValueKind: JsonValueKind.Array } array)
            options.AddRange(array.EnumerateArray()
                .Where(o => o.ValueKind == JsonValueKind.String)
                .Select(o => o.GetString()!.Trim())
                .Where(o => o.Length > 0));

        return new GoalQuestion { Question = text, Why = Text(e, "why"), Options = options };
    }

    // ── Detected goal ───────────────────────────────────

    /// <summary>
    /// The goal sentence read out of a detection run.
    /// <para>Plain text rather than JSON, alone among these three, because the answer is one sentence
    /// that goes straight into the composer for the user to edit. There is nothing to lose by taking
    /// the whole answer when the tool adds a preamble — the user is about to read it — and a JSON
    /// wrapper around a single string would be one more thing that can fail to parse.</para>
    /// </summary>
    public static string ParseDetectedGoal(string? response)
    {
        var text = (response ?? "").Trim();
        if (text.Length == 0) return "";

        // Some tools wrap even a one-liner in a fence. Unwrap it, so what lands in the composer is the
        // sentence and not the backticks around it.
        if (FencedBlock().Matches(text) is [.., var last] && last.Value.Trim() == text)
            text = last.Groups["body"].Value.Trim();

        if (ExtractJson(text) is { } root && Text(root, "goal") is { Length: > 0 } fromJson)
            text = fromJson;

        return text;
    }

    // ── Reading a JSON object without believing anything about it ──

    /// <summary>
    /// A boolean, however the tool chose to write it.
    /// <para><c>"goalMet": "true"</c> is common enough to be worth a line: a model shown a schema in
    /// prose quotes values as readily as it does keys, and asking only for <see cref="JsonValueKind
    /// .True"/> read that as a failure — an implementation that met its goal spent the whole budget
    /// being told it had not.</para>
    /// </summary>
    private static bool IsTrue(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.String } s => bool.TryParse(s.GetString(), out var b) && b,
        _ => false,
    };

    /// <summary>A JSON null is an absent answer, not a negative one.</summary>
    private static JsonElement? Nulled(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Null } ? null : value;

    /// <summary>
    /// A property, found whatever case the tool spelled it in.
    /// <para><c>TryGetProperty</c> is case-sensitive, and a model shown a schema in prose writes
    /// <c>GoalMet</c> as readily as <c>goalMet</c> — .NET's own serialiser default, and what a tool
    /// modelling the answer on a C# class produces. One capital letter dropped the entire review into
    /// the prose path, where it had no findings and no verdict and blocked the goal for ever.</para>
    /// </summary>
    private static JsonElement? Property(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;

        // The exact match first, which is the overwhelmingly common case and needs no enumeration.
        if (obj.TryGetProperty(name, out var value)) return value;

        foreach (var property in obj.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;

        return null;
    }

    private static string Text(JsonElement obj, string name) =>
        Property(obj, name) is { ValueKind: JsonValueKind.String } s ? (s.GetString() ?? "").Trim() : "";
}
