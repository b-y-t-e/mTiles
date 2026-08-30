using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The AI page: what typing into it actually stores, and what it refuses to store without asking.
/// </summary>
/// <remarks>Everything on this page could already be launched through and tested; none of it could be
/// typed anywhere, so the page is the whole of what these assertions are about.</remarks>
public sealed class AiSettingsPageTests : IDisposable
{
    private readonly TempSettings _settings = new();

    public void Dispose() => _settings.Dispose();

    private SettingsViewModel OnTheAiTab()
    {
        var vm = new SettingsViewModel(_settings.Service);
        vm.SelectTabCommand.Execute(SettingsTabs.Ai);
        return vm;
    }

    /// <summary>A provider typed in is a provider stored — the whole point of the page.</summary>
    [Fact]
    public void A_provider_can_be_added()
    {
        var vm = OnTheAiTab();
        vm.AddProviderInstanceCommand.Execute(null);
        vm.EditProviderName = "Work";
        vm.EditProviderKind = "OpenRouter";
        vm.EditProviderApiKey = "sk-test";
        vm.SaveProviderInstanceCommand.Execute(null);

        var stored = Assert.Single(_settings.Service.Settings.AiProviderInstances);
        Assert.Equal("Work", stored.Name);
        Assert.Equal("openrouter", stored.ProviderId);
        Assert.Equal("sk-test", stored.ApiKey);
        Assert.False(vm.IsEditingAnything);
    }

    /// <summary>An agent instance points at a provider the list shows, and stores its id.</summary>
    [Fact]
    public void An_agent_instance_is_pointed_at_a_provider()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentName = "Claude on OpenRouter";
        vm.EditAgentProvider = vm.ProviderChoices.Single(choice => choice.Id == "p1");
        vm.EditAgentModel = "some/model";
        vm.SaveAgentInstanceCommand.Execute(null);

