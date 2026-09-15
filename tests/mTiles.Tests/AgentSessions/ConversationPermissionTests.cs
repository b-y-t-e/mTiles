using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions.OpenCode;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// What a conversation lets an agent do without asking, pinned as <c>AiAgentTests</c> pins the TUI's flags:
/// one changed line here is an agent editing somebody's repository with nobody approving it.
/// </summary>
public class ConversationPermissionTests
{
    [Theory]
    [InlineData(AiBehaviour.ToolDefault, null, null)]
    [InlineData(AiBehaviour.Plan, "on-request", "read-only")]
    [InlineData(AiBehaviour.Ask, "on-request", "workspace-write")]
    [InlineData(AiBehaviour.AcceptEdits, "never", "workspace-write")]
    [InlineData(AiBehaviour.Auto, "never", "workspace-write")]
    [InlineData(AiBehaviour.BypassPermissions, "never", "danger-full-access")]
    public void Codex_app_server_permissions_follow_the_tui_table(AiBehaviour behaviour, string? approval,
        string? sandbox) =>
        Assert.Equal((approval, sandbox), CodexAgent.AppServerPermissions(behaviour));

    [Theory]
    [InlineData(AiBehaviour.ToolDefault, "agent stdio")]
    [InlineData(AiBehaviour.Plan, "--permission-mode default agent stdio")]
    [InlineData(AiBehaviour.Ask, "--permission-mode default agent stdio")]
    [InlineData(AiBehaviour.AcceptEdits, "--permission-mode acceptEdits agent stdio")]
    [InlineData(AiBehaviour.Auto, "--permission-mode auto agent stdio")]
    [InlineData(AiBehaviour.BypassPermissions, "agent --always-approve stdio")]
    public void Grok_acp_arguments_carry_the_mode(AiBehaviour behaviour, string arguments) =>
        Assert.Equal(arguments, string.Join(' ', GrokAgent.AcpArguments(behaviour)));

    [Fact]
    public void Opencode_tool_default_leaves_its_own_rules() =>
        Assert.Null(OpenCodeServerSession.PermissionRules(AiBehaviour.ToolDefault));

    [Fact]
    public void Opencode_bypass_allows_everything() =>
        Assert.Equal(["*|*|allow", "external_directory|*|allow"],
            Rules(AiBehaviour.BypassPermissions));

    [Theory]
    [InlineData(AiBehaviour.Plan, "ask")]
    [InlineData(AiBehaviour.Ask, "ask")]
    [InlineData(AiBehaviour.AcceptEdits, "allow")]
    [InlineData(AiBehaviour.Auto, "allow")]
    public void Opencode_asks_for_everything_but_reading_and_edits_only_in_the_editing_modes(
        AiBehaviour behaviour, string edit)
    {
        var rules = Rules(behaviour);

        Assert.Equal("*|*|ask", rules[0]);
        Assert.Equal($"edit|*|{edit}", rules[^1]);
        Assert.Contains("read|*.env|ask", rules);
        Assert.DoesNotContain(rules, r => r.StartsWith("bash|") || r.StartsWith("external_directory|"));
    }

    private static List<string> Rules(AiBehaviour behaviour) =>
        JsonSerializer.SerializeToElement(OpenCodeServerSession.PermissionRules(behaviour))
            .EnumerateArray()
            .Select(r => $"{r.GetProperty("permission")}|{r.GetProperty("pattern")}|{r.GetProperty("action")}")
            .ToList();
}
