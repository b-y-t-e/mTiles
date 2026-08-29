using Avalonia.Input;
using Avalonia.Interactivity;

namespace mTiles.ViewModels;

/// <summary>
/// What a <see cref="TileKey"/> is to a control that reads the keyboard, and how it is delivered.
/// </summary>
/// <remarks>
/// <para><b>One map, because a second one is a way to be wrong.</b> Both destinations a key can reach —
/// a focused text control and the active tile's own input surface (<see cref="ITextInputTile.TryPressKey"/>)
/// — need the same answer, and while each kept its own copy, adding a fourth key meant finding both.</para>
/// <para><b>A synthesised <see cref="InputElement.KeyDownEvent"/>, not bytes.</b> There is no string that
/// means "Enter" to a <c>TextBox</c>, and what Up means on the wire depends on two modes the terminal
/// control owns and does not expose — DECCKM (application cursor keys, which every full-screen agent
/// sets, turning <c>ESC [ A</c> into <c>ESC O A</c>) and win32-input-mode, where keys travel as
/// INPUT_RECORDs. Handing a control a key event lets it decide the one way it decides for the keyboard.
/// Only the key <em>down</em> is raised: both consumers act on it, and the terminal's own encoder
/// synthesises the down/up pair the child sees.</para>
/// </remarks>
public static class TileKeyPress
{
    /// <summary>Presses the key at a control.</summary>
    public static void At(Interactive target, TileKey key) =>
        target.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Source = target,
            Key = ToAvalonia(key),
            KeyModifiers = KeyModifiers.None,
        });

    /// <summary>What each key is, to a control that reads the keyboard.</summary>
    /// <remarks>
    /// Every member spelled out and the default throwing, rather than an <c>Enter</c> to fall back on.
    /// The set is closed at compile time — that is the doctrine <see cref="TileKey"/> is written to — but
    /// a catch-all here would quietly opt out of it: a fourth key added to the enum and missed in this
    /// one place would be sent as <em>Enter</em>, which of the three is the one that cannot be taken
    /// back — it answers the prompt an agent is sitting on with whatever that prompt's default is.
    /// <para>Safe to reach, because every press is wrapped: the caller reports a failure as "the key
    /// could not be delivered", which is the truth.</para>
    /// </remarks>
    private static Key ToAvalonia(TileKey key) => key switch
    {
        TileKey.Enter => Key.Enter,
        TileKey.Up => Key.Up,
        TileKey.Down => Key.Down,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No key is mapped for this."),
    };
}
