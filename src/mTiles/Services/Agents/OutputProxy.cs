using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// How an agent takes an output proxy — a program that rewrites the shell commands the agent runs so
/// that their output costs fewer tokens. Today that program is <b>rtk</b>.
/// </summary>
/// <remarks>
/// <para><b>Why a whole argv fragment and not a flag.</b> The same reason
/// <c>IAiAgent.EffortArgs</c> answers one: the three routes measured have three shapes. Claude Code
/// takes a generated <em>settings</em> file (<c>--settings</c>) carrying a <c>PreToolUse</c> hook; pi
/// takes a generated <em>extension</em> file (<c>-e</c>); opencode takes a plugin in a directory it
/// owns and has no per-run flag at all. Two of the three are "a file we generate plus an argument",
/// which a fragment expresses and a flag-and-value pair does not — and the third is a route this
/// application will not take, for the reason <see cref="Support.WritesOutsideOurDirectories"/> gives.
/// </para>
/// <para><b>Nothing here writes into a file the user owns.</b> That is the bar every other
/// agent-facing feature in this application keeps — the generated opencode provider config, the
/// opencode session import, Claude Code's session settings — and it is what makes this a per-instance
/// decision rather than a machine-wide one: <c>rtk init --global</c> patches
/// <c>~/.claude/settings.json</c>, and then every Claude Code on this machine is rewritten whether it
/// was launched from here or not.</para>
/// </remarks>
public static class OutputProxy
{
    /// <summary>The program itself.</summary>
    public const string BinaryName = "rtk";

    /// <summary>Its own page.</summary>
    public const string InstallUrl = "https://github.com/rtk-ai/rtk";

    /// <summary>Whether an agent can be given the proxy, and by which route.</summary>
    public enum Support
    {
        /// <summary>No measured route. The default, and what an agent whose author has measured
        /// nothing answers.</summary>
        None,

        /// <summary>A file this application generates, handed over on the command line — so the proxy
        /// applies to the sessions launched from here and to no others. Claude Code and pi.</summary>
        GeneratedFile,

        /// <summary>A route that exists and writes outside the directories this application owns — a
        /// plugin dropped into the CLI's own global configuration, with no per-run flag to carry it.
        /// Measured, named, and deliberately not taken: it would turn a per-instance checkbox into a
        /// change to every session of that CLI on the machine, including the ones started outside
        /// mTiles, and nothing here could take it back off on the instance's behalf.</summary>
        WritesOutsideOurDirectories,
    }

    /// <summary>Where this machine's <c>rtk</c> is, or null.</summary>
    /// <remarks>Through <see cref="ExecutableFinder.Anywhere"/> rather than a bare <c>PATH</c> lookup,
    /// for the reason that method exists: a GUI process does not inherit the <c>PATH</c> a login shell
    /// builds, and rtk installs itself under <c>~/.local/bin</c>.</remarks>
    public static string? Locate() => ExecutableFinder.Anywhere(BinaryName);

    /// <summary>Whether it is on this machine at all.</summary>
    public static bool IsInstalled => Locate() is not null;

    /// <summary>What an Install… button would run, or <c>null</c> where this application has no route
    /// it has actually checked — in which case the row offers <see cref="InstallUrl"/> and nothing
    /// else, the rule <c>IAiAgent.InstallPlan</c> already keeps.</summary>
    /// <remarks>
    /// <para><b>Deliberately not npm.</b> rtk is a single Rust binary and publishes no npm package —
    /// which was the first guess here and was wrong, so it is written down. Its own routes are
    /// Homebrew, a shell installer, winget, and <c>cargo install --git</c>.</para>
    /// <para><b>Never bare <c>cargo install rtk</c></b>, on any platform: crates.io carries a
    /// different program under that exact name — Rust <em>Type</em> Kit — so the obvious command
    /// installs something else entirely, which then answers <c>rtk --version</c> and fails every
    /// hook. rtk's own installation notes lead with that warning.</para>
    /// <para><b>Windows only, for now.</b> winget names it <c>rtk-ai.rtk</c> and puts the binary in
    /// <c>%USERPROFILE%\.local\bin</c>, which is where <see cref="ExecutableFinder.Anywhere"/> already
    /// looks. On Linux the published route is a piped shell installer, and this application does not
    /// put <c>curl … | sh</c> behind a button: what the user would be approving is a URL whose
    /// contents nobody here has read, and the confirmation could not say what it does. That row shows
    /// the link instead — which is less convenient and is the honest amount of help.</para>
    /// </remarks>
    public static InstallPlan? Plan { get; } = OperatingSystem.IsWindows()
        ? new InstallPlan("winget", ["install", "--exact", "--id", "rtk-ai.rtk"],
            "Installs the rtk CLI. rtk rewrites shell commands your agents run so their output costs "
            + "fewer tokens; mTiles passes it to the agent instances you tick and writes nothing into "
            + "your own configuration.")
        : null;

    /// <summary>The command rtk's own hook runs, for the agent it names itself.</summary>
    /// <remarks><para>Measured against rtk 0.46.0 on 2026-09-22, by running
    /// <c>rtk init --global --hook-only</c> against a sandboxed config directory and reading what it
    /// asked to have added. The whole of what it wants is one <c>PreToolUse</c> entry matching
    /// <c>Bash</c> whose command is <c>rtk hook claude</c>.</para>
    /// <para>The agent's name is rtk's own vocabulary and not ours: <c>rtk init --agent</c> lists
    /// claude, cursor, windsurf, cline, kilocode, antigravity, kimi, pi, hermes, droid and vibe. Two
    /// of those overlap with agents here under different spellings, which is why this is a map and not
    /// <c>IAiAgent.Id</c> passed through.</para></remarks>
    /// <param name="rtkPath">Where this machine's rtk is — <see cref="Locate"/>'s answer, and never the
    /// bare name: the binary is found through <see cref="ExecutableFinder.Anywhere"/>, which looks past
    /// <c>PATH</c>, so a bare name would be a hook whose command Claude Code cannot find. Quoted, and
    /// with forward slashes, which both Git Bash and <c>cmd</c> read as a path.</param>
    public static string HookCommandFor(string rtkPath, string rtkAgentName) =>
        $"\"{rtkPath.Replace('\\', '/')}\" hook {rtkAgentName}";
}
