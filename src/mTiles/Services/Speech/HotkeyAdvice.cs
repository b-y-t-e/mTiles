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

    /// <summary>A setting that names something the application cannot listen for.</summary>
    public const string Unparseable = "Not a shortcut this application can listen for.";

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
