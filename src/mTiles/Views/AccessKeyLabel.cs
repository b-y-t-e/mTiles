using System.Linq;
using Avalonia.Input;

namespace mTiles.Views;

/// <summary>Marks the access key in a button's label — <c>Yes</c> into <c>_Yes</c>, so Alt+Y presses
/// it.</summary>
/// <remarks>A class of its own rather than two methods on <see cref="MessageDialog"/>: the rule is
/// pure and is the kind of thing that is argued in a table test, the same construction as
/// <c>ChooserNavigation</c> and <c>ModelSearch</c> — and a second dialog whose buttons are labelled
/// by its caller will want exactly this.</remarks>
public static class AccessKeyLabel
{
    /// <summary>The label with an access key marked in it: <c>Yes</c> becomes <c>_Yes</c>, so Alt+Y
    /// answers the dialog.</summary>
    /// <remarks>
    /// <para><b>Derived from the label rather than fixed as Y and N</b>, because these buttons are
    /// not always spelled that way: <c>ConfirmAsync</c> takes the two words from the caller, and
    /// today's callers say Discard, Delete, Unload and OK as often as Yes. A hard-coded pair would
    /// be wrong on half the dialogs in the application.</para>
    /// <para><b>The letter answers on its own as well as with Alt</b> (<see cref="Answers"/>), which
    /// is why the mark is shown from the start rather than only while Alt is held. What keeps a bare
    /// <c>D</c> from making a discard as cheap as a typo is not the modifier but the settling window:
    /// these dialogs open under somebody's typing, so <c>MessageDialog</c> refuses the one-finger
    /// answer for <c>MessageDialog.SettlingTime</c> after it opens, while Alt+letter, Enter and
    /// Escape — gestures nobody makes by accident — work from the first frame.</para>
    /// <para><paramref name="taken"/> is the other button's letter: two buttons offering the same
    /// access key is Avalonia cycling between them rather than pressing either, so the second one
    /// takes the first letter that is still free and goes without one if there is none — a missing
    /// mnemonic is a button reached by Tab, which every button here already is.</para>
    /// </remarks>
    public static string Mark(string label, char? taken) => Mark(label, [taken]);

    /// <summary><see cref="Mark(string, char?)"/> for a third button, whose letter has to avoid both of the
    /// others'.</summary>
    public static string Mark(string label, char? taken, char? takenToo) => Mark(label, [taken, takenToo]);

    private static string Mark(string label, char?[] taken)
    {
        // Whatever is already in the text is literal: an underscore in a label is a word the caller
        // wrote, not a mark, and left alone it would silently eat the following character.
        var literal = label.Replace("_", "__");

        var at = IndexOf(label, taken);
        if (at < 0)
            return literal;

        // The index moves by one for every underscore doubled ahead of it.
        var shift = label[..at].Count(c => c == '_');
        return literal.Insert(at + shift, "_");
    }

    /// <summary>Which letter <see cref="Mark"/> would mark, or null for none.</summary>
    /// <remarks>Takes the same <paramref name="taken"/> as <see cref="Mark"/> and must be asked with
    /// it: the two answering differently is a dialog underlining one letter and accepting another.
    /// </remarks>
    public static char? KeyOf(string label, char? taken = null) => KeyOf(label, [taken]);

    /// <summary><see cref="KeyOf(string, char?)"/> for a third button.</summary>
    public static char? KeyOf(string label, char? taken, char? takenToo) => KeyOf(label, [taken, takenToo]);

    private static char? KeyOf(string label, char?[] taken)
    {
        var at = IndexOf(label, taken);
        return at < 0 ? null : char.ToLowerInvariant(label[at]);
    }

    private static int IndexOf(string label, char?[] taken)
    {
        for (var i = 0; i < label.Length; i++)
        {
            if (!char.IsLetter(label[i]))
                continue;
            if (taken.Contains(char.ToLowerInvariant(label[i])))
                continue;
            return i;
        }

        return -1;
    }

    /// <summary>Whether <paramref name="key"/> is the bare letter that answers
    /// <paramref name="accessKey"/>.</summary>
    /// <remarks>
    /// <para>Only the unmodified letter keys: Alt+D already reaches the button through Avalonia's
    /// own access-key handling, and Ctrl+D or Shift+D are somebody else's gesture that this must
    /// not swallow.</para>
    /// <para>Latin letters and nothing else, because that is what <see cref="Key"/> can be asked
    /// about without a keyboard layout. A label whose first letter is outside it simply keeps the
    /// Alt route and Tab, which is what every button here has anyway.</para>
    /// </remarks>
    public static bool Answers(Key key, KeyModifiers modifiers, char? accessKey)
    {
        if (accessKey is not { } letter || modifiers != KeyModifiers.None)
            return false;
        if (key is < Key.A or > Key.Z)
            return false;

        return (char)('a' + (key - Key.A)) == char.ToLowerInvariant(letter);
    }
}
