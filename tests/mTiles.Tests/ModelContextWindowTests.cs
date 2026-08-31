using System.Text.Json;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The auto-compact window: the 80% rule, its clamp, and the variables Claude Code is given for a
/// third-party model.
/// </summary>
/// <remarks>
/// <para><b>The 80% is this application's policy, not the CLI's documented default</b> — which is
/// exactly why it is pinned here as a table rather than left in a remark. The env-var half is pinned
/// the way <c>AgentProviderRoutingTests</c> pins the rest of Claude Code's environment: through
/// <c>EnvFor</c>, against a runtime that carries what a launch resolved.</para>
/// <para>The documented facts these tests rest on (env-vars reference, 2026-08-31):
/// <c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c> takes a plain token count from 100 000 to 1 000 000 and the
/// CLI clamps outside that range; <c>ANTHROPIC_SMALL_FAST_MODEL</c> is deprecated in favour of
/// <c>ANTHROPIC_DEFAULT_HAIKU_MODEL</c>; the <c>ANTHROPIC_DEFAULT_*_MODEL</c> family pins what the
/// <c>opus</c>/<c>sonnet</c>/<c>haiku</c>/<c>fable</c> aliases resolve to.</para>
/// </remarks>
public class ModelContextWindowTests
{
    // ── The 80% rule ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, null)]            // the provider did not say — set nothing
    [InlineData(0L, null)]              // a zero is a provider that said nothing useful
    [InlineData(32_768L, null)]         // 80% is below the CLI's own minimum; the clamp would eat it
    [InlineData(124_999L, null)]        // 99 999 — one under the minimum
    [InlineData(125_000L, 100_000L)]    // the boundary: 80% lands exactly on the minimum
    [InlineData(131_072L, 104_857L)]    // 104 857.6, rounded down
    [InlineData(200_000L, 160_000L)]
    [InlineData(1_000_000L, 800_000L)]
    [InlineData(1_250_000L, 1_000_000L)]// the clamp's boundary
    [InlineData(2_000_000L, 1_000_000L)]// clamped: the CLI enforces the same ceiling
    public void The_window_is_80_percent_of_the_context_rounded_down(long? context, long? window) =>
        Assert.Equal(window, ModelContextWindow.Window(context));

    // ── The variables Claude Code is given ───────────────────────────────────────────────────────

    private static IAiAgent Agent(string id) =>
        AiAgentCatalog.Find(id) ?? throw new InvalidOperationException($"No agent '{id}'.");

    private static (AppSettings Settings, AiAgentInstance Instance) Configured(string agentId,
        string providerId, string key)
    {
        var provider = new AiProviderInstance { ProviderId = providerId, ApiKey = key };
        var instance = AiAgentCatalog.SeedInstanceFor(Agent(agentId));
        instance.ApiAccountId = provider.Id;

        var settings = new AppSettings();
        settings.AiProviderInstances.Add(provider);
        settings.AiAgentInstances.Add(instance);
        return (settings, instance);
    }

    [Fact]
    public void The_resolved_window_reaches_the_environment_unchanged()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        // The runtime carries the window ModelContextWindow resolved — already reduced. An agent that
        // applied the 80% rule to it again would launch Claude Code at 64% of the context.
        var runtime = AgentRuntime.For(settings, instance, agent: Agent("claude"),
            autoCompactWindow: 160_000);

