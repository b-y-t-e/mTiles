using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services;

public static class AppPaths
{
    /// <summary>The directory this application keeps everything in, under whatever the platform calls
    /// "application data".</summary>
    private const string DirectoryName = "mTiles";

    /// <summary>What it was called before the application was renamed.</summary>
    private const string LegacyDirectoryName = "MTerminal";

    /// <summary>
    /// Resolved once per process, because resolving it can <b>move a directory</b> and that is not a
    /// thing to do on every call. <see cref="Lazy{T}"/> rather than a null check: this is read from the
    /// crash handler and the log writer before anything else runs, and two threads racing to rename the
    /// same directory is exactly the kind of failure that leaves half of it behind.
    /// </summary>
    private static readonly Lazy<string> Root = new(Resolve);

    public static string GetAppDataDirectory() => Root.Value;

    /// <summary>
    /// What the move did, if there was one to do, kept for somebody to write to the log later.
    /// </summary>
    /// <remarks>
    /// <para>Held rather than traced, because at the moment this runs there is nowhere for a trace to
    /// go. The <b>first</b> caller of <see cref="GetAppDataDirectory"/> is <c>FileLogWriter</c>'s own
    /// constructor, and <c>LogTraceListener</c> is only added after that returns — so the one line
    /// saying "your whole installation has been moved", or worse "it could not be moved and the old
    /// path is still in use", went to the default listener and nowhere near the log file anybody would
    /// search when their configuration appeared to vanish.</para>
    /// <para>Tracing from inside <see cref="Resolve"/> is also the shape of a deadlock waiting for a
    /// second caller: a listener that resolves this directory while <see cref="Lazy{T}"/> is still
    /// computing it throws <see cref="InvalidOperationException"/> at startup. Today that is prevented
    /// only by the order of two lines in <c>Program.Main</c>. Not tracing here removes the dependence
    /// on that order rather than documenting it.</para>
    /// </remarks>
    public static string? MigrationNote { get; private set; }

    /// <summary>
    /// The current directory, having moved the old one into place if that is what is there.
    /// </summary>
    /// <remarks>
    /// <para>Everything the user has is in here: <c>settings.json</c> with their profiles and
    /// DPAPI-encrypted database passwords, their workspaces and layouts, the phone bridge's private
    /// key, and hundreds of megabytes of downloaded speech models. Renaming the directory without
    /// moving it would present every existing installation with a first run — and the first run
    /// <em>saves</em>, so within milliseconds the old contents stop being reachable and the new
    /// defaults are written over the top.</para>
    /// <para>A move, not a copy: it is one rename on the same volume, so the models are not read, and
    /// there is no window in which two copies disagree about which is authoritative.</para>
    /// <para><b>A move that fails keeps using the old directory.</b> That is the whole safety
    /// property. Anything can hold a handle open — a virus scanner, a second instance, a backup tool —
    /// and answering "then use the new empty path" would turn a locked file into a lost installation.
    /// The old path still works, the next launch tries again, and the only cost of failing forever is
    /// a directory with the previous name.</para>
    /// </remarks>
    private static string Resolve() => Resolve(
        OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"));

    /// <summary>
    /// The same decision against a given parent directory, so it can be argued with in a test.
    /// </summary>
    /// <remarks>
    /// Split out for one reason: the rules here are identical to <see cref="WorkspacePaths"/>'s and the
    /// stakes are far higher — the settings file, the phone bridge's private key, hundreds of
    /// megabytes of speech models — and the version that read <c>SpecialFolder</c> for itself could not
    /// be tested at all. One parameter is the whole cost of being able to state the four cases.
    /// </remarks>
    internal static string Resolve(string parent)
    {
        var current = Path.Combine(parent, DirectoryName);
        var legacy = Path.Combine(parent, LegacyDirectoryName);

        // Nothing to do in the ordinary case, and deliberately nothing to do when *both* exist: a new
        // directory that is already there is the one this application has been writing to, and merging
        // two installations is a decision no code here can make correctly.
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return current;

        try
        {
            Directory.Move(legacy, current);
            MigrationNote = $"Moved the application data directory from {legacy} to {current}.";
            return current;
        }
        catch (Exception ex)
        {
            MigrationNote =
                $"Could not move {legacy} to {current} ({ex.Message}); continuing to use the old path.";
            return legacy;
        }
    }

    public static string GetLogsDirectory() =>
        Path.Combine(GetAppDataDirectory(), AppDefaults.LogSubdirectory);

    public static string GetWorkspacesDirectory() =>
        Path.Combine(GetAppDataDirectory(), "workspaces");

    /// <summary>Where downloaded speech-to-text models live. Hundreds of megabytes each.</summary>
    public static string GetSpeechModelsDirectory() =>
        Path.Combine(GetAppDataDirectory(), "models");

    /// <summary>Where the phone bridge keeps its TLS material. Contains a private key.</summary>
    public static string GetPhoneDirectory() =>
        Path.Combine(GetAppDataDirectory(), "phone");

    public static string GetSettingsFilePath() =>
        Path.Combine(GetAppDataDirectory(), "settings.json");

    public static string GetWorkspacesFilePath() =>
        Path.Combine(GetAppDataDirectory(), "workspaces.json");
}
