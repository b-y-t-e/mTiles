using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// The completion-criteria panel: the tile's <see cref="GoalCompletionCriteria"/> as something a form
/// can be bound to.
/// <para>Its own class because it is its own job. <see cref="GoalTileViewModel"/> runs a workflow,
/// keeps a transcript and owns a file; editing a handful of settings is none of those, and it was one
/// observable property and one change handler per setting sitting in the middle of them.</para>
/// <para>It exists at all because <see cref="GoalCompletionCriteria"/> is a DTO with no change
/// notification — the convention for everything under <c>Models/</c>. Mirroring is what gives each edit
/// somewhere to be written down: bound straight through, an edit would be held only in memory and lost
/// with the tile.</para>
/// </summary>
public partial class GoalCriteriaEditor : ObservableObject
{
    private readonly Func<GoalCompletionCriteria> _read;
    private readonly Action<GoalCompletionCriteria> _write;

    /// <param name="read">Where the criteria currently live — the engine, which the tile also saves.</param>
    /// <param name="write">What to do with an edit. Called for every keystroke in a text box, so the
    /// tile decides for itself whether that is worth a save.</param>
    public GoalCriteriaEditor(Func<GoalCompletionCriteria> read, Action<GoalCompletionCriteria> write)
    {
        _read = read;
        _write = write;
        Solid = [..SolidPrincipleCatalog.All.Select(p => new GoalSolidToggle(p, Changed))];
        Reload();
    }

    /// <summary>
    /// The five SOLID principles this goal is held to, in the order that spells the acronym.
    /// </summary>
    /// <remarks>
    /// Built once and refilled, never rebuilt: the row is bound to this list, and replacing it on every
    /// reload would throw away the control the user is in the middle of clicking. It is the same reason
    /// the number fields are properties rather than a regenerated form.
    /// </remarks>
    public IReadOnlyList<GoalSolidToggle> Solid { get; }

    [ObservableProperty] private int _maxIterations = 5;
    [ObservableProperty] private int _maxErrors;
    [ObservableProperty] private int _maxWarnings;
    [ObservableProperty] private bool _requireGoalMet = true;

    /// <summary>Whether the finished work has to leave the project building. See
    /// <see cref="GoalCompletionCriteria.RequireBuild"/> — it is a sentence in the prompts, not a
    /// command this tile runs.</summary>
    [ObservableProperty] private bool _requireBuild = true;

    /// <summary>Whether the finished work has to leave the tests passing. See
    /// <see cref="GoalCompletionCriteria.RequireTestsPass"/>.</summary>
    [ObservableProperty] private bool _requireTestsPass = true;

    /// <summary>
    /// Whether a finished run commits its own work by itself. See
    /// <see cref="GoalCompletionCriteria.CommitWhenDone"/>.
    /// </summary>
    /// <remarks>
    /// The only switch here that starts off, and the only one that writes to the user's history. Off
    /// does not mean the feature is unavailable: the same conditions put a Commit button in the
    /// summary, and either way the commit is confirmed in a dialog first.
    /// </remarks>
    [ObservableProperty] private bool _commitWhenDone;

    /// <summary>Empty unless the typed attempt count is outside what will actually be run. See
    /// <see cref="ShowClampNotes"/>.</summary>
    [ObservableProperty] private string _attemptsNote = "";

    /// <summary>Empty unless a typed tolerance is below zero, which reads as none. See
    /// <see cref="ShowClampNotes"/>.</summary>
    [ObservableProperty] private string _tolerancesNote = "";

    /// <summary>True while the fields are being filled from the criteria, so the setters below do not
    /// read their own writes back as edits and save once per field on load. No number in that sentence:
    /// it said seven, the fields have changed twice since, and a count is a fact about the method below
    /// that nothing keeps in step.</summary>
    private bool _filling;

