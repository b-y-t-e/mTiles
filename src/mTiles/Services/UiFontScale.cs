using mTiles.Models;
using System.Collections.Generic;
using System.Linq;

namespace mTiles.Services;

/// <summary>Every type size in the interface, as a multiple of the one size the user chose.</summary>
/// <remarks>
/// <para>There used to be two families of size token and only one of them was alive. <c>UiFontSize</c>
/// and <c>UiFontSizeSm</c> were written into the application's resources from <c>AppSettings.FontSize</c>
/// at every settings change; <c>FontXs</c>, <c>FontSm</c>, <c>FontMd</c> and <c>FontLg</c> were four
/// literal numbers in <c>AppTheme.axaml</c> that nothing ever touched. Both were spelled as a
/// <c>DynamicResource</c>, so nothing on screen and nothing in the markup told the two apart — and the
/// dead family had 136 uses against the live one's 73. That is exactly what the user saw: the font size
/// in Settings moved some of the interface and left most of it where it was.</para>
/// <para>So there is one family now, every member of it is a ratio, and the ratios are the sizes the
/// two families had between them at the default of 14pt — nothing on screen was meant to change, only
/// to start following the setting. <c>FontSm</c> is where the duplicate went: <c>FontSm</c> was 11 and
/// <c>UiFontSizeSm</c> 11.2, one fifth of a pixel apart, which is the clearest possible evidence that
/// they were the same intention written twice.</para>
/// <para><c>FontChip</c> is new and is not an invention: it is the fifteen places that had given up on
/// the scale altogether and written <c>FontSize="8"</c> or <c>"9"</c> into a view. They are all the
/// same thing — the boxed <c>NOT FOUND</c>/<c>RW</c>/<c>MANUAL</c> chip the design rules describe — so
/// they are one token rather than fifteen literals and two sizes.</para>
/// <para>The rule is enforced rather than documented: <c>FontScaleTests</c> reads every AXAML file in
/// the application and fails on a numeric <c>FontSize</c>, on a token that is not one of these, and on
/// one of these that nothing uses. That last one is what a dead token looks like on the day it is
/// added — <c>LogoFontSize</c> was computed at every settings change for a view that had stopped
/// asking for it.</para>
/// </remarks>
public static class UiFontScale
{
    /// <summary>The base size — <c>FontBase</c> — is the size the user chose, and everything else is a
    /// fraction or a multiple of it.</summary>
    public const string Base = "FontBase";

    /// <summary>Each token and what it is worth, relative to the user's own size.</summary>
    /// <remarks>Ordered smallest first, because that is the order the scale is read in and the order
    /// the test reports an offender against.</remarks>
    public static readonly IReadOnlyList<KeyValuePair<string, double>> Ratios =
    [
        // The chip: a boxed exception on a row, seen one at a time. Smaller than anything else here
        // on purpose — it is read as a mark rather than as a word.
        new("FontChip", 0.60),

        // Metadata beside a name, a status line, a column header.
        new("FontXs", 0.72),

        // The secondary line of a row, and most of the Settings dialog.
        new("FontSm", 0.80),

        // Body text in a panel that is not a list.
        new("FontMd", 0.86),

        new(Base, 1.00),

        // A heading, and the one line of an empty state.
        new("FontLg", 1.15),
    ];

    /// <summary>
    /// The prefix of the same scale measured against the <em>terminal's</em> font size.
    /// </summary>
    /// <remarks>
    /// <para>One table, two bases. A surface that is a transcript rather than a page — the Goal tile,
    /// and the findings dialog opened out of it — is drawn entirely in the terminal's face already, and
    /// a monospace face at the proportional face's size is the one thing that gives away that the two
    /// were set by different hands. Terminal Font Size in Settings is where somebody says how big they
    /// want to read code, and this is a surface made of code.</para>
    /// <para>Not a second family in the sense the one this class replaced was: those were the same
    /// question answered twice, with two spellings and no way to tell them apart. These are two
    /// different questions — how big is the interface, how big is the terminal — sharing one set of
    /// steps, so a step cannot mean one thing here and another there.</para>
    /// </remarks>
    public const string TerminalPrefix = "Term";

    /// <summary>The token names of one family, for the test that pins the markup to this list.</summary>
    public static IReadOnlyCollection<string> Names { get; } =
        Ratios.Select(r => r.Key).ToArray();

    /// <inheritdoc cref="Names"/>
    public static IReadOnlyCollection<string> TerminalNames { get; } =
        Ratios.Select(r => TerminalPrefix + r.Key).ToArray();

    /// <summary>Every token of both families.</summary>
    public static IReadOnlyCollection<string> AllNames { get; } =
        Names.Concat(TerminalNames).ToArray();

    /// <summary>The whole scale in points, for a chosen base size.</summary>
    /// <remarks>
    /// Rounded to a tenth: the ratios are chosen to land on the sizes this interface already had, and
    /// carrying <c>10.079999999999998</c> into a resource dictionary makes every one of those look like
    /// a number somebody derived rather than a size somebody picked.
    /// </remarks>
    public static IEnumerable<KeyValuePair<string, double>> For(double baseSize, string prefix = "")
    {
        var size = Clamp(baseSize);

        foreach (var (name, ratio) in Ratios)
            yield return new(prefix + name, Math.Round(size * ratio, 1));
    }

    /// <summary>One token's size, or the base size for a name this scale does not know.</summary>
    public static double Of(string token, double baseSize) =>
        For(baseSize, token.StartsWith(TerminalPrefix, StringComparison.Ordinal) ? TerminalPrefix : "")
            .FirstOrDefault(r => r.Key == token, new(token, Clamp(baseSize))).Value;

    /// <summary>
    /// The range a base size is allowed to take.
    /// </summary>
    /// <remarks>
    /// A settings file is hand-editable and is also read after a rollback, so a size of zero or of a
    /// thousand is a reachable state and neither is recoverable from inside the application: at zero
    /// every label in Settings — including the field holding the mistake — has no height, and at a
    /// thousand the dialog cannot be reached at all. The ends are deliberately wider than the chooser
    /// offers, because this is a guard against nonsense and not a second opinion about what is legible.
    /// </remarks>
    public const double MinBase = 6;

    /// <inheritdoc cref="MinBase"/>
    public const double MaxBase = 48;

    private static double Clamp(double size) =>
        double.IsFinite(size) ? Math.Clamp(size, MinBase, MaxBase) : AppDefaults.FontSize;
}
