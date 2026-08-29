using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using mTiles.Models;
using mTiles.ViewModels;
using Terminal.Avalonia;

namespace mTiles.Services.Speech;

/// <summary>
/// Puts a transcript where the user was going to type it.
/// </summary>
/// <remarks>
/// Two destinations, tried in that order: whatever text control has the keyboard, and otherwise the
/// tile's terminal. The first covers a note, a commit message or the Goal tile's prompt box without
/// this class knowing any of them exist; the second is the case the feature was built for, where the
/// thing reading the keyboard is a shell.
/// <para><c>AutoSubmitEnter</c> applies to the terminal only, deliberately. A carriage return sent to a
/// shell runs the line; typed into a text box it is a newline in the middle of somebody's note, and
/// there is no general way to press a dialog's OK button from here. The setting is worded as submitting
/// a command for that reason.</para>
/// </remarks>
internal static class DictationTextSink
{
    /// <returns>False when there was nowhere to put the text.</returns>
    public static bool Insert(LeafTileNodeViewModel? tile, string text, SpeechSettings settings,
        IInputElement? focused = null)
    {
        var payload = Compose(text, settings);

        // Nothing left to insert is not a failure to insert, and the difference is the message the user
        // gets. False here is reported as "there was nowhere to put the text" — sending them to look at a
        // tile that closed or a shell that exited, when in fact the destination was fine and the
        // transcript was a cough. The service already treats an empty transcript as a silence worth
        // nothing more than a trace line; sanitising can produce the same emptiness one step later (a
        // result of nothing but control characters survives its whitespace check and not this one), and
        // it deserves the same answer rather than the opposite one.
        if (payload.Length == 0)
        {
            Trace.WriteLine("[speech] nothing was left to insert after sanitising the transcript");
            return true;
        }

        if (InsertIntoTextControl(focused, payload))
            return true;

        return SendToTerminal(tile, payload, settings.AutoSubmitEnter);
    }

    /// <summary>
    /// What actually gets typed: the transcript, sanitised, with the trailing space if it was asked for.
    /// </summary>
    /// <remarks>
    /// Separate from the routing so it can be tested. Where the text goes depends on Avalonia's focus and
    /// on a live terminal; <em>what</em> goes there is arithmetic on a string, and it is the part that
    /// decides whether a command runs.
    /// </remarks>
    internal static string Compose(string text, SpeechSettings settings)
    {
        var payload = Sanitize(text);
        if (payload.Length == 0)
            return "";

        return settings.AppendTrailingSpace ? payload + " " : payload;
    }

    /// <summary>
    /// Whether a control captured earlier is still somewhere the user can see.
    /// </summary>
    /// <remarks>
    /// The focused element is captured when the key goes down and used seconds later, by which time its
    /// dialog may have closed or its tile been removed — writing into a detached control puts the text
    /// somewhere nobody can see, and returning true then reports that as delivered, which is the one
    /// thing the caller must not be told.
    /// <para>Attached <em>and</em> visible. A window that has been hidden rather than closed keeps its
    /// tree, so the ancestor check alone passes for a control nobody can see — which is exactly what the
    /// settings dialog is: an overlay that is hidden, not removed.</para>
    /// <para>Shared with <see cref="Phone.PhoneKeys"/>, which routes a key press from a phone by the same
    /// rule so that the key lands where the transcript before it did.</para>
    /// </remarks>
    internal static bool IsOnScreen(IInputElement? focused) =>
        focused is not Visual visual
        || (visual.FindAncestorOfType<TopLevel>(includeSelf: true) is not null && visual.IsEffectivelyVisible);

    /// <summary>
    /// The focused control when it is one a transcript may be typed into, and null otherwise.
    /// </summary>
    /// <remarks>
    /// <para><b>Which controls count is decided here and nowhere else.</b> The rule has two consumers —
    /// this class, which inserts text into whatever comes back, and <see cref="Phone.PhoneKeys"/>, which
    /// raises a key at it — and they are used in one breath: you dictate a line from a phone and then
    /// press Enter to send it. A key that chose its destination by a different rule than the text did
    /// would submit an empty prompt in one place while the sentence sat in another, which is the one
    /// failure the shared destination exists to rule out. Held apart as two copies of a switch it would
    /// have drifted the first time a third kind of text control was added to one of them.</para>
    /// <para>Read-only controls are refused rather than pressed at, so the key or the sentence falls
    /// through to the terminal instead of being swallowed by a diff view — half the text in this
    /// application is in a read-only editor.</para>
    /// <para>Returned as <see cref="Interactive"/> because that is all both callers need: one raises an
    /// event at it, the other asks what it is. What is <em>done</em> with it is genuinely different work
    /// and stays where it is done.</para>
    /// </remarks>
    internal static Interactive? WritableTextTarget(IInputElement? focused)
    {
        if (!IsOnScreen(focused))
            return null;

        return focused switch
        {
            TextBox { IsReadOnly: false } box => box,
            TextEditor { IsReadOnly: false } editor => editor,
            _ => null,
        };
    }

