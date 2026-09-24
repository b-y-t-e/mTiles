using mTiles.Services.Agents.SessionLogs;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which lines of a CLI's own transcript are the conversation — what a terminal agent tile hands over
/// when its agent changes.
/// </summary>
/// <remarks>The lines are the shapes measured 2026-09-23 (Claude Code 2.1.274, codex 0.154.0), trimmed.
/// Somebody else's format, so pinned the way <c>AiAgentTests</c> pins the flags.</remarks>
public class TranscriptTurnTests
{
    [Fact]
    public void Claude_reads_what_the_user_typed_and_what_the_agent_wrote()
    {
        Assert.Equal(new TranscriptTurn(true, "fix the icon"),
            ClaudeSessionLog.TurnIn("""{"type":"user","message":{"role":"user","content":"fix the icon"}}"""));
        Assert.Equal(new TranscriptTurn(true, "fix the icon"),
            ClaudeSessionLog.TurnIn("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"fix the icon"},{"type":"image","source":{}}]}}"""));
        Assert.Equal(new TranscriptTurn(false, "Done."),
            ClaudeSessionLog.TurnIn("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Done."}]}}"""));
    }

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"ok"}]}}""")]
    [InlineData("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bash","input":{}}]}}""")]
    [InlineData("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hm"}]}}""")]
    [InlineData("""{"type":"user","isMeta":true,"message":{"role":"user","content":[{"type":"text","text":"[Image: source: /tmp/x.png]"}]}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"<command-name>/clear</command-name>"}}""")]
    [InlineData("""{"type":"attachment"}""")]
    [InlineData("not json")]
    public void Claude_leaves_out_the_work_and_what_it_writes_of_its_own(string line) =>
        Assert.Null(ClaudeSessionLog.TurnIn(line));

    [Fact]
    public void Claude_keeps_a_message_that_merely_starts_with_a_tag() =>
        Assert.Equal(new TranscriptTurn(true, "<div> does not render"),
            ClaudeSessionLog.TurnIn("""{"type":"user","message":{"role":"user","content":"<div> does not render"}}"""));

    [Fact]
    public void An_odd_line_is_skipped_rather_than_failing_the_whole_transcript()
    {
        Assert.Null(ClaudeSessionLog.TurnIn("""{"type":7,"message":{"content":"x"}}"""));
        Assert.Null(ClaudeSessionLog.TurnIn("""{"type":"user","message":{"content":[{"type":"text","text":5}]}}"""));
        Assert.Null(CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":3}}"""));
    }

    [Fact]
    public void Codex_reads_both_event_shapes()
    {
        Assert.Equal(new TranscriptTurn(true, "say ok"),
            CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"text","text":"say ok"}]}}}"""));
        Assert.Equal(new TranscriptTurn(false, "ok"),
            CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"AgentMessage","content":[{"type":"Text","text":"ok"}]}}}"""));
        Assert.Equal(new TranscriptTurn(true, "say ok"),
            CodexSessionLog.TurnIn("""{"type":"event_msg","payload":{"type":"user_message","message":"say ok"}}"""));
    }

    [Fact]
    public void Codex_leaves_out_what_was_sent_to_the_model()
    {
        Assert.Null(CodexSessionLog.TurnIn(
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<recommended_plugins>"}]}}"""));
        Assert.Null(CodexSessionLog.TurnIn(
            """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"ContextCompaction"}}}"""));
    }
}
