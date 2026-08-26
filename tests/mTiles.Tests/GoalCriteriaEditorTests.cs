using mTiles.Models;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The completion-criteria panel on its own. <see cref="GoalCriteriaEditor"/> is built from two lambdas
/// — where the criteria live and what to do with an edit — so none of this needs a tile, a settings
/// file, a temporary directory or a headless UI thread, and these used to spin up all four to ask
/// questions about a form.
/// </summary>
public class GoalCriteriaEditorTests
{
    /// <summary>An editor over a criteria object that nothing else is looking at.</summary>
    private static GoalCriteriaEditor Editor()
    {
        var criteria = new GoalCompletionCriteria();
        return new GoalCriteriaEditor(() => criteria, c => criteria = c);
    }

    /// <summary>
    /// A number the run will not honour is said out loud rather than left sitting in the box looking
    /// like a setting. The tile clamps what it is given; without the note, a user who typed 999 was
    /// shown 999 and got 50, and one who typed -1 was shown a tolerance nothing was applying.
    /// </summary>
    [Fact]
    public void An_attempt_count_outside_what_will_run_is_shown_as_such()
    {
        var criteria = Editor();

        Assert.Equal("", criteria.AttemptsNote);
        Assert.Equal("", criteria.TolerancesNote);

        criteria.MaxErrors = -1;
        Assert.Equal("below zero reads as none", criteria.TolerancesNote);
        criteria.MaxErrors = 0;
        Assert.Equal("", criteria.TolerancesNote);

        criteria.MaxIterations = 999;
        Assert.Equal("using 50", criteria.AttemptsNote);

        criteria.MaxIterations = 0;
        Assert.Equal("using 1", criteria.AttemptsNote);

        criteria.MaxIterations = 3;
        Assert.Equal("", criteria.AttemptsNote);
    }

    /// <summary>
    /// What happens to junk left in a number field when it loses focus.
    /// <para>"50x" never reaches the property — Avalonia reports a failed conversion as a binding error,
    /// so the setter is not called at all and the field still holds what it held. Assigning the same
    /// value back is a no-op that ObservableObject quite correctly raises nothing for, which is why
    /// <see cref="GoalCriteriaEditor.Refresh"/> has to notify by hand: the binding must be told to
    /// re-read a source that did not move, or the junk stays on screen looking like a setting.</para>
    /// </summary>
    [Fact]
    public void A_number_field_that_never_converted_is_redrawn_when_it_is_left()
    {
        var criteria = Editor();
        criteria.MaxIterations = 7;

        var notified = 0;
        criteria.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(criteria.MaxIterations)) notified++;
        };

        criteria.Refresh();

        Assert.True(notified > 0);
        Assert.Equal(7, criteria.MaxIterations);
    }
    /// <summary>
    /// Emptying the box is a refusal, not a choice — so it must not count as one.
    /// </summary>
    /// <remarks>
    /// The row disappears with the command, so the panel then has no field to type into; and
    /// <c>AdoptVerifyCommandAsync</c> stands down in front of a command the user typed, so no later
    /// clarification round would offer one either. Latching the flag on an empty string therefore
    /// disabled verification for the rest of the session, for every goal after it, under a message
    /// telling the user to say what has to pass in the goal instead. There is no command here to have
    /// been chosen.
    /// </remarks>
    [Fact]
    public void Clearing_the_command_is_not_the_user_choosing_one()
    {
        var criteria = new GoalCompletionCriteria { VerifyCommand = "npm test" };
        var editor = new GoalCriteriaEditor(() => criteria, c => criteria = c);

        // Arrived from outside, so it needs consent and was not typed.
        Assert.False(editor.VerifyCommandWasTyped);

        editor.VerifyCommand = "dotnet test";
        Assert.True(editor.VerifyCommandWasTyped);

        editor.VerifyCommand = "";
        Assert.False(editor.VerifyCommandWasTyped);
        Assert.False(editor.HasVerifyCommand);
    }
    /// <summary>
    /// A box being emptied on the way to a new value is not a box being refused.
    /// </summary>
    /// <remarks>
    /// Bound to whether the box has anything in it, the row vanished under the cursor the moment
    /// somebody held backspace to retype a command, taking the focus with it. Nothing here can tell
    /// "clearing to retype" from "clearing to drop" while the user is still typing, so the row stays
    /// until the panel is filled from the goal again.
    /// </remarks>
    [Fact]
    public void Clearing_the_box_to_retype_does_not_take_the_field_away()
    {
        var criteria = new GoalCompletionCriteria { VerifyCommand = "npm test" };
        var editor = new GoalCriteriaEditor(() => criteria, c => criteria = c);

        Assert.True(editor.ShowVerifyRow);

        editor.VerifyCommand = "";

        Assert.False(editor.HasVerifyCommand);
        Assert.True(editor.ShowVerifyRow);
        Assert.False(editor.CanAddVerifyCommand);

        editor.VerifyCommand = "dotnet test";
        Assert.Equal("dotnet test", criteria.VerifyCommand);
    }

    /// <summary>
    /// A goal with no command gets no row, and a click that was never typed into does not outlive the
    /// goal it was made for.
    /// </summary>
    [Fact]
    public void Reloading_from_a_goal_without_a_command_closes_the_row()
    {
        var criteria = new GoalCompletionCriteria();
        var editor = new GoalCriteriaEditor(() => criteria, c => criteria = c);

        Assert.False(editor.ShowVerifyRow);
        Assert.True(editor.CanAddVerifyCommand);

        editor.AddVerifyCommandCommand.Execute(null);
        Assert.True(editor.ShowVerifyRow);

        editor.Reload();
        Assert.False(editor.ShowVerifyRow);
        Assert.True(editor.CanAddVerifyCommand);
    }
}
