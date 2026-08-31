using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What typing into a model field finds.
/// </summary>
/// <remarks>A table rather than prose, because this is an opinion about matching and the rows are the
/// argument: each one is a thing somebody would type against a catalogue that runs to hundreds of
/// entries, and the reason a plain "contains" is not enough.</remarks>
public class ModelSearchTests
{
    /// <summary>Every word has to appear; where, and in what order, does not matter.</summary>
    [Theory]
    // The case this exists for: the separator is the part nobody remembers.
    [InlineData("glm 5.3", "z-ai/glm-5.3-flash", true)]
    [InlineData("5.3 glm", "z-ai/glm-5.3-flash", true)]
    // Punctuation the user did not type: "zai" is how a vendor read on screen gets spelled back.
    [InlineData("zai flash", "z-ai/glm-5.3-flash", true)]
    [InlineData("glm53", "z-ai/glm-5.3-flash", true)]
    // Typing the id the way it is actually written still works.
    [InlineData("glm-5.3", "z-ai/glm-5.3-flash", true)]
    [InlineData("z-ai/glm", "z-ai/glm-5.3-flash", true)]
    // Every word, not any: this is what keeps a two-word search from widening the list.
    [InlineData("glm 4.6", "z-ai/glm-5.3-flash", false)]
    [InlineData("glm sonnet", "z-ai/glm-5.3-flash", false)]
    // Case is not something anybody should have to get right.
    [InlineData("GLM Flash", "z-ai/glm-5.3-flash", true)]
    // Nothing typed offers everything, which is what makes the field usable before the first keystroke.
    [InlineData("", "z-ai/glm-5.3-flash", true)]
    [InlineData("   ", "z-ai/glm-5.3-flash", true)]
    public void Matching(string search, string candidate, bool expected) =>
        Assert.Equal(expected, ModelSearch.Matches(search, candidate));

    /// <summary>
    /// A dot holds a version together.
    /// </summary>
    /// <remarks>The one separator deliberately left out. Split on it, <c>5.3</c> becomes <c>5</c> and
    /// <c>3</c>, and <c>gpt-5-codex-3</c> would answer a search for GPT 5.3 — a match that looks
    /// deliberate and is nonsense.</remarks>
    [Fact]
    public void A_version_number_is_one_word()
    {
        Assert.True(ModelSearch.Matches("5.3", "z-ai/glm-5.3-flash"));
        Assert.False(ModelSearch.Matches("5.3", "openai/gpt-5-codex-3"));

        // And the forgiving spelling cannot smuggle it back in, by either route: "53" run together is
        // not "5" then "3", and a word that carries the dot is not matched against a stripped id.
        Assert.False(ModelSearch.Matches("53", "openai/gpt-5-codex-3"));
        Assert.False(ModelSearch.Matches("5.3", "openai/gpt-5-3-turbo"));
    }

    /// <summary>Nothing to match against is not a match — except for an empty search, which is one.
    /// </summary>
    /// <remarks>The filter is handed whatever is in the list, and this is a UI predicate: it answers
    /// rather than throwing, the same rule the provider layer follows.</remarks>
    [Theory]
    [InlineData("glm", null, false)]
    [InlineData("glm", "", false)]
    [InlineData(null, "anything", true)]
    public void Missing_values(string? search, string? candidate, bool expected) =>
        Assert.Equal(expected, ModelSearch.Matches(search, candidate));
}
