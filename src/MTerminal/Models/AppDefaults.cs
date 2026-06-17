namespace MTerminal.Models;

public static class AppDefaults
{
    public const string FontFamily = "Cascadia Mono, Consolas, monospace";
    public const double FontSize = 14;
    public const string Theme = "Dark";
    public const string ColorThemeName = "Monokai";

    public const double FontSizeEpsilon = 0.01;
    public const double CheckSizeRatio = 1.4;
    public const double LogoFontSizeRatio = 1.2;

    public const int SaveDebounceMs = 1000;
    public const int SettingsDebounceMs = 500;
    public const int WatcherDebounceMs = 500;
    public const int NoteSaveDebounceMs = 2000;
    public const int FileRetryDelayMs = 500;
}
