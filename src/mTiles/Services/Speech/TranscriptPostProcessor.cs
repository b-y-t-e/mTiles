using System.Text.RegularExpressions;

namespace mTiles.Services.Speech;

/// <summary>
/// Tidies a raw transcript into something worth typing into a prompt.
/// </summary>
/// <remarks>
/// A pared-down port of Handy's <c>audio_toolkit/text.rs</c>: filler words, then the stutter and
/// whitespace collapse it calls normalisation. Handy's fuzzy vocabulary correction is deliberately not
/// here — the user's own words are handed to whisper as its initial prompt instead, which biases
/// recognition rather than editing the result and cannot mangle a word it merely resembles.
/// </remarks>
internal static partial class TranscriptPostProcessor
{
    /// <summary>
    /// Noises that are not a word in any language these models can produce, so removing them cannot
    /// corrupt a transcript whatever language it turned out to be in.
    /// </summary>
    /// <remarks>
    /// Handy's own list, and deliberately short: anything that is a real word <em>somewhere</em> — "um"
    /// (Portuguese "a", German "at"), "ah" and "eh" as interjections, "mm" as millimetres — belongs in
    /// the gated lists below instead.
    /// </remarks>
    private static readonly string[] UniversalFillers =
        ["uh", "uhm", "umm", "uhh", "uhhh", "ehh", "ehm", "ahm", "hmm", "hm", "mmm", "хм", "ммм"];

    /// <summary>
    /// Fillers that may only be removed when the language is known, because the same token means
    /// something elsewhere.
    /// </summary>
    /// <remarks>
    /// Handy's split, arrived at the same way. Ours keeps a Polish list it has no equivalent of, and
    /// that list is the clearest illustration of why these are gated: <c>aaa</c> and <c>ee</c> are
    /// hesitations in Polish and <c>AAA</c> and <c>EE</c> are things people dictate into a prompt.
    /// Handy's English list also has <c>ha</c>, left out here for the same reason one step further —
    /// <c>ha</c> is a hectare in most of the languages on the list, this one included.
    /// </remarks>
    private static readonly Dictionary<string, string[]> LanguageFillers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = ["um", "ah", "eh"],
        ["pl"] = ["yyy", "eee", "aaa", "yy", "ee"],
        ["de"] = ["äh", "ähm"],
        ["fr"] = ["euh"],
    };

    /// <summary>
    /// <paramref name="language"/> is the code the transcript was produced with — evidence, not a
    /// preference. <c>auto</c> or an unknown code applies the universal list only.
    /// </summary>
    public static string Clean(string text, string language, bool removeFillerWords)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var result = RemoveNonSpeechAnnotations(text);
        if (removeFillerWords)
            result = RemoveFillers(result, language);

        result = CollapseStutter(result);
        result = WhitespaceRun().Replace(result, " ");
        return result.Trim();
    }

    /// <summary>
    /// Drops whisper's own annotations for things that are not speech — <c>[muzyka]</c>, <c>[Music]</c>,
    /// <c>*silence*</c>, <c>(upbeat music)</c>.
    /// </summary>
    /// <remarks>
    /// Measured, not theoretical: two seconds of silence through <c>ggml-base</c> transcribes as
    /// "[muzyka]". Handy needs no such list because Silero VAD never hands it the silence in the first
    /// place; without a VAD, this is where that job lands. Square brackets and asterisks go wherever
    /// they appear, since neither survives being spoken aloud; round brackets only when they are the
    /// whole transcript, because a dictated aside really can contain them.
    /// </remarks>
    private static string RemoveNonSpeechAnnotations(string text)
    {
        var stripped = BracketedAnnotation().Replace(text, " ").Trim();
        return WholeParenthetical().IsMatch(stripped) ? "" : stripped;
    }

    /// <summary>
    /// One compiled pattern per language, built once.
    /// </summary>
    /// <remarks>
    /// The previous shape ran <see cref="Regex.Replace(string,string,string)"/> once per filler word —
    /// sixteen or more freshly-built patterns per transcript, against a static regex cache that holds
    /// fifteen. Every dictation recompiled the lot.
    /// </remarks>
    private static readonly Dictionary<string, Regex> Patterns = new(StringComparer.OrdinalIgnoreCase);

    private static Regex PatternFor(string language)
    {
        // Keyed by the language that changes the answer, not by the one that was asked about: everything
        // without a gated list produces the same universal pattern, so they share one entry. The
        // language reaches this from a settings file, which can say anything at all — and every distinct
        // string was a compiled Regex kept for the life of the process.
        var key = LanguageFillers.ContainsKey(language) ? language : "";

        lock (Patterns)
        {
            if (Patterns.TryGetValue(key, out var cached))
                return cached;

            // No language, no gated list: this **fails closed**, which is Handy's rule and the right way
            // round. Applying every list on a miss — which is what this did — is a guess, and it is the
            // guess that eats real words: with Parakeet the language is always unknown here, so a
            // dictated "AAA" or "EE" was being stripped from English by the Polish list. The other way
            // costs a "yyy" left standing in a prompt, which costs nothing at all.
            var words = LanguageFillers.TryGetValue(key, out var gated)
                ? UniversalFillers.Concat(gated)
                : UniversalFillers.AsEnumerable();

            var pattern = new Regex(
                $@"\b(?:{string.Join('|', words.Distinct().Select(Regex.Escape))})\b[,.]?",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

            Patterns[key] = pattern;
            return pattern;
        }
    }

    private static string RemoveFillers(string text, string language) =>
        PatternFor(BaseLanguage(language)).Replace(text, "");

    /// <summary>Three or more of the same word in a row become one — what a repeated false start
    /// sounds like to the model.</summary>
    private static string CollapseStutter(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return text;

        var kept = new List<string>(parts.Length);
        var run = 1;
        for (var i = 0; i < parts.Length; i++)
        {
            var isRepeat = i > 0
                && parts[i].Equals(parts[i - 1], StringComparison.OrdinalIgnoreCase)
                && parts[i].All(char.IsLetter);
            run = isRepeat ? run + 1 : 1;

            if (run == 3)
                kept.RemoveAt(kept.Count - 1);   // drop the second of the three as well
            if (run < 3)
                kept.Add(parts[i]);
        }
        return string.Join(' ', kept);
    }

    /// <summary>The language part of a code such as <c>pl</c> or <c>zh-Hans</c>.</summary>
    private static string BaseLanguage(string language) =>
        language.Split('-', '_')[0];

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRun();

    /// <summary>
    /// Whisper's own annotations: <c>[muzyka]</c>, <c>[BLANK_AUDIO]</c>, <c>*silence*</c>.
    /// </summary>
    /// <remarks>
    /// The asterisk half is deliberately narrow — letters and single spaces between the markers, and
    /// only where the markers themselves are not stuck to a word. <c>\*[^*]*\*</c> matched anything at
    /// all between two asterisks, so a dictated <c>2 * 3 * 4</c> came out as <c>2 4</c>: arithmetic and
    /// glob patterns are ordinary things to say into a terminal, and losing the middle of one silently
    /// is worse than leaving <c>*upbeat music*</c> in.
    /// </remarks>
    [GeneratedRegex(@"\[[^\]]*\]|(?<![^\s(])\*[A-Za-z][A-Za-z ]{0,30}\*(?![^\s.,!?)])")]
    private static partial Regex BracketedAnnotation();

    [GeneratedRegex(@"^\([^)]*\)[.!?]?$")]
    private static partial Regex WholeParenthetical();
}
