using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services;
using mTiles.Services.Providers;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The output proxy: what it asks Claude Code to run, which agents may be given it, and the three
/// facts about this machine that decide whether a ticked instance actually gets it.
/// </summary>
/// <remarks>The hook's shape is <b>somebody else's CLI contract</b> — rtk's, measured against 0.46.0
/// on 2026-09-22 — so it is pinned here the way <c>AiAgentTests</c> pins the agents' flags: when rtk
/// moves it, this fails as a build rather than as sessions that quietly stop being filtered.</remarks>
public class OutputProxyTests : IDisposable
{
    // The launch writes its settings file under the application's directory; this keeps it out of
    // the developer's own.
    private readonly TempAppData _appData = new();

    public void Dispose() => _appData.Dispose();

    /// <summary>A sign-in whose Claude Code settings live in a directory of this test's own.</summary>
    private AiSignIn SignInWithSettings(string? settings)
    {
        var directory = Path.Combine(_appData.Root, "signin-" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        if (settings is not null)
            File.WriteAllText(Path.Combine(directory, "settings.json"), settings);
        return new AiSignIn { AgentId = "claude", ConfigDirectory = directory };
    }

    [Theory]
    [InlineData("""{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"rtk hook claude"}]}]}}""")]
    [InlineData("""{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"~/.claude/hooks/rtk-rewrite.sh"}]}]}}""")]
    public void A_hook_in_the_accounts_own_settings_is_found(string settings)
    {
        // Today's `rtk init --global` and the older one that pointed at a script of its own.
        Assert.True(OutputProxyGlobalHook.IsHookedForClaude(SignInWithSettings(settings)));
    }

    [Fact]
    public void Settings_without_the_hook_or_with_no_file_are_not_hooked()
    {
        Assert.False(OutputProxyGlobalHook.IsHookedForClaude(SignInWithSettings("""{"outputStyle":"Concise"}""")));
        Assert.False(OutputProxyGlobalHook.IsHookedForClaude(SignInWithSettings(null)));
    }

    [Fact]
    public void A_hook_in_the_workspaces_own_settings_is_found()
    {
        var workspace = Path.Combine(_appData.Root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, ".claude"));
        File.WriteAllText(Path.Combine(workspace, ".claude", "settings.local.json"), "rtk hook claude");

        Assert.True(OutputProxyGlobalHook.IsHookedForClaude(SignInWithSettings(null), workspace));
    }

    [Fact]
    public void The_plain_settings_file_is_what_it_always_was()
    {
        // Every Claude Code session this application holds is started with this, proxy or no proxy.
        Assert.Equal(ClaudeSessionSettings.Content, ClaudeSessionSettings.ContentFor(null));
        Assert.Contains("Concise", ClaudeSessionSettings.ContentFor(null));
        Assert.DoesNotContain("hooks", ClaudeSessionSettings.ContentFor(null));
    }

    [Fact]
    public void The_hook_file_is_the_block_rtk_asks_for()
    {
        // Measured: `rtk init --global --hook-only` against a sandboxed config directory asks for one
        // PreToolUse entry, matcher "Bash", command "rtk hook claude". Read as JSON rather than as a
        // string, because the shape is the contract and the whitespace is not.
        using var document = JsonDocument.Parse(ClaudeSessionSettings.ContentFor(@"C:\tools\rtk.exe"));
        var root = document.RootElement;

        // The output style survives the hook being added — the two files differ in one thing only.
        Assert.Equal("Concise", root.GetProperty("outputStyle").GetString());

        var entry = Assert.Single(root.GetProperty("hooks").GetProperty("PreToolUse").EnumerateArray());
        Assert.Equal("Bash", entry.GetProperty("matcher").GetString());

        var hook = Assert.Single(entry.GetProperty("hooks").EnumerateArray());
        Assert.Equal("command", hook.GetProperty("type").GetString());
        Assert.Equal("\"C:/tools/rtk.exe\" hook claude", hook.GetProperty("command").GetString());
    }

