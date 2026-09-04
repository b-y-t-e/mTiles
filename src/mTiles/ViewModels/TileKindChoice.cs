using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace mTiles.ViewModels;

/// <summary>
/// One kind this tile could become, as the header's "Change type" menu offers it.
/// </summary>
/// <remarks>
/// <para>A view model per entry rather than one command taking the kind's id as a parameter, for the
/// reason <see cref="AgentInstanceChoice"/> gives: a <c>CommandParameter</c> reaching the leaf from
/// inside an item template has to be routed back out through <c>$parent[MenuItem]</c>, a binding that
/// fails silently and leaves a menu whose items do nothing.</para>
/// <para>The icon and the accent are carried as the kind's own names for them, not as a drawing or a
/// brush: which picture and which colour those mean is a fact about the drawing, and it is answered in
/// <c>Views/TileIcons</c> — the same pair the chooser's cards are built from, so the two lists cannot
/// come to look different.</para>
/// </remarks>
public sealed class TileKindChoice(string label, string iconId, string accentKey, Func<Task> changeTo)
{
    /// <summary>What the chooser card and the tile header call this kind.</summary>
    public string Label { get; } = label;

    /// <summary>The kind's icon name.</summary>
    public string IconId { get; } = iconId;

    /// <summary>The resource key the kind's glyph is drawn in.</summary>
    public string AccentKey { get; } = accentKey;

    /// <summary>Turns the tile into this kind, having asked first.</summary>
    public ICommand ChangeCommand { get; } = new AsyncRelayCommand(changeTo);
}
