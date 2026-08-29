using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mTiles.Services.Speech;
using mTiles.ViewModels;

namespace mTiles.Services.Phone;

/// <summary>
/// A key the phone's page is allowed to press.
/// </summary>
/// <remarks>
/// A closed set, not a key name off the wire. What arrives here has crossed a network from a device the
/// user paired once and may have left in a coat pocket, and the destination is a shell — so the set of
/// things it can say is decided in this process, at compile time, and everything else is nonsense that
/// gets no reply. Three is also what the page needs: answering the prompt an agent is waiting on, and
/// moving up and down the choices it is offering.
/// </remarks>
internal enum PhoneKey
{
    Enter,
    Up,
    Down,
}

/// <summary>
/// Presses one of those keys where the user was going to press it.
/// </summary>
/// <remarks>
/// <para><b>The same two destinations as a transcript, in the same order</b> (see
/// <see cref="DictationTextSink"/>): whatever text control has the keyboard, and otherwise the active
/// tile's terminal. Deliberately the same rule rather than a simpler one aimed straight at the terminal —
/// the two are used in one breath. You dictate a line and then press Enter to send it, and a key that
/// chose its target by a different rule than the text did would submit an empty prompt in one place while
/// the sentence sat in another.</para>
/// <para><b>A synthesised <see cref="InputElement.KeyDownEvent"/>, not bytes.</b> What Up means on the
/// wire depends on two modes the terminal control owns and does not expose — DECCKM (application cursor
/// keys, which every full-screen agent sets, turning <c>ESC [ A</c> into <c>ESC O A</c>) and
/// win32-input-mode, where keys travel as INPUT_RECORDs instead of VT sequences and where a bare
/// <c>\r</c> is not what the child is parsing. Handing it a key event lets it make that decision the one
/// way it makes it for the keyboard. It is also what makes a text box work at all: there is no string
/// that means "Enter" to a <see cref="TextBox"/>.</para>
/// <para>Only the key <em>down</em> is raised. Both consumers act on it and nothing here reads a key up —
/// the terminal's own encoder synthesises the down/up pair the child sees from this single event.</para>
/// </remarks>
internal static class PhoneKeys
{
    /// <summary>The wire names, which are the page's own words.</summary>
    public static bool TryParse(string? name, out PhoneKey key)
    {
        switch (name)
        {
            case "enter": key = PhoneKey.Enter; return true;
            case "up": key = PhoneKey.Up; return true;
            case "down": key = PhoneKey.Down; return true;
            default: key = default; return false;
        }
    }

    /// <summary>What each of them is, to a control that reads the keyboard.</summary>
    /// <remarks>
    /// Every member spelled out and the default throwing, rather than an <c>Enter</c> to fall back on.
    /// The set is closed at compile time — that is the doctrine this whole file is written to, and
    /// <see cref="TryParse"/> already keeps it — but a catch-all here quietly opted out of it: a fourth
    /// key added to the enum and to the wire names and missed in this one place would have been sent as
    /// <em>Enter</em>, which of the three is the one that cannot be taken back. It answers the prompt an
    /// agent is sitting on with whatever that prompt's default is.
    /// <para>A throw is safe to reach because the press is wrapped: <c>PhoneBridgeManager</c> catches it
    /// and the phone is told the key could not be delivered, which is the truth. Before that wrapper
    /// existed this would have cost the connection instead, which is presumably why it was not written
    /// this way the first time.</para>
    /// </remarks>
    public static Key ToAvalonia(PhoneKey key) => key switch
    {
        PhoneKey.Enter => Key.Enter,
        PhoneKey.Up => Key.Up,
        PhoneKey.Down => Key.Down,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No key is mapped for this."),
    };

    /// <summary>
    /// Presses the key at the first of the two destinations that will take it.
    /// </summary>
    /// <remarks>
    /// Both destinations are resolved by <see cref="DictationTextSink"/> rather than worked out again
    /// here. Not tidiness: the rule about <em>where</em> input from a phone lands has to be one rule, or
    /// the sentence and the Enter that submits it can part company — a key routed by a copy that had
    /// drifted would submit an empty prompt in one place while the words sat in another.
    /// </remarks>
    /// <returns>False when there was nowhere to press it.</returns>
    public static bool Press(LeafTileNodeViewModel? tile, PhoneKey key, IInputElement? focused = null)
        => Press(DictationTextSink.WritableTextTarget(focused), key)
        || Press(DictationTextSink.LiveTerminal(tile), key);

    private static bool Press(Interactive? target, PhoneKey key)
    {
        if (target is null)
            return false;

        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = target,
            Key = ToAvalonia(key),
            KeyModifiers = KeyModifiers.None,
        });

        return true;
    }
}
