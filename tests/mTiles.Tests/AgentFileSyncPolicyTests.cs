using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>The decision table behind the CLAUDE.md/AGENTS.md sync wizard, argued in a table test the
/// way <c>ChainPolicy</c>/<c>UsagePace</c> are.</summary>
public sealed class AgentFileSyncPolicyTests
{
    [Fact]
    public void Already_answered_never_asks_again_however_the_files_look()
    {
        Assert.Equal(AgentFileSyncWizardMode.None, AgentFileSyncPolicy.Decide(
            claudeExists: true, agentsExists: true, contentsDiffer: () => true,
            needsClaudeStyle: true, needsAgentsStyleOnly: true,
            wizardAlreadyAnswered: true, globallyEnabled: true));
    }

    [Fact]
    public void Global_switch_off_never_asks()
    {
        Assert.Equal(AgentFileSyncWizardMode.None, AgentFileSyncPolicy.Decide(
            claudeExists: true, agentsExists: true, contentsDiffer: () => true,
            needsClaudeStyle: true, needsAgentsStyleOnly: true,
            wizardAlreadyAnswered: false, globallyEnabled: false));
    }

    [Fact]
    public void Both_files_present_and_different_asks_which_is_authoritative()
    {
        Assert.Equal(AgentFileSyncWizardMode.AskEnableAndPickAuthoritative, AgentFileSyncPolicy.Decide(
            claudeExists: true, agentsExists: true, contentsDiffer: () => true,
            needsClaudeStyle: false, needsAgentsStyleOnly: false,
            wizardAlreadyAnswered: false, globallyEnabled: true));
    }

    [Fact]
    public void Both_files_present_and_identical_only_asks_to_enable()
    {
        Assert.Equal(AgentFileSyncWizardMode.AskEnableOnly, AgentFileSyncPolicy.Decide(
            claudeExists: true, agentsExists: true, contentsDiffer: () => false,
            needsClaudeStyle: false, needsAgentsStyleOnly: false,
            wizardAlreadyAnswered: false, globallyEnabled: true));
    }

    [Fact]
    public void Only_agents_md_exists_and_a_claude_tile_needs_the_other_one()
    {
        Assert.Equal(AgentFileSyncWizardMode.AskEnableOnly, AgentFileSyncPolicy.Decide(
            claudeExists: false, agentsExists: true, contentsDiffer: () => false,
            needsClaudeStyle: true, needsAgentsStyleOnly: false,
            wizardAlreadyAnswered: false, globallyEnabled: true));
    }

    [Fact]
    public void Only_agents_md_exists_and_nothing_needs_claude_md_asks_nothing()
    {
        Assert.Equal(AgentFileSyncWizardMode.None, AgentFileSyncPolicy.Decide(
            claudeExists: false, agentsExists: true, contentsDiffer: () => false,
            needsClaudeStyle: false, needsAgentsStyleOnly: true,
            wizardAlreadyAnswered: false, globallyEnabled: true));
    }

    [Fact]
    public void Only_claude_md_exists_and_a_non_claude_tile_needs_the_other_one()
    {
        Assert.Equal(AgentFileSyncWizardMode.AskEnableOnly, AgentFileSyncPolicy.Decide(
            claudeExists: true, agentsExists: false, contentsDiffer: () => false,
            needsClaudeStyle: false, needsAgentsStyleOnly: true,
            wizardAlreadyAnswered: false, globallyEnabled: true));
    }

    [Fact]
    public void Only_claude_md_exists_and_nothing_needs_agents_md_asks_nothing()
    {
        Assert.Equal(AgentFileSyncWizardMode.None, AgentFileSyncPolicy.Decide(
            claudeExists: true, agentsExists: false, contentsDiffer: () => false,
            needsClaudeStyle: true, needsAgentsStyleOnly: false,
            wizardAlreadyAnswered: false, globallyEnabled: true));
    }

    [Fact]
    public void Neither_file_exists_asks_nothing()
    {
        Assert.Equal(AgentFileSyncWizardMode.None, AgentFileSyncPolicy.Decide(
            claudeExists: false, agentsExists: false, contentsDiffer: () => false,
            needsClaudeStyle: true, needsAgentsStyleOnly: true,
            wizardAlreadyAnswered: false, globallyEnabled: true));
    }

    /// <summary>Reading both files is the expensive part of the question, and an answered workspace is
    /// asked it again on every tile-tree change — so it must not be read at all.</summary>
    [Fact]
    public void An_answered_workspace_never_compares_the_two_files()
    {
        var compared = false;

        var mode = AgentFileSyncPolicy.Decide(
            claudeExists: true, agentsExists: true,
            contentsDiffer: () => { compared = true; return true; },
            needsClaudeStyle: true, needsAgentsStyleOnly: true,
            wizardAlreadyAnswered: true, globallyEnabled: true);

        Assert.Equal(AgentFileSyncWizardMode.None, mode);
        Assert.False(compared);
    }
}