    [Fact]
    public void The_two_variants_are_two_files()
    {
        // One file rewritten per launch is two tiles on two instances racing over a path they have both
        // already been handed. The name says what is in it, so there is nothing to race.
        Assert.NotEqual(ClaudeSessionSettings.PathFor(true), ClaudeSessionSettings.PathFor(false));
        Assert.Equal(ClaudeSessionSettings.PathFor(), ClaudeSessionSettings.PathFor(false));
    }

    [Fact]
    public void Only_an_agent_with_a_route_offers_the_proxy()
    {
        // Claude Code takes a generated settings file, which this application already hands it.
        Assert.Equal(OutputProxy.Support.GeneratedFile,
            AiAgentCatalog.Find("claude")!.OutputProxySupport);

        // opencode's route writes into the user's own configuration and has no per-run flag. Named
        // rather than called None, so the next reader finds the measurement instead of repeating it.
        Assert.Equal(OutputProxy.Support.WritesOutsideOurDirectories,
            AiAgentCatalog.Find("opencode")!.OutputProxySupport);

        // Everything else has nothing measured, and an agent nobody measured gets no proxy.
        foreach (var agent in AiAgentCatalog.All)
        {
            if (agent.Id is "claude" or "opencode") continue;
            Assert.Equal(OutputProxy.Support.None, agent.OutputProxySupport);
        }
    }

    [Fact]
    public void An_instance_that_did_not_ask_gets_the_plain_file()
    {
        var claude = AiAgentCatalog.Find("claude")!;
        var instance = new AiAgentInstance { AgentId = "claude" };

        var arguments = claude.SessionDefaultArgs(new AgentRuntime(instance, null, null, ""));

        Assert.Equal("--settings", arguments[0]);
        Assert.Equal(ClaudeSessionSettings.PathFor(false), arguments[1]);
    }

    [Fact]
    public void A_ticked_instance_gets_the_hook_file_only_where_the_machine_can_run_it()
    {
        var claude = AiAgentCatalog.Find("claude")!;
        var instance = new AiAgentInstance { AgentId = "claude", UseOutputProxy = true };

        var arguments = claude.SessionDefaultArgs(new AgentRuntime(instance, null, null, ""));
        Assert.Equal("--settings", arguments[0]);

        // Three facts decide it and none of them is the tick alone: rtk has to be on this machine, and
        // Claude Code's own settings must not already carry the hook — two of them on one command is
        // one command handed to the proxy twice. This asserts the rule rather than a machine, so it
        // passes on a build agent with no rtk and on a developer's box with one.
        var expected = OutputProxy.IsInstalled && !OutputProxyGlobalHook.IsHookedForClaude(null);
        Assert.Equal(ClaudeSessionSettings.PathFor(expected), arguments[1]);
    }

