using mTiles.Services.Activity;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Taking the escape sequences out of a chunk of output, so a CLI's own words can be matched.
/// </summary>
/// <remarks>
/// <para>Not a parser and not a test of one: what is asserted here is that the shapes carrying no text
/// disappear and everything else survives. The cases are the ones that broke matching — a colour run
/// splitting a phrase, an OSC title landing in the middle of the text, and a status bar redrawn in
/// place running into the line above it.</para>
/// <para>Escapes are written <c>\u001b</c> and not <c>\x1b</c> throughout, and that is not a style
/// preference: <c>\x</c> takes <em>up to</em> four hex digits, so <c>"\x07b"</c> is the single character
/// U+007B rather than a BEL followed by a "b". Two cases here were written that way first and failed
/// against perfectly correct code.</para>
/// </remarks>
public class AnsiTextTests
{
    [Fact]
    public void A_colour_run_does_not_split_the_words_inside_it() =>
        Assert.Equal("esc to interrupt",
            AnsiText.Strip("\u001b[2mesc\u001b[0m \u001b[1mto interrupt\u001b[0m"));

    /// <summary>An OSC string ends at BEL or at ST, and everything between is the terminal's business
    /// rather than text. Left in, a title would be matched as though the child had printed it.</summary>
    [Theory]
    [InlineData("a\u001b]0;Do you want to proceed?\u0007b", "ab")]
    [InlineData("a\u001b]0;Do you want to proceed?\u001b\\b", "ab")]
    public void An_osc_string_leaves_nothing_behind(string input, string expected) =>
        Assert.Equal(expected, AnsiText.Strip(input));

    /// <summary>
    /// A carriage return is how a TUI overwrites the line it is on, so it has to break. Dropped, the
    /// redrawn status bar runs into the line before it and produces matches for words that were never
    /// next to each other.
    /// </summary>
    [Fact]
    public void A_redrawn_line_does_not_run_into_the_one_before_it() =>
        Assert.Equal("Working\nDone", AnsiText.Strip("Working\r\rDone"));

    /// <summary>Cursor moves, scroll regions and mode sets carry no text at all.</summary>
    [Fact]
    public void Cursor_and_mode_sequences_disappear() =>
        Assert.Equal("Working...",
            AnsiText.Strip("\u001b[?25l\u001b[2J\u001b[3;1HWorking...\u001b[?25h"));

    /// <summary>Two-character escapes are one unit, so the byte after ESC is never left as text.
    /// </summary>
    [Fact]
    public void A_two_character_escape_takes_its_second_byte_with_it() =>
        Assert.Equal("ok", AnsiText.Strip("\u001b(B\u001b=ok\u001b7"));

    /// <summary>Non-ASCII survives: these UIs are built out of box drawing and the markers sit inside
    /// it.</summary>
    [Fact]
    public void Box_drawing_and_accented_text_survive() =>
        Assert.Equal("│ ✱ zażółć │", AnsiText.Strip("\u001b[36m│ ✱ zażółć │\u001b[0m"));

    /// <summary>A sequence cut off by the end of the buffer takes what is left with it rather than
    /// spilling its parameters into the text — the ring's front edge is exactly this case.</summary>
    [Fact]
    public void An_unterminated_sequence_does_not_spill_into_the_text() =>
        Assert.Equal("ok", AnsiText.Strip("ok\u001b[38;5;2"));
}
