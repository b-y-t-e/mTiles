using mTiles.AgentSessions.Events;
using mTiles.Models;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Providers;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>What opening a stored conversation takes back, and what it only says.</summary>
public class StoredSessionPolicyTests
{
    private static readonly AiAgentInstance Here = Row("here", signIn: null);
    private static readonly AiAgentInstance Other = Row("other", signIn: "second");
    private static readonly SessionAccount Running = new("claude", Here.Id, Here.Name);

    public static TheoryData<string, SessionAccount?, string?, bool> Accounts => new()
    {
        { "nothing said", null, null, false },
        { "the account running", new SessionAccount("claude", "here", "Here"), null, false },
        { "another agent's stretch", new SessionAccount("codex", "other", "Other", "second"), null, false },
        { "a row still here", new SessionAccount("claude", "other", "Other", "second"), "other", false },
        { "a row gone, same login", new SessionAccount("claude", "gone", "Gone"), null, false },
        { "a row gone, another login", new SessionAccount("claude", "gone", "Gone", "second"), null, true },
        { "this row, sign-in since edited", new SessionAccount("claude", "here", "Here", "second"), null, true },
    };

    [Theory]
    [MemberData(nameof(Accounts))]
    public void The_stored_account_is_taken_back_or_its_move_is_said(
        string _, SessionAccount? stored, string? takes, bool says)
    {
        var decision = StoredSessionPolicy.DecideAccount(stored, Running, [Here, Other]);

        Assert.Equal(takes, decision.InstanceToTake?.Id);
        Assert.Equal(says, decision.Notice is not null);
    }

    [Fact]
    public void An_edited_sign_in_is_not_called_an_account_that_is_gone()
    {
        var notice = StoredSessionPolicy.DecideAccount(
            new SessionAccount("claude", "here", "Here", "second"), Running, [Here]).Notice;

        Assert.DoesNotContain("no longer available", notice);
        Assert.Contains("\"Here\"", notice);
    }

    public static TheoryData<string, SessionSettings, SessionOverrides, bool, SessionSettings> Settings => new()
    {
        { "restores what the instance would not", new SessionSettings("opus", "Plan", "Max"),
            SessionOverrides.None, true, new SessionSettings("opus", "Plan", "Max") },
        { "leaves the instance's own answers", new SessionSettings("sonnet", "Auto", "High"),
            SessionOverrides.None, true, new SessionSettings() },
        { "never adopts bypass", new SessionSettings(null, "BypassPermissions", null),
            SessionOverrides.None, true, new SessionSettings() },
        { "never overrules the user", new SessionSettings("opus", "Plan", "Max"),
            new SessionOverrides("haiku", AiBehaviour.Ask, AiEffort.Low), true, new SessionSettings() },
        { "fills only what the user left", new SessionSettings("opus", "Plan", "Max"),
            new SessionOverrides("haiku", AiBehaviour.Ask, null), true, new SessionSettings(null, null, "Max") },
        { "leaves a model spelled for another account", new SessionSettings("opus", "Plan", null),
            SessionOverrides.None, false, new SessionSettings(null, "Plan") },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void Settings_are_restored_only_where_nobody_else_answers(
        string _, SessionSettings stored, SessionOverrides overrides, bool modelStillFits, SessionSettings expected)
    {
        var instance = Row("here", signIn: null);
        instance.Model = "sonnet";
        instance.DefaultBehaviour = AiBehaviour.Auto;
        instance.DefaultEffort = AiEffort.High;

        Assert.Equal(expected, StoredSessionPolicy.SettingsToRestore(stored, overrides, instance, modelStillFits));
    }

    [Fact]
    public void A_model_resolved_at_every_launch_is_never_written_down()
    {
        var instance = Row("here", signIn: null);
        instance.Model = AiModelChoice.FirstLoaded;

        var restored = StoredSessionPolicy.SettingsToRestore(
            new SessionSettings("qwen3"), SessionOverrides.None, instance, modelStillFits: true);

        Assert.Null(restored.Model);
    }

    private static AiAgentInstance Row(string id, string? signIn) =>
        new() { Id = id, AgentId = "claude", Name = char.ToUpperInvariant(id[0]) + id[1..], SignInId = signIn ?? "" };
}
