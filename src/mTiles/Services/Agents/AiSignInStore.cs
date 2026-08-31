using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// Where a sign-in's directory is, and how one is brought into being.
/// </summary>
/// <remarks>
/// <para><b>Derived rather than stored</b>, the same rule <c>TileScript</c> follows for an OpenCode
/// session file: the directory is a pure function of the sign-in's id, so <c>settings.json</c> holds
/// nothing that stops being true on another machine. A path typed by hand overrides it and is used
/// verbatim — that is somebody pointing at a directory that already exists, and rewriting it would be
/// this application deciding it knows better about a location it did not choose.</para>
/// <para><b>Nothing here ever deletes.</b> A sign-in's directory holds the CLI's own refresh token and
/// the whole history that came with the account, and neither is ours: removing the row removes the
/// row.</para>
/// </remarks>
public static class AiSignInStore
{
    /// <summary>The directory this sign-in points at.</summary>
    public static string DirectoryFor(AiSignIn signIn) =>
        signIn.ConfigDirectory.Length > 0
            ? signIn.ConfigDirectory
            : Path.Combine(AppPaths.GetAgentAccountsDirectory(),
                SafePathComponent.Of(signIn.AgentId),
                SafePathComponent.Of(signIn.Id));

    /// <summary>
    /// Creates the directory, owner-only, and answers whether it is there.
    /// </summary>
    /// <remarks><para>Owner-only for the reason <see cref="PrivateFile"/> gives: the CLI is about to
    /// write a refresh token in here, and outside Windows the default is umask-dependent and routinely
    /// group-readable. The mode is set as the directory is created rather than afterwards, or there is
    /// a window in which the credentials exist at whatever the umask said.</para>
    /// <para>Fails soft. A directory that cannot be made is a sign-in that reads as not signed in,
    /// which is true and recoverable; throwing here would be a Settings page that cannot be
    /// opened.</para></remarks>
    public static bool Ensure(AiSignIn signIn, IAiAgent? agent = null)
    {
        var root = DirectoryFor(signIn);
        var made = Create(root);

        // Every directory the CLI is actually pointed at, not only the one this class names. opencode's
        // XDG_DATA_HOME is <root>/data, and it is *that* directory auth.json lands in - so creating the
        // root alone left the one holding the credentials to be made by the CLI at whatever the umask
        // said, which is the guarantee this method exists to give. Asked of the agent rather than
        // listed here, because every value in SignInEnv is a directory by construction and an agent
        // that grows a second one must not have to remember a second place.
        if (agent is not null)
            foreach (var value in agent.SignInEnv(root).Values)
                if (value is { Length: > 0 } directory)
                    made &= Create(directory);

        return made;
    }

    private static bool Create(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) return true;

            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
                Directory.CreateDirectory(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not create the sign-in directory {0}: {1}", directory, ex.Message);
            return false;
        }
    }

    /// <summary>The sign-ins belonging to one agent, which is all a chooser may ever offer.</summary>
    /// <remarks>A login is one CLI's. Offering a Claude Code account to codex would be a pairing that
    /// cannot work, stored — the same failure <c>AiProviderCatalog.IsCompatible</c> exists to stop one
    /// level up.</remarks>
    public static IEnumerable<AiSignIn> For(AppSettings settings, string agentId) =>
        settings.AiSignIns.Where(signIn => signIn.AgentId == agentId);

    /// <summary>Finds a sign-in by id, or null.</summary>
    public static AiSignIn? Find(AppSettings settings, string signInId) =>
        signInId.Length == 0
            ? null
            : settings.AiSignIns.FirstOrDefault(signIn => signIn.Id == signInId);
}
