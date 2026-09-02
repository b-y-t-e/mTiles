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
internal static class TileIcons
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
}
