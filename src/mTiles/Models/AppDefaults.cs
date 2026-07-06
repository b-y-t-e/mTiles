namespace mTiles.Models;

public static class AppDefaults
{
    public const string FontFamily = "Inter, Segoe UI, sans-serif";
    public const string TerminalFontFamily = "Cascadia Mono, JetBrains Mono, Fira Code, Consolas, Liberation Mono, DejaVu Sans Mono, Noto Sans Mono, monospace";
    public const double FontSize = 14;
    public const string ColorThemeName = "Monokai";

    public const double FontSizeEpsilon = 0.01;
    public const double CheckSizeRatio = 1.4;
    public const double LogoFontSizeRatio = 1.2;
    public const double SmallFontSizeRatio = 0.8;

    public const int LogRetentionDays = 7;
    public const string LogSubdirectory = "logs";

    public const int SaveDebounceMs = 1000;
    public const int SettingsDebounceMs = 500;
    public const int WatcherDebounceMs = 500;
    public const int FileRetryDelayMs = 500;
}
