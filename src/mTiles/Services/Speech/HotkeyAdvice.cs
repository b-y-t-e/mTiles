using Avalonia.Input;

namespace mTiles.Services.Speech;

/// <summary>
/// What is worth telling the user about the shortcut they just chose.
/// </summary>
/// <remarks>
/// One sentence, in one place, because two places that say this would eventually say it differently —
/// and the Speech tab and the setup wizard both let somebody bind a shortcut. A statement rather than a
/// refusal: someone who deliberately gives <c>F13</c> to dictation is right, and the cost of a bare key
/// is real but theirs to accept.
/// </remarks>
internal static class HotkeyAdvice
{
    /// <summary>Null when there is nothing worth saying.</summary>
    public static string? For(HotkeyGesture gesture) =>
        gesture.Modifiers == KeyModifiers.None
            ? "Without a modifier, the shell stops seeing this key whenever dictation is ready to record."
            : null;

    /// <summary>
    /// The same advice with the desktop's answer folded in, which arrives later than the rest.
    /// </summary>
    /// <remarks>Both at once is a real combination — a bare key can be a launcher's too — and two
    /// sentences in the one slot is what keeps the page from having a second place where advice can
    /// appear, which is the whole reason this class exists.</remarks>
    public static string? For(HotkeyGesture gesture, ShortcutOwner? takenBy)
    {
        var advice = For(gesture);
        if (takenBy is not { } owner) return advice;

        return advice is null ? TakenBy(owner) : $"{advice} {TakenBy(owner)}";
    }

    /// <summary>A setting that names something the application cannot listen for.</summary>
    public const string Unparseable = "Not a shortcut this application can listen for.";

    /// <summary>
    /// The sentence for a shortcut the desktop itself has already given away.
    /// </summary>
    /// <remarks>
    /// <para>The one failure here that leaves nothing at all on screen: the shortcut is a handler on our
    /// own window, so a compositor that has taken the keys does not beat us to them — they never arrive,
    /// and the user is left holding two keys at an application nobody told. Naming the culprit is most
    /// of the value, because the fix is in somebody else's settings and they have to know whose.</para>
    /// <para>It says what to do in both directions, since neither is obviously right: a shortcut that
    /// somebody deliberately gave to their launcher is one they may want back, and a user who does not
    /// care which keys dictation uses wants the other sentence. Where the shortcut cannot be taken back
    /// at all — the Start menu on Windows is the shell's — the offer to go and unbind it is
    /// dropped rather than left standing, because it would send the user looking for a screen that does
    /// not exist.</para>
    /// </remarks>
    public static string TakenBy(ShortcutOwner owner) =>
        owner.CanBeFreed
            ? $"{owner.Name} already has this shortcut, so mTiles never receives it. "
              + "Choose another, or take it away in your desktop settings."
            : $"{owner.Name} has this shortcut and does not give it up, so mTiles never receives it. "
              + "Choose another.";

    /// <summary>
    /// The same advice, about a shortcut as it is stored — which is how it arrives from a file or from
    /// another window.
    /// </summary>
    /// <remarks>
    /// The three cases have to be answered together, and were not: the Speech tab worked them out in the
    /// property setter, which only runs when somebody edits the box. Loading the settings and returning
    /// from the setup wizard both write the backing field instead — deliberately, so neither saves
    /// everything back — so a shortcut that arrived either way kept whatever warning happened to be on
    /// screen. A bare key set in the wizard was accepted in silence, a warning from before it stayed up
    /// afterwards, and an unusable shortcut in the settings file opened the tab with nothing to say about
    /// why the feature was dead.
    /// </remarks>
    public static string? ForSetting(string? hotkey)
    {
        // Empty is a decision, not a failure to parse one: it is how the shortcut is switched off.
        if (string.IsNullOrWhiteSpace(hotkey))
            return null;

        return HotkeyGesture.TryParse(hotkey, out var gesture) ? For(gesture) : Unparseable;
    }
}
