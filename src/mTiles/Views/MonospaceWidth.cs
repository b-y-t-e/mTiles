using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using mTiles.Models;
using System.Globalization;

namespace mTiles.Views;

/// <summary>
/// A width said in characters of the terminal's own face, rather than in pixels.
/// </summary>
/// <remarks>
/// <para>For the things that stand beside monospaced text and have to line up with its grid: a gutter,
/// a rail, an indent. In pixels they line up at one font size and at one font, and this application
/// lets the user change both — Terminal Font Size moves every figure on the surface these sit on, and
/// a 12px inset that was one character becomes two thirds of one.</para>
/// <para>Measured rather than assumed: the face is whatever <c>TerminalFontFamily</c> resolves to,
/// which is the embedded JetBrains Mono unless somebody typed another name into Settings, and nothing
/// here may assume that name is monospaced at all — a space is measured in the font that is actually
/// going to be drawn.</para>
/// <para>Re-measured when the resources change, which is what a theme or a font-size change raises.
/// A face the resources do not name is measured against the application's own defaults rather than
/// left alone: an element at its natural size is a misalignment, and an element at zero is one nobody
/// can find.</para>
/// </remarks>
public static class MonospaceWidth
{
    /// <summary>How many characters wide this element should be.</summary>
    public static readonly AttachedProperty<double> CharactersProperty =
        AvaloniaProperty.RegisterAttached<Layoutable, double>("Characters", typeof(MonospaceWidth));

    public static double GetCharacters(Layoutable element) => element.GetValue(CharactersProperty);
    public static void SetCharacters(Layoutable element, double value) =>
        element.SetValue(CharactersProperty, value);

    static MonospaceWidth()
    {
        CharactersProperty.Changed.AddClassHandler<Layoutable>((element, _) => Attach(element));
    }

    private static void Attach(Layoutable element)
    {
        Apply(element);
        // Once. The handler is idempotent, and the property is set from markup exactly once per element.
        element.AttachedToVisualTree -= OnAttached;
        element.AttachedToVisualTree += OnAttached;
        element.ResourcesChanged -= OnResourcesChanged;
        element.ResourcesChanged += OnResourcesChanged;
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Layoutable element) Apply(element);
    }

    private static void OnResourcesChanged(object? sender, ResourcesChangedEventArgs e)
    {
        if (sender is Layoutable element) Apply(element);
    }

    private static void Apply(Layoutable element)
    {
        var characters = GetCharacters(element);
        if (characters <= 0) return;

        element.Width = CellWidth(element) * characters;
    }

    /// <summary>The width of one character of the face this element's text is actually drawn in.</summary>
    /// <remarks>
    /// <para>A digit, not a space. <c>FormattedText.Width</c> is the width <i>excluding trailing
    /// whitespace</i>, so measuring <c>" "</c> answers 0 however large the font is — and a rail whose
    /// width came back as zero is an element nobody can find, which is exactly what the fold handle
    /// beside every patch was. In a monospaced face every glyph is the same width, and in a
    /// proportional one a digit is at least a glyph that was drawn.</para>
    /// <para>The resources are the answer where there are any, and the application's own defaults
    /// where there are not — never <i>no answer</i>. <c>TerminalFontFamily</c> and <c>TermFontBase</c>
    /// are written into the application's resources by <c>App.ApplyFontResources</c> at startup, so a
    /// lookup made before that first write, in the XAML previewer, or in a headless test that hosts
    /// this view on its own finds nothing — and an element left at its natural size is the zero-width
    /// handle over again, because the mark this slot carries is measured the same way.</para>
    /// </remarks>
    private static double CellWidth(StyledElement element)
    {
        var family = Resource(element, "TerminalFontFamily") as FontFamily
            ?? new FontFamily(AppDefaults.TerminalFontFamily);
        var size = Resource(element, "TermFontBase") is double resolved && resolved > 0
            ? resolved
            : AppDefaults.FontSize;

        var cell = new FormattedText("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family), size, Brushes.Transparent).Width;
        // A face that measures to nothing at all is not one this can line up with; half the type size
        // is the proportion a monospaced cell keeps, and any floor at all beats a width of zero.
        return cell > 0 ? cell : size / 2;
    }

    private static object? Resource(StyledElement element, string key) =>
        element.TryFindResource(key, out var value) ? value : null;
}
