namespace MTerminal.Services;

public static class AppPaths
{
    public static string GetAppDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MTerminal");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "MTerminal");
    }

    public static string GetWorkspacesDirectory() =>
        Path.Combine(GetAppDataDirectory(), "workspaces");

    public static string GetSettingsFilePath() =>
        Path.Combine(GetAppDataDirectory(), "settings.json");

    public static string GetWorkspacesFilePath() =>
        Path.Combine(GetAppDataDirectory(), "workspaces.json");
}
