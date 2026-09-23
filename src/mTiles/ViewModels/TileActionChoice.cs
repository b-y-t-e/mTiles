using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace mTiles.ViewModels;

/// <summary>
/// One of the content's own actions, as the tile header's overflow menu offers it.
/// </summary>
/// <remarks>
/// A view model per entry rather than one command taking the action's id, for the reason
/// <see cref="TileKindChoice"/> gives. The enabled state is the snapshot the menu was opened on, which is
/// what the list is rebuilt for every time it opens; the content asks again when the action arrives.
/// </remarks>
public sealed class TileActionChoice(TileAction action, Func<Task> invoke)
{
    /// <summary>What the menu calls it.</summary>
    public string Label { get; } = action.Label;

    /// <summary>The action's icon name.</summary>
    public string IconId { get; } = action.Icon;

    /// <summary>Asks the content to do it.</summary>
    public ICommand InvokeCommand { get; } = new AsyncRelayCommand(invoke, () => action.IsEnabled);
}
