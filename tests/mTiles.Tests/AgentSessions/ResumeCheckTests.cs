using mTiles.Services.Agents.Sessions;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Telling a resume that brought the history back from one that did not — for the two CLIs that used to
/// fail in silence.
/// </summary>
/// <remarks>Every row is a situation measured live on 2026-09-17 against pi 0.84.4 and agy 1.2.3; the
/// comments say what the CLI did in it.</remarks>
public class ResumeCheckTests
{
    private const string Id = "4eea1b3f-3ca9-46ef-9b75-79723d52d473";

    [Theory]
    // Resumed in the same working directory: get_state answered messageCount 2.
    [InlineData(Id, true, 2L, false)]
    // Another working directory, a deleted session file, another agent directory: pi created an empty
    // session under the same id — stderr warned, messageCount 0.
    [InlineData(Id, true, 0L, true)]
    // A conversation nobody spoke in: pi writes no session file until the first message, so the resume is
    // always empty — and nothing was lost.
    [InlineData(Id, false, 0L, false)]
    // A new conversation is not a resume at all.
    [InlineData("", true, 0L, false)]
    [InlineData(null, true, 0L, false)]
    // pi did not say: no claim either way.
    [InlineData(Id, true, null, false)]
    public void Pi(string? requested, bool hasHistory, long? messageCount, bool lost) =>
        Assert.Equal(lost, ResumeCheck.PiLost(requested, hasHistory, messageCount));

    [Theory]
    // Resumed: init named the conversation it was asked for.
    [InlineData("d581c5dd-2513-4562-8d48-4f00138ca4e4", "d581c5dd-2513-4562-8d48-4f00138ca4e4", false)]
    // An unknown id, a malformed one, a moved-away .db file: agy warned on stderr, started a new
    // conversation, exited 0 — and init named the new id.
    [InlineData("3f2b9c1e-7a4d-4e8b-9c0a-1234567890ab", "ad402f86-7ecb-49d8-a9ef-92f8fe80c8a4", true)]
    [InlineData("not-a-uuid;x", "87052483-5ef7-4ed8-90e7-566fd06f4981", true)]
    // A new conversation: there was nothing to resume, so whatever init names is simply the id.
    [InlineData(null, "d581c5dd-2513-4562-8d48-4f00138ca4e4", false)]
    [InlineData("", "d581c5dd-2513-4562-8d48-4f00138ca4e4", false)]
    // init carrying no id is no evidence.
    [InlineData("d581c5dd-2513-4562-8d48-4f00138ca4e4", null, false)]
    public void Agy(string? requested, string? reported, bool lost) =>
        Assert.Equal(lost, ResumeCheck.AgyLost(requested, reported));

    /// <summary>The sentence says which half is gone: the transcript stays, the agent's memory of it does not.
    /// </summary>
    [Fact]
    public void The_notice_names_the_agent_and_what_was_lost()
    {
        var notice = ResumeCheck.Lost("Pi Agent");
        Assert.StartsWith("Pi Agent did not find this conversation", notice, StringComparison.Ordinal);
        Assert.Contains("does not remember it", notice, StringComparison.Ordinal);
    }
}
