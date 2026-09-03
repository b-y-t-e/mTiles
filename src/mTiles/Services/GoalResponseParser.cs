using System.Globalization;
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

    /// <summary>
    /// The object this answer was asked for, or null when there is none to read.
    /// </summary>
    /// <remarks>
    /// <para><b>Marker keys are the strongest signal and are read first.</b> A tool answers with bare
    /// JSON — no fence — and routinely wraps it in prose that carries braces of its own: a code
    /// suggestion, an example, a sentence naming a literal. Measured against z-ai/glm-5.3-flash
    /// through OpenRouter, 2026-09-01. The old rule took the text from the first <c>{</c> to the last
    /// <c>}</c>, so one brace either side of the review turned the whole answer into one string that
    /// parses as nothing, and a structured review was dropped into the transcript as raw text with its
    /// verdict invented by the fallback. The scan below walks the whole answer and hands back the
    /// <b>last</b> balanced object carrying any of the keys the caller reads — last, because that is
    /// the verdict rule the fenced reader already follows.</para>
    /// <para><b>A candidate that fails to parse is offered to <see cref="JsonRepair"/> once</b>, and
    /// only then given up on. What that catches is the one defect every reader here used to die on —
    /// an unescaped quote or a raw newline inside a string value, which is what a block composed as
    /// text rather than serialised produces — and it catches it for all four callers rather than for
    /// the review alone, which is the one phase that had an AI salvage round behind it. It is asked
    /// only after a refusal and its answer is used only if it parses, so it can turn a failure into an
    /// answer and never the other way about.</para>
    /// <para>What is still not rescued: an answer cut off part way through. The brackets that would
    /// make the fragment legal are content nobody wrote, and inventing them would turn a visible
    /// failure into a review with findings quietly missing from it — so those still end in a usable
    /// answer the other way, through the prose fallback, which is the contract this class was written
    /// for.</para>
    /// <para>The fenced walk and the outermost span keep their places after it, and every caller that
    /// passes no markers behaves exactly as it did.</para>
    /// </remarks>
    internal static JsonElement? ExtractJson(string? text, params string[] markerKeys)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (markerKeys.Length > 0)
        {
            var candidates = BalancedObjects(text);
            for (var i = candidates.Count - 1; i >= 0; i--)
                if (TryParseObject(candidates[i]) is { } parsed && CarriesAnyKey(parsed, markerKeys))
                    return parsed;
        }

        foreach (Match m in FencedBlock().Matches(text).Reverse())
            if (TryParseObject(m.Groups["body"].Value) is { } fenced)
                return fenced;

        // Unfenced, which is what a tool told to answer "with JSON only" tends to do. The braces are
        // taken at their outermost, so prose either side of the object does not stop it being read.
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        return open >= 0 && close > open ? TryParseObject(text[open..(close + 1)]) : null;
    }

    private static bool CarriesAnyKey(JsonElement obj, string[] markerKeys)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;

        foreach (var property in obj.EnumerateObject())
            foreach (var marker in markerKeys)
                if (string.Equals(property.Name, marker, StringComparison.OrdinalIgnoreCase))
                    return true;

        return false;
    }

    /// <summary>
    /// Whether a text that failed to parse still <em>looks</em> like the JSON this tile asked for.
    /// </summary>
    /// <remarks>
    /// The salvage round's trigger, and deliberately shallow: it asks whether the words of the requested
    /// shape are there, not whether the text can be read — that question failed already. A prose answer
    /// that never mentioned the keys does not earn a second AI call; a review the tool wrote as JSON but
    /// broke with one unescaped quote does. Measured live, 2026-09-01: GLM answering a review prompt
    /// put a C# line with an unescaped quote inside a string value, and every reading of the block —
    /// fenced, balanced, span — died on it.
    /// <para><b>The keys are matched quoted.</b> JSON keys are never bare, and a bare word matched too
    /// much prose the wrong way round: a reviewer writing a "Findings:" heading is writing a section
    /// header, and the one thing a salvage call would get back from it is more prose. The quote is what
    /// says this text was meant to be JSON — a broken block keeps its quoted keys however mangled its
    /// values are.</para>
    /// <para><b>The clarification's own keys are here too</b>, and their absence was a hole rather
    /// than a decision: a clarify block broken the same way had no second line at all, so it went
    /// straight into the transcript as raw braces for the user to read and into the clarification
    /// history for the planner to be handed. Measured live, 2026-09-03: a round quoting the document
    /// it was reading — <c>„z pytaniem do użytkownika"</c>, whose closing mark is an ordinary quote —
    /// died exactly as the review had.</para>
    /// </remarks>
    internal static bool LooksLikeJson(string? text) =>
        Marker(text, "goalMet") || Marker(text, "goal_met") || Marker(text, "findings")
        || Marker(text, "needsClarification") || Marker(text, "needs_clarification")
        || Marker(text, "questions");

    private static bool Marker(string? text, string key) =>
        (text ?? "").Contains($"\"{key}\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The outermost balanced <c>{…}</c> spans of the text, reading strings and escapes as it goes.
    /// </summary>
    /// <remarks>A brace inside a quoted finding must not unbalance the count, which is why the walk
    /// tracks <c>"</c> and <c>\</c>. A candidate that never closes runs to the end of the text and dies
    /// in <see cref="TryParseObject"/> — the answer an unterminated fragment deserves. Only spans that
    /// open at depth zero are kept: a findings entry's own braces are inside the object being read,
    /// not candidates beside it.</remarks>
    private static List<string> BalancedObjects(string text)
    {
        var spans = new List<string>();
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    spans.Add(text[start..(i + 1)]);
                    start = -1;
                }
            }
        }

        return spans;
    }

    private static JsonElement? TryParseObject(string candidate)
    {
        var trimmed = candidate.Trim();
        if (!trimmed.StartsWith('{')) return null;

        return Parsed(trimmed) ?? (JsonRepair.Repaired(trimmed) is { } repaired ? Parsed(repaired) : null);
    }

    /// <summary>One parse, or null when the text is not a JSON object this side can read.</summary>
    private static JsonElement? Parsed(string text)
    {
        try
        {
            // Cloned: the JsonDocument is disposed on the way out and its elements die with it.
            using var doc = JsonDocument.Parse(text,
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
        var json = ExtractJson(raw, "goalMet", "goal_met", "findings");

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
        var json = ExtractJson(raw, "needsClarification", "needs_clarification", "questions");

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

    // ── Commit plan ─────────────────────────────────────

    /// <summary>
    /// The commits a run proposed for its own work, or an empty list when nothing usable came back.
    /// </summary>
    /// <remarks>
    /// <para>Empty is a real answer and the caller acts on it: there is no prose fallback here, unlike
    /// the review's <c>VERDICT: PASS</c>. A review that cannot be parsed still has to be given a
    /// verdict, because the run cannot continue without one; a commit plan that cannot be parsed can
    /// simply not be made, and the work stays in the working tree exactly as the user left it. Guessing
    /// a grouping out of prose would be inventing commit messages for somebody's repository.</para>
    /// <para>An entry with no files is dropped rather than repaired. It is a commit that would touch
    /// nothing, and <c>git commit</c> refuses it anyway — dropping it here means the user is told about
    /// files nothing claimed, instead of about a git error.</para>
    /// </remarks>
    public static List<GoalCommit> ParseCommitPlan(string? response)
    {
        var commits = new List<GoalCommit>();
        if (ExtractJson(response, "commits") is not { } root) return commits;

        if (Property(root, "commits") is not { ValueKind: JsonValueKind.Array } array) return commits;

        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var files = new List<string>();
            if (Property(entry, "files") is { ValueKind: JsonValueKind.Array } listed)
                files.AddRange(listed.EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String)
                    .Select(f => (f.GetString() ?? "").Trim())
                    .Where(f => f.Length > 0));

            if (files.Count == 0) continue;

            var type = Text(entry, "type");
            commits.Add(new GoalCommit
            {
                // Lower-cased because the convention is, and a `Feat:` prefix beside forty `feat:` ones
                // is the sort of thing a person has to go back and fix by hand. Not otherwise checked —
                // see GoalCommit.Type for why the set is left open.
                Type = type.Length == 0 ? "chore" : type.ToLowerInvariant(),
                Subject = Text(entry, "subject"),
                Files = files,
            });
        }

        return commits;
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

        if (ExtractJson(text, "goal") is { } root && Text(root, "goal") is { Length: > 0 } fromJson)
            text = fromJson;

        // Also on the path where no JSON was found at all: a tool that answers with a bare sentence can
        // have escaped it just as readily as one that answers with an object.
        return Readable(text);
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
        Property(obj, name) is { ValueKind: JsonValueKind.String } s
            ? Readable((s.GetString() ?? "").Trim())
            : "";

    /// <summary>
    /// Text a person can read, from a tool that escaped it one time too many.
    /// </summary>
    /// <remarks>
    /// <para><b>What goes wrong.</b> A model asked for JSON often escapes non-ASCII itself, writing
    /// <c>dzia\u0142</c> inside the string. That is legal and harmless — the parser decodes it — until
    /// the escaping happens twice, which is what a model does when it composes the JSON as text and
    /// then quotes it: the wire carries <c>dzia\\u0142</c>, one decode strips one backslash, and the
    /// goal that reaches the transcript reads
    /// <c>Esencje dzia\u0142\u00f3w generowane przez ...</c>. Nothing downstream can recover from it,
    /// because by then it is exactly what the tool said.</para>
    /// <para><b>The rule is deliberately narrow.</b> Only a string that is <em>entirely ASCII</em> and
    /// contains at least one well-formed <c>\uXXXX</c> is decoded. A finding about escaping — a code
    /// review that quotes <c>"\u0142"</c> out of somebody's source — is written in a sentence that has
    /// its own accented letters, or none at all, and either way is left alone. The alternative rules
    /// are worse in both directions: decoding always rewrites a review of i18n code, and decoding never
    /// leaves every Polish goal unreadable.</para>
    /// <para>Verified against the goal files on this machine before it was written: every stored goal
    /// decodes cleanly, so the escapes are not the state file's doing — they are what arrived.</para>
    /// </remarks>
    internal static string Readable(string text)
    {
        if (text.Length == 0 || text.Any(c => c > 127)) return text;

        var match = EscapedChar();
        if (!match.IsMatch(text)) return text;

        return match.Replace(text,
            m => ((char)int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber)).ToString());
    }

    /// <summary>A JSON unicode escape that survived being parsed, which means it was escaped twice.
    /// </summary>
    [GeneratedRegex(@"\\u(?<hex>[0-9a-fA-F]{4})")]
    private static partial Regex EscapedChar();
}
