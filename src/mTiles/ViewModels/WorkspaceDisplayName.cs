using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// What a workspace is called in the panel, which is not always what is written in
/// <c>workspaces.json</c>.
/// </summary>
/// <remarks>
/// <para>A workspace takes its name from the last part of its directory, and for the home directory
/// that is the login — so the first thing a new user sees is a row called <c>andrz</c>, which names the
/// account rather than the place and reads as a stray folder somebody added. <see cref="Home"/> says
/// what it is.</para>
/// <para>A display rule and not a rename: the stored name is left exactly as it is, so moving the
/// workspace elsewhere, or opening the same file on another machine with a different login, shows the
/// directory's own name again rather than a label this application decided years earlier.</para>
/// </remarks>
public static class WorkspaceDisplayName
{
    /// <summary>What the user's own directory is called on screen.</summary>
    public const string Home = "Home";

    public static string For(string storedName, string directoryPath) =>
        SpecialDirectories.IsHome(directoryPath) ? Home : storedName;
}
