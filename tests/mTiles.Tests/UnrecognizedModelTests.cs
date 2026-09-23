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

    /// <summary>The CLI's own tag is the one signal matched, because a quoted sentence cannot fake it.
    /// </summary>
    [Theory]
    // A plain run's stderr dump, and a streamed run, where the tag on stderr is all there is.
    [InlineData(PlainRefusal, true)]
    [InlineData(StreamedRefusal, true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // Ordinary failures.
    [InlineData("I got as far as renaming Cart.cs.\n\n[error] Credit balance is too low", false)]
    [InlineData("[stderr] opencode run [message..]\nrun opencode with a message", false)]
    // A run about this application carries its sources, which quote the sentence; the sentence names nothing.
    [InlineData("The guard message \"There's an issue with the selected model\" is user-hostile.", false)]
    [InlineData("The CLI prints \"is not a model this version of Claude Code recognizes\" on stderr.", false)]
    public void Only_the_clis_own_tag_names_the_refusal(string? toolOutput, bool named) =>
        Assert.Equal(named, UnrecognizedModel.Named(toolOutput));

    [Fact]
    public void The_advice_names_the_route_that_still_works()
    {
        // An agent tile on the same instance runs the model interactively, where no such check exists —
        // that asymmetry is the whole of the way out, so the advice has to say it.
        Assert.Contains("agent tile", UnrecognizedModel.Advice);
        Assert.Contains("headless", UnrecognizedModel.Advice);
    }
}
