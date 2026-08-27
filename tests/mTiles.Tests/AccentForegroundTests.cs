using Avalonia.Media;
using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a label is written in when it sits on the accent.
/// </summary>
/// <remarks>
/// The rule this pins is not "white on the accent": a theme whose accent is pale gets dark text, and
/// that case is the whole reason the function exists rather than a constant. The threshold itself is a
/// judgement — these cases say which side of it the colours we actually ship fall on, so moving it has
/// to be a decision rather than a tweak nobody notices.
/// </remarks>
public class AccentForegroundTests
{
    private static bool IsLight(Color c) => c.R > 0x80;

    [Theory]
    // The blues of the dark themes this application ships — every one of them needs light text.
    [InlineData("#2472c8", true)]   // Default Dark
    [InlineData("#bd93f9", false)]  // Dracula: a pale lavender, and the case a constant would get wrong
    [InlineData("#81a1c1", false)]  // Nord: pale enough to need dark text
    [InlineData("#66d9ef", false)]  // Monokai: a bright cyan, likewise
    [InlineData("#268bd2", true)]   // Solarized Dark
    [InlineData("#89b4fa", false)]  // Catppuccin Mocha
    public void The_label_takes_the_side_the_accent_leaves_free(string accent, bool expectLightText)
    {
        var chosen = ThemeBridge.OnAccent(Color.Parse(accent));
        Assert.Equal(expectLightText, IsLight(chosen));
    }

    /// <summary>
    /// Green weighs more than blue, and that is the point of using luma rather than an average.
    /// </summary>
    /// <remarks>
    /// A plain average of the channels calls a saturated blue "light" and hands it dark text, which is
    /// the worst pairing available. These two have the same average and must come out differently.
    /// </remarks>
    [Fact]
    public void A_saturated_blue_and_a_saturated_green_are_not_the_same_brightness()
    {
        var onBlue = ThemeBridge.OnAccent(Color.Parse("#0000ff"));
        var onGreen = ThemeBridge.OnAccent(Color.Parse("#00ff00"));

        Assert.True(IsLight(onBlue), "a pure blue is dark and needs light text");
        Assert.False(IsLight(onGreen), "a pure green is light and needs dark text");
    }

    /// <summary>Neither answer is pure black or pure white.</summary>
    /// <remarks>
    /// A filled button is a large area, and maximum contrast on one reads as a hole punched in the page.
    /// </remarks>
    [Theory]
    [InlineData("#2472c8")]
    [InlineData("#bd93f9")]
    public void Neither_answer_is_an_extreme(string accent)
    {
        var chosen = ThemeBridge.OnAccent(Color.Parse(accent));
        Assert.NotEqual(Colors.White, chosen);
        Assert.NotEqual(Colors.Black, chosen);
    }
}
