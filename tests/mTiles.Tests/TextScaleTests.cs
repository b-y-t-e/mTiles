using mTiles.Models;
using mTiles.Services;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace mTiles.Tests;

/// <summary>The desktop's text-scaling factor reaches every size, or it is worse than not reaching any.</summary>
/// <remarks>
/// <para>A factor applied to some surfaces and not others is the fault that made the font size in
/// Settings move a minority of the interface, arriving from the other end. It is easier to reach here:
/// the obvious place to multiply is <c>ApplyFontResources</c>, which would scale every label and leave
/// the terminal, the git diff, the notes and the database log exactly where they were — because those
/// take their size straight from settings, AvaloniaEdit and the terminal measuring a cell grid rather
/// than reading a resource.</para>
/// <para>So the rule is the same shape as the one on the markup: a size is read through
/// <see cref="TextScale"/> and never off <c>AppSettings</c>, and the list of places allowed to read it
/// raw is short, named, and has a reason attached to each.</para>
/// </remarks>
public class TextScaleTests
{
    /// <summary>
    /// The four places allowed to read a stored font size without scaling it.
    /// </summary>
    /// <remarks>
    /// <c>TextScale</c> is where the multiplication happens. <c>SettingsViewModel</c> holds the two
    /// spinners, which must show the number the user typed rather than what the desktop made of it —
    /// otherwise typing 14 shows 17.5 and typing it again shows 21.9. <c>AppSettings</c> declares them.
    /// <c>SettingsPortability</c> moves the file, where the stored value is the whole point.
    /// </remarks>
    private static readonly string[] MayReadRaw =
    [
        "TextScale.cs", "SettingsViewModel.cs", "SettingsViewModel.Ai.cs",
        "AppSettings.cs", "SettingsPortability.cs",
    ];

    [Fact]
    public void Nothing_reads_a_font_size_without_the_desktop_s_say()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            if (MayReadRaw.Contains(Path.GetFileName(file))) continue;

            var line = 0;

            foreach (var text in File.ReadLines(file))
            {
                line++;

                // A comment naming the property is prose about the rule, not a breach of it — and the
                // comment explaining why a call site scales is exactly where the name appears.
                var code = text.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)
                    || code.StartsWith("///", StringComparison.Ordinal)
                    || code.StartsWith('*')) continue;

                // A settings object under any of the names it is given here, then the property. Not a
                // bare "FontSize": the view models have one of their own, which is the scaled value and
                // exactly what everything downstream should be reading.
                if (Regex.IsMatch(text, @"\b(s|settings|Settings|_settingsService\.Settings)\.(Terminal)?FontSize\b"))
                    offenders.Add($"{Path.GetFileName(file)}:{line} {text.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These read a stored font size without the desktop's text scale, so they would stay put " +
            "while the rest of the interface grew:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    /// <summary>A factor no desktop should report still leaves the text readable.</summary>
    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-2, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(100, TextScale.Max)]
    [InlineData(0.01, TextScale.Min)]
    [InlineData(1.25, 1.25)]
    public void A_factor_read_off_the_machine_is_made_safe(double read, double expected) =>
        Assert.Equal(expected, TextScale.Normalise(read));

    /// <summary>Both shapes <c>gsettings</c> prints, and the decimal point it prints them with.</summary>
    /// <remarks>
    /// The invariant culture is the load-bearing one: this machine's own culture uses a comma, so
    /// <c>1.25</c> parsed with the current culture is refused and read as no answer at all — on exactly
    /// the machines where nobody would think to look.
    /// </remarks>
    [Theory]
    [InlineData("1.25", 1.25)]                          // gsettings get
    [InlineData("text-scaling-factor: 1.25", 1.25)]     // gsettings monitor
    [InlineData("  1.5\n", 1.5)]
    [InlineData("1.0", 1.0)]
    [InlineData("", 1.0)]
    [InlineData(null, 1.0)]
    [InlineData("uint32 2", 1.0)]                       // a key of another type, read as no answer
    [InlineData("No such schema", 1.0)]
    public void A_line_gsettings_printed_is_read_or_ignored(string? printed, double expected) =>
        Assert.Equal(expected, DesktopTextScale.Parse(printed));

    /// <summary>The stored size and the factor multiply, and both font sizes follow.</summary>
    /// <remarks>The terminal too, which is the decision this carries: it is the one surface made
    /// entirely of text, and a text-size setting that moved every label except the code would be
    /// answering the wrong question.</remarks>
    [Fact]
    public void Both_sizes_follow_the_factor()
    {
        var settings = new AppSettings { FontSize = 14, TerminalFontSize = 16 };

        Assert.True(TextScale.Set(1.5));
        try
        {
            Assert.Equal(21, TextScale.UiFontSize(settings));
            Assert.Equal(24, TextScale.TerminalFontSize(settings));
        }
        finally
        {
            TextScale.Set(1.0);
        }

        Assert.Equal(14, TextScale.UiFontSize(settings));
        Assert.Equal(16, TextScale.TerminalFontSize(settings));
    }

    /// <summary>A factor that has not moved is not a reason to redraw the application.</summary>
    /// <remarks>The watcher prints a line per change to the schema, so this answers whether that line
    /// was about a value that actually differs — every change is announced as a settings change, which
    /// rebuilds every resource and every tile's font.</remarks>
    [Fact]
    public void Setting_the_same_factor_again_reports_no_change()
    {
        try
        {
            Assert.True(TextScale.Set(1.25));
            Assert.False(TextScale.Set(1.25));
            Assert.True(TextScale.Set(1.0));
        }
        finally
        {
            TextScale.Set(1.0);
        }
    }

    private static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <inheritdoc cref="XmlDocPlacementTests"/>
    private static string Root([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
}
