using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// Whether the output proxy is already hooked into a CLI's <em>own</em> configuration — what
/// <c>rtk init --global</c> does — so this application can say so instead of adding a second hook
/// beside it.
/// </summary>
/// <remarks>
/// <para><b>Two hooks on one command is not twice the saving.</b> Claude Code runs every matching
/// <c>PreToolUse</c> entry, so a command already rewritten by the user's own global hook would be
/// handed to <c>rtk hook claude</c> a second time. What comes of that is rtk's business and not
/// ours — it may pass through, it may rewrite a rewrite — and either way it is a behaviour nobody
/// chose, arrived at by two pieces of configuration that cannot see each other.</para>
/// <para><b>Read, never written.</b> This opens the user's <c>settings.json</c> to answer one
/// question and closes it. The whole premise of doing this through a generated <c>--settings</c> file
/// is that nothing here edits that file, and a check that repaired what it found would be the same
/// trespass arriving by the back door.</para>
/// <para><b>A substring and not a parse</b>, deliberately. The question is "does this file mention the
/// proxy's hook at all", which is what decides whether a second one would be piled on top; a JSON walk
/// looking for the exact shape would answer <em>no</em> for a hook the user wrote by hand, spelled with
/// an absolute path, or wrapped in a shell — all of which are still a live rtk hook. Being wrong
/// towards "it is already there" costs the tick on one instance and says why; being wrong the other way
/// is the double rewrite this exists to prevent.</para>
/// </remarks>
public static class OutputProxyGlobalHook
{
    /// <summary>What a hooked settings file mentions, whoever wrote it.</summary>
    /// <remarks>The command rtk's own <c>init</c> asks for today is <c>rtk hook &lt;agent&gt;</c>; this
    /// looks for the part before the agent's name, so a file hooked for a spelling of Claude Code this
    /// build does not know still answers yes. Earlier <c>init</c>s pointed the entry at a script of
    /// their own, <c>~/.claude/hooks/rtk-rewrite.sh</c>, which is still live on machines set up then —
    /// its name is the evidence for those (not measured here; being wrong towards "already there" is
    /// the cheap direction).</remarks>
    private static readonly string[] Evidence =
    [
        OutputProxy.BinaryName + " hook",
        OutputProxy.BinaryName + ".exe hook",
        OutputProxy.BinaryName + "\\\" hook",
        OutputProxy.BinaryName + ".exe\\\" hook",
        OutputProxy.BinaryName + "-rewrite",
    ];

    /// <summary>Whether this settings text mentions the proxy's hook — the rule itself, without a file.</summary>
    /// <remarks>Also matches an absolute path, quoted (JSON escapes the quote as <c>\"</c>) or not,
    /// and <c>rtk.exe</c> on Windows.</remarks>
    public static bool MentionsTheHook(string settings) =>
        Evidence.Any(evidence => settings.Contains(evidence, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether Claude Code's own settings on this machine already carry an rtk hook.</summary>
    /// <remarks>Both files the CLI layers are read, because <c>rtk init --global</c> patches the user
    /// one and a project's own <c>.claude/settings.json</c> is just as live — and a workspace is
    /// exactly where somebody would have run <c>rtk init</c> without <c>--global</c>. Every failure is
    /// <c>false</c>: a file that cannot be read is not evidence of a hook, and the cost of guessing
    /// wrong here is a tick offered rather than a session broken.</remarks>
    /// <param name="signIn">The sign-in the session runs as, or null for the default account: a
    /// sign-in's Claude Code reads its <c>settings.json</c> out of its own <c>CLAUDE_CONFIG_DIR</c>,
    /// not out of <c>~/.claude</c>.</param>
    /// <param name="workspaceDirectory">The workspace whose project-level settings to check, or null
    /// to ask only about the user's own.</param>
    public static bool IsHookedForClaude(AiSignIn? signIn, string? workspaceDirectory = null)
    {
        foreach (var path in ClaudeSettingsFiles(signIn, workspaceDirectory))
            if (Mentions(path))
                return true;

        return false;
    }

    /// <summary>The settings files Claude Code layers on this machine, user first.</summary>
    private static IEnumerable<string> ClaudeSettingsFiles(AiSignIn? signIn, string? workspaceDirectory)
    {
        yield return Path.Combine(UserDirectory(signIn), "settings.json");

        if (workspaceDirectory is { Length: > 0 })
        {
            yield return Path.Combine(workspaceDirectory, ".claude", "settings.json");
            yield return Path.Combine(workspaceDirectory, ".claude", "settings.local.json");
        }
    }

    /// <summary>The directory Claude Code keeps its user settings in for this account.</summary>
    private static string UserDirectory(AiSignIn? signIn)
    {
        if (signIn is not null) return AiSignInStore.DirectoryFor(signIn);

        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return configured is { Length: > 0 }
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    /// <summary>Whether this file exists and mentions the proxy's hook.</summary>
    private static bool Mentions(string path)
    {
        try
        {
            return File.Exists(path) && MentionsTheHook(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
