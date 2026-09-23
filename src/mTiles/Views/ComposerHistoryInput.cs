using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mTiles.Controls;
using mTiles.Services;

namespace mTiles.Views;

/// <summary>
/// The history of a composer — Up and Down through what was sent, and a list of it behind a button — for
/// every conversation's composer (the Goal tile's and the Agent tile's), so the keys mean one thing in both.
/// </summary>
/// <remarks>
/// <para><b>The arrows move the caret first and the history second.</b> Up walks back only from the very start
/// of the box and Down forward only from its very end; Up anywhere on the first line goes to the start, and
/// Down anywhere on the last line to the end. So the caret never jumps a message while there is text above or
/// below it to move through, and two presses always reach the next message from anywhere on the edge line.</para>
/// <para><b>"The first line" is the one on screen, wrapping included</b>, and that is read off the box itself
/// rather than computed: the box is given the key first, and when it did not move the caret there was no line
/// to move to.</para>
/// <para><b>A walk back leaves the caret at the start and a walk forward at the end</b>, which is what makes
/// the next press of the same key go on walking.</para>
/// <para><b>Picking from the list is a step of the same walk</b>, so it keeps the draft as the arrows do.</para>
/// <para><b>An edit is noticed at the next step, not as it happens.</b> The walk ends when the box no longer
/// holds what the walk last put there. <see cref="TextBox.TextChanged"/> cannot say it: Avalonia posts that
/// event to the dispatcher, so it arrives after the step that set the text has finished and cannot be told
/// apart from a keystroke — every step then ended the walk it had just taken.</para>
/// </remarks>
public sealed class ComposerHistoryInput
{
    private readonly TextBox _box;
    private readonly Func<IReadOnlyList<string>> _sent;
    private readonly Func<bool> _isPickingAFile;
    private readonly ComposerHistory _history;
    private string? _shown;
    private IReadOnlyList<HistoryRow> _listed = [];
    private int? _caretBeforeTheBox;

    private ComposerHistoryInput(TextBox box, Func<IReadOnlyList<string>> entries, Func<bool> isPickingAFile)
    {
        _box = box;
        _sent = () => ComposerHistory.CollapseRepeats(entries());
        _isPickingAFile = isPickingAFile;
        _history = new ComposerHistory(_sent);
    }

    /// <param name="box">The composer.</param>
    /// <param name="entries">What was sent from it, oldest first.</param>
    /// <param name="isPickingAFile">Whether the <c>@</c> suggestions are open — they have the arrows then.</param>
    /// <param name="picker">The history button, when there is one.</param>
    public static void Attach(TextBox box, Func<IReadOnlyList<string>> entries, Func<bool> isPickingAFile,
        Picker? picker = null)
    {
        var input = new ComposerHistoryInput(box, entries, isPickingAFile);
        box.AddHandler(InputElement.KeyDownEvent, input.WalkOnTheEdge, RoutingStrategies.Tunnel);
        // After the box has had the key: a caret that did not move was on the edge line.
        box.AddHandler(InputElement.KeyDownEvent, input.MoveToTheEdge, RoutingStrategies.Bubble,
            handledEventsToo: true);
        if (picker is not null) input.AttachPicker(picker);
    }

    /// <summary>Ends the walk when the box holds something other than what the walk put there.</summary>
    private void EndWalkIfEdited()
    {
        if (_history.IsWalking && _box.Text != _shown) _history.Reset();
    }

    /// <summary>Up at the very start and Down at the very end walk the history; any other arrow is the box's.</summary>
    private void WalkOnTheEdge(object? sender, KeyEventArgs e)
    {
        _caretBeforeTheBox = null;
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Up or Key.Down) || _isPickingAFile()) return;
        EndWalkIfEdited();

        if (e.Key == Key.Up && IsCaretAtStart())
        {
            if (_history.Older(_box.Text ?? "") is { } older) Show(older, caretAtStart: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && IsCaretAtEnd() && _history.IsWalking)
        {
            if (_history.Newer() is { } newer) Show(newer, caretAtStart: false);
            e.Handled = true;
        }
        else _caretBeforeTheBox = _box.CaretIndex;
    }

    /// <summary>An arrow the box could not move the caret with goes to that end of the text instead.</summary>
    private void MoveToTheEdge(object? sender, KeyEventArgs e)
    {
        var before = _caretBeforeTheBox;
        _caretBeforeTheBox = null;
        if (e.Key is not (Key.Up or Key.Down) || before != _box.CaretIndex) return;
        _box.CaretIndex = e.Key == Key.Up ? 0 : _box.Text?.Length ?? 0;
        e.Handled = true;
    }

    private bool HasNoSelection => _box.SelectionStart == _box.SelectionEnd;

    private bool IsCaretAtStart() => _box.CaretIndex == 0 && HasNoSelection;

    private bool IsCaretAtEnd() => _box.CaretIndex >= (_box.Text?.Length ?? 0) && HasNoSelection;

    private void Show(string text, bool caretAtStart)
    {
        _shown = text;
        _box.Text = text;
        _box.ClearSelection();
        _box.CaretIndex = caretAtStart ? 0 : text.Length;
    }

    private void AttachPicker(Picker picker)
    {
        picker.OptionSelector = item => item is HistoryRow row
            ? new PickerOption { Id = row.Index.ToString(), Title = OneLine(row.Text), Keywords = row.Text }
            : null;
        picker.PropertyChanged += (_, e) =>
        {
            if (e.Property == Picker.IsDropDownOpenProperty && picker.IsDropDownOpen) picker.Options = _listed = NewestFirst();
        };
        picker.SelectionRequested += (_, e) => Pick(e.Option.Id);
    }

    /// <summary>Newest first, which is the order somebody looking for "what I just asked" reads in.</summary>
    private List<HistoryRow> NewestFirst() =>
        _sent().Select((text, index) => new HistoryRow(index, text)).Reverse().ToList();

    /// <summary>
    /// A message picked from the list is a jump in the same walk the arrows take, so the draft it replaced
    /// comes back with Down exactly as it would after walking there.
    /// </summary>
    /// <remarks>The row is the one listed when the list opened, and it is jumped to only while the history
    /// still holds that message at that place: a tile switched to another conversation meanwhile would
    /// otherwise answer with whatever that conversation has at the same position.</remarks>
    private void Pick(string optionId)
    {
        if (_listed.FirstOrDefault(row => row.Index.ToString() == optionId) is not { } row) return;
        if (!IsStillAt(row)) return;
        EndWalkIfEdited();
        if (_history.JumpTo(row.Index, _box.Text ?? "") is not { } picked) return;
        Show(picked, caretAtStart: false);
        _box.Focus();
    }

    private bool IsStillAt(HistoryRow row)
    {
        var sent = _sent();
        return row.Index < sent.Count && sent[row.Index] == row.Text;
    }

    private sealed record HistoryRow(int Index, string Text);

    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