    /// <summary>
    /// The tile's terminal, when there is one and its shell is still running.
    /// </summary>
    /// <remarks>
    /// Shared with <see cref="Phone.PhoneKeys"/> for the reason <see cref="WritableTextTarget"/> is, and
    /// this half shares cleanly: both callers want the same control under the same liveness test, and
    /// neither wants anything else. A dead terminal is refused rather than typed at — text sent to a
    /// shell that has exited goes nowhere, and saying so is the difference between the phone showing a
    /// reason and the user pressing again.
    /// </remarks>
    internal static TerminalControl? LiveTerminal(LeafTileNodeViewModel? tile)
    {
        if (tile?.Content is not TerminalTileViewModel terminalTile)
            return null;

        return terminalTile.CachedControl is TerminalControl terminal && terminal.IsRunning
            ? terminal
            : null;
    }

    private static bool InsertIntoTextControl(IInputElement? focused, string text)
    {
        // The switch below is a second dispatch on purpose: *which* control is a destination is the
        // shared rule above, and *how* text is inserted into it is not — a selection dance in a TextBox
        // and a document insert in an editor have nothing in common. A type reaching the resolver and
        // not this switch falls through to false, which is the same answer as no destination at all.
        switch (WritableTextTarget(focused))
        {
            // Selected text is replaced, not left in place with the transcript pushed in beside it —
            // typing is what dictation stands in for, and typing overwrites a selection. Getting this
            // wrong is silent: the user selects a sentence to say again, and ends up with both.
            case TextBox box:
            {
                var length = (box.Text ?? "").Length;
                var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, length);
                var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, length);
                if (end == start)
                    start = end = Math.Clamp(box.CaretIndex, 0, length);

                // Through the selection rather than by assigning Text: this is the path the control uses
                // for typing, so whatever it does about undo, clamping and MaxLength happens to a
                // dictated sentence too, without this class having to know what any of that is.
                // Collapsing the selection first is what makes it an insertion at the caret when nothing
                // was selected. (Undo survives either way — measured, both forms restore the previous
                // text — so this is about staying on the control's own path, not a defect repaired.)
                box.SelectionStart = start;
                box.SelectionEnd = end;
                box.SelectedText = text;
                box.CaretIndex = start + text.Length;
                return true;
            }
            case TextEditor editor:
            {
                if (editor.SelectionLength > 0)
                    editor.SelectedText = text;
                else
                    editor.Document.Insert(editor.CaretOffset, text);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool SendToTerminal(LeafTileNodeViewModel? tile, string text, bool submit) =>
        LiveTerminal(tile) is { } terminal && Type(terminal.SendText, text, submit);

    /// <summary>
    /// Hands the payload to whatever types into a shell, adding the carriage return if one was asked for.
    /// </summary>
    /// <remarks>
    /// <para>Split from resolving the terminal so it can be tested: this is the only branch in the
    /// feature that can <em>run a command</em>, and until this seam existed it had no test at all —
    /// <see cref="Compose"/> never touches <c>\r</c>, so nothing that tested composing came near it.</para>
    /// <para>The return goes on separately and only when asked. <see cref="Sanitize"/> has already turned
    /// every control character in the transcript into a space, so this is the one <c>\r</c> that can
    /// reach the child, and it is there because the user asked for it rather than because a model heard
    /// a newline.</para>
    /// </remarks>
    internal static bool Type(Func<string, bool> send, string text, bool submit) =>
        send(submit ? text + "\r" : text);

    /// <summary>
    /// Strips everything a console would read as a command rather than as characters.
    /// <para>Control bytes are the danger: 0x03 on the way to a shell is not the text "^C", it is an
    /// interrupt for the whole pseudo-console, and a newline is a submitted command line. A transcript
    /// is text the user dictated, not a script, so it may contain neither.</para>
    /// </summary>
    internal static string Sanitize(string text)
    {
        var result = new StringBuilder(text.Length);
        var lastWasSpace = false;

        foreach (var c in text)
        {
            var isSpace = char.IsWhiteSpace(c) || char.IsControl(c);
            if (isSpace)
            {
                if (result.Length > 0 && !lastWasSpace)
                    result.Append(' ');
                lastWasSpace = true;
                continue;
            }

            result.Append(c);
            lastWasSpace = false;
        }

        return result.ToString().TrimEnd();
    }
}
