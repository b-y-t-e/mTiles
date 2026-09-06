using mTiles.Models;
using mTiles.Services;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Every type size in the application follows the size the user chose.
/// </summary>
/// <remarks>
/// <para>It did not, and there was nothing on screen or in the markup to say so. Two families of size
/// token existed side by side, both spelled as a <c>DynamicResource</c>: <c>UiFontSize</c> and
/// <c>UiFontSizeSm</c>, written from <c>AppSettings.FontSize</c> at every settings change, and
/// <c>FontXs</c>/<c>FontSm</c>/<c>FontMd</c>/<c>FontLg</c>, four literal numbers in a styles file that
/// nothing ever touched. The dead family had 136 uses against the live one's 73, plus fifteen views
/// that had written a number straight into the markup. Changing the font size in Settings therefore
/// moved a minority of the interface, which is what the user reported.</para>
/// <para>Fixing the 224 sites is a morning's work; keeping them fixed is this file. A new view is
/// written by copying an old one, and the failure mode has no symptom at the time — the size looks
/// right, because at the default size every one of these tokens is worth what the literal was.</para>
/// </remarks>
public class FontScaleTests
{
    /// <summary>The scale is the whole vocabulary: no view may write a size of its own.</summary>
    /// <remarks>
    /// Both spellings of it — the attribute on a control and the <c>Setter</c> in a style — because
    /// the fifteen offenders found on the day this was written were split across the two.
    /// </remarks>
    [Fact]
    public void No_view_sets_a_font_size_of_its_own()
    {
        var offenders = new List<string>();

        foreach (var file in MarkupFiles())
        {
            var line = 0;

            foreach (var text in File.ReadLines(file))
            {
                line++;

                foreach (var value in FontSizeValues(text))
                    if (!IsScaleToken(value))
                        offenders.Add($"{Path.GetFileName(file)}:{line} sets FontSize to {value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A font size must be one of UiFontScale's tokens, so that it follows the size the user " +
            "chose in Settings:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>A token nothing uses is a token nobody will notice has stopped working.</summary>
    /// <remarks>
    /// <para><c>LogoFontSize</c> is why: it was computed from the user's font size on every settings
    /// change, for a view that had stopped asking for it. Nothing failed and nothing was drawn — the
    /// cost was only that the next reader of <c>ApplyFontResources</c> believed there were three live
    /// tokens where there were two.</para>
    /// <para>The interface family only, deliberately. The terminal family is the <em>same table</em>
    /// against a second base, generated in one loop, so a step nobody uses there is a step the table
    /// has rather than a token somebody wrote and forgot — and requiring every one of them to be used
    /// would mean picking, per step, whether the Goal tile is allowed to reach for it.</para>
    /// </remarks>
    [Fact]
    public void Every_size_in_the_scale_is_used_somewhere()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in MarkupFiles())
            foreach (var text in File.ReadLines(file))
                foreach (var value in FontSizeValues(text))
                    if (TokenOf(value) is { } token)
                        used.Add(token);

        var unused = UiFontScale.Names.Where(n => !used.Contains(n)).ToList();

        Assert.True(unused.Count == 0,
            "These sizes are computed at every settings change and drawn nowhere: " + string.Join(", ", unused));
    }

    /// <summary>
    /// The literal sizes in <c>AppTheme.axaml</c> are the scale at the default size, exactly.
    /// </summary>
    /// <remarks>
    /// They exist only so the previewer and a lookup made before the first write have something to
    /// find. Left to drift they become a second opinion about the same size, which is the fault this
    /// whole file exists for, one level up.
    /// </remarks>
    [Fact]
    public void The_theme_defaults_are_the_scale_at_the_default_size()
    {
        var declared = new Dictionary<string, double>(StringComparer.Ordinal);
        // Both families, so a Term* default that drifts is caught by the same rule as a bare one.
        var pattern = new Regex("""<x:Double x:Key="(?<key>\w*Font\w*)">(?<value>[\d.]+)</x:Double>""");

        foreach (var text in File.ReadLines(Path.Combine(Source(), "Styles", "AppTheme.axaml")))
            if (pattern.Match(text) is { Success: true } m)
                declared[m.Groups["key"].Value] = double.Parse(m.Groups["value"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);

        var expected = UiFontScale.For(AppDefaults.FontSize)
            .Concat(UiFontScale.For(AppDefaults.FontSize, UiFontScale.TerminalPrefix))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        Assert.Equal(expected, declared);
    }

    /// <summary>Doubling the chosen size doubles every size in the interface.</summary>
    /// <remarks>The property the whole arrangement is for, asserted directly rather than inferred from
    /// the ratios being multiplied — a token pinned to a constant would still pass a test that only
    /// read the table it was written in.</remarks>
    [Theory]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(28)]
    public void Every_size_follows_the_chosen_one(double baseSize)
    {
        foreach (var (name, size) in UiFontScale.For(baseSize))
        {
            Assert.True(size > 0, $"{name} came out at {size} for a base of {baseSize}");
            Assert.Equal(UiFontScale.Of(name, baseSize), size);
        }

        Assert.Equal(baseSize, UiFontScale.Of(UiFontScale.Base, baseSize));
    }

    /// <summary>A size a settings file could never produce is drawn as the default, not as nothing.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(double.NaN)]
    [InlineData(100000)]
    public void An_impossible_size_still_draws(double stored)
    {
        foreach (var (name, size) in UiFontScale.For(stored))
            Assert.True(size >= UiFontScale.MinBase * 0.5 && size <= UiFontScale.MaxBase * 1.5,
                $"{name} came out at {size} for a stored size of {stored}");
    }

    /// <summary>The Goal tile is sized by the terminal, everywhere, including its findings dialog.</summary>
    /// <remarks>
    /// <para>It is a transcript, not a page: every row in it is already set in the terminal's face, and
    /// a monospace face at the proportional face's size is exactly what makes two surfaces look as
    /// though they were set by different hands. So the size comes from Terminal Font Size, which is
    /// where somebody says how big they want to read code.</para>
    /// <para>The dialog is in the list because it is the half that is easy to forget: it is drawn in
    /// the main window rather than inside the tile, which is why this could not be a resource scoped to
    /// the tile's own tree — and why a later view added to it would reach for the interface family
    /// without anything looking wrong.</para>
    /// </remarks>
    [Theory]
    [InlineData("Views/GoalTileView.axaml")]
    [InlineData("Styles/GoalFindings.axaml")]
    public void The_goal_tile_is_sized_by_the_terminal(string file)
    {
        var offenders = new List<string>();
        var line = 0;

        foreach (var text in File.ReadLines(Path.Combine(Source(), file.Replace('/', Path.DirectorySeparatorChar))))
        {
            line++;

            foreach (var value in FontSizeValues(text))
                if (TokenOf(value) is { } token && !UiFontScale.TerminalNames.Contains(token))
                    offenders.Add($"{Path.GetFileName(file)}:{line} uses {token}");
        }

        Assert.True(offenders.Count == 0,
            "These take the interface's size in a surface drawn entirely in the terminal's face:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The two families are one table, so a step means the same thing in both.</summary>
    [Fact]
    public void The_terminal_family_is_the_same_scale_against_a_second_base()
    {
        var ui = UiFontScale.For(20).Select(r => r.Value).ToList();
        var terminal = UiFontScale.For(20, UiFontScale.TerminalPrefix).Select(r => r.Value).ToList();

        Assert.Equal(ui, terminal);
        Assert.Equal(UiFontScale.Names.Select(n => UiFontScale.TerminalPrefix + n), UiFontScale.TerminalNames);
    }

    /// <summary>Every value a <c>FontSize</c> is set to on one line of markup.</summary>
    private static IEnumerable<string> FontSizeValues(string text)
    {
        foreach (Match m in Regex.Matches(text, "FontSize=\"(?<value>[^\"]*)\""))
            yield return m.Groups["value"].Value;

        foreach (Match m in Regex.Matches(
                     text, "<Setter\\s+Property=\"FontSize\"\\s+Value=\"(?<value>[^\"]*)\""))
            yield return m.Groups["value"].Value;
    }

    /// <summary>
    /// The three view-model sizes a resource cannot express.
    /// </summary>
    /// <remarks>
    /// <para>AvaloniaEdit and the terminal measure a cell grid rather than read a resource, so the
    /// editors take a number their view model computes — and each of these three recomputes it from
    /// <c>AppSettings.FontSize</c> when the settings change, which is the property the tokens buy
    /// everywhere else. <c>GitTileViewModel.DiffFontSize</c> is the one with an opinion of its own: the
    /// diff is 80% of the tile's size.</para>
    /// <para>A named list rather than "any binding is fine": a binding is exactly what a view would use
    /// to reintroduce a size nothing follows, and the point of this file is that adding one has to be a
    /// decision somebody wrote down.</para>
    /// </remarks>
    private static readonly string[] ComputedSizes = ["FontSize", "DiffFontSize"];

    private static bool IsScaleToken(string value) =>
        TokenOf(value) is not null
        || ComputedSizes.Any(p => value.Trim() == $"{{Binding {p}}}");

    /// <summary>The scale token a markup value names, or null for anything else.</summary>
    /// <remarks>A binding is left alone: the git tile's diff and the database tile's log carry their
    /// own size, computed from the user's in a view model, because AvaloniaEdit and the terminal
    /// measure a cell grid rather than take a resource.</remarks>
    private static string? TokenOf(string value)
    {
        var m = Regex.Match(value.Trim(), @"^\{DynamicResource\s+(?<key>\w+)\}$");

        if (!m.Success) return null;

        var key = m.Groups["key"].Value;
        return UiFontScale.AllNames.Contains(key) ? key : null;
    }

    private static IEnumerable<string> MarkupFiles() =>
        Directory.EnumerateFiles(Source(), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <inheritdoc cref="XmlDocPlacementTests"/>
    private static string Source([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "src", "mTiles"));
}
