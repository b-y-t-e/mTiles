using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// The completion-criteria panel: the tile's <see cref="GoalCompletionCriteria"/> as something a form
/// can be bound to.
/// <para>Its own class because it is its own job. <see cref="GoalTileViewModel"/> runs a workflow,
/// keeps a transcript and owns a file; editing seven settings is none of those, and it was seven
/// observable properties, seven change handlers and three methods sitting in the middle of them.</para>
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
    [ObservableProperty] private string _verifyCommand = "";
    [ObservableProperty] private int _maxErrors;
    [ObservableProperty] private int _maxWarnings;
    [ObservableProperty] private bool _requireGoalMet = true;

    /// <summary>Empty unless the typed attempt count is outside what will actually be run. See
    /// <see cref="ShowClampNotes"/>.</summary>
    [ObservableProperty] private string _attemptsNote = "";

    /// <summary>Empty unless a typed tolerance is below zero, which reads as none. See
    /// <see cref="ShowClampNotes"/>.</summary>
    [ObservableProperty] private string _tolerancesNote = "";

    /// <summary>
    /// The verify command as the approval dialog would show it, but only when that differs from what
    /// the box is showing.
    /// </summary>
    /// <remarks>
    /// A text box cannot sanitise what it is editing, so it shows the raw string — including a
    /// right-to-left override that reverses everything after it, or a run of spaces long enough to push
    /// the rest out of sight. That is the same deception <see cref="CommandDisplay"/> exists to stop in
    /// the dialog, and it matters more here than it looks: editing this field is what makes a command
    /// "chosen", so a user deciding by reading the box is deciding on the version the dialog would
    /// refuse to show them.
    /// </remarks>
    public string VerifyCommandAsShown =>
        VerifyCommand.Length > 0 && !CommandDisplay.RendersHonestly(VerifyCommand)
            ? $"Runs as: {CommandDisplay.ForDialog(VerifyCommand)}"
            : "";

    public bool HasVerifyCommandNote => VerifyCommandAsShown.Length > 0;

    /// <summary>
    /// Whether this goal has a verify command at all — and therefore whether the panel shows a row for
    /// one.
    /// </summary>
    /// <remarks>
    /// <para>The row used to be there always, empty, asking the user to translate their own goal into
    /// a shell command and to know which command this project uses. It is written by the clarification
    /// round now, from the goal, by a tool standing in the repository — so an empty box is a question
    /// nobody is being asked any more, and the field went away with the question.</para>
    /// <para>It is still a <em>box</em> rather than a label whenever there is something in it. What
    /// arrives is a proposal, and the two things a user wants to do with a proposal — read it and
    /// change it — both need the text where it can be selected and edited. Emptying it is how one is
    /// refused, and typing in it is what <see cref="VerifyCommandWasTyped"/> is about.</para>
    /// </remarks>
    public bool HasVerifyCommand => VerifyCommand.Length > 0;

    /// <summary>
    /// Whether the panel shows the verify row at all: because there is a command, or because the user
    /// asked for the field.
    /// </summary>
    /// <remarks>
    /// <para>The second half is not a convenience. <c>verify</c> is read only out of a structured JSON
    /// clarification, and three of the four supported tools answer in prose often enough that the
    /// proposal simply never arrives — as it also never arrives for a repository whose build the tool
    /// cannot identify, or a goal about writing documentation. Hiding the field whenever nothing had
    /// been proposed therefore took a working feature away from those users entirely: no field, and no
    /// way to arm the one gate in this tile that is not a model's opinion of its own work.</para>
    /// <para>So the row is hidden, not removed, and one quiet button brings it back. The panel stays
    /// free of an empty box nobody is being asked to fill, which is what the row's disappearance was
    /// for, and the manual route survives for the users who need it most.</para>
    /// <para>Once shown it <b>stays</b> shown until the panel is reloaded, and that is not stickiness
    /// for its own sake: bound to whether the box has anything in it, the field vanished under the
    /// cursor the moment somebody held backspace to retype a command, taking the focus with it. A box
    /// being emptied on the way to a new value is not a box being refused, and nothing here can tell
    /// the two apart while the user is still typing. <see cref="Reload"/> is where the panel is filled
    /// from the goal, so that is where the question is asked again.</para>
    /// </remarks>
    public bool ShowVerifyRow => HasVerifyCommand || _verifyRowShown;

    /// <summary>Shown exactly when the row is not: the way back to a field this panel no longer offers
    /// by default.</summary>
    public bool CanAddVerifyCommand => !ShowVerifyRow;

    /// <summary>Whether this panel is currently showing the verify row — because a command arrived in
    /// it, or because the button below was clicked. Reset by <see cref="Reload"/> and by nothing
    /// else.</summary>
    private bool _verifyRowShown;

    /// <summary>Opens the verify row on an empty command, so it can be typed into.</summary>
    [RelayCommand]
    private void AddVerifyCommand()
    {
        _verifyRowShown = true;
        OnPropertyChanged(nameof(ShowVerifyRow));
        OnPropertyChanged(nameof(CanAddVerifyCommand));
    }

    /// <summary>True while the fields are being filled from the criteria, so the setters below do not
    /// read their own writes back as edits and save seven times on load.</summary>
    private bool _filling;

    /// <summary>
    /// Whether the verify command showing here is one the user typed in this session, rather than one
    /// that arrived in the saved file. The tile asks before running the second kind.
    /// <para>Recomputed on every edit against <see cref="_notTyped"/> rather than latched, and that is
    /// the difference between "they changed it" and "they touched it". Latched, a command out of the
    /// file gained consent from a keystroke and its undo: type a character into <c>rm -rf /</c>, delete
    /// it again, and the string about to be handed to a shell was unchanged while the dialog that
    /// guards it had gone. The flag is the one barrier between a file's contents and a shell, so it
    /// tracks the value and not the gesture.</para>
    /// </summary>
    public bool VerifyCommandWasTyped { get; private set; }

    /// <summary>The last verify command known to have arrived from somewhere other than this keyboard:
    /// the goal file, or the tile clearing it. Anything equal to it is not the user's choice, however it
    /// came to be in the box.</summary>
    private string _notTyped = "";

    /// <summary>Fills the fields from the criteria as they now stand.</summary>
    public void Reload()
    {
        var before = VerifyCommand;

        var c = _read();
        _filling = true;
        try
        {
            MaxIterations = c.MaxIterations;
            VerifyCommand = c.VerifyCommand;
            MaxErrors = c.MaxErrors;
            MaxWarnings = c.MaxWarnings;
            RequireGoalMet = c.RequireGoalMet;
            foreach (var chip in Solid) chip.Fill(c.Solid);
        }
        finally { _filling = false; }

        // Only when the command itself moved does a new value count as having arrived from outside.
        // Reload runs for reasons that have nothing to do with this box — Continue reloads it to show
        // the raised attempt ceiling — and treating that as an arrival turned a command the user had
        // typed a minute earlier back into one "from the file", asked them to approve their own command,
        // and deleted it on a no.
        if (!string.Equals(before, VerifyCommand, StringComparison.Ordinal))
        {
            _notTyped = VerifyCommand;
            VerifyCommandWasTyped = false;
        }

        // The one place the row closes. Reload is the panel being filled from the goal, so a goal with
        // no command gets no row — and a click on "+ verify command" that was never typed into does not
        // outlive the goal it was made for.
        _verifyRowShown = HasVerifyCommand;
        OnPropertyChanged(nameof(ShowVerifyRow));
        OnPropertyChanged(nameof(CanAddVerifyCommand));

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
        // bindings to read them again. Calling Reload here also cleared VerifyCommandWasTyped, and this
        // runs whenever the user leaves a number field — so typing a verify command and then tabbing
        // out of the attempts box turned it back into a command "from the file", asked about it, and
        // deleted it on a no.
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

    partial void OnVerifyCommandChanged(string value)
    {
        // Latched here, in the handler for the one field it is about, rather than worked out in
        // Changed() by comparing the box against the criteria. The comparison was reasoning about
        // values to answer a question about *provenance*, in the one flag standing between a file's
        // contents and a shell — and it ran in a handler shared by all seven fields, so every edit
        // anywhere had to be argued about before this could be trusted. Editing this box is the whole
        // of "the user typed it"; nothing else needs to be established.
        // Recomputed, not latched: see VerifyCommandWasTyped. Typing a character into a command that
        // came out of the file and deleting it again left the string unchanged and the gate open.
        //
        // An empty box is never a choice, and that half is a fix rather than a nicety. Emptying the
        // field is how a proposal is refused, and the row then disappears with it — so latching the
        // flag on "" left a tile that could not be given a command by anyone: the panel had no field to
        // type into, and AdoptVerifyCommandAsync stands down in front of a command the user typed, so
        // no later round could offer one either. One clearing disabled verification for the rest of the
        // session, for every goal after it, under a message promising the opposite. There is no command
        // here to have chosen.
        if (!_filling)
            VerifyCommandWasTyped =
                value.Length > 0 && !string.Equals(value, _notTyped, StringComparison.Ordinal);

        OnPropertyChanged(nameof(VerifyCommandAsShown));
        OnPropertyChanged(nameof(HasVerifyCommandNote));
        OnPropertyChanged(nameof(HasVerifyCommand));

        // A command arriving keeps the row open, so clearing the box to retype does not pull the
        // field out from under the cursor. It closes again at the next Reload, which is where the
        // panel is filled from the goal and the question is worth asking.
        if (value.Length > 0) _verifyRowShown = true;
        OnPropertyChanged(nameof(ShowVerifyRow));
        OnPropertyChanged(nameof(CanAddVerifyCommand));

        Changed();
    }
    partial void OnMaxErrorsChanged(int value) => Changed();
    partial void OnMaxWarningsChanged(int value) => Changed();
    partial void OnRequireGoalMetChanged(bool value) => Changed();

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
            VerifyCommand = VerifyCommand,
            MaxErrors = MaxErrors,
            MaxWarnings = MaxWarnings,
            RequireGoalMet = RequireGoalMet,
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
