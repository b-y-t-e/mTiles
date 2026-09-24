using System.Text.Json.Nodes;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;
using mTiles.Services.Providers;
using mTiles.Services.Tiles;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>Switching a conversation's model, mode and effort, and the images that go with a message.</summary>
public class ConversationSettingsTests
{
    [Fact]
    public void A_change_is_laid_over_what_the_tile_already_overrides()
    {
        var overrides = new SessionOverrides(Model: "opus")
            .With(new SessionSettings(Mode: "Plan"))
            .With(new SessionSettings(Effort: "Max"))
            .With(new SessionSettings(Mode: "not-a-mode"));

        Assert.Equal(new SessionOverrides("opus", AiBehaviour.Plan, AiEffort.Max), overrides);
    }

    [Fact]
    public void Overrides_change_a_copy_and_never_the_instance_every_tile_shares()
    {
        var instance = AiAgentCatalog.SeedInstanceFor(AiAgentCatalog.Find("claude")!);
        instance.Model = "sonnet";
        instance.ExtraArgs = ["--add-dir", "x"];

        var run = new SessionOverrides("opus", AiBehaviour.BypassPermissions).ApplyTo(instance);

        Assert.Equal(("opus", AiBehaviour.BypassPermissions, instance.Id), (run.Model, run.DefaultBehaviour, run.Id));
        Assert.Equal(["--add-dir", "x"], run.ExtraArgs);
        Assert.Equal("sonnet", instance.Model);
        Assert.Same(instance, SessionOverrides.None.ApplyTo(instance));
    }

    [Theory]
    [InlineData("pi", "openrouter/z-ai/glm-5.3-flash")]
    [InlineData("opencode", "openrouter/openrouter/auto")]
    public void A_model_the_session_listed_is_qualified_once_again_at_the_next_launch(string agentId, string listed)
    {
        var agent = AiAgentCatalog.Find(agentId)!;
        var provider = new AiProviderInstance { ProviderId = "openrouter", ApiKey = "sk-test" };
        var instance = AiAgentCatalog.SeedInstanceFor(agent);
        instance.ApiAccountId = provider.Id;
        var settings = new AppSettings();
        settings.AiProviderInstances.Add(provider);

        var stored = agent.InstanceModel(AgentRuntime.For(settings, instance, agent: agent), listed);
        var relaunched = new SessionOverrides(stored).ApplyTo(instance);

        Assert.Equal(listed, agent.QualifiedModel(AgentRuntime.For(settings, relaunched, agent: agent)));
    }

    [Fact]
    public void An_instance_on_a_provider_is_offered_only_that_providers_models()
    {
        var agent = AiAgentCatalog.Find("opencode")!;
        var provider = new AiProviderInstance { ProviderId = "openrouter", ApiKey = "sk-test" };
        var instance = AiAgentCatalog.SeedInstanceFor(agent);
        var settings = new AppSettings();
        settings.AiProviderInstances.Add(provider);
        SessionOption[] listed =
            [new("anthropic/claude-sonnet-4", "a"), new("openrouter/anthropic/claude-sonnet-4", "b")];

        var onItsOwnAccount = SessionSettingOptions.ModelsOfProvider(listed, AgentRuntime.For(settings, instance, agent: agent));
        instance.ApiAccountId = provider.Id;
        var onTheProvider = SessionSettingOptions.ModelsOfProvider(listed, AgentRuntime.For(settings, instance, agent: agent));

        Assert.Equal(2, onItsOwnAccount.Count);
        Assert.Equal(["openrouter/anthropic/claude-sonnet-4"], onTheProvider.Select(model => model.Id));
    }

    [Fact]
    public void A_copy_of_an_instance_carries_every_property_and_shares_no_collection()
    {
        var instance = AiAgentCatalog.SeedInstanceFor(AiAgentCatalog.Find("claude")!);
        instance.ExtraEnv["A"] = "1";

        var copy = instance.Clone();
        copy.ExtraEnv["B"] = "2";

        foreach (var property in typeof(AiAgentInstance).GetProperties().Where(p => p.Name != nameof(AiAgentInstance.ExtraEnv)))
            Assert.Equivalent(property.GetValue(instance), property.GetValue(copy));
        Assert.False(instance.ExtraEnv.ContainsKey("B"));
    }

    [Fact]
    public void Modes_and_efforts_are_offered_in_the_application_s_words_narrowed_to_the_agent()
    {
        var agent = AiAgentCatalog.Find("pi")!;
        var instance = AiAgentCatalog.SeedInstanceFor(agent);

        var modes = SessionSettingOptions.Modes(agent, instance);

        Assert.Equal(agent.SupportedBehaviours(instance, AiUsage.Interactive).Select(m => m.ToString()), modes.Select(m => m.Id));
        Assert.All(SessionSettingOptions.Efforts(agent, instance), e => Assert.NotNull(SessionSettingOptions.ParseEffort(e.Id)));
    }

