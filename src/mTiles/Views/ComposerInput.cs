using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace mTiles.Views;

/// <summary>
/// How a box a message is typed into behaves — the one set of keys and gestures every conversation's
/// composer answers to (the Goal tile's and the Agent tile's), so a key cannot mean one thing in one tile
/// and another in the next.
/// </summary>
/// <remarks>
/// <para><b>Enter sends and Shift+Enter breaks the line, and that needs the tunnel.</b> A multi-line
/// <see cref="TextBox"/> takes Enter in its own class handler, inserts the line break and marks the key
/// handled, so a <c>KeyDown</c> wired in the markup — bubbling, handled events skipped — never sees it:
/// both keys broke the line and nothing was sent. Found by a real key press through a window
/// (<c>ComposerEnterTests</c>); calling the handler directly passed.</para>
/// <para><b>Enter is taken whether or not anything is sent.</b> What sending means is the tile's (an empty
/// Goal composer deliberately sends nothing); a line break on Enter is what no composer here wants.</para>
/// <para><b>The <c>@</c> list outranks it.</b> <see cref="FileMentionBehavior"/> takes Enter for the row
/// it has lit, and its handler can be registered after this one, so the list being open is asked
/// here too.</para>
/// </remarks>
public static class ComposerInput
{
    /// <summary>Wires <paramref name="box"/>.</summary>
    /// <param name="send">Sends what is in the box; the tile decides whether there is anything to send.</param>
    /// <param name="isPickingAFile">Whether the <c>@</c> suggestions are open.</param>
    /// <param name="frame">The border drawn round the box, when the box gives up its own: it shows the
    /// box's focus, and a click on it (its padding, the prompt glyph) puts the caret in the box.</param>
    /// <param name="pasteImage">Takes a pasted image (<see cref="ComposerPaste"/>); null where the box takes
    /// none.</param>
    /// <param name="pasteFiles">Takes files copied in a file manager and pasted here; null where the box
    /// takes none.</param>
    public static void Attach(TextBox box, Action send, Func<bool> isPickingAFile,
        Border? frame = null, Action<Bitmap>? pasteImage = null,
        Func<IReadOnlyList<Avalonia.Platform.Storage.IStorageItem>, Task>? pasteFiles = null)
    {
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (pasteImage is not null && ComposerPaste.TryPaste(box, e, pasteImage, pasteFiles)) return;
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || isPickingAFile()) return;

            e.Handled = true;
            send();
        }, RoutingStrategies.Tunnel);

        if (frame is null) return;

        void ShowFocus(object? sender, RoutedEventArgs e) => frame.Classes.Set("focused", box.IsFocused);
        box.GotFocus += ShowFocus;
        box.LostFocus += ShowFocus;

        frame.PointerPressed += (_, e) =>
        {
            // A click that lands on the box or a button already does the right thing.
            if (e.Source is Visual source &&
                (source.FindAncestorOfType<TextBox>(includeSelf: true) is not null ||
                 source.FindAncestorOfType<Button>(includeSelf: true) is not null))
                return;

            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        };
    }
}
