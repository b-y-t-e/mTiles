using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Anthropic's model list, as the one source of a context window for a tile with no provider.
/// </summary>
/// <remarks>The document here is a transcription of a real answer, read on 2026-09-18 from
/// <c>GET api.anthropic.com/v1/models</c> with Claude Code's own OAuth token. It is pinned because the
/// figure it carries is what the context bar divides by, and the alternative — assuming one — drew a
/// full bar over a conversation at a quarter of its real window.</remarks>
public class ClaudeModelCatalogTests
{
    private const string RealAnswer = """
        {"data":[
          {"type":"model","id":"claude-opus-5","display_name":"Claude Opus 5",
           "max_input_tokens":1000000,"max_tokens":128000},
          {"type":"model","id":"claude-opus-4-5-20251101","display_name":"Claude Opus 4.5",
           "max_input_tokens":200000,"max_tokens":64000},
          {"type":"model","id":"claude-haiku-4-5-20251001","display_name":"Claude Haiku 4.5",
           "max_input_tokens":200000,"max_tokens":64000}
        ],"has_more":false}
        """;

    [Fact]
    public void The_window_is_the_input_limit_and_not_what_the_model_may_write()
    {
        // max_tokens sits right beside it and is a different number — how much the model may write in
        // one reply, 128 000 on the current families. Taken as the window it would draw every
        // conversation of any length as long past full.
        var windows = ClaudeModelCatalog.Parse(RealAnswer);

        Assert.Equal(1_000_000, windows["claude-opus-5"]);
        Assert.Equal(200_000, windows["claude-opus-4-5-20251101"]);
    }

    [Fact]
    public void The_families_do_not_share_one_window_which_is_why_it_is_asked_rather_than_assumed()
    {
        // The measurement that settled it: opus-5 is served a million tokens and opus-4.5 two hundred
        // thousand, on one account, at the same moment. No single figure is right for both.
        var windows = ClaudeModelCatalog.Parse(RealAnswer);

        Assert.NotEqual(windows["claude-opus-5"], windows["claude-opus-4-5-20251101"]);
    }

    [Fact]
    public void A_model_the_list_does_not_carry_is_not_guessed_at()
    {
        // A subscription pointed at a third-party model through a gateway is a configuration this
        // endpoint knows nothing about. Absent means absent.
        var windows = ClaudeModelCatalog.Parse(RealAnswer);

        Assert.False(windows.ContainsKey("z-ai/glm-5.3-flash"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"error":{"type":"authentication_error"}}""")]
    [InlineData("""{"data":[{"type":"model","id":"claude-opus-5"}]}""")]
    [InlineData("""{"data":[{"type":"model","id":"claude-opus-5","max_input_tokens":0}]}""")]
    public void An_answer_that_says_nothing_useful_costs_a_bar_and_not_a_tile(string json)
    {
        // Every one of these has to end as "no window", which the gauge draws as a count with no bar.
        Assert.Empty(ClaudeModelCatalog.Parse(json));
    }

    [Theory]
    [InlineData("claude-sonnet-4-5[1m]", 1_000_000)]
    [InlineData("claude-sonnet-4-5-20250929[1m]", 1_000_000)]
    [InlineData("claude-sonnet-4-5[500k]", 500_000)]
    public void A_long_context_variant_names_its_own_window_and_outranks_the_catalogue(string model,
        long expected)
    {
        // The suffix travels to the API as part of the model string, so it is what the transcript
        // carries. Matched against the catalogue's ids it finds nothing, and the plain entry beside it
        // is the *short* window — 200 000 for a session really running on a million, which draws a full
        // bar from a fifth of the way in.
        Assert.Equal(expected, ClaudeModelCatalog.VariantWindow(model));
    }

    [Theory]
    [InlineData("claude-opus-5")]
    [InlineData("claude-sonnet-4-5[]")]
    [InlineData("claude-sonnet-4-5[beta]")]
    [InlineData("claude-sonnet-4-5[0m]")]
    public void A_suffix_that_is_not_a_size_is_not_an_answer(string model)
    {
        // Read rather than tabulated — and a suffix in any other shape falls through to the plain id
        // being looked up, which is what happened before any of this existed.
        Assert.Null(ClaudeModelCatalog.VariantWindow(model));
    }

    [Fact]
    public async Task A_variant_is_answered_without_asking_anybody()
    {
        // Free and authoritative: the id itself says it, so there is nothing for the endpoint to add.
        var seen = new List<HttpRequestMessage>();
        ClaudeModelCatalog.HandlerFactory = () => FakeHttpHandler.Canned(RealAnswer, seen: seen);
        var credentials = Path.Combine(Path.GetTempPath(), "mtiles-cred-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(credentials,
            """{"claudeAiOauth":{"accessToken":"live-token","expiresAt":32503680000000}}""");

        try
        {
            var window = await ClaudeModelCatalog.ContextWindowAsync(credentials,
                "claude-opus-4-5-20251101[1m]");

            Assert.Equal(1_000_000, window);
            Assert.Empty(seen);
        }
        finally
        {
            ClaudeModelCatalog.HandlerFactory = null;
            File.Delete(credentials);
        }
    }

    [Fact]
    public void Only_the_agent_with_a_measured_route_claims_one()
    {
        // Five of the six have no measured way to ask their own service, and answering null there is
        // what keeps a guess out of the one figure the bar divides by.
        foreach (var agent in AiAgentCatalog.All.Where(agent => agent.Id != "claude"))
            Assert.Null(agent.AccountContextWindowAsync(null, "some-model").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task The_request_carries_both_headers_the_endpoint_wants()
    {
        // Measured 2026-09-18: without anthropic-version the answer is
        // 400 "anthropic-version: header is required". It was left out once, by copying the request
        // shape from ClaudeUsageReader — same host, same token, and that endpoint does not need it — and
        // because every failure here becomes "no window", the symptom was a bar that silently never
        // appeared rather than anything anybody could see.
        var seen = new List<HttpRequestMessage>();
        ClaudeModelCatalog.HandlerFactory = () => FakeHttpHandler.Canned(RealAnswer, seen: seen);
        var credentials = Path.Combine(Path.GetTempPath(), "mtiles-cred-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(credentials,
            """{"claudeAiOauth":{"accessToken":"live-token","expiresAt":32503680000000}}""");

        try
        {
            var window = await ClaudeModelCatalog.ContextWindowAsync(credentials, "claude-opus-5");

            Assert.Equal(1_000_000, window);
            var request = Assert.Single(seen);
            Assert.Equal("live-token", request.Headers.Authorization?.Parameter);
            Assert.Contains("2023-06-01", request.Headers.GetValues("anthropic-version"));
            Assert.Contains("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta"));
        }
        finally
        {
            ClaudeModelCatalog.HandlerFactory = null;
            File.Delete(credentials);
        }
    }
}