        var stored = _settings.Service.Settings.AiAgentInstances[^1];
        Assert.Equal("Claude on OpenRouter", stored.Name);
        Assert.Equal("p1", stored.ProviderInstanceId);
        Assert.Equal("some/model", stored.Model);
    }

    /// <summary>Two providers named alike are still two providers: the one that was chosen is the one
    /// stored, and the one the form shows when it is reopened.</summary>
    /// <remarks>Nothing makes an instance's name unique — a new one is seeded with the provider's own
    /// display name — so two keys for the same service are two identically spelled rows. Keyed by name,
    /// both the save and the reopen answered with the first of them, and the agent authenticated as the
    /// wrong account with nothing on screen saying so.</remarks>
    [Fact]
    public void Two_providers_with_the_same_name_are_told_apart()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p1", ProviderId = "openrouter", Name = "OpenRouter" });
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p2", ProviderId = "openrouter", Name = "OpenRouter" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentName = "On the second key";
        vm.EditAgentProvider = vm.ProviderChoices.Single(choice => choice.Id == "p2");
        vm.SaveAgentInstanceCommand.Execute(null);

        var stored = _settings.Service.Settings.AiAgentInstances[^1];
        Assert.Equal("p2", stored.ProviderInstanceId);

        var row = vm.AgentInstances.Single(r => r.Name == "On the second key");
        vm.EditAgentInstanceCommand.Execute(row);
        Assert.Equal("p2", vm.EditAgentProvider?.Id);
    }

    /// <summary>A provider the agent cannot speak to is not offered at all.</summary>
    /// <remarks>Stored, the pairing makes the instance unavailable everywhere — gone from the Agent
    /// tile's chooser and from the Goal tile's list — so offering it here is offering a configuration
    /// that stops working the moment it is saved.</remarks>
    [Fact]
    public void An_incompatible_provider_is_not_offered()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "ollama", Name = "Ollama" });
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "router", ProviderId = "openrouter", Name = "Work" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "Codex";

        // Codex speaks /v1/responses; Ollama serves /v1/chat/completions.
        Assert.DoesNotContain(vm.ProviderChoices, choice => choice.Id == "local");
        Assert.Contains(vm.ProviderChoices, choice => choice.Id == "router");
    }

    /// <summary>Choosing an agent that cannot use the provider already selected clears the choice
    /// rather than leaving a pairing the chooser no longer shows.</summary>
    [Fact]
    public void Changing_the_agent_drops_a_provider_it_cannot_use()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "ollama", Name = "Ollama" });

        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentAgentName = "OpenCode";
        vm.EditAgentProvider = vm.ProviderChoices.Single(choice => choice.Id == "local");

        vm.EditAgentAgentName = "Codex";

        Assert.Equal(ProviderChoice.OwnAccount, vm.EditAgentProvider);
    }

    /// <summary>A stored pairing that cannot work says so on its row — the only place that can.</summary>
    [Fact]
    public void An_incompatible_instance_says_why_it_is_not_offered()
    {
        _settings.Service.Settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "local", ProviderId = "ollama", Name = "Ollama" });
        _settings.Service.Settings.AiAgentInstances.Add(new AiAgentInstance
        {
            AgentId = "codex", Name = "Codex on Ollama", ProviderInstanceId = "local",
        });

        var vm = OnTheAiTab();

        var row = vm.AgentInstances.Single(r => r.Name == "Codex on Ollama");
        Assert.True(row.IsIncompatible);
        Assert.Contains("Ollama", row.IncompatibleNote);
    }

    /// <summary>The effort chooser offers what the agent accepts, and a level it does not falls back
    /// to the tool's own default rather than staying selected in a list that no longer holds it.
    /// </summary>
    [Fact]
    public void The_effort_chooser_follows_the_agent()
    {
        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentEffort = AiEfforts.Label(AiEffort.High);

        // Measured: opencode's effort is not something the CLI takes at all.
        vm.EditAgentAgentName = "OpenCode";

        Assert.Equal([AiEfforts.Label(AiEffort.ToolDefault)], vm.EffortLabels);
        Assert.Equal(AiEfforts.Label(AiEffort.ToolDefault), vm.EditAgentEffort);
    }

    /// <summary>The behaviour chooser offers what the agent has a gate for, and a mode it has none for
    /// falls back to the tool's own default.</summary>
    /// <remarks>Offering <c>plan</c> for an agent that cannot plan is a row promising a restriction
    /// that does not exist: it is stored, rounded away, and the agent runs unrestricted anyway.
    /// </remarks>
    [Fact]
    public void The_behaviour_chooser_follows_the_agent()
    {
        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentBehaviour = AiBehaviours.Label(AiBehaviour.Plan);

        // Measured: pi has no permission gate at all, so only bypass and the tool's default are real.
        vm.EditAgentAgentName = "Pi Agent";

        Assert.Equal(
            [AiBehaviours.Label(AiBehaviour.BypassPermissions),
             AiBehaviours.Label(AiBehaviour.ToolDefault)],
            vm.BehaviourLabels);
        Assert.Equal(AiBehaviours.Label(AiBehaviour.ToolDefault), vm.EditAgentBehaviour);
    }

    /// <summary>
    /// Turning every safeguard off is asked about, and an unwired dialog answers no.
    /// </summary>
    /// <remarks>The same rule the Goal tile's strip follows, and for the same reason: it applies
    /// wherever the instance is used, and the first place it is noticed is a run that has already
    /// happened.</remarks>
    [Fact]
    public void Bypass_is_not_stored_without_an_answer()
    {
        var vm = OnTheAiTab();
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentBehaviour = AiBehaviours.Label(AiBehaviour.BypassPermissions);
        vm.SaveAgentInstanceCommand.Execute(null);

        Assert.DoesNotContain(_settings.Service.Settings.AiAgentInstances,
            instance => instance.DefaultBehaviour == AiBehaviour.BypassPermissions);
    }

    /// <summary>Agreeing stores it — or the question above would be a refusal dressed as one.</summary>
    [Fact]
    public void Bypass_is_stored_when_it_is_agreed_to()
    {
        var vm = OnTheAiTab();
        vm.ConfirmAction = _ => Task.FromResult(true);
        vm.AddAgentInstanceCommand.Execute(null);
        vm.EditAgentBehaviour = AiBehaviours.Label(AiBehaviour.BypassPermissions);
        vm.SaveAgentInstanceCommand.Execute(null);

        Assert.Equal(AiBehaviour.BypassPermissions,
            _settings.Service.Settings.AiAgentInstances[^1].DefaultBehaviour);
    }

    /// <summary>Deleting is destructive, so an unwired dialog answers no here too.</summary>
    [Fact]
    public void Nothing_is_deleted_without_an_answer()
    {
        _settings.Service.Settings.AiAgentInstances.Add(
            new AiAgentInstance { AgentId = AiAgentCatalog.All[0].Id, Name = "Mine" });

        var vm = OnTheAiTab();
        var row = vm.AgentInstances.Single(r => r.Name == "Mine");
        vm.DeleteAgentInstanceCommand.Execute(row);

        Assert.Contains(_settings.Service.Settings.AiAgentInstances,
            instance => instance.Name == "Mine");
    }

    /// <summary>Every seeded instance has a row, so a machine that has never been in here still shows
    /// what it can run.</summary>
    [Fact]
    public void The_seeded_instances_are_listed()
    {
        // Seeded by the settings service itself, so this is the state a first run opens the page in.
        var rows = OnTheAiTab().AgentInstances;

        Assert.Equal(AiAgentCatalog.All.Count, rows.Count);
        foreach (var agent in AiAgentCatalog.All)
            Assert.Contains(rows, row => row.AgentName == agent.DisplayName);
    }
}