    [Fact]
    public void A_tile_keeps_its_overrides_in_the_layout()
    {
        using var settings = new TempSettings();
        var kind = TestTiles.Catalog(settings.Service).Entries.Single(e => e.Kind.Id == TileKindIds.AgentConversation).Kind;
        var context = new TileContext(Path.GetTempPath(), settings.Service);
        var state = new JsonObject
        {
            [AgentStateKeys.AgentIdKey] = "claude",
            [AgentConversationTileKind.ModelKey] = "opus",
            [AgentConversationTileKind.ModeKey] = "Plan",
            [AgentConversationTileKind.EffortKey] = "from-a-newer-build",
        };

        using var tile = (AgentConversationTileViewModel)kind.Create(context, state);
        var saved = kind.Save(tile)!;

        Assert.Equal(new SessionOverrides("opus", AiBehaviour.Plan), tile.Overrides);
        Assert.Equal("opus", saved[AgentConversationTileKind.ModelKey]!.GetValue<string>());
        Assert.Equal("Plan", saved[AgentConversationTileKind.ModeKey]!.GetValue<string>());
        Assert.Null(saved[AgentConversationTileKind.EffortKey]);
    }

    [Fact]
    public async Task Choosing_a_setting_before_the_agent_runs_is_kept_and_saved_but_not_sent()
    {
        using var settings = new TempSettings();
        var saves = 0;
        using var vm = NewTile(settings, () => saves++);

        await vm.ChangeSettingsAsync(new SessionSettings(Model: "opus"));

        Assert.Equal("opus", vm.Overrides.Model);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void Drawing_what_the_session_runs_as_is_not_a_choice_somebody_made()
    {
        using var settings = new TempSettings();
        var saves = 0;
        using var vm = NewTile(settings, () => saves++);

        vm.Draw(ConversationReducer.Replay(
        [
            new SessionOptionsReported([], [new SessionOption("Plan", "plan"), new SessionOption("Auto", "auto")], []),
            new SessionConfigured(null, "Plan", null),
        ]));

        Assert.Equal("Plan", vm.SelectedMode?.Id);
        Assert.Equal(0, saves);
        Assert.True(vm.Overrides.IsEmpty);
    }

    [Fact]
    public void An_image_too_large_or_one_too_many_is_refused_with_a_sentence()
    {
        using var settings = new TempSettings();
        using var vm = NewTile(settings, null);
        var small = new ImageAttachment("image/png", AgentConversationViewTests.OnePixelPng);
        var huge = new ImageAttachment("image/png", new string('A', (AgentConversationTileViewModel.MaxImageBytes + 1024) * 4 / 3));

        vm.AttachImage(huge);
        Assert.False(vm.Attachments.HasItems);
        Assert.NotNull(vm.ComposerNotice);

        for (var i = 0; i <= AgentConversationTileViewModel.MaxImages; i++) vm.AttachImage(small);
        Assert.Equal(AgentConversationTileViewModel.MaxImages, vm.Attachments.Items.Count);
        Assert.NotNull(vm.ComposerNotice);

        vm.RemoveAttachmentCommand.Execute(vm.Attachments.Items[0]);
        Assert.Equal(AgentConversationTileViewModel.MaxImages - 1, vm.Attachments.Items.Count);
    }

    /// <summary>
    /// Opening a conversation is being handed the end of it. A transcript replayed out of the store
    /// arrives as one change to the timeline, and the anchor cannot tell that from a reader who had
    /// scrolled — so the view model says which of the two it was.
    /// </summary>
    [Fact]
    public void A_transcript_that_has_only_now_arrived_says_so_once()
    {
        using var settings = new TempSettings();
        using var vm = NewTile(settings, null);
        var opened = 0;
        vm.TranscriptOpened += () => opened++;

        vm.Draw(ConversationReducer.Replay([new UserMessageAdded("m1", "hello", [])]));
        Assert.Equal(1, opened);

        // Every line after it is the same conversation carrying on, and the reader is left where
        // they were.
        vm.Draw(ConversationReducer.Replay(
            [new UserMessageAdded("m1", "hello", []), new UserMessageAdded("m2", "again", [])]));
        Assert.Equal(1, opened);
    }

    private static AgentConversationTileViewModel NewTile(TempSettings settings, Action? requestSave) =>
        ConversationTiles.New(settings, requestSave: requestSave);
}