    [Theory]
    [InlineData(@"{""command"":""rtk hook claude""}")]
    [InlineData(@"{""command"":""C:\tools\rtk.exe hook claude""}")]
    [InlineData(@"{""command"":""\""/home/u/.local/bin/rtk\"" hook claude""}")]
    [InlineData(@"{""command"":""\""C:\tools\rtk.exe\"" hook claude""}")]
    [InlineData(@"{""command"":""~/.claude/hooks/rtk-rewrite.sh""}")]
    public void A_hook_is_recognised_however_its_path_is_spelled(string settings) =>
        Assert.True(OutputProxyGlobalHook.MentionsTheHook(settings));

    [Fact]
    public void A_settings_file_without_the_hook_is_not_read_as_hooked() =>
        Assert.False(OutputProxyGlobalHook.MentionsTheHook(@"{""hooks"":{""PreToolUse"":[]}}"));

    [Fact]
    public void The_tick_is_carried_by_a_copy()
    {
        // Clone is memberwise, so this is true by construction — and the instance editor works on a
        // copy, so a property that did not travel would be one the form silently dropped on Save.
        var instance = new AiAgentInstance { AgentId = "claude", UseOutputProxy = true };
        Assert.True(instance.Clone().UseOutputProxy);
    }

    [Fact]
    public void A_fresh_instance_does_not_rewrite_anything()
    {
        // The rule DefaultBehaviour keeps: a row nobody has been asked about must not quietly do
        // something to what the agent runs.
        Assert.False(new AiAgentInstance().UseOutputProxy);
    }

    [Fact]
    public void The_install_plan_never_reaches_for_the_wrong_crate()
    {
        // crates.io carries a different program under the name `rtk` — Rust Type Kit — so a plan that
        // ran `cargo install rtk` would install something that answers `rtk --version` and fails every
        // hook. Asserted rather than trusted to a comment, because the obvious command is the wrong one.
        if (OutputProxy.Plan is not { } plan) return;

        Assert.DoesNotContain("cargo", plan.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(plan.Note);
    }

    [Fact]
    public void There_is_no_install_where_winget_cannot_be_found()
    {
        // Measured 2026-09-23: %LOCALAPPDATA%\Microsoft\WindowsApps is in no process' PATH on a good
        // many Windows 11 machines, so a plan built unconditionally gave the row a button whose command
        // answered "command not found" in every shell. No winget, no plan — the row shows the link.
        Assert.Null(OutputProxy.PlanFor(_ => null));
    }

    [Fact]
    public void The_install_answers_every_question_winget_could_ask()
    {
        // Nothing is watching it, so a prompt is a process hung until BackgroundInstaller.Timeout kills
        // it. Skipped off Windows, where there is deliberately no plan at all.
        if (!OperatingSystem.IsWindows()) return;

        var plan = OutputProxy.PlanFor(_ => @"C:\winget.exe");
        Assert.NotNull(plan);
        Assert.Contains("--accept-source-agreements", plan!.Arguments);
        Assert.Contains("--accept-package-agreements", plan.Arguments);
        Assert.Contains("--disable-interactivity", plan.Arguments);

        // The package, exactly — winget's fuzzy search would otherwise answer with whatever it likes.
        Assert.Contains("--exact", plan.Arguments);
        Assert.Contains("rtk-ai.rtk", plan.Arguments);
    }

    [Fact]
    public void An_install_is_not_a_sign_in()
    {
        // The split that decides tile or no tile. An install runs to an exit code; a sign-in only
        // starts at the command and then waits for the user, so it keeps its terminal.
        if (OutputProxy.PlanFor(_ => @"C:\winget.exe") is { } plan) Assert.False(plan.NeedsATerminal);
    }

    [Fact]
    public async Task A_plan_needing_a_terminal_is_refused_by_the_background_installer()
    {
        var signIn = new InstallPlan("claude /login", [], "a login") { NeedsATerminal = true };

        var outcome = await BackgroundInstaller.RunAsync(signIn, _ => @"C:\claude.exe", childPath: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("terminal", outcome.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_installer_that_is_not_there_is_named_rather_than_thrown()
    {
        // This starts a process directly, so there is no shell lookup behind it to fall back on — which
        // is the whole point, and also why a missing binary has to be said out loud rather than arriving
        // as an opaque Win32Exception.
        var outcome = await BackgroundInstaller.RunAsync(
            new InstallPlan("nosuchinstaller", ["--version"], ""), _ => null, childPath: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("nosuchinstaller", outcome.Problem!);
    }

    [Fact]
    public async Task An_installer_that_exits_cleanly_succeeds()
    {
        var outcome = await BackgroundInstaller.RunAsync(
            new InstallPlan("dotnet", ["--version"], ""), _ => "dotnet", childPath: null);

        Assert.True(outcome.Succeeded, outcome.Problem);
    }

    [Fact]
    public async Task A_failing_installer_names_its_exit_code_and_its_last_lines()
    {
        // The tail is the only account of a failed install the user gets, so it must reach Problem.
        var outcome = await BackgroundInstaller.RunAsync(
            new InstallPlan("dotnet", ["nosuchcommandforthistest"], ""), _ => "dotnet", childPath: null);

        Assert.False(outcome.Succeeded);
        Assert.Contains("exit code", outcome.Problem!);
        Assert.Contains("nosuchcommandforthistest", outcome.Problem!);
    }
}
