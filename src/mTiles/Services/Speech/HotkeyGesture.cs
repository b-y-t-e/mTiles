using Avalonia.Input;

namespace mTiles.Services.Speech;

/// <summary>
/// A shortcut as the user writes it in settings — <c>Alt+Space</c>, <c>Ctrl+Shift+D</c>.
/// </summary>
/// <remarks>
/// Avalonia has <c>KeyGesture</c>, which parses the same strings, but this feature needs one thing it
/// does not offer: knowing which physical keys have to come <em>up</em> again for a push-to-talk to end.
/// That is why the modifiers are kept as keys as well, and why parsing is here and testable.
/// </remarks>
internal readonly record struct HotkeyGesture(KeyModifiers Modifiers, Key Key)
{
    private static readonly Dictionary<string, KeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = KeyModifiers.Control,
        ["control"] = KeyModifiers.Control,
        ["alt"] = KeyModifiers.Alt,
        ["shift"] = KeyModifiers.Shift,
        ["win"] = KeyModifiers.Meta,
        ["meta"] = KeyModifiers.Meta,
        ["cmd"] = KeyModifiers.Meta,
        ["super"] = KeyModifiers.Meta,
    };

    /// <summary>
    /// Parses <paramref name="text"/>. A gesture must name exactly one non-modifier key; anything else
    /// — no key, two keys, a lone modifier — is rejected rather than guessed at.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = KeyModifiers.None;
        Key? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 0)
                return false;

            if (ModifierNames.TryGetValue(token, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            // Enum.TryParse also accepts numbers: "Alt+207" parses to a Key that is not a key, and
            // "Alt+9999" to one that does not exist at all. Both then sit in the settings file as a
            // shortcut no keyboard can press — a feature switched off by a typo, with the box showing
            // something that looks configured. A name, or nothing.
            if (key is not null
                || !Enum.TryParse<Key>(token, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
                return false;
            // Modifiers, and `None` — which is a defined member and so survives every check above, while
            // naming no key at all. See IsBindable.
            if (!IsBindable(parsed))
                return false;

            key = parsed;
        }

        if (key is null)
            return false;

        gesture = new HotkeyGesture(modifiers, key.Value);
        return true;
    }

    /// <summary>True when this key event is the gesture being pressed.</summary>
    public bool MatchesPress(Key key, KeyModifiers modifiers) =>
        key == Key && modifiers == Modifiers;

    /// <summary>
    /// True when this key going up ends the gesture — either the key itself or one of the modifiers it
    /// needs held. Releasing Alt while still holding Space ends Alt+Space just as surely.
    /// </summary>
    public bool MatchesRelease(Key key)
    {
        if (key == Key)
            return true;

        return key switch
        {
            Key.LeftCtrl or Key.RightCtrl => Modifiers.HasFlag(KeyModifiers.Control),
            Key.LeftAlt or Key.RightAlt => Modifiers.HasFlag(KeyModifiers.Alt),
            Key.LeftShift or Key.RightShift => Modifiers.HasFlag(KeyModifiers.Shift),
            Key.LWin or Key.RWin => Modifiers.HasFlag(KeyModifiers.Meta),
            _ => false,
        };
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    /// <summary>
    /// Whether <paramref name="key"/> can be half of a shortcut at all.
    /// </summary>
    /// <remarks>
    /// <see cref="Key.None"/> is what the toolkit reports for a keystroke it could not map — a dead key,
    /// some IME output, a keyboard the platform layer has no virtual key for. It is a defined enum member,
    /// so every check that asks only whether the value is a real <see cref="Key"/> lets it through: bound,
    /// it shows in the wizard as the keycap <c>None</c> and in settings as <c>Alt+None</c>, and it then
    /// matches whatever *other* unmappable key the user presses next. The parser already refuses numbers
    /// for precisely this reason — "a shortcut no keyboard can press" — and this is the same hole one step
    /// along, reachable through the capture rather than through a hand-edited file.
    /// </remarks>
    public static bool IsBindable(Key key) => key != Key.None && !IsModifierKey(key);

    /// <summary>
    /// The keys to press, in the order they are written — <c>Alt</c>, <c>Space</c>.
    /// </summary>
    /// <remarks>
    /// The setup wizard shows a shortcut as one chip per key, because <c>Alt+Space</c> set in a text box
    /// reads as a configuration value while two keycaps read as an instruction — and the wizard's job is
    /// to get somebody to press it, not to describe it. Taken apart here rather than by splitting the
    /// string this method builds: the modifier names would then be decided in one place and re-derived in
    /// another, and <see cref="TryParse"/> accepts spellings (<c>Control</c>, <c>Cmd</c>, <c>Super</c>)
    /// that this list deliberately does not produce.
    /// <para>A method rather than a property because it builds a fresh list on each call. As a property it
    /// read as something the gesture <em>has</em>, which invites both indexing it in a loop — reallocating
    /// every time round — and writing to it, which compiles and silently does nothing.</para>
    /// </remarks>
    public IReadOnlyList<string> GetParts()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(Key.ToString());
        return parts;
    }

    public override string ToString() => string.Join('+', GetParts());
}
