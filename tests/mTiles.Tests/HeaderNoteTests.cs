using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What an agent tile says it is running, beside its name.
/// </summary>
/// <remarks>
/// <para>Two tiles both called <c>Agent#N</c> may be Claude Code on a subscription and Codex on
/// OpenRouter, which is the whole reason <c>IDescribedTile</c> exists — and it arrived with no test at
/// all while everything around it was pinned closely. Two rules in here are not obvious: the sentinel
/// must never be shown as if it were a model name, and the note has to be the <em>instance's</em> name
/// rather than the CLI's, because the instance is the thing the user configured and named.</para>
/// <para>Nothing is launched: the note is read off a tile that has been constructed and no more.</para>
/// </remarks>
public sealed class HeaderNoteTests : IDisposable
{
    private readonly TempSettings _settings = new();
    private readonly TempDirectory _directory = new();

    public void Dispose()
    {
        _settings.Dispose();
        _directory.Dispose();
    }

    /// <summary>The instance's name and the model, in the words the Settings row uses.</summary>
    [Fact]
    public void It_names_the_instance_and_the_model()
    {
        var tile = TileOn(new AiAgentInstance
        {
            AgentId = "claude", Name = "Claude on OpenRouter", Model = "z-ai/glm-5.3-flash",
        });

        Assert.Equal("Claude on OpenRouter · glm-5.3-flash", tile.HeaderNote);
    }

    /// <summary>
    /// The model is shortened for the narrowest line in the application, and only there.
    /// </summary>
    /// <remarks>Ids are namespaced by whoever published them; the vendor is dropped from a line that is
    /// already the second thing to give way, and the full name is a tooltip away and unchanged
    /// everywhere it is stored or sent.</remarks>
    [Theory]
    [InlineData("z-ai/glm-5.3-flash", "glm-5.3-flash")]
    [InlineData("openai/gpt-5.5", "gpt-5.5")]
    [InlineData("gemma-4-12b", "gemma-4-12b")]
    [InlineData("a/b/c", "c")]
    public void The_model_is_shortened_to_the_part_that_tells_models_apart(string model, string shown)
    {
        var tile = TileOn(new AiAgentInstance { AgentId = "claude", Name = "Mine", Model = model });

        Assert.Equal($"Mine · {shown}", tile.HeaderNote);
    }

    /// <summary>
    /// The sentinel is not a model name and is never shown as one.
    /// </summary>
    /// <remarks>It reaches here before a launch has resolved it — <c>__first_loaded__</c> in a header
    /// is worse than nothing, and "whatever the server has loaded" is not something the narrowest line
    /// on screen should spend itself saying.</remarks>
    [Fact]
    public void The_first_loaded_sentinel_is_not_shown()
    {
        var tile = TileOn(new AiAgentInstance
        {
            AgentId = "claude", Name = "Local", Model = AiModelChoice.FirstLoaded,
        });

        Assert.Equal("Local", tile.HeaderNote);
        Assert.DoesNotContain("_", tile.HeaderNote);
    }

    /// <summary>An instance naming no model says nothing about one.</summary>
    /// <remarks>"Whatever the agent picks" is not a model, and printing a word for its absence fills
    /// the scarcest line on screen with the absence of information.</remarks>
    [Fact]
    public void No_model_is_no_second_half()
    {
        var tile = TileOn(new AiAgentInstance { AgentId = "claude", Name = "Mine", Model = "" });

        Assert.Equal("Mine", tile.HeaderNote);
    }

    /// <summary>
    /// An unnamed instance falls back to the CLI's own name rather than showing nothing.
    /// </summary>
    /// <remarks>The instance's name comes first because it is what the user configured and what every
    /// chooser identifies the row by; the CLI's name is what is left when there is none.</remarks>
    [Fact]
    public void An_unnamed_instance_is_named_by_its_cli()
    {
        var tile = TileOn(new AiAgentInstance { AgentId = "claude", Name = "", Model = "" });

        Assert.Equal("Claude Code", tile.HeaderNote);
    }

    /// <summary>A deleted instance leaves a tile that still says which agent it is.</summary>
    /// <remarks>The tile keeps running on the seeded instance for that agent — the header should not go
    /// blank because a row in Settings was removed.</remarks>
    [Fact]
    public void A_tile_whose_instance_is_gone_still_names_its_agent()
    {
        var agent = AiAgentCatalog.Find("claude")!;
        var tile = new AgentTileViewModel(_directory.Path, null, _settings.Service, agent,
            instanceId: "never-existed", tileId: () => Guid.NewGuid().ToString());

        Assert.Contains("Claude Code", tile.HeaderNote);
    }

    /// <summary>An agent tile announces the capability; the header reads it through the interface.</summary>
    [Fact]
    public void An_agent_tile_is_a_described_tile()
    {
        var tile = TileOn(new AiAgentInstance { AgentId = "claude", Name = "Mine" });

        Assert.IsAssignableFrom<IDescribedTile>(tile);
    }

    private AgentTileViewModel TileOn(AiAgentInstance instance)
    {
        _settings.Service.Settings.AiAgentInstances.Add(instance);
        var agent = AiAgentCatalog.Find(instance.AgentId)!;

        return new AgentTileViewModel(_directory.Path, null, _settings.Service, agent, instance.Id,
            tileId: () => Guid.NewGuid().ToString());
    }
}
