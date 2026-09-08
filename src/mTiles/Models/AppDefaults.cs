namespace mTiles.Models;

public static class AppDefaults
{
    /// <summary>The interface's typeface, and the terminal's, both starting with the copy that ships
    /// inside the executable.</summary>
    /// <remarks>
    /// <para><c>fonts:JetBrainsMono#JetBrains Mono</c> is the embedded collection — see
    /// <c>Services/AppFonts.cs</c> — so this is the one entry in either list that cannot fail to
    /// resolve. Everything after it is the fallback for a user who edits the field and for glyphs
    /// JetBrains Mono does not carry.</para>
    /// <para><b>The interface is monospaced too, and that is a choice.</b> This application is a
    /// terminal manager: nearly every string on screen is a path, a branch, a model id, a command or a
    /// figure — things read character by character — and a proportional face beside a monospaced
    /// terminal reads as two applications sharing a window. It is one setting away for anybody who
    /// disagrees.</para>
    /// </remarks>
    public const string FontFamily = "fonts:JetBrainsMono#JetBrains Mono, Inter, Segoe UI, sans-serif";
    public const string TerminalFontFamily = "fonts:JetBrainsMono#JetBrains Mono, Cascadia Mono, Fira Code, Consolas, Liberation Mono, DejaVu Sans Mono, Noto Sans Mono, monospace";

    /// <summary>What these two defaulted to before the font was embedded.</summary>
    /// <remarks>Read once, by <c>SettingsService.MigrateLegacySettings</c>: a stored value that is
    /// exactly one of these is a default nobody typed, so it is replaced by the current one. Anything
    /// else is a font the user chose and is left alone. Kept as a list rather than a single string
    /// because a default that moves twice would otherwise strand whoever upgraded in between.</remarks>
    public static readonly string[] PreviousFontFamilies =
    [
        "Inter, Segoe UI, sans-serif"
    ];

    public static readonly string[] PreviousTerminalFontFamilies =
    [
        "Cascadia Mono, JetBrains Mono, Fira Code, Consolas, Liberation Mono, DejaVu Sans Mono, Noto Sans Mono, monospace"
    ];
    public const double FontSize = 14;
    public const string ColorThemeName = "Monokai";

    public const double FontSizeEpsilon = 0.01;
    public const double CheckSizeRatio = 1.4;

    public const int LogRetentionDays = 7;
    public const string LogSubdirectory = "logs";

    public const int SaveDebounceMs = 1000;
    public const int SettingsDebounceMs = 500;
    public const int WatcherDebounceMs = 500;
    public const int FileRetryDelayMs = 500;
}
