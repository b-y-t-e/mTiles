using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
/// A face that cannot be measured leaves the width alone: an element at its natural size is a
/// misalignment, and an element at zero is one nobody can find.</para>
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

        if (Resource(element, "TerminalFontFamily") is not FontFamily family) return;
        if (Resource(element, "TermFontBase") is not double size || size <= 0) return;

        var space = new FormattedText(" ", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family), size, Brushes.Transparent).Width;
        if (space <= 0) return;

        element.Width = space * characters;
    }

    private static object? Resource(StyledElement element, string key) =>
        element.TryFindResource(key, out var value) ? value : null;
}
