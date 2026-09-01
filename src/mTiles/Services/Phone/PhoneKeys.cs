using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mTiles.Services.Speech;
using mTiles.ViewModels;

namespace mTiles.Services.Phone;

/// <summary>
/// Presses one of the keys the phone's page offers, where the user was going to press it.
/// </summary>
/// <remarks>
/// <para><b>The same two destinations as a transcript, in the same order</b> (see
/// <see cref="DictationTextSink"/>): whatever text control has the keyboard, and otherwise the active
/// tile's own input surface. Deliberately the same rule rather than a simpler one aimed straight at the
/// terminal — the two are used in one breath. You dictate a line and then press Enter to send it, and a
/// key that chose its target by a different rule than the text did would submit an empty prompt in one
/// place while the sentence sat in another.</para>
/// <para><b>The set is closed in this process, not by the message.</b> What arrives here has crossed a
/// network from a device the user paired once and may have left in a coat pocket, and the destination is
/// a shell — so <see cref="TryParse"/> is the whole of what a phone may say, and anything else is
/// nonsense that gets no reply. What each key <em>is</em> to a control that reads the keyboard is the
/// tile's business rather than this class's, because the answer depends on modes the terminal control
/// owns and does not expose; see <see cref="ITextInputTile.TryPressKey"/>.</para>
/// </remarks>
internal static class PhoneKeys
{
    /// <summary>The wire names, which are the page's own words.</summary>
    public static bool TryParse(string? name, out TileKey key)
    {
        switch (name)
        {
            case "enter": key = TileKey.Enter; return true;
            case "up": key = TileKey.Up; return true;
            case "down": key = TileKey.Down; return true;
            case "left": key = TileKey.Left; return true;
            case "right": key = TileKey.Right; return true;
            case "escape": key = TileKey.Escape; return true;
            default: key = default; return false;
        }
    }

    /// <summary>
    /// Presses the key at the first of the two destinations that will take it.
    /// </summary>
    /// <returns>False when there was nowhere to press it.</returns>
    public static bool Press(LeafTileNodeViewModel? tile, TileKey key, IInputElement? focused = null)
        => PressAtControl(DictationTextSink.WritableTextTarget(focused), key)
        || DictationTextSink.TileInput(tile)?.TryPressKey(key) == true;

    /// <summary>
    /// Presses it at a focused text control.
    /// </summary>
    /// <remarks>
    /// Through <see cref="TileKeyPress"/>, the one place that says what a <see cref="TileKey"/> is to a
    /// control that reads the keyboard: a focused <see cref="TextBox"/> and a tile's own input surface
    /// are two destinations for the same keystroke, and while each had its own map, a key added to the
    /// enum had to be found twice.
    /// </remarks>
    private static bool PressAtControl(Interactive? target, TileKey key)
    {
        if (target is null)
            return false;

        TileKeyPress.At(target, key);
        return true;
    }
}
