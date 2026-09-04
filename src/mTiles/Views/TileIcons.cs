using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Material.Icons;

namespace mTiles.Views;

/// <summary>
/// What an icon name means to this application's icon set.
/// </summary>
/// <remarks>
/// <para>A kind names its icon with a string, and that is not a concession to layering: the phone needs
/// an icon name on the wire anyway, so a string is what the value actually is. This is the one place it
/// becomes a drawing, and it lives in <c>Views/</c> for the reason <see cref="SpecialDirectoryIcon"/>
/// does — which picture stands for a thing is a fact about the drawing.</para>
/// <para>An unrecognised name falls back rather than throwing. A wrong glyph is legible; a tile header
/// that cannot be built is not, and the names come from kinds that may be registered by code this file
/// has never heard of.</para>
/// </remarks>
public static class TileIcons
{
    /// <summary>What a tile with no kind yet wears: the one glyph that says nothing about which kind it
    /// is going to become.</summary>
    public const MaterialIconKind Placeholder = MaterialIconKind.PlusBoxOutline;

    public static MaterialIconKind Kind(string? iconId) => iconId switch
    {
        "console" => MaterialIconKind.Console,
        "powershell" => MaterialIconKind.Powershell,
        "bash" => MaterialIconKind.Bash,
        "fish" => MaterialIconKind.Fish,
        "note" => MaterialIconKind.NoteEditOutline,
        "checklist" => MaterialIconKind.CheckboxMarkedOutline,
        "source-branch" => MaterialIconKind.SourceBranch,
        "database" => MaterialIconKind.DatabaseOutline,
        "goal" => MaterialIconKind.BullseyeArrow,
        "gauge" => MaterialIconKind.SpeedometerSlow,
        "robot" => MaterialIconKind.RobotOutline,
        "script-outline" => MaterialIconKind.ScriptOutline,
        "restart" => MaterialIconKind.Restart,
        "refresh" => MaterialIconKind.Refresh,
        "check" => MaterialIconKind.Check,
        "upload" => MaterialIconKind.Upload,
        "play" => MaterialIconKind.Play,
        "pause" => MaterialIconKind.Pause,
        _ => Placeholder,
    };

    /// <summary>The same mapping for a list built from an <c>ItemsSource</c>, where the icon can only
    /// arrive through a binding.</summary>
    /// <remarks>The "Change type" menu is built from the registry the same way the chooser's cards are,
    /// and its items have no code-behind to ask on their behalf - the shape
    /// <see cref="SpecialDirectoryIcon"/> already uses from markup.</remarks>
    public static readonly FuncValueConverter<string?, MaterialIconKind> Icon = new(Kind);

    /// <summary>A kind's accent, as the brush its resource key names right now.</summary>
    /// <remarks>Resolved once per binding rather than followed like a <c>DynamicResource</c>, which is
    /// enough for what asks: the menu it serves is rebuilt every time it opens, so a theme changed
    /// underneath it is picked up by the next opening.</remarks>
    public static readonly FuncValueConverter<string?, IBrush?> Accent = new(AccentBrush);

    private static IBrush? AccentBrush(string? accentKey) =>
        accentKey is { Length: > 0 } && Application.Current is { } app
        && app.Resources.TryGetResource(accentKey, app.ActualThemeVariant, out var value)
            ? value as IBrush
            : null;
}
