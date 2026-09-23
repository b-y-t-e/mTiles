using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// What the waiting row says the agent is doing.
/// </summary>
/// <remarks>Driven through <see cref="ConversationReducer"/> rather than by building a state by hand,
/// because the claim being made is about <i>the events</i> — that this works for every agent follows from
/// <c>ToolStarted</c> being in the shared contract, and a test that assembles the state itself would prove
/// nothing about the route the events actually take.</remarks>
public class TurnStageTests
{
    private static ConversationState Replay(params AgentEvent[] events) =>
        ConversationReducer.Replay(events);

    [Fact]
    public void Nothing_running_says_nothing()
    {
        Assert.Equal("", TurnStage.For(null));
        Assert.Equal("", TurnStage.For(Replay(new UserMessageAdded("u", "go", []))));
    }

    [Fact]
    public void A_running_tool_names_itself()
    {
        var state = Replay(
            new UserMessageAdded("u", "go", []),
            new TurnStarted { TurnId = "t" },
            new ToolStarted("c", ToolKind.Command, "Bash", "dotnet build", new ToolDetail(Command: "dotnet build")));

        Assert.Equal("dotnet build", TurnStage.For(state));
    }

    /// <summary>A tool that has answered is not what the agent is doing now.</summary>
    [Fact]
    public void A_finished_tool_is_not_the_stage()
    {
        var state = Replay(
            new TurnStarted { TurnId = "t" },
            new ToolStarted("c", ToolKind.Command, "Bash", "dotnet build", new ToolDetail()),
            new ToolCompleted("c", ToolStatus.Completed, "ok", new ToolDetail()));

        Assert.Equal("", TurnStage.For(state));
    }

    /// <summary>Several in flight: the newest, because the older ones are what it has plainly been doing
    /// for the last minute already.</summary>
    [Fact]
    public void The_newest_running_tool_wins()
    {
        var state = Replay(
            new TurnStarted { TurnId = "t" },
            new ToolStarted("a", ToolKind.FileRead, "Read", "Read Foo.cs", new ToolDetail()),
            new ToolStarted("b", ToolKind.Command, "Bash", "dotnet test", new ToolDetail()));

        Assert.Equal("dotnet test", TurnStage.For(state));
    }

    /// <summary>A title is often a command line. The clock beside it is the part that must stay readable,
    /// because it is what says the tile has not stalled — so the title is what gives way.</summary>
    [Fact]
    public void A_long_title_is_cut_and_a_multi_line_one_keeps_its_first_line()
    {
        var long_ = new string('x', TurnStage.MaxLength + 20);
        var cut = TurnStage.For(Replay(new ToolStarted("c", ToolKind.Command, "Bash", long_, new ToolDetail())));
        Assert.Equal(TurnStage.MaxLength + 1, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);

        Assert.Equal("git status",
            TurnStage.For(Replay(new ToolStarted("c", ToolKind.Command, "Bash", "git status\n--porcelain",
                new ToolDetail()))));
    }
}
