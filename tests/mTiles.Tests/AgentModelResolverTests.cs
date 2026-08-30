using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which model a launch runs on, asked the same way wherever an agent is started.
/// </summary>
/// <remarks>The rule used to live in the agent tile alone, and the Goal tile did not have it: a goal on
/// an instance asking for the first loaded model ran with no model at all while its environment still
/// pointed at the local server. That is why this is a service with a test rather than a method on a view
/// model.</remarks>
public class AgentModelResolverTests
{
    private static readonly IAiAgent Claude = AiAgentCatalog.Find("claude")!;

    /// <summary>A named model is the answer, unchanged and without asking anybody.</summary>
    [Fact]
    public async Task A_named_model_passes_through()
    {
        var (model, problem) = await AgentModelResolver.ResolveAsync(new AppSettings(), Claude,
            new AiAgentInstance { AgentId = Claude.Id, Model = "claude-opus-5" });

        Assert.Null(problem);
        Assert.Equal("claude-opus-5", model);
    }

    /// <summary>No model at all is the agent's own choice, which is a configuration and not a fault.
    /// </summary>
    [Fact]
    public async Task No_model_is_not_a_problem()
    {
        var (model, problem) = await AgentModelResolver.ResolveAsync(new AppSettings(), Claude,
            new AiAgentInstance { AgentId = Claude.Id });

        Assert.Null(problem);
        Assert.Equal("", model);
    }

    /// <summary>
    /// The sentinel with nothing to ask is refused rather than dropped.
    /// </summary>
    /// <remarks>Dropping it is the failure this exists to prevent: the launch succeeds, the model is
    /// whatever the CLI would have picked, and the only record of it is a line in a log file.</remarks>
    [Fact]
    public async Task First_loaded_without_a_provider_is_refused()
    {
        var (model, problem) = await AgentModelResolver.ResolveAsync(new AppSettings(), Claude,
            new AiAgentInstance { AgentId = Claude.Id, Model = AiModelChoice.FirstLoaded });

        Assert.Null(model);
        Assert.Contains("provider", problem);
    }

    /// <summary>A model named on an agent that has no way of being told one is said out loud, because
    /// the setting is on the instance's row and does nothing at all.</summary>
    [Fact]
    public async Task A_model_on_an_agent_that_cannot_carry_one_is_refused()
    {
        // The generic agent is the one that says so: nothing is known about the binary, so there is no
        // flag and no variable to put a model in, and pretending otherwise is a launch that runs on
        // something nobody chose.
        var agent = new GenericAgent("mystery");
        Assert.False(agent.AcceptsModel);

        var (model, problem) = await AgentModelResolver.ResolveAsync(new AppSettings(), agent,
            new AiAgentInstance { AgentId = agent.Id, Model = "something" });

        Assert.Null(model);
        Assert.Contains(agent.DisplayName, problem);
    }

    /// <summary>
    /// An instance whose provider has been deleted refuses the launch instead of starting quietly.
    /// </summary>
    /// <remarks>The chooser and the Goal tile's list already hide it
    /// (<c>AiAgentCatalog.IsAvailable</c>), but a tile restored from a layout is handed its stored
    /// instance without anybody asking — and that is the one path where the user is not choosing
    /// anything, so it is the one where a silent swap to the CLI's own account would never be
    /// noticed.</remarks>
    [Fact]
    public async Task A_deleted_provider_is_refused()
    {
        var (model, problem) = await AgentModelResolver.ResolveAsync(new AppSettings(), Claude,
            new AiAgentInstance { AgentId = Claude.Id, ProviderInstanceId = "gone" });

        Assert.Null(model);
        Assert.Contains("provider", problem);
    }

    /// <summary>A pairing the flavors do not allow is said out loud for the same reason.</summary>
    /// <remarks>codex speaks <c>OpenAiResponses</c> and Ollama serves
    /// <c>OpenAiChatCompletions</c>, so an instance repointed at one after being configured for the
    /// other cannot authenticate at all.</remarks>
    [Fact]
    public async Task An_incompatible_provider_is_refused()
    {
        var codex = AiAgentCatalog.Find("codex")!;
        var settings = new AppSettings();
        var provider = new AiProviderInstance { ProviderId = "ollama" };
        settings.AiProviderInstances.Add(provider);

        var (model, problem) = await AgentModelResolver.ResolveAsync(settings, codex,
            new AiAgentInstance { AgentId = codex.Id, ProviderInstanceId = provider.Id });

        Assert.Null(model);
        Assert.Contains(codex.DisplayName, problem);
    }
}
