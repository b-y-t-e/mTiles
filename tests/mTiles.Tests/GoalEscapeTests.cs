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
    public void A_detected_goal_carrying_them_is_read_the_same_way()
    {
        Assert.Equal(
            "Wątki są jednym źródłem",
            GoalResponseParser.ParseDetectedGoal(
                "```json\n{\"goal\": \"W\\u0105tki s\\u0105 jednym \\u017ar\\u00f3d\\u0142em\"}\n```"));
    }

    [Theory]
    // Wholly escaped: decoded.
    [InlineData(@"Esencje dzia\u0142\u00f3w generowane przez distill_course.py", "Esencje działów generowane przez distill_course.py")]
    // Already readable: nothing to do, and touching it would be inventing.
    [InlineData("Esencje działów generowane przez distill_course.py", "Esencje działów generowane przez distill_course.py")]
    // A sentence about an escape in text with its own accented letters is left as written.
    [InlineData(@"Kontrola pisze \u0142 zamiast ł w nazwie pliku", @"Kontrola pisze \u0142 zamiast ł w nazwie pliku")]
    [InlineData("A plain English goal with no escapes in it", "A plain English goal with no escapes in it")]
    [InlineData("", "")]
    // The case this cannot win, stated: a wholly-ASCII sentence quoting an escape is decoded too.
    [InlineData(@"writes \u0142 instead", "writes ł instead")]
    public void Escapes_are_decoded_only_where_the_whole_text_is_escaped(string text, string expected) =>
        Assert.Equal(expected, GoalResponseParser.Readable(text));
}
