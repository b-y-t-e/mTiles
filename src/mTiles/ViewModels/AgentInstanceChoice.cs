using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace mTiles.ViewModels;

/// <summary>
/// One configured way of running this tile's agent, as the header's "Run as" menu offers it.
/// </summary>
/// <remarks>
/// <para>A view model per entry rather than one command taking the instance's id as a parameter: the
/// menu is built from an <c>ItemsSource</c>, and a <c>CommandParameter</c> reaching the leaf from
/// inside an item template has to be routed back out through <c>$parent[MenuItem]</c> — a binding that
/// fails silently and leaves a menu whose items do nothing. Each entry carrying its own command is the
/// shape <see cref="GoalSolidToggle"/> and its neighbours already use.</para>
/// <para>Built when the menu opens and thrown away with it, so nothing here follows an instance that is
/// renamed or deleted in Settings while the tile lives.</para>
/// </remarks>
public sealed class AgentInstanceChoice(string label, bool isCurrent, Func<Task> switchTo)
{
    /// <summary>What the instance is called — the same name the Settings row and the tile's header show.
    /// </summary>
    public string Label { get; } = label;

    /// <summary>Whether this is the instance the tile is running now, which the menu ticks.</summary>
    public bool IsCurrent { get; } = isCurrent;

    /// <summary>Switches the tile to this instance, having asked first.</summary>
    public ICommand SwitchCommand { get; } = new AsyncRelayCommand(switchTo);
}
