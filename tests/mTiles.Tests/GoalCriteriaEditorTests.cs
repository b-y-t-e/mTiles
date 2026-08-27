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
    /// The two health checks are on unless the user says otherwise, and each one writes through on its
    /// own. They are prompt text rather than a gate, so nothing else in this panel can catch a switch
    /// that reads or writes the wrong one.
    /// </summary>
    [Fact]
    public void The_health_checks_default_to_on_and_write_through_one_at_a_time()
    {
        var criteria = new GoalCompletionCriteria();
        var editor = new GoalCriteriaEditor(() => criteria, c => criteria = c);

        Assert.True(editor.RequireBuild);
        Assert.True(editor.RequireTestsPass);

        editor.RequireTestsPass = false;
        Assert.False(criteria.RequireTestsPass);
        Assert.True(criteria.RequireBuild);

        editor.RequireBuild = false;
        Assert.False(criteria.RequireBuild);

        // And back from the criteria, on the same objects the row is bound to.
        criteria.RequireBuild = true;
        editor.Reload();
        Assert.True(editor.RequireBuild);
        Assert.False(editor.RequireTestsPass);
    }
}
