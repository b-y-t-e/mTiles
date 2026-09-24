using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a terminal agent tile says it is running, beside its name.
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

    /// <summary>The instance's name and the model, the model shortened for the narrowest line in the
    /// application — and only there; the full id is a tooltip away and unchanged wherever it is stored.
    /// </summary>
    /// <remarks>Read through <see cref="IDescribedTile"/>, which is how the header reads it.</remarks>
    [Theory]
    [InlineData("Mine", "z-ai/glm-5.3-flash", "Mine · glm-5.3-flash")]
    [InlineData("Mine", "openai/gpt-5.5", "Mine · gpt-5.5")]
    [InlineData("Mine", "gemma-4-12b", "Mine · gemma-4-12b")]
    [InlineData("Mine", "a/b/c", "Mine · c")]
    // The sentinel is not a model name and is never shown as one.
    [InlineData("Local", AiModelChoice.FirstLoaded, "Local")]
    // No model is no second half: a word for its absence fills the scarcest line with nothing.
    [InlineData("Mine", "", "Mine")]
    // An unnamed instance falls back to the CLI's own name rather than showing nothing.
    [InlineData("", "", "Claude Code")]
    public void It_names_the_instance_and_the_part_of_the_model_that_tells_models_apart(
        string name, string model, string expected)
    {
        IDescribedTile tile = TileOn(new AiAgentInstance { AgentId = "claude", Name = name, Model = model });

        Assert.Equal(expected, tile.HeaderNote);
    }

    /// <summary>A deleted instance leaves a tile that still says which agent it is.</summary>
    /// <remarks>The tile keeps running on the seeded instance for that agent — the header should not go
    /// blank because a row in Settings was removed.</remarks>
    [Fact]
    public void A_tile_whose_instance_is_gone_still_names_its_agent()
    {
        var agent = AiAgentCatalog.Find("claude")!;
        var tile = new TerminalAgentTileViewModel(_directory.Path, null, _settings.Service, agent,
            instanceId: "never-existed", tileId: () => Guid.NewGuid().ToString());

        Assert.Contains("Claude Code", tile.HeaderNote);
    }

    private TerminalAgentTileViewModel TileOn(AiAgentInstance instance)
    {
        _settings.Service.Settings.AiAgentInstances.Add(instance);
        var agent = AiAgentCatalog.Find(instance.AgentId)!;

        return new TerminalAgentTileViewModel(_directory.Path, null, _settings.Service, agent, instance.Id,
            tileId: () => Guid.NewGuid().ToString());
    }
}
