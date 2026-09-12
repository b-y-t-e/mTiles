using Avalonia.Input;

namespace mTiles.Services.Speech;

/// <summary>
/// One shortcut, written the way each desktop writes it — so it can be asked whether it already has one.
/// </summary>
/// <remarks>
/// <para>Pure, and apart from the asking for the reason <c>ChainPolicy</c> is apart from the chain: the
/// three spellings are somebody else's convention, they are what a wrong answer would come from, and
/// they can be read in a table rather than by installing three desktops. <see cref="DesktopShortcuts"/>
/// is the half that runs programs.</para>
/// <para><b>Null is "this desktop has no way to say it", never a guess.</b> A key nothing here can
/// spell — a media key, something the toolkit maps oddly — means the question is not asked at all, which
/// leaves the user with the empirical answer the setup wizard already gives them. A spelling invented
/// for it would be asked about a shortcut nobody has, and answered "free" with great confidence.</para>
/// </remarks>
internal static class ShortcutSpelling
{
    // Qt's own numbering (qnamespace.h), which is what KGlobalAccel's D-Bus API takes: a key code with
    // the modifier bits or-ed into the top of it.
    private const int QtShift = 0x02000000;
    private const int QtControl = 0x04000000;
    private const int QtAlt = 0x08000000;
    private const int QtMeta = 0x10000000;

    /// <summary>
    /// The gesture as one Qt key code, for KDE's <c>getGlobalShortcutsByKey</c>.
    /// </summary>
    /// <remarks>Verified 2026-09-12 against Plasma 6 on this machine: <c>Alt+Space</c> is
    /// <c>134217760</c> (<c>0x08000020</c>), which the service answers for with KRunner.</remarks>
    public static int? QtKeyCode(HotkeyGesture gesture)
    {
        if (QtKey(gesture.Key) is not { } key) return null;

        var modifiers = 0;
        if (gesture.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers |= QtShift;
        if (gesture.Modifiers.HasFlag(KeyModifiers.Control)) modifiers |= QtControl;
        if (gesture.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers |= QtAlt;
        if (gesture.Modifiers.HasFlag(KeyModifiers.Meta)) modifiers |= QtMeta;

        return key | modifiers;
    }

    /// <remarks>The three contiguous runs are arithmetic rather than three tables, and they are safe to
    /// treat that way: both enums are laid out in the same order, letters after digits after function
    /// keys, and both have been since they were written. Everything else is named.</remarks>
    private static int? QtKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => 0x41 + (key - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (key - Key.D0),
        >= Key.F1 and <= Key.F24 => 0x01000030 + (key - Key.F1),
        Key.Space => 0x20,
        Key.Escape => 0x01000000,
        Key.Tab => 0x01000001,
        Key.Back => 0x01000003,
        Key.Return => 0x01000004,
        Key.Insert => 0x01000006,
        Key.Delete => 0x01000007,
        Key.Home => 0x01000010,
        Key.End => 0x01000011,
        Key.Left => 0x01000012,
        Key.Up => 0x01000013,
        Key.Right => 0x01000014,
        Key.Down => 0x01000015,
        Key.PageUp => 0x01000016,
        Key.PageDown => 0x01000017,
        _ => null,
    };

    /// <summary>
    /// The key's X keysym name, which is what GNOME stores and what Hyprland's config uses.
    /// </summary>
    /// <remarks>Letters and digits come back as the bare character, because that is how both write them
    /// — <c>&lt;Alt&gt;t</c>, not <c>&lt;Alt&gt;T</c>, the shift being a modifier rather than a case.
    /// </remarks>
    public static string? KeySymName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => $"F{1 + (key - Key.F1)}",
        Key.Space => "space",
        Key.Escape => "Escape",
        Key.Tab => "Tab",
        Key.Back => "BackSpace",
        Key.Return => "Return",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.Left => "Left",
        Key.Up => "Up",
        Key.Right => "Right",
        Key.Down => "Down",
        Key.PageUp => "Page_Up",
        Key.PageDown => "Page_Down",
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="accelerator"/>, as GNOME stores it, is this gesture.
    /// </summary>
    /// <remarks>
    /// <para>Compared as a parsed pair rather than as strings, because the same gesture has several
    /// legal spellings: the modifiers may be in any order, <c>&lt;Primary&gt;</c> is Control's other
    /// name, and <c>&lt;Super&gt;</c>, <c>&lt;Meta&gt;</c> and <c>&lt;Mod4&gt;</c> are one key. Sample
    /// read on this machine: <c>org.gnome.desktop.wm.keybindings activate-window-menu
    /// ['&lt;Alt&gt;space']</c> — GNOME takes Alt+Space for the window menu exactly as Windows does.
    /// </para>
    /// <para>A modifier this does not know makes the whole accelerator <em>not</em> a match, rather than
    /// one that is ignored: an unknown token means a different shortcut, and a match claimed on the
    /// strength of the part we understood would name the wrong owner.</para>
    /// </remarks>
    public static bool MatchesGnomeAccelerator(HotkeyGesture gesture, string accelerator)
    {
        if (KeySymName(gesture.Key) is not { } expected) return false;

        var modifiers = KeyModifiers.None;
        var rest = accelerator.Trim();

        while (rest.StartsWith('<'))
        {
            var close = rest.IndexOf('>');
            if (close < 0) return false;

            var name = rest[1..close];
            rest = rest[(close + 1)..];

            switch (name.ToLowerInvariant())
            {
                case "shift": modifiers |= KeyModifiers.Shift; break;
                case "control" or "ctrl" or "primary": modifiers |= KeyModifiers.Control; break;
                case "alt" or "mod1": modifiers |= KeyModifiers.Alt; break;
                case "super" or "meta" or "mod4": modifiers |= KeyModifiers.Meta; break;
                default: return false;
            }
        }

        return modifiers == gesture.Modifiers
            && string.Equals(rest, expected, StringComparison.OrdinalIgnoreCase);
    }

    // Hyprland's modmask, from its own list: SHIFT 1, CAPS 2, CTRL 4, ALT 8, MOD2 16, MOD3 32,
    // SUPER 64, MOD5 128.
    private const int HyprShift = 1;
    private const int HyprControl = 4;
    private const int HyprAlt = 8;
    private const int HyprSuper = 64;

    /// <summary>
    /// Whether a bind Hyprland reports — a mod mask and a key name — is this gesture.
    /// </summary>
    /// <remarks><b>Unmeasured</b>, unlike the two above: there is no Hyprland on the machine this was
    /// written on, so the mask values and the key spelling are read from its documentation rather than
    /// from a running compositor. It fails the safe way if they are wrong — a bind that does not match
    /// is one this never mentions, which leaves the user exactly where they were.</remarks>
    public static bool MatchesHyprlandBind(HotkeyGesture gesture, int modMask, string key)
    {
        if (KeySymName(gesture.Key) is not { } expected) return false;

        var modifiers = KeyModifiers.None;
        if ((modMask & HyprShift) != 0) modifiers |= KeyModifiers.Shift;
        if ((modMask & HyprControl) != 0) modifiers |= KeyModifiers.Control;
        if ((modMask & HyprAlt) != 0) modifiers |= KeyModifiers.Alt;
        if ((modMask & HyprSuper) != 0) modifiers |= KeyModifiers.Meta;

        return modifiers == gesture.Modifiers
            && string.Equals(key, expected, StringComparison.OrdinalIgnoreCase);
    }
}
