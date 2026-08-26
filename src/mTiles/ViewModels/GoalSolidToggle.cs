using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// One letter of SOLID on the criteria panel: whether this goal is held to that principle.
/// </summary>
/// <remarks>
/// <para>A view model per principle rather than five properties on <see cref="GoalCriteriaEditor"/>,
/// because the panel shows them as a row of five identical chips and five properties would need five
/// bindings, five change handlers and five entries in every place that copies the criteria — the shape
/// where four of them agreeing and one not is a switch that silently does nothing.</para>
/// <para>Each chip <b>carries its own principle</b> rather than being matched to one by position. The
/// editor built the row from the catalog and then walked both by index to fill it and to read it back,
/// which is correct only while the two lists stay the same length in the same order — an invariant
/// nothing states and one insertion breaks, and what it breaks is silent: the panel would light the
/// wrong letters and write the user's answer onto the wrong principles. Holding the principle removes
/// the pairing rather than documenting it.</para>
/// <para>It carries no opinion of its own about persistence. Changing it calls back into the editor,
/// which is where every other edit on that panel is written from, so a change here is saved by exactly
/// the same route as a change to the attempt count.</para>
/// </remarks>
public partial class GoalSolidToggle : ObservableObject
{
    private readonly SolidPrinciple _principle;
    private readonly Action _changed;

    internal GoalSolidToggle(SolidPrinciple principle, Action changed)
    {
        _principle = principle;
        _changed = changed;
    }

    /// <summary>The single character shown on the chip.</summary>
    public string Letter => _principle.Letter;

    /// <summary>The principle's full name, for the tooltip. The chip has room for one character and the
    /// letters only spell anything to somebody who already knows what they stand for.</summary>
    public string Name => _principle.Name;

    /// <summary>Whether this goal is held to the principle.</summary>
    /// <remarks>
    /// <c>IsOn</c> rather than <c>IsEnabled</c>, which is what <c>Control</c> calls something else
    /// entirely: a chip that is off is a principle the user switched off, not a control greyed out and
    /// refusing to be clicked. Nothing collides — this is not a control — but the row is defined in the
    /// middle of a page of them.
    /// </remarks>
    [ObservableProperty] private bool _isOn = true;

    /// <summary>Reads this chip out of a set of switches — the panel being filled from the goal.</summary>
    internal void Fill(SolidPrinciples principles) => IsOn = _principle.IsOn(principles);

    /// <summary>Writes this chip into a set of switches — the panel being read back.</summary>
    internal void Apply(SolidPrinciples principles) => _principle.SetOn(principles, IsOn);

    // The editor's own writer, which already declines to save while the panel is being filled. Nothing
    // here needs to know about that: this is an edit like any other on the panel.
    partial void OnIsOnChanged(bool value) => _changed();
}