    /// <summary>Fills the fields from the criteria as they now stand.</summary>
    public void Reload()
    {
        var c = _read();
        _filling = true;
        try
        {
            MaxIterations = c.MaxIterations;
            MaxErrors = c.MaxErrors;
            MaxWarnings = c.MaxWarnings;
            RequireGoalMet = c.RequireGoalMet;
            RequireBuild = c.RequireBuild;
            RequireTestsPass = c.RequireTestsPass;
            CommitWhenDone = c.CommitWhenDone;
            foreach (var chip in Solid) chip.Fill(c.Solid);
        }
        finally { _filling = false; }

        ShowClampNotes();
    }

    /// <summary>
    /// Puts the fields back to what the tile is really using, once the user has left one.
    /// <para>The notifications are raised by hand, and that is the entire method. Assigning the
    /// properties does nothing here: a failed string-to-int conversion means the property was
    /// <b>never set</b>, so it still holds the last good value, and assigning that value back is a
    /// no-op that <c>ObservableObject</c> quite correctly declines to raise a change for — leaving
    /// "50x" sitting in the box. The binding has to be told to re-read a source that did not move.</para>
    /// </summary>
    public void Refresh()
    {
        // Notifications only — deliberately *not* Reload. The properties already hold the last good
        // values, because a failed conversion never set them; all that is needed is telling the
        // bindings to read them again.
        // By name, one per number field. OnPropertyChanged(string.Empty) would say "all of them" and
        // remove the duplication, but only by convention — nothing in the type system says so, and
        // A_number_field_that_never_converted_is_redrawn_when_it_is_left cannot check for a name that is
        // not raised. A new number field has to be added here too, and that test is where forgetting it
        // shows up.
        OnPropertyChanged(nameof(MaxIterations));
        OnPropertyChanged(nameof(MaxErrors));
        OnPropertyChanged(nameof(MaxWarnings));
    }

    partial void OnMaxIterationsChanged(int value) => Changed();

    partial void OnMaxErrorsChanged(int value) => Changed();
    partial void OnMaxWarningsChanged(int value) => Changed();
    partial void OnRequireGoalMetChanged(bool value) => Changed();
    partial void OnRequireBuildChanged(bool value) => Changed();
    partial void OnRequireTestsPassChanged(bool value) => Changed();
    partial void OnCommitWhenDoneChanged(bool value) => Changed();

    /// <summary>
    /// One edit, written through to wherever the criteria live.
    /// <para>The values are stored exactly as typed. A field showing 0 while the run quietly used 1
    /// would be the panel lying about what it is doing; the bounds are applied where they are used
    /// instead — <see cref="GoalWorkflowEngine.MaxIter"/> and <see cref="GoalCompletionPolicy"/> —
    /// which is also the only place a value out of a <em>file</em> can be caught.</para>
    /// </summary>
    private void Changed()
    {
        if (_filling) return;

        _write(new GoalCompletionCriteria
        {
            MaxIterations = MaxIterations,
            MaxErrors = MaxErrors,
            MaxWarnings = MaxWarnings,
            RequireGoalMet = RequireGoalMet,
            RequireBuild = RequireBuild,
            RequireTestsPass = RequireTestsPass,
            CommitWhenDone = CommitWhenDone,
            Solid = SolidFromToggles(),
        });

        ShowClampNotes();
    }

    /// <summary>The chips as the model that goes to disk and into the prompts.</summary>
    private SolidPrinciples SolidFromToggles()
    {
        var principles = new SolidPrinciples();
        foreach (var chip in Solid) chip.Apply(principles);
        return principles;
    }

    /// <summary>
    /// Says, beside the field, where a typed number is not the number the run will use.
    /// <para>The value itself is left alone: snapping it back would fight anyone typing "10" one digit
    /// at a time. This is what stops the panel quietly disagreeing with the run instead.</para>
    /// </summary>
    private void ShowClampNotes()
    {
        var effective = GoalCompletionPolicy.Attempts(_read());
        AttemptsNote = effective == MaxIterations ? "" : $"using {effective}";
        TolerancesNote = MaxErrors < 0 || MaxWarnings < 0 ? "below zero reads as none" : "";
    }
}
