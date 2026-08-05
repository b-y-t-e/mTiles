using Avalonia;
using Avalonia.Media;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// The single translation of a <see cref="TerminalTheme"/> into colours: the terminal's own palette
/// (<see cref="ToPalette"/>) and the application's UI resources (<see cref="Apply"/>), which are derived
/// from the same background, foreground and accents so the chrome always matches what the shell renders.
/// <para>Dark or light follows <c>TerminalTheme.IsDark</c> — there is no separate UI theme to keep in
/// step, by design.</para>
/// </summary>
public static class ThemeBridge
{
    /// <summary>
    /// The same theme as the terminal control reads it: our hex strings mapped onto its palette.
    /// <para>Here rather than in the terminal tile's view because this is the one place that knows how
    /// a <see cref="TerminalTheme"/> becomes colours — the UI resources below are the other half of the
    /// same mapping, and they went out of step when the two lived apart.</para>
    /// </summary>
    public static Terminal.Avalonia.TerminalTheme ToPalette(TerminalTheme theme) => new(
        background: Color.Parse(theme.Background),
        foreground: Color.Parse(theme.Foreground),
        cursor: Color.Parse(theme.Cursor),
        selection: Color.Parse(theme.Selection),
        ansi16:
        [
            Color.Parse(theme.Black), Color.Parse(theme.Red), Color.Parse(theme.Green), Color.Parse(theme.Yellow),
            Color.Parse(theme.Blue), Color.Parse(theme.Magenta), Color.Parse(theme.Cyan), Color.Parse(theme.White),
            Color.Parse(theme.BrightBlack), Color.Parse(theme.BrightRed), Color.Parse(theme.BrightGreen),
            Color.Parse(theme.BrightYellow), Color.Parse(theme.BrightBlue), Color.Parse(theme.BrightMagenta),
            Color.Parse(theme.BrightCyan), Color.Parse(theme.BrightWhite),
        ]);

    public static void Apply(TerminalTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        var bg = Color.Parse(theme.Background);
        var fg = Color.Parse(theme.Foreground);
        var blue = Color.Parse(theme.Blue);
        var brightBlue = Color.Parse(theme.BrightBlue);
        var red = Color.Parse(theme.Red);
        var brightRed = Color.Parse(theme.BrightRed);
        var selection = Color.Parse(theme.Selection);

        var bgSurface = Shift(bg, -12);
        var bgElevated = Shift(bg, 14);
        var borderSubtle = Shift(bg, 28);
        var borderStrong = Shift(bg, 42);

        var textSecondary = Lerp(fg, bg, 0.40);
        var textMuted = Lerp(fg, bg, 0.58);
        var textFaint = borderStrong;
        var textHover = Lerp(fg, Colors.White, 0.25);

        var green = Color.Parse(theme.Green);
        var dangerSubtle = WithAlpha(red, 0.12, bg);
        var dangerText = brightRed;

        Set(app, "BgBase", bg);
        Set(app, "BgSurface", bgSurface);
        Set(app, "BgElevated", bgElevated);

        Set(app, "BorderSubtle", borderSubtle);
        Set(app, "BorderStrong", borderStrong);

        Set(app, "TextPrimary", fg);
        Set(app, "TextSecondary", textSecondary);
        Set(app, "TextMuted", textMuted);
        Set(app, "TextFaint", textFaint);
        Set(app, "TextHover", textHover);

        Set(app, "InteractiveHover", borderSubtle);
        Set(app, "InteractivePressed", borderStrong);
        Set(app, "AccentDefault", blue);
        Set(app, "AccentHover", brightBlue);

        Set(app, "DangerSubtle", dangerSubtle);
        Set(app, "DangerText", dangerText);
        Set(app, "TagColor", green);
    }

    private static void Set(Application app, string key, Color color)
    {
        app.Resources[key] = new SolidColorBrush(color);
    }

    private static Color Shift(Color c, int amount)
    {
        return Color.FromRgb(
            Clamp(c.R + amount),
            Clamp(c.G + amount),
            Clamp(c.B + amount));
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color WithAlpha(Color c, double alpha, Color bg)
    {
        return Color.FromRgb(
            (byte)(bg.R + (c.R - bg.R) * alpha),
            (byte)(bg.G + (c.G - bg.G) * alpha),
            (byte)(bg.B + (c.B - bg.B) * alpha));
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
