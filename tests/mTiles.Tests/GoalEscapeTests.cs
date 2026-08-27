using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A goal that reached the transcript still wearing its JSON escapes.
/// </summary>
/// <remarks>
/// Seen in the wild: <c>Esencje dzia\u0142\u00f3w generowane przez `distill_course.py` ...</c>. The
/// tool escaped the text once itself and the transport escaped it again, so one decode left the
/// sequences standing as characters. Nothing downstream can tell that from a sentence the model meant
/// to write, which is why it is undone here, at the first place the text is read.
/// </remarks>
public class GoalEscapeTests
{
    [Fact]
    public void A_wholly_escaped_sentence_is_decoded()
    {
        Assert.Equal(
            "Esencje działów generowane przez distill_course.py",
            GoalResponseParser.Readable(@"Esencje dzia\u0142\u00f3w generowane przez distill_course.py"));
    }

    [Fact]
    public void A_detected_goal_carrying_them_is_read_the_same_way()
    {
        Assert.Equal(
            "Wątki są jednym źródłem",
            GoalResponseParser.ParseDetectedGoal(
                "```json\n{\"goal\": \"W\\u0105tki s\\u0105 jednym \\u017ar\\u00f3d\\u0142em\"}\n```"));
    }

    [Theory]
    // Already readable: nothing to do, and touching it would be inventing.
    [InlineData("Esencje działów generowane przez distill_course.py")]
    // A sentence *about* an escape, in text that has its own accented letters — a review of i18n code
    // says exactly this, and rewriting it would destroy the thing being reviewed.
    [InlineData(@"Kontrola pisze \u0142 zamiast ł w nazwie pliku")]
    // Nothing that looks like an escape at all.
    [InlineData("A plain English goal with no escapes in it")]
    [InlineData("")]
    public void Anything_else_is_left_exactly_as_it_came(string text) =>
        Assert.Equal(text, GoalResponseParser.Readable(text));

    [Fact]
    public void An_ascii_only_sentence_that_merely_mentions_an_escape_is_the_case_this_cannot_win()
    {
        // Stated rather than hidden: a wholly-ASCII review sentence quoting an escape is decoded too.
        // The trade is deliberate — that sentence is rare and survives as its own character, while the
        // alternative leaves every Polish goal unreadable.
        Assert.Equal("writes ł instead", GoalResponseParser.Readable(@"writes \u0142 instead"));
    }
}
