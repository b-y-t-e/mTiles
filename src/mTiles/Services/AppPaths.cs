using mTiles.Models;

namespace mTiles.Services;

public static class AppPaths
{
    public static string GetAppDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MTerminal");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "MTerminal");
    }

    public static string GetLogsDirectory() =>
        Path.Combine(GetAppDataDirectory(), AppDefaults.LogSubdirectory);

    public static string GetWorkspacesDirectory() =>
        Path.Combine(GetAppDataDirectory(), "workspaces");

    /// <summary>Where downloaded speech-to-text models live. Hundreds of megabytes each.</summary>
    public static string GetSpeechModelsDirectory() =>
        Path.Combine(GetAppDataDirectory(), "models");

    public static string GetSettingsFilePath() =>
        Path.Combine(GetAppDataDirectory(), "settings.json");

    public static string GetWorkspacesFilePath() =>
        Path.Combine(GetAppDataDirectory(), "workspaces.json");
}
