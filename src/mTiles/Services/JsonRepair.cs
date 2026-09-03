namespace mTiles.Services;

/// <summary>
/// One repair of a JSON object a language model wrote by hand, tried only after the parser has already
/// refused it.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> Every block this tile asks for is composed as text by a model rather
/// than serialised by a library, and the characteristic way that fails is a double quote inside a
/// string value that was never escaped. Measured live twice: a reviewer answering in Polish put a C#
/// interpolation with its own quotes inside a finding's detail (2026-09-01), and a clarify round quoted
/// the plan it was reading — <c>„z pytaniem do użytkownika"</c>, a Polish quotation whose closing mark
/// is an ordinary <c>"</c> — inside a <c>why</c> value (2026-09-03). Both answers were the shape that
/// was asked for, both died in <c>JsonDocument.Parse</c>, and both were printed into the transcript as
/// raw braces for the user to read.</para>
/// <para><b>Why here and not only in the salvage round.</b> The AI salvage round exists and works, and
/// it costs a call, a wait and money for a defect that is a single missing backslash. This is the free
/// first attempt, it is deterministic, and — being inside
/// <see cref="GoalResponseParser.ExtractJson"/>'s one parse — it covers the review, the clarification,
/// the commit plan and the detected goal at once, rather than the one phase that happened to have a
/// second line of defence.</para>
/// <para><b>It cannot make anything worse.</b> It is asked only for text that has already failed to
/// parse, and its answer is used only if it parses. A repair that guesses wrong produces text that
/// does not parse and the caller carries on exactly as it does today.</para>
/// <para><b>What it deliberately does not do.</b> It does not close an unterminated object: an answer
/// cut off part way through is missing content, and inventing the brackets that would make the
/// fragment legal turns a visible failure into a review with findings silently missing from it. It
/// does not repair anything outside a string value either — a model that invented a different schema
/// wrote valid JSON saying the wrong thing, which is the parser's business and not this one's.</para>
/// </remarks>
internal static class JsonRepair
{
    /// <summary>
    /// The candidate with the escaping a JSON parser requires, or <c>null</c> when there was nothing
    /// to change — which is the answer that says "this text failed for some other reason", and saves
    /// the caller a second parse of a string it has already refused.
    /// </summary>
    public static string? Repaired(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return null;

        var text = candidate;
        var sb = new System.Text.StringBuilder(text.Length + 16);
        var inString = false;
        var changed = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (!inString)
            {
                sb.Append(c);
                if (c == '"') inString = true;
                continue;
            }

            switch (c)
            {
                // An escape and whatever it escapes travel together, so a `\"` already written
                // correctly is never counted as a quote at all. A lone trailing backslash is left as
                // it is: the text ends there, and this method does not finish anybody's sentence.
                case '\\' when i + 1 < text.Length:
                    sb.Append(c).Append(text[i + 1]);
                    i++;
                    break;

                case '"' when ClosesTheString(text, i + 1):
                    sb.Append(c);
                    inString = false;
                    break;

                case '"':
                    sb.Append('\\').Append('"');
                    changed = true;
                    break;

                // A raw line break inside a string is illegal JSON for the same reason and arrives the
                // same way — a model writing a multi-line detail as it would write prose.
                case '\n':
                    sb.Append("\\n");
                    changed = true;
                    break;

                case '\r':
                    sb.Append("\\r");
                    changed = true;
                    break;

                case '\t':
                    sb.Append("\\t");
                    changed = true;
                    break;

                case < ' ':
                    sb.Append("\\u").Append(((int)c).ToString("x4"));
                    changed = true;
                    break;

                default:
                    sb.Append(c);
                    break;
            }
        }

        return changed ? sb.ToString() : null;
    }

    /// <summary>
    /// Whether the quote just read ends its string, judged by what follows it.
    /// </summary>
    /// <remarks>
    /// <para>This is the whole of the guesswork, and the rule is: a string ends where the grammar could
    /// go on. After the closing quote of a value or a key, the next thing that is not whitespace is
    /// <c>:</c>, <c>}</c>, <c>]</c> or <c>,</c> — anything else and the quote was a character somebody
    /// typed.</para>
    /// <para><b>The comma is the case that decides the Polish quotation</b>, and taking it at face
    /// value is what a simpler rule gets wrong. <c>„z pytaniem",</c> is a closing quotation mark
    /// followed by a comma in the middle of a sentence, which reads exactly like the end of a value —
    /// so the comma is accepted only when a value or a key actually follows it. <c>, ale ...</c> is
    /// prose and the quote is escaped; <c>, "options": …</c> is the next pair and the string ends.</para>
    /// <para>The literals are matched in full — <c>true</c>, <c>false</c>, <c>null</c> — rather than by
    /// their first letter, because a sentence carrying on after a comma begins with a letter far more
    /// often than a JSON value does.</para>
    /// </remarks>
    private static bool ClosesTheString(string text, int from)
    {
        var i = SkipSpace(text, from);
        if (i >= text.Length) return true;

        return text[i] switch
        {
            ':' or '}' or ']' => true,
            ',' => StartsAValue(text, SkipSpace(text, i + 1)),
            _ => false,
        };
    }

    private static bool StartsAValue(string text, int i)
    {
        if (i >= text.Length) return true;

        var rest = text.AsSpan(i);
        return rest[0] is '"' or '{' or '[' or '-'
               || char.IsAsciiDigit(rest[0])
               || rest.StartsWith("true", StringComparison.Ordinal)
               || rest.StartsWith("false", StringComparison.Ordinal)
               || rest.StartsWith("null", StringComparison.Ordinal);
    }

    private static int SkipSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }
}