        Assert.Equal("160000",
            Agent("claude").EnvFor(runtime)["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    /// <summary>The whole chain, as a launch runs it: provider → resolver → runtime → environment.
    /// The end-to-end pin that the double reduction fell through.</summary>
    [Fact]
    public void The_whole_chain_reduces_once()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        using var _ = new ModelContextWindowTestsHttp(
            """{"data":[{"id":"z-ai/glm-5.3-flash","context_length":200000}]}""");
        ModelContextWindow.Reset();

        var resolved = ModelContextWindow.ResolveAsync(settings, Agent("claude"), instance,
            "z-ai/glm-5.3-flash").GetAwaiter().GetResult();

        var environment = Agent("claude").EnvFor(
            AgentRuntime.For(settings, instance, "z-ai/glm-5.3-flash", Agent("claude"), resolved));

        Assert.Equal(160_000, resolved);
        Assert.Equal("160000", environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    /// <summary>And the same chain on a model whose context is below the variable's minimum: the
    /// resolver answers null and the environment is left without the variable — not with a window the
    /// CLI would clamp up past the model's whole context.</summary>
    [Fact]
    public void The_chain_sets_nothing_when_the_context_is_below_the_minimum()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "gemma-3-4b";

        using var _ = new ModelContextWindowTestsHttp(
            """{"data":[{"id":"gemma-3-4b","context_length":32768}]}""");
        ModelContextWindow.Reset();

        var resolved = ModelContextWindow.ResolveAsync(settings, Agent("claude"), instance,
            "gemma-3-4b").GetAwaiter().GetResult();

        var environment = Agent("claude").EnvFor(
            AgentRuntime.For(settings, instance, "gemma-3-4b", Agent("claude"), resolved));

        Assert.Null(resolved);
        // The chosen account's unset block clears the variable; a null answer leaves it cleared.
        Assert.Null(environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    [Fact]
    public void The_typed_auto_compact_window_reaches_the_environment_unchanged()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";
        instance.AutoCompactWindow = 250_000;

        var runtime = AgentRuntime.For(settings, instance, agent: Agent("claude"),
            autoCompactWindow: 160_000);

        // The typed value wins even where the resolution would have answered — it is a decision,
        // and the resolution is the fallback for the field left empty.
        Assert.Equal("250000",
            Agent("claude").EnvFor(runtime)["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    [Fact]
    public void A_null_window_sets_none_on_an_account_of_its_own()
    {
        var instance = new AiAgentInstance { Model = "claude-opus-4-8" };

        var runtime = AgentRuntime.For(new AppSettings(), instance, agent: Agent("claude"),
            autoCompactWindow: null);

        // No account chosen, so no unset block either — the variable is simply never written.
        Assert.False(Agent("claude").EnvFor(runtime)
            .ContainsKey("CLAUDE_CODE_AUTO_COMPACT_WINDOW"));
    }

    [Fact]
    public void A_window_typed_on_the_row_is_honoured_on_the_clis_own_account_too()
    {
        var instance = new AiAgentInstance { Model = "claude-opus-4-8", AutoCompactWindow = 250_000 };

        var runtime = AgentRuntime.For(new AppSettings(), instance, agent: Agent("claude"),
            autoCompactWindow: 160_000);

        // Typed is a decision, wherever the CLI reads it — the same rule the fast model follows, and
        // the promise the field's hint makes. Only the *resolution* waits for a provider, because
        // that is the one account where the CLI's own window assumption can be wrong by half.
        Assert.Equal("250000",
            Agent("claude").EnvFor(runtime)["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    [Fact]
    public void Other_agents_are_not_given_the_variable()
    {
        var (settings, instance) = Configured("codex", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        var runtime = AgentRuntime.For(settings, instance, agent: Agent("codex"),
            autoCompactWindow: 160_000);

        Assert.False(Agent("codex").EnvFor(runtime)
            .ContainsKey("CLAUDE_CODE_AUTO_COMPACT_WINDOW"));
    }

    // ── The alias pins ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_large_aliases_are_pinned_to_the_rows_model_on_a_provider()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("z-ai/glm-5.3-flash", environment["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Equal("z-ai/glm-5.3-flash", environment["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Equal("z-ai/glm-5.3-flash", environment["ANTHROPIC_DEFAULT_FABLE_MODEL"]);
    }

    [Fact]
    public void No_alias_pin_on_the_clis_own_account()
    {
        var instance = new AiAgentInstance { Model = "claude-opus-4-8" };

        var environment = Agent("claude").EnvFor(
            AgentRuntime.For(new AppSettings(), instance));

        Assert.Equal("claude-opus-4-8", environment["ANTHROPIC_MODEL"]);
        Assert.False(environment.ContainsKey("ANTHROPIC_DEFAULT_OPUS_MODEL"));
        Assert.False(environment.ContainsKey("ANTHROPIC_DEFAULT_SONNET_MODEL"));
        Assert.False(environment.ContainsKey("ANTHROPIC_DEFAULT_FABLE_MODEL"));
    }

    [Fact]
    public void A_chosen_account_clears_every_model_variable_it_does_not_set()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "";

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        // A machine that exports any of these globally would otherwise run an instance that names
        // no model on somebody else's — the failure the ANTHROPIC_MODEL unset exists for, reached
        // by five more spellings.
        Assert.Null(environment["ANTHROPIC_MODEL"]);
        Assert.Null(environment["ANTHROPIC_SMALL_FAST_MODEL"]);
        Assert.Null(environment["ANTHROPIC_DEFAULT_OPUS_MODEL"]);
        Assert.Null(environment["ANTHROPIC_DEFAULT_SONNET_MODEL"]);
        Assert.Null(environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Null(environment["ANTHROPIC_DEFAULT_FABLE_MODEL"]);
        Assert.Null(environment["CLAUDE_CODE_AUTO_COMPACT_WINDOW"]);
    }

    // ── The small-model slot ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_fast_model_goes_through_the_current_spelling()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";
        instance.FastModel = "z-ai/glm-5.3-air";

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        // ANTHROPIC_SMALL_FAST_MODEL is deprecated; the CLI reads this slot through
        // ANTHROPIC_DEFAULT_HAIKU_MODEL now.
        Assert.Equal("z-ai/glm-5.3-air", environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
        Assert.Null(environment["ANTHROPIC_SMALL_FAST_MODEL"]);
    }

    [Fact]
    public void The_haiku_fallback_stays_off_the_clis_own_account()
    {
        var instance = new AiAgentInstance { Model = "claude-opus-4-8" };

        var environment = Agent("claude").EnvFor(
            AgentRuntime.For(new AppSettings(), instance));

        // On a subscription the default small model exists and answers; moving every background
        // call onto opus is what an unset row must not do. The fallback exists for the providers
        // that do not serve a haiku at all.
        Assert.False(environment.ContainsKey("ANTHROPIC_DEFAULT_HAIKU_MODEL"));
    }

    [Fact]
    public void A_fast_model_typed_on_the_row_is_honoured_on_the_own_account_too()
    {
        var instance = new AiAgentInstance { Model = "claude-opus-4-8", FastModel = "claude-haiku-4-5" };

        var environment = Agent("claude").EnvFor(
            AgentRuntime.For(new AppSettings(), instance));

        // Typed is a decision, wherever the CLI reads it; only the *fallback* is provider-only.
        Assert.Equal("claude-haiku-4-5", environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
    }

    // ── The resolver's gates ─────────────────────────────────────────────────────────────────────

    /// <summary>Only opencode's slot lives in a generated document; the question is the agent's own.
    /// </summary>
    [Theory]
    [InlineData("claude", false)]
    [InlineData("opencode", true)]
    [InlineData("codex", false)]
    [InlineData("pi", false)]
    [InlineData("agy", false)]
    public void Only_the_agent_with_a_generated_document_needs_a_declared_endpoint(string agentId,
        bool needs) => Assert.Equal(needs, Agent(agentId).FastModelNeedsDeclaredEndpoint);

    [Fact]
    public void A_typed_window_means_no_provider_is_asked()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";
        instance.AutoCompactWindow = 250_000;

        // The stub would answer a window if it were asked; null is therefore the gate's own answer,
        // not the network's.
        using var _ = new ModelContextWindowTestsHttp(
            """{"data":[{"id":"z-ai/glm-5.3-flash","context_length":200000}]}""");
        ModelContextWindow.Reset();

        Assert.Null(ModelContextWindow.ResolveAsync(settings, Agent("claude"), instance,
            "z-ai/glm-5.3-flash").GetAwaiter().GetResult());
    }

    [Fact]
    public void The_window_is_resolved_only_for_the_agent_that_reads_it()
    {
        var (settings, instance) = Configured("codex", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        using var _ = new ModelContextWindowTestsHttp(
            """{"data":[{"id":"z-ai/glm-5.3-flash","context_length":200000}]}""");
        ModelContextWindow.Reset();

        Assert.Null(ModelContextWindow.ResolveAsync(settings, Agent("codex"), instance,
            "z-ai/glm-5.3-flash").GetAwaiter().GetResult());
    }

    [Fact]
    public void The_resolved_answer_is_the_window_not_the_context()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        using var _ = new ModelContextWindowTestsHttp(
            """{"data":[{"id":"z-ai/glm-5.3-flash","context_length":200000}]}""");
        ModelContextWindow.Reset();

        Assert.Equal(160_000, ModelContextWindow.ResolveAsync(settings, Agent("claude"), instance,
            "z-ai/glm-5.3-flash").GetAwaiter().GetResult());
    }

    [Fact]
    public void The_second_resolution_within_the_cache_window_costs_no_call()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        using var _ = new ModelContextWindowTestsHttp(
            """{"data":[{"id":"z-ai/glm-5.3-flash","context_length":200000}]}""");
        ModelContextWindow.Reset();
        ModelContextWindowTestsHttp.Requests = 0;

        var first = ModelContextWindow.ResolveAsync(settings, Agent("claude"), instance,
            "z-ai/glm-5.3-flash").GetAwaiter().GetResult();
        var second = ModelContextWindow.ResolveAsync(settings, Agent("claude"), instance,
            "z-ai/glm-5.3-flash").GetAwaiter().GetResult();

        Assert.Equal(first, second);
        Assert.Equal(1, ModelContextWindowTestsHttp.Requests);
    }

    /// <summary>One canned reply for every request, counting what was asked — the seam
    /// <c>AiProviderTests</c> uses, plus the counter the cache test needs.</summary>
    private sealed class ModelContextWindowTestsHttp : IDisposable
    {
        public static int Requests;

        public ModelContextWindowTestsHttp(string body) =>
            AiProvider.HandlerFactory = () => new Canned(body);

        public void Dispose() => AiProvider.HandlerFactory = null;

        private sealed class Canned(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Requests);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            }
        }
    }

    // ── The tolerant converter ───────────────────────────────────────────────────────────────────

    /// <summary>A hand-typed string reads as the number it spells, and rubbish reads as unset —
    /// anything else would be a JsonException that quarantines the whole settings file.</summary>
    [Theory]
    [InlineData("""{"AutoCompactWindow":500000}""", 500_000L)]     // what this application writes
    [InlineData("""{"AutoCompactWindow":"500000"}""", 500_000L)]   // what a hand edit writes
    [InlineData("""{"AutoCompactWindow":null}""", null)]           // unset
    [InlineData("""{"AutoCompactWindow":"abc"}""", null)]          // rubbish reads as unset
    [InlineData("""{"AutoCompactWindow":true}""", null)]           // the wrong shape entirely
    [InlineData("""{"AutoCompactWindow":-5}""", null)]             // negative: the CLI would clamp it
    [InlineData("""{"AutoCompactWindow":"-5"}""", null)]           // negative spelled as a string
    [InlineData("""{}""", null)]                                   // absent
    public void The_auto_compact_window_is_read_tolerantly(string json, long? expected) =>
        Assert.Equal(expected, JsonSerializer.Deserialize<AiAgentInstance>(json)?.AutoCompactWindow);
}
