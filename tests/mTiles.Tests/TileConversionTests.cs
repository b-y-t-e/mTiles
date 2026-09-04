using mTiles.Models;
using mTiles.Services.Tiles;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a tile about to be changed into another kind promises the user.
/// </summary>
/// <remarks>
/// The sentence is the whole of the safety analysis in <c>docs/TILE-KIND-CHANGE.md</c> §2, and it is a
/// judgement rather than a mechanism: a shell and its children die and cannot be brought back, a note's
/// file is left exactly where it was. One sentence for all of them would be a lie in six directions,
/// which is what a table test is for — and the last one here is the guard against a kind being added
/// with nothing said about it.
/// </remarks>
public sealed class TileConversionTests
{
    [Theory]
    [InlineData(TileKindIds.Terminal, "The shell and everything running in it will be ended.")]
    [InlineData(TileKindIds.Agent, "The conversation stays with the agent; the tile will stop opening it.")]
    [InlineData(TileKindIds.Note, "The file stays in .mtiles/notes/; the tile will stop pointing at it.")]
    [InlineData(TileKindIds.Todo, "The file stays in .mtiles/todos/; the tile will stop pointing at it.")]
    [InlineData(TileKindIds.Goal, "The run will be paused, and its record stays in .mtiles/goals/.")]
    [InlineData(TileKindIds.Git, "Nothing from this tile will be lost.")]
    [InlineData(TileKindIds.Database, "Nothing from this tile will be lost.")]
    [InlineData(TileKindIds.Usage, "Nothing from this tile will be lost.")]
    public void Each_kind_says_what_changing_it_costs(string kindId, string expected)
    {
        var warning = TileConversion.Warning(kindId, "Note");

        Assert.Contains(expected, warning);

        // And what it is becoming, because the cost alone is only half of the decision.
        Assert.Contains("Note", warning);
    }

    /// <summary>An agent loses its shell as well as its conversation, so it says both.</summary>
    /// <remarks>The one kind with two sentences, and the reason the table above is by fragment rather
    /// than by whole string: dropping the shell's half would leave a warning that reads as though only
    /// the tile's bookkeeping were at stake.</remarks>
    [Fact]
    public void An_agent_loses_its_shell_as_well_as_the_tile_it_was_reached_through()
    {
        var warning = TileConversion.Warning(TileKindIds.Agent, "Note");

        Assert.Contains("The shell and everything running in it will be ended.", warning);
        Assert.Contains("The conversation stays with the agent", warning);
    }

    /// <summary>The two kinds that end something that cannot be started again where it left off.</summary>
    [Theory]
    [InlineData(TileKindIds.Terminal, true)]
    [InlineData(TileKindIds.Agent, true)]
    [InlineData(TileKindIds.Note, false)]
    [InlineData(TileKindIds.Todo, false)]
    [InlineData(TileKindIds.Goal, false)]
    [InlineData(TileKindIds.Git, false)]
    [InlineData(TileKindIds.Database, false)]
    [InlineData(TileKindIds.Usage, false)]
    [InlineData(TileKindIds.None, false)]
    public void Only_a_live_process_is_work_that_is_destroyed(string kindId, bool expected) =>
        Assert.Equal(expected, TileConversion.DestroysWork(kindId));

    /// <summary>A kind nobody here has heard of is still convertible, just without a promise.</summary>
    /// <remarks>The registry is open, so a kind registered by later code has to be able to become
    /// something else. What it does not get is a sentence about what it leaves behind, because nothing
    /// in this file knows.</remarks>
    [Fact]
    public void A_kind_this_rule_has_never_heard_of_gets_the_general_sentence()
    {
        Assert.Contains("Whatever this tile is holding will be replaced.",
            TileConversion.Warning("kaleidoscope", "Note"));
    }

    /// <summary>
    /// Every kind the application ships has a sentence of its own.
    /// </summary>
    /// <remarks>The guard against a ninth kind arriving with the general sentence: it would be true
    /// about nothing in particular, on a question the user is being asked to answer once and for
    /// good.</remarks>
    [Fact]
    public void No_registered_kind_falls_through_to_it()
    {
        using var settings = new TempSettings();
        var general = TileConversion.Warning("kaleidoscope", "Note");

        foreach (var entry in TestTiles.Catalog(settings.Service).Entries)
            Assert.NotEqual(general, TileConversion.Warning(entry.Kind.Id, "Note"));
    }
}
