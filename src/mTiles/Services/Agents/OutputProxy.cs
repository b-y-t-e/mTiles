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
    /// builds, and rtk installs itself under <c>~/.local/bin</c>.
    /// Also the login shell's <c>PATH</c> once it has been read, so rtk a tile's shell can run is never
    /// reported missing — but never waits for that read: this is asked on the UI thread.</remarks>
    public static string? Locate() =>
        ExecutableFinder.Anywhere(BinaryName) ?? OnThePathsOf(() => LoginShellPath.ValueIfRead);

    /// <summary>Whether it is on this machine at all.</summary>
    public static bool IsInstalled => Locate() is not null;

    /// <summary>
    /// Where a shell we launch would find <c>rtk</c> by its bare name, or null.
    /// </summary>
    /// <remarks>
    /// <para><b>A different question from <see cref="Locate"/>, and not the one the hook is gated on.</b>
    /// The hook is written wherever <see cref="Locate"/> finds rtk; this decides only whether
    /// <see cref="DirectoryToPrependToPath()"/> has to put rtk's directory on the session's <c>PATH</c>.
    /// Measured 2026-09-23 by feeding a <c>PreToolUse</c> payload to <c>rtk hook claude</c>: it answers
    /// <c>{"updatedInput":{"command":"rtk git status"}}</c> — the rewrite is spelled with the <b>bare
    /// name</b>. So whatever path we put in the hook, what finally runs is <c>rtk …</c> in the tile's
    /// own shell, and a machine where rtk sits somewhere that shell does not search answers
    /// <c>rtk: command not found</c> — the agent's command <em>fails</em> rather than merely missing
    /// its saving. That is worse than having no proxy, which is why a miss here is closed by prepending
    /// rtk's directory rather than hoped away — and never by refusing the hook, which is the regression
    /// ADR 0005's third amendment removed.</para>
    /// <para><b>Both paths a child of ours can have</b>: ours, which a tile inherits, and — on Unix —
    /// the login shell's, since the tile's shell reads the rc files that nvm, asdf and friends write
    /// into and this application's own <c>PATH</c> does not carry them. The same pair
    /// <see cref="BackgroundInstaller"/> searches, for the same reason.</para>
    /// <para>Deliberately <b>not</b> <see cref="ExecutableFinder.Anywhere"/>: that looks in places no
    /// shell searches, which is right for a binary we start ourselves by its full path and exactly
    /// wrong for a name somebody else's rewrite is about to emit.</para>
    /// <para><b>Never waits for the login shell</b>: this is asked on the way to starting a tile, on
    /// the UI thread, and that read is a process with a ten-second deadline. Until it has finished, rtk
    /// found only there reads as absent, and rtk's directory is prepended though it was not needed —
    /// harmless, since a duplicate entry costs one failed lookup.</para>
    /// </remarks>
    public static string? OnTheShellsPath() => OnThePathsOf(() => LoginShellPath.ValueIfRead);

    /// <summary>Whether <see cref="OnTheShellsPath"/> is already a final answer: rtk is on our own
    /// <c>PATH</c>, or the login shell's has been read.</summary>
    public static bool IsShellsPathKnown =>
        ExecutableFinder.OnPathRunnable(BinaryName) is not null || LoginShellPath.IsRead;

    /// <summary>Waits, off the UI thread, until <see cref="Locate"/> and <see cref="OnTheShellsPath"/> are final — for a launch
    /// that is about to decide whether to write the hook, so a tile restored at startup is not left
    /// unfiltered only because the login shell had not answered yet.</summary>
    public static Task WhenShellsPathIsKnownAsync() =>
        IsShellsPathKnown ? Task.CompletedTask : LoginShellPath.ReadAsync();

    /// <summary>Whether a tile's shell would find rtk by name without help; decides only whether the
    /// launch prepends its directory, never whether the hook is written.</summary>
    public static bool IsUsableByAShell => OnTheShellsPath() is not null;

    /// <summary>
    /// The directory a session's <c>PATH</c> has to gain for the rewrite to resolve, or null when it
    /// already would — or when there is no rtk to reach.
    /// </summary>
    /// <remarks>
    /// <para><b>Making it reachable beats reporting that it is not.</b> The first answer to a machine
    /// where rtk is installed somewhere no shell searches was a sentence on the Settings row, telling
    /// the user to fix their <c>PATH</c> and restart mTiles. That is a real fact and a poor response:
    /// the environment a session runs in is one this application composes anyway
    /// (<c>IAiAgent.EnvFor</c>), and every route that carries the hook carries the environment too, so
    /// the gap can simply be closed.</para>
    /// <para><b>The case it exists for is ordinary, not exotic.</b> winget installs into
    /// <c>%LOCALAPPDATA%\Microsoft\WinGet\Links</c> and adds that to the *user's* <c>PATH</c> — a
    /// change no already-running process ever sees. So mTiles that installed rtk from its own Settings
    /// row is, by construction, a process whose <c>PATH</c> does not carry what it just installed,
    /// until somebody restarts it.</para>
    /// <para><b>Null where nothing needs doing</b>, so a <c>PATH</c> override is added only where it
    /// buys something and every other session's environment is left exactly as it was.</para>
    /// </remarks>
    public static string? DirectoryToPrependToPath() => DirectoryToPrependToPath(Locate);

    /// <summary>As above, with where rtk is supplied by the caller — so a test states the machine
    /// instead of planting a binary in the developer's own home directory.</summary>
    internal static string? DirectoryToPrependToPath(Func<string?> locate) =>
        IsUsableByAShell ? null : locate() is { } found ? Path.GetDirectoryName(found) : null;

    /// <summary><paramref name="inherited"/> with <paramref name="directory"/> in front of it.</summary>
    /// <remarks>Pure, so the rule is argued without a machine. <b>In front</b>, because the point is to
    /// be found — and additive, so a shell rc file that appends to what it was given keeps it. A
    /// duplicate entry costs one failed lookup and nothing else, so none is searched for.</remarks>
    internal static string PathWith(string directory, string? inherited) =>
        string.IsNullOrEmpty(inherited) ? directory : directory + Path.PathSeparator + inherited;

    /// <summary>Our <c>PATH</c>, then — on Unix — the login shell's, read only when ours misses:
    /// reading it can take seconds and this is asked on the way to starting a tile.</summary>
    private static string? OnThePathsOf(Func<string?> loginShellPath) =>
        ExecutableFinder.OnPathRunnable(BinaryName)
        ?? (OperatingSystem.IsWindows() ? null : LoginShellPath.Find(BinaryName, loginShellPath()));

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
    /// <para><b>Windows only, and only where winget itself can be found.</b> It names the package
    /// <c>rtk-ai.rtk</c> and puts the binary in <c>%USERPROFILE%\.local\bin</c>, which is where
    /// <see cref="ExecutableFinder.Anywhere"/> already looks. <b>Asked per call rather than held in a
    /// static</b>, because the answer is a fact about this machine: winget lives in
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>, which on a good many Windows 11 installations is in
    /// no process' <c>PATH</c> at all — measured 2026-09-23, where the alias was present and reachable
    /// from neither PowerShell nor Git Bash — so a plan built unconditionally gave the row a button
    /// whose command answered "command not found" in every shell. Where winget cannot be found this
    /// answers null and the row shows the link instead, the rule an agent with no plan already keeps.
    /// On Linux the published route is a piped shell installer, and this application does not
    /// put <c>curl … | sh</c> behind a button: what the user would be approving is a URL whose
    /// contents nobody here has read, and the confirmation could not say what it does. That row shows
    /// the link instead — which is less convenient and is the honest amount of help.</para>
    /// </remarks>
    public static InstallPlan? Plan => PlanFor(ExecutableFinder.Anywhere);

    /// <summary>As above, with where a binary is found supplied by the caller.</summary>
    internal static InstallPlan? PlanFor(Func<string, string?> locate)
    {
        if (!OperatingSystem.IsWindows() || locate("winget") is null) return null;

        return new InstallPlan("winget",
            [
                "install", "--exact", "--id", "rtk-ai.rtk",
                // Nothing is watching this one, so every question it could ask is answered up front.
                // Measured need rather than belt and braces: winget's source and package agreements are
                // an interactive y/n on a machine that has not accepted them, which in the background
                // is a process hung until BackgroundInstaller.Timeout kills it.
                "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity",
                "--silent",
            ],
            "Installs the rtk CLI. rtk rewrites shell commands your agents run so their output costs "
            + "fewer tokens; mTiles passes it to the agent instances you tick and writes nothing into "
            + "your own configuration.");
    }

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
