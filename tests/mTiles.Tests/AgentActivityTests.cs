using mTiles.Models;
using mTiles.Services.Activity;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What each CLI's own signals mean, pinned.
/// </summary>
/// <remarks>
/// <para>The same job <c>AiAgentTests</c> does for the flags, and for the same reason: every table here
/// is somebody else's user interface, it moves, and when it moves that has to arrive as a failing build
/// rather than as a tile that quietly stops reporting. The measurements behind each are recorded in the
/// agent class itself, with a date and a version.</para>
/// <para><b>An agent nothing has been measured about is expected to answer Unknown</b>, and that is
/// asserted rather than left implicit: Unknown falls through to the raw-output light, which is the
/// behaviour the tile has always had, while a guess would be a light that is wrong and looks
/// deliberate.</para>
/// </remarks>
public class AgentActivityTests
{
    private static string Detail(IAgentActivityReader reader, string recent)
    {
        reader.ReadRecentOutput(recent, out var detail);
        return detail ?? "";
    }

    // ---- Claude Code -----------------------------------------------------------------------------

    /// <summary>The braille cell is the spinner mechanism, so matching the class of character survives
    /// the words beside it changing — which they have, twice.</summary>
    [Theory]
    [InlineData("⠋ Claude Code", TileActivity.Working)]
    [InlineData("⢹ anything at all", TileActivity.Working)]
    [InlineData("✱ Claude Code", TileActivity.Idle)]
    [InlineData("✳ my session name", TileActivity.Idle)]
    [InlineData("Claude Code v2.1.263", TileActivity.Unknown)]
    [InlineData("", TileActivity.Unknown)]
    public void Claude_reads_its_own_title(string title, TileActivity expected) =>
        Assert.Equal(expected, new ClaudeAgent().ReadTitle(title));

    /// <summary>A title carrying neither glyph is Unknown and not Idle — a build that does not do this,
    /// or a title somebody else set, must not be able to put the light out.</summary>
    [Fact]
    public void Claude_says_nothing_about_a_title_it_does_not_recognise() =>
        Assert.Equal(TileActivity.Unknown, new ClaudeAgent().ReadTitle("bash"));

    [Theory]
    [InlineData("thinking... (esc to interrupt)", TileActivity.Working)]
    [InlineData("✱ Compacting conversation", TileActivity.Working)]
    [InlineData("Bash(rm -rf build)\nDo you want to proceed?", TileActivity.Blocked)]
    [InlineData("Do you want to make this edit to Cart.cs?", TileActivity.Blocked)]
    [InlineData("just some ordinary output", TileActivity.Unknown)]
    public void Claude_reads_its_own_status_line(string recent, TileActivity expected) =>
        Assert.Equal(expected, new ClaudeAgent().ReadRecentOutput(recent, out _));

    /// <summary>
    /// The rule that makes a window of <em>recent</em> text usable at all: both markers are in it,
    /// because both were painted, and the order they were painted in is the only evidence of which is
    /// current. Precedence by kind gets this exactly backwards — it leaves a tile marked as waiting for
    /// an answer the user has already given.
    /// </summary>
    [Fact]
    public void The_marker_painted_last_is_the_one_that_counts()
    {
        var agent = new ClaudeAgent();

        Assert.Equal(TileActivity.Working,
            agent.ReadRecentOutput("Do you want to proceed?\nyes\nesc to interrupt", out _));
        Assert.Equal(TileActivity.Blocked,
            agent.ReadRecentOutput("esc to interrupt\ndone\nDo you want to proceed?", out _));
    }

    /// <summary>A blocked reading carries a sentence, because "something is happening" is not what the
    /// user needs to know at the one moment they have to come back.</summary>
    [Fact]
    public void A_blocked_reading_says_what_it_is_waiting_for() =>
        Assert.Equal("Waiting for permission",
            Detail(new ClaudeAgent(), "Do you want to proceed?"));

    // ---- codex -----------------------------------------------------------------------------------

