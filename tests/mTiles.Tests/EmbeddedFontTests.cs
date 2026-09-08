using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Platform;
using mTiles.Models;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The typeface this application ships with is inside the executable, and resolves.
/// </summary>
/// <remarks>
/// Both halves are worth asserting and neither implies the other: the faces are Avalonia resources, so
/// one line removed from the csproj takes them out of the build with nothing to see until somebody
/// runs it on a machine that has no JetBrains Mono installed — where the failure is silent, because a
/// family that cannot be found falls through to the next name in the list and the interface merely
/// looks wrong. Which is exactly the machine this is for.
/// </remarks>
public class EmbeddedFontTests
{
    private static readonly string[] Faces =
    [
        "Regular", "Italic", "Medium", "SemiBold", "Bold", "BoldItalic"
    ];

    /// <remarks>On the UI thread because the asset loader is a service of a running application, not
    /// a static reader of the file on disk.</remarks>
    [Fact]
    public void Every_face_is_compiled_into_the_assembly() => OnUiThread(() =>
    {
        foreach (var face in Faces)
        {
            var uri = new Uri($"{AppFonts.Assets}/JetBrainsMono-{face}.ttf");
            Assert.True(AssetLoader.Exists(uri), $"{uri} is not in the build");
        }
    });

    /// <summary>The family named in the defaults is the one the font manager answers with.</summary>
    /// <remarks>Run on the UI thread because the font manager belongs to a running application, and
    /// against the test application, which registers the same collection <c>Program</c> does — so what
    /// this resolves is what ships rather than something the test set up for itself.</remarks>
    [Theory]
    [InlineData(nameof(AppDefaults.FontFamily))]
    [InlineData(nameof(AppDefaults.TerminalFontFamily))]
    public void The_default_families_resolve_to_the_embedded_copy(string which) => OnUiThread(() =>
    {
        var family = which == nameof(AppDefaults.FontFamily)
            ? AppDefaults.FontFamily
            : AppDefaults.TerminalFontFamily;

        // The first name in the list is the embedded one, and it is the one that has to answer: the
        // rest are fallbacks for a machine that happens to have them.
        Assert.StartsWith(AppFonts.JetBrainsMono, family, StringComparison.Ordinal);

        Assert.True(
            FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(family)), out var typeface),
            $"nothing answered for {family}");
        Assert.Equal("JetBrains Mono", typeface.FamilyName);
    });

    /// <summary>A weight the interface asks for is a real face and not a synthesised one.</summary>
    [Theory]
    [InlineData(FontWeight.Normal, FontStyle.Normal)]
    [InlineData(FontWeight.Medium, FontStyle.Normal)]
    [InlineData(FontWeight.SemiBold, FontStyle.Normal)]
    [InlineData(FontWeight.Bold, FontStyle.Normal)]
    [InlineData(FontWeight.Normal, FontStyle.Italic)]
    [InlineData(FontWeight.Bold, FontStyle.Italic)]
    public void The_weights_the_interface_uses_are_shipped(FontWeight weight, FontStyle style)
        => OnUiThread(() =>
        {
            var typeface = new Typeface(new FontFamily(AppFonts.JetBrainsMono), style, weight);

            Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out var glyphs),
                $"no face for {weight} {style}");

            // "JetBrains Mono Medium" and "JetBrains Mono SemiBold" are what those two faces call
            // themselves - the family name in a static instance of a variable font carries the weight -
            // so the assertion is that the answer came from this typeface at all, rather than from
            // whatever the machine would have fallen back to.
            Assert.StartsWith("JetBrains Mono", glyphs.FamilyName, StringComparison.Ordinal);
        });

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(EmbeddedFontTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
