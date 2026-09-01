using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Claude Code refusing to start headless on a model it cannot verify against the provider.
/// </summary>
/// <remarks>
/// Both shapes below are the real thing, captured 2026-09-01 against 2.1.252 on OpenRouter. The plain
/// shape is what <c>--output-format text</c> leaves on stdout (and what <c>AiProcessRunner</c> glues the
/// stderr dump onto); the streamed shape is all that survives of the same refusal, because the stream
/// reader drops every line that is not one of its events and only the CLI's tag on stderr is left.
/// </remarks>
public class UnrecognizedModelTests
{
    private const string PlainRefusal =
        "There's an issue with the selected model (z-ai/glm-5.3-flash). It may not exist or you may not " +
        "have access to it. Run --model to pick a different model.\n\n" +
        "[stderr] ⚠ claude.ai connectors are disabled because ANTHROPIC_API_KEY or another auth source is " +
        "set and takes precedence over your claude.ai login · Unset it to load your organization's " +
        "connectors\n\"z-ai/glm-5.3-flash\" is not a model this version of Claude Code recognizes, so " +
        "auto-compact will keep this session within 200k tokens (the context window it assumes).\n" +
        "[claude-code:unrecognized_model] {\"model\":\"z-ai/glm-5.3-flash\",\"query_source\":\"sdk\"}";

    private const string StreamedRefusal =
        "[stderr] ⚠ claude.ai connectors are disabled because ANTHROPIC_API_KEY or another auth source " +
        "is set and takes precedence over your claude.ai login\n" +
        "[claude-code:unrecognized_model] {\"model\":\"z-ai/glm-5.3-flash\",\"query_source\":\"sdk\"}";

    [Fact]
    public void The_plain_run_refusal_is_named_by_the_tag_in_its_stderr_dump()
    {
        Assert.True(UnrecognizedModel.Named(PlainRefusal));
    }

    [Fact]
    public void The_streamed_run_refusal_is_named_by_the_cli_tag_alone()
    {
        // The stdout sentence never reaches this shape — a streamed run's plain lines are dropped — so
        // the tag on stderr is the only signal there is.
        Assert.True(UnrecognizedModel.Named(StreamedRefusal));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Nothing_printed_is_nothing_named(string? toolOutput)
    {
        Assert.False(UnrecognizedModel.Named(toolOutput));
    }

    [Fact]
    public void An_ordinary_failure_is_not_read_as_the_model_refusal()
    {
        Assert.False(UnrecognizedModel.Named(
            "I got as far as renaming Cart.cs.\n\n[error] Credit balance is too low"));
        Assert.False(UnrecognizedModel.Named(
            "[stderr] opencode run [message..]\nrun opencode with a message"));
    }

    [Fact]
    public void A_failed_run_quoting_the_sentence_is_not_read_as_the_refusal()
    {
        // This tile's goals are goals about this application, whose sources contain the sentence — a
        // run that failed for an unrelated reason would carry its diff into the failure text. The tag
        // is the one signal matched, precisely because a quoted sentence cannot fake it; the sentence
        // alone names nothing.
        var quoted = "The guard message \"There's an issue with the selected model\" is user-hostile.";
        Assert.False(UnrecognizedModel.Named(quoted));
        Assert.False(UnrecognizedModel.Named(
            "The CLI prints \"is not a model this version of Claude Code recognizes\" on stderr."));
    }

    [Fact]
    public void The_advice_names_the_route_that_still_works()
    {
        // An agent tile on the same instance runs the model interactively, where no such check exists —
        // that asymmetry is the whole of the way out, so the advice has to say it.
        Assert.Contains("agent tile", UnrecognizedModel.Advice);
        Assert.Contains("headless", UnrecognizedModel.Advice);
    }
}
