using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace mTiles.Services;

/// <summary>
/// A shell command as it can honestly be shown to somebody being asked to approve it.
/// <para>Its own class because it is not a view model's job and, more to the point, because it is the
/// security barrier: the string it renders comes out of a file that a branch can carry in from
/// anywhere, and the whole question is whether what the user reads is what will run. That deserves to
/// be testable on its own, without a tile.</para>
/// <para>Two attacks, both ordinary rather than exotic — they are simply how one writes a thing and
/// displays another: a run of newlines pushes the part that matters off the bottom of the box, and a
/// right-to-left override reverses what follows it so <c>rm -rf /</c> reads as something harmless. A
/// third, truncation, was self-inflicted: cutting the display at 200 characters hid everything past
/// the ellipsis, so the payload simply moved there.</para>
/// </summary>
internal static partial class CommandDisplay
{
    /// <summary>
    /// How long a command may be and still be something a person can be asked to approve.
    /// <para>Generous — a real one is <c>dotnet build; dotnet test --filter X</c> — and a hard stop
    /// rather than a truncation. Nothing here elides: a command that will not fit in the question is
    /// refused instead, because asking about a command while hiding part of it is worse than not asking
    /// at all. It collects a yes for something nobody saw.</para>
    /// </summary>
    public const int MaxConsentable = 1_000;

    /// <summary>The command on one line, with everything that can lie about itself made visible.</summary>
    public static string ForDialog(string command) =>
        string.Join(" ⏎ ", Visible(command)
            .ReplaceLineEndings("\n")
            .Split('\n')
            .Select(line => Spaces().Replace(line, " ").Trim())
            .Where(line => line.Length > 0));

    /// <summary>A run of spaces, which is the same trick as a run of newlines with the box turned on its
    /// side: five hundred of them between two commands pushes the second off the right-hand edge, and
    /// counts towards the length that decides whether this can be shown at all. Collapsing them changes
    /// nothing about what runs — this string is never executed.
    /// <para>Only the ASCII space needs matching here because <see cref="Show"/> has already turned every
    /// other kind into one.</para></summary>
    [GeneratedRegex(@"[ ]{2,}")]
    private static partial Regex Spaces();

    /// <summary>Whether this command can be shown in full, and so consented to at all.</summary>
    public static bool CanBeConsentedTo(string command) => ForDialog(command).Length <= MaxConsentable;

    /// <summary>
    /// The same text with the characters that display as something other than themselves replaced by a
    /// visible mark.
    /// <para>Replaced rather than deleted, and with a glyph: a command that had something in it must not
    /// come out looking innocent, and the reader has to be able to see that it was tampered with.</para>
    /// </summary>
    private static string Visible(string text)
    {
        var sb = new StringBuilder(text.Length);

        // By rune, not by char — the distinction is the whole of whether this mangles legitimate text.
        // Everything outside the basic plane is stored as a surrogate pair, and asking each half
        // whether it is a surrogate says yes to both, so every emoji in an honest command came out as
        // two blobs.
        foreach (var rune in text.EnumerateRunes())
            sb.Append(Show(rune));

        return sb.ToString();
    }

    private static string Show(Rune rune)
    {
        // U+2028 and U+2029 are line breaks to ReplaceLineEndings, which ForDialog runs afterwards, and
        // they are neither control nor format characters — so they would pass through here untouched
        // and be split on a line later. That is the right outcome, and it was reached by relying on the
        // exact set of characters one framework method happens to recognise. Named here instead.
        if (rune.Value is '\n' or '\r' or '\u2028' or '\u2029') return "\n";

        // Every kind of space becomes the ordinary one, so the run-collapsing below sees them all. A
        // tab was already handled; the rest were not, and they are the same trick with a different
        // character: five hundred non-breaking spaces push the second half of a command off the right
        // of the box exactly as five hundred ordinary ones would, and `[ ]{2,}` never saw them. The
        // categories are the separators plus what char.IsWhiteSpace knows, which is where U+00A0,
        // U+2000—200A and U+3000 live.
        if (Rune.IsWhiteSpace(rune)) return " ";

        var deceptive = Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Format
            or UnicodeCategory.PrivateUse;

        return deceptive ? "␦" : rune.ToString();
    }
}
