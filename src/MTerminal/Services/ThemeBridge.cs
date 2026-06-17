using Avalonia;
using Avalonia.Media;
using MTerminal.Models;

namespace MTerminal.Services;

public static class ThemeBridge
{
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