    /// <summary>Measured out of codex-cli 0.153.2: the footer is composed at runtime, so " to interrupt"
    /// is the whole of the fixed part, and the four questions are literals.</summary>
    [Theory]
    [InlineData("⏎ send   esc to interrupt", TileActivity.Working)]
    [InlineData("Would you like to run the following command?", TileActivity.Blocked)]
    [InlineData("Would you like to grant these permissions?", TileActivity.Blocked)]
    [InlineData("Would you like to make the following edits?", TileActivity.Blocked)]
    [InlineData("nothing in particular", TileActivity.Unknown)]
    public void Codex_reads_its_own_status_line(string recent, TileActivity expected) =>
        Assert.Equal(expected, new CodexAgent().ReadRecentOutput(recent, out _));

    /// <summary>codex sets no title carrying a state, and claiming one would be a table nobody
    /// measured.</summary>
    [Fact]
    public void Codex_reads_no_title() =>
        Assert.Equal(TileActivity.Unknown, new CodexAgent().ReadTitle("codex"));

    // ---- opencode --------------------------------------------------------------------------------

    /// <summary>Measured out of opencode 1.18.18, whose status line renders <c>busyText ?? "Working..."</c>.
    /// </summary>
    [Theory]
    [InlineData("Working...", TileActivity.Working)]
    [InlineData("bash\nAllow always", TileActivity.Blocked)]
    [InlineData("nothing in particular", TileActivity.Unknown)]
    public void OpenCode_reads_its_own_status_line(string recent, TileActivity expected) =>
        Assert.Equal(expected, new OpenCodeAgent().ReadRecentOutput(recent, out _));

    /// <summary>And it is only right in English — which is a limit worth stating, because the failure it
    /// produces is silent: a localised TUI simply never matches, and the tile falls back to its output
    /// light with nothing on screen saying so.</summary>
    [Fact]
    public void OpenCode_says_nothing_about_a_translated_prompt() =>
        Assert.Equal(TileActivity.Unknown, new OpenCodeAgent().ReadRecentOutput("Altijd toestaan", out _));

    // ---- Antigravity -----------------------------------------------------------------------------

    /// <summary>agy pipes its own <c>agent_state</c> to the script named by the <c>"title"</c> setting
    /// and injects the result as the window title, so a machine following that recipe has a title that
    /// <em>is</em> the state.</summary>
    [Theory]
    [InlineData("thinking", TileActivity.Working)]
    [InlineData("Running", TileActivity.Working)]
    [InlineData("  working  ", TileActivity.Working)]
    [InlineData("waiting", TileActivity.Blocked)]
    [InlineData("idle", TileActivity.Idle)]
    [InlineData("error", TileActivity.Idle)]
    public void Antigravity_reads_its_state_out_of_the_title(string title, TileActivity expected) =>
        Assert.Equal(expected, new AntigravityAgent().ReadTitle(title));

    /// <summary>
    /// The whole title has to be the word. A substring rule would read an ordinary title — "working on
    /// the parser" — as a state, and there is no version of that mistake that is merely cosmetic: it
    /// puts a light on and keeps it there.
    /// </summary>
    [Theory]
    [InlineData("working on the parser")]
    [InlineData("~/src/mtiles")]
    [InlineData("")]
    public void Antigravity_does_not_read_a_state_out_of_an_ordinary_title(string title) =>
        Assert.Equal(TileActivity.Unknown, new AntigravityAgent().ReadTitle(title));

    // ---- The unmeasured ones ---------------------------------------------------------------------

    /// <summary>pi and anything built as a generic binary answer nothing, deliberately. This is asserted
    /// so that a table added later is added with a measurement and a test rather than by someone filling
    /// in a blank.</summary>
    [Fact]
    public void An_agent_with_nothing_measured_says_nothing()
    {
        foreach (IAiAgent agent in new IAiAgent[] { new PiAgent(), new GenericAgent("x") })
        {
            Assert.Equal(TileActivity.Unknown, agent.ReadTitle("anything"));
            Assert.Equal(TileActivity.Unknown, agent.ReadRecentOutput("anything", out _));
        }
    }
}
