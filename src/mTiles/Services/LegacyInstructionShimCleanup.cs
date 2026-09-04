using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// Clears up after the shim <see cref="AgentFileSyncEngine"/> replaces: a <c>CLAUDE.md</c> whose whole
/// content is <c>@AGENTS.md</c>, written by the version of <see cref="WorkspaceAgentFiles"/> that
/// closed the "Claude Code does not read AGENTS.md" gap with a one-line import.
/// </summary>
/// <remarks>
/// <para><b>A class of its own, and called explicitly</b> — the rule
/// <see cref="LegacyDatabaseSectionCleanup"/> already follows: it has its own reason to change and its
/// own expiry date, and it removes a file from somebody else's repository, which is not something a
/// constructor should do as a side effect of being reached.</para>
/// <para><b>Why it only runs where the sync is switched on.</b> Taking the shim out of the code does
/// not take it off anyone's disk, and left there it reads to the wizard as two files whose content
/// differs — a user who answers "CLAUDE.md is the current one", which is the true answer about their
/// own instructions, replaces the whole of <c>AGENTS.md</c> with the single line <c>@AGENTS.md</c>.
/// That is what <see cref="IsPresentIn"/> is for: the question is not asked, because a file holding
/// none of the user's words is not a version of anything. The <em>deletion</em> waits for the sync to
/// actually be enabled, and the file comes straight back as a copy of <c>AGENTS.md</c>. Run on every
/// open instead, it takes the only instruction file Claude Code reads away from a user who declined the
/// wizard — or who wrote that one-line import themselves, which is the arrangement chapter 2 of
/// <c>docs/AGENTS-MD-SYNC.md</c> recommends — and leaves them worse off than before this feature
/// existed.</para>
/// <para><b>Only a file that is still exactly the shim.</b> Content says what a file is: a
/// <c>CLAUDE.md</c> somebody has since written in is theirs, and an unreadable one is not ours to
/// judge. <b>The file it points at need not be there</b> — a shim whose <c>AGENTS.md</c> has gone
/// imports nothing and holds none of the user's words either way, and left unrecognised it reaches
/// the wizard as a version to choose between and is seeded into a new <c>AGENTS.md</c> whose whole
/// content is the circular <c>@AGENTS.md</c>, which codex, pi and agy read as the project's
/// instructions. <see cref="Run"/> is reached only on the paths that are about to start the engine
/// that takes both files over, so the deletion never outruns the sync that replaces it — a user who
/// declines, or whose global switch is off, keeps the file exactly as it was.</para>
/// </remarks>
public static class LegacyInstructionShimCleanup
{
    /// <summary>The whole content the old writer put in the shim, ignoring surrounding whitespace.</summary>
    private const string ShimContent = "@" + WorkspaceAgentFiles.CanonicalInstructionFile;

    /// <summary>Whether this workspace's <c>CLAUDE.md</c> is still nothing but the old import — asked
    /// before the wizard is built, so that a file holding none of the user's own words is not offered as
    /// one of two versions to choose between. The file the import names need not exist: a shim whose
    /// target has gone is still a shim, and recognising it is what keeps the seeding from turning the
    /// circle into the workspace's new <c>AGENTS.md</c>.</summary>
    public static bool IsPresentIn(string workspaceDir) =>
        IsShim(Path.Combine(workspaceDir, AgentFileSyncEngine.ClaudeFileName));

    /// <summary>Removes the shim from this workspace, if that is still what the file is. Called only
    /// where sync is being switched on — the engine that takes both files over starts immediately
    /// after, so the workspace is not left without either instruction file for longer than that
    /// start.</summary>
    public static void Run(string workspaceDir)
    {
        var shimPath = Path.Combine(workspaceDir, AgentFileSyncEngine.ClaudeFileName);
        try
        {
            if (!IsPresentIn(workspaceDir)) return;
            File.Delete(shimPath);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not remove the legacy instruction shim '{0}': {1}",
                shimPath, ex.Message);
        }
    }

    private static bool IsShim(string path)
    {
        try
        {
            return File.Exists(path)
                   && File.ReadAllText(path).Trim().Equals(ShimContent, StringComparison.Ordinal);
        }
        catch
        {
            // Unreadable is not ours: the one thing that must not happen is deleting a file whose
            // content nobody could look at.
            return false;
        }
    }
}
