using System.Net;
using System.Net.Http;
using System.Text;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The provider layer: what an address typed by hand means, which agent can be pointed at which
/// service, what a provider's silence about effort is allowed to do, and what reaches an agent's
/// environment.
/// </summary>
/// <remarks>No key and no network: every call that would leave the machine goes through
/// <c>AiProvider.HandlerFactory</c>, the same style of seam as <c>TerminalControl.PtyFactory</c>.
/// </remarks>
public class AiProviderTests
{
    // ── The endpoint parser ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What people actually type into an address field, and what each of those means.
    /// </summary>
    /// <remarks>The last two rows are the ones a <c>Split(':')</c> gets wrong: a bare IPv6 literal is
    /// all colons and no port, and reading it as host and port loses the address entirely.</remarks>
    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("localhost", "http://localhost:1234/")]
    [InlineData("192.168.1.10", "http://192.168.1.10:1234/")]
    [InlineData("192.168.1.10:8080", "http://192.168.1.10:8080/")]
    [InlineData("http://box.local:9000/v1", "http://box.local:9000/v1/")]
    [InlineData("https://api.example.com/", "https://api.example.com/")]
    [InlineData("https://gw.example.com/openai", "https://gw.example.com/openai/")]
    [InlineData("[::1]", "http://[::1]:1234/")]
    [InlineData("[::1]:8080", "http://[::1]:8080/")]
    [InlineData("::1", "http://[::1]:1234/")]
    [InlineData("ftp://box.local", null)]
    [InlineData("box:notaport", null)]
    [InlineData("box:70000", null)]
    public void An_address_is_read_the_way_it_was_meant(string typed, string? expected)
    {
        var parsed = ProviderEndpoint.Parse(typed, defaultPort: 1234);
        Assert.Equal(expected, parsed?.ToString());
    }

    /// <summary>
    /// A gateway address with a path keeps that path once something is appended to it.
    /// </summary>
    /// <remarks>The composition every caller does is <c>new Uri(base, relative)</c>, and RFC 3986 drops
    /// everything after the base's last slash — so without the normalisation the gateway's own prefix
    /// disappears and the call goes to the wrong server's root. This is the form provider documentation
    /// prints, so it is the form that gets pasted in.</remarks>
    [Fact]
    public void A_gateway_path_survives_having_a_relative_path_appended()
    {
        var parsed = ProviderEndpoint.Parse("https://gw.example.com/openai", defaultPort: 1234);

        Assert.Equal("https://gw.example.com/openai/v1", new Uri(parsed!, "v1").ToString());
    }

    // ── Compatibility ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// codex plus a local server is <b>not</b> compatible, which is the whole reason the OpenAI flavor
    /// is split in two.
    /// </summary>
    /// <remarks>Both are "OpenAI" in ordinary speech, and the pairing does not work: codex speaks
    /// <c>/v1/responses</c> and a local server serves <c>/v1/chat/completions</c>. Offering it and
    /// failing the launch is worse than never offering it.</remarks>
    [Fact]
    public void Codex_is_not_compatible_with_a_local_server()
    {
        var codex = Agent("codex");

        Assert.False(AiProviderCatalog.IsCompatible(codex, Provider("ollama")));
        Assert.False(AiProviderCatalog.IsCompatible(codex, Provider("lmstudio")));
        Assert.True(AiProviderCatalog.IsCompatible(codex, Provider("openai")));
        Assert.True(AiProviderCatalog.IsCompatible(codex, Provider("openrouter")));
    }

    /// <summary>Claude Code needs an Anthropic-shaped endpoint, and three of the six serve one.</summary>
    [Fact]
    public void Claude_is_compatible_with_exactly_the_anthropic_shaped_services()
    {
        Assert.Equal(
            ["anthropic", "openrouter", "zai"],
            AiProviderCatalog.CompatibleWith(Agent("claude")).Select(p => p.Id).Order());
    }

    /// <summary>opencode and pi speak the shape a local server serves, which is what makes running a
    /// model on this machine possible at all.</summary>
    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void The_chat_completions_agents_can_use_a_local_server(string agentId)
    {
        Assert.True(AiProviderCatalog.IsCompatible(Agent(agentId), Provider("lmstudio")));
        Assert.True(AiProviderCatalog.IsCompatible(Agent(agentId), Provider("ollama")));
    }

    // ── Effort: what "the provider did not say" is allowed to do ─────────────────────────────────

    /// <summary>
    /// An unknown per-model effort list leaves the agent's own list exactly as it was.
    /// </summary>
    /// <remarks>Only OpenRouter answers this honestly, so treating silence as a denial would empty the
    /// effort chooser for five providers out of six — the same tri-state rule the workspace panel's
    /// <c>HasRepository</c> follows.</remarks>
    [Fact]
    public void A_provider_that_says_nothing_about_effort_narrows_nothing()
    {
        IReadOnlyList<AiEffort> agent = [AiEffort.Low, AiEffort.High, AiEffort.Max];

        Assert.Equal(agent, AiProviderCatalog.NarrowEfforts(agent, modelEfforts: null));
    }

    /// <summary>A list that <em>is</em> given narrows to it, keeping "pass nothing" — which is not an
    /// effort the provider could have an opinion about.</summary>
    [Fact]
    public void A_provider_that_does_say_narrows_the_agents_list()
    {
        IReadOnlyList<AiEffort> agent =
            [AiEffort.Low, AiEffort.High, AiEffort.Max, AiEffort.ToolDefault];

        var narrowed = AiProviderCatalog.NarrowEfforts(agent, [AiEffort.Low, AiEffort.Medium]);

        Assert.Equal([AiEffort.Low, AiEffort.ToolDefault], narrowed);
    }

    /// <summary>A model that takes no reasoning at all still leaves something choosable: an empty
    /// chooser is a control the user cannot say anything with.</summary>
    [Fact]
    public void A_model_that_takes_no_effort_leaves_the_tool_default()
    {
        Assert.Equal(
            [AiEffort.ToolDefault],
            AiProviderCatalog.NarrowEfforts([AiEffort.High], modelEfforts: []));
    }

    // ── The environment an agent runs with ───────────────────────────────────────────────────────

    /// <summary>
    /// Claude Code pointed at another service authenticates with the token <b>and the inherited key is
    /// removed</b>.
    /// </summary>
    /// <remarks>That single null is what stage 2 was for. Without it, a machine exporting a global
    /// <c>ANTHROPIC_API_KEY</c> keeps sending it beside our token, and the session runs on somebody
    /// else's account with nothing on screen saying so.</remarks>
    [Fact]
    public void A_configured_provider_unsets_the_inherited_anthropic_key()
    {
        var (settings, instance) = Configured("claude", "zai", key: "zzz");

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("https://api.z.ai/api/anthropic", environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("zzz", environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.True(environment.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Null(environment["ANTHROPIC_API_KEY"]);
    }

    /// <summary>An instance with no provider contributes nothing: the agent runs on whatever it was
    /// configured with, which is what a first run is in.</summary>
    [Fact]
    public void An_instance_with_no_provider_leaves_the_environment_alone()
    {
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(Agent("claude"));

        Assert.Empty(Agent("claude").EnvFor(AgentRuntime.For(settings, instance)));
    }

    /// <summary>
    /// What the user set by hand wins, including putting back a variable the agent asked to remove.
    /// </summary>
    /// <remarks>Merged last on purpose, and the reason <c>EnvFor</c> is not virtual: an agent free to
    /// override the whole method could drop that rule with nothing noticing.</remarks>
    [Fact]
    public void The_users_own_variables_are_merged_last()
    {
        var (settings, instance) = Configured("claude", "zai", key: "zzz");
        instance.ExtraEnv["ANTHROPIC_API_KEY"] = "mine";

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("mine", environment["ANTHROPIC_API_KEY"]);
    }

    /// <summary>The OpenAI-compatible agents get the pair their own CLI reads, at the provider's own
    /// path.</summary>
    [Fact]
    public void An_openai_compatible_agent_is_given_the_address_and_the_key()
    {
        var (settings, instance) = Configured("opencode", "openrouter", key: "sk-test");

        var environment = Agent("opencode").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("https://openrouter.ai/api/v1", environment["OPENAI_BASE_URL"]);
        Assert.Equal("sk-test", environment["OPENAI_API_KEY"]);
    }

    /// <summary>
    /// OpenRouter's two shapes are two addresses, and Claude Code gets the shorter one.
    /// </summary>
    /// <remarks>The provider serves the Anthropic shape at <c>api/v1/messages</c> and Claude Code
    /// appends <c>/v1/messages</c> itself, so handing it the same <c>api/v1</c> the OpenAI-shaped
    /// agents get produced a doubled version and a 404 — which Claude Code reports as a model that does
    /// not exist. Both halves are asserted together: the fix is that they differ, so a test of one
    /// alone would pass on the bug.</remarks>
    [Fact]
    public void Openrouter_gives_claude_an_api_root_and_the_openai_agents_a_versioned_one()
    {
        var (anthropicSide, claude) = Configured("claude", "openrouter", key: "sk-test");
        var (openAiSide, opencode) = Configured("opencode", "openrouter", key: "sk-test");

        Assert.Equal("https://openrouter.ai/api",
            Agent("claude").EnvFor(AgentRuntime.For(anthropicSide, claude))["ANTHROPIC_BASE_URL"]);
        Assert.Equal("https://openrouter.ai/api/v1",
            Agent("opencode").EnvFor(AgentRuntime.For(openAiSide, opencode))["OPENAI_BASE_URL"]);
    }

    /// <summary>An address the user typed replaces the provider's own, port and all.</summary>
    [Fact]
    public void A_local_server_is_reached_where_the_user_said_it_was()
    {
        var (settings, instance) = Configured("pi", "lmstudio", key: "");
        settings.AiProviderInstances[0].BaseUrl = "192.168.1.10";

        var environment = Agent("pi").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("http://192.168.1.10:1234/v1", environment["OPENAI_BASE_URL"]);
    }

    // ── Availability ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An instance pointing at a provider that has been deleted is not offered.
    /// </summary>
    /// <remarks>It would launch — on the agent's own configuration, a different account and possibly a
    /// different model — and nothing on screen would say that is what happened.</remarks>
    [Fact]
    public void An_instance_whose_provider_has_been_deleted_is_not_offered()
    {
        var (settings, instance) = Configured("claude", "zai", key: "zzz");
        settings.AiProviderInstances.Clear();

        Assert.False(AiAgentCatalog.IsAvailable(instance, settings));
    }

    /// <summary>A pairing the flavor check rules out is not offered either, however configured it
    /// is.</summary>
    [Fact]
    public void An_incompatible_pairing_is_not_offered()
    {
        var (settings, instance) = Configured("codex", "ollama", key: "");

        Assert.False(AiAgentCatalog.IsAvailable(instance, settings));
    }

    // ── Talking to a provider ────────────────────────────────────────────────────────────────────

    /// <summary>OpenRouter is the one provider that says what is left on the key.</summary>
    [Fact]
    public async Task A_balance_is_read_where_the_service_gives_one()
    {
        using var _ = new StubHttp("""{"data":{"limit_remaining":12.5}}""");

        var check = await new OpenRouterProvider().TestAsync(Instance("openrouter", "sk-test"));

        Assert.True(check.Ok);
        Assert.Equal(12.5m, check.Balance);
    }

    /// <summary>
    /// A service with no balance endpoint answers null, and null is not zero.
    /// </summary>
    /// <remarks>Shown as 0 it would tell a user whose key works perfectly well that they had run out.
    /// </remarks>
    [Fact]
    public async Task A_service_that_does_not_say_answers_null_and_not_zero()
    {
        using var _ = new StubHttp("""{"data":[{"id":"claude-opus-4"}]}""");

        var check = await new AnthropicProvider().TestAsync(Instance("anthropic", "sk-ant"));

        Assert.True(check.Ok);
        Assert.Null(check.Balance);
    }

    /// <summary>A provider that will not answer is a failure with a reason, never an exception at the
    /// button that asked.</summary>
    [Fact]
    public async Task A_provider_that_cannot_be_reached_answers_rather_than_throws()
    {
        using var _ = new StubHttp("nope", HttpStatusCode.Unauthorized);

        var check = await new OpenAiProvider().TestAsync(Instance("openai", "wrong"));

        Assert.False(check.Ok);
        Assert.NotEqual("", check.Message);
    }

    /// <summary>OpenRouter's <c>supported_parameters</c> is the one per-model effort answer any of these
    /// give — and a model that does not mention reasoning says so, rather than saying nothing.</summary>
    [Fact]
    public async Task Only_a_model_that_mentions_reasoning_is_reported_as_taking_effort()
    {
        using var _ = new StubHttp("""
            {"data":[
              {"id":"thinks","supported_parameters":["reasoning","tools"]},
              {"id":"plain","supported_parameters":["tools"]},
              {"id":"quiet"}
            ]}
            """);

        var models = await new OpenRouterProvider().ModelsAsync(Instance("openrouter", "sk-test"));

        Assert.NotEmpty(models.Single(m => m.Id == "thinks").SupportedEfforts!);
        Assert.Empty(models.Single(m => m.Id == "plain").SupportedEfforts!);
        Assert.Null(models.Single(m => m.Id == "quiet").SupportedEfforts);
    }

    // ── The "first loaded" sentinel ──────────────────────────────────────────────────────────────

    /// <summary>A hosted service cannot say what is loaded, so the sentinel is refused with a reason
    /// rather than resolved into a model of our choosing.</summary>
    [Fact]
    public async Task First_loaded_has_no_meaning_on_a_hosted_service()
    {
        var (model, problem) = await AiModelChoice.ResolveAsync(new OpenAiProvider(),
            Instance("openai", "sk"), AiModelChoice.FirstLoaded);

        Assert.Null(model);
        Assert.NotNull(problem);
    }

    /// <summary>
    /// On a local server it is whatever that server has in memory right now.
    /// </summary>
    /// <remarks>Resolved at every launch and never written down: persisting the answer would mean
    /// changing the model in LM Studio no longer changed it here, which is the whole point of having a
    /// sentinel at all.</remarks>
    [Fact]
    public async Task First_loaded_is_what_the_local_server_has_in_memory()
    {
        using var _ = new StubHttp("""{"models":[{"name":"qwen3:8b"}]}""");

        var (model, problem) = await AiModelChoice.ResolveAsync(new OllamaProvider(),
            Instance("ollama", "", "localhost"), AiModelChoice.FirstLoaded);

        Assert.Equal("qwen3:8b", model);
        Assert.Null(problem);
    }

    /// <summary>A local server with nothing loaded fails the resolution and says so — it does not pick
    /// something for the user.</summary>
    [Fact]
    public async Task A_local_server_with_nothing_loaded_is_a_readable_failure()
    {
        using var _ = new StubHttp("""{"models":[]}""");

        var (model, problem) = await AiModelChoice.ResolveAsync(new OllamaProvider(),
            Instance("ollama", "", "localhost"), AiModelChoice.FirstLoaded);

        Assert.Null(model);
        Assert.NotNull(problem);
    }

    /// <summary>Anything that is not the sentinel is passed through untouched, without a call.</summary>
    [Fact]
    public async Task A_named_model_is_not_resolved_against_anything()
    {
        var (model, problem) = await AiModelChoice.ResolveAsync(new OllamaProvider(),
            Instance("ollama", "", "localhost"), "qwen3:8b");

        Assert.Equal("qwen3:8b", model);
        Assert.Null(problem);
    }

    // ── Persistence ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A provider instance survives a restart, key included, and the key is not in the file in plain
    /// text.
    /// </summary>
    /// <remarks>The same route the database passwords take. Nothing is seeded: an empty list means
    /// nothing has been configured, not six services none of which work.</remarks>
    [Fact]
    public void A_provider_instance_round_trips_without_its_key_in_the_open()
    {
        using var settings = new TempSettings();
        Assert.Empty(settings.Service.Settings.AiProviderInstances);

        settings.Service.Settings.AiProviderInstances.Add(new AiProviderInstance
        {
            Id = "p1",
            ProviderId = "openrouter",
            Name = "Mine",
            ApiKey = "sk-secret-value",
        });
        settings.Service.Save();

        using var reopened = new TempSettings(settings.Directory);
        var stored = Assert.Single(reopened.Service.Settings.AiProviderInstances);

        Assert.Equal("sk-secret-value", stored.ApiKey);
        Assert.DoesNotContain("sk-secret-value", File.ReadAllText(settings.SettingsFile));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static IAiAgent Agent(string id) =>
        AiAgentCatalog.Find(id) ?? throw new InvalidOperationException($"No agent '{id}'.");

    private static IAiProvider Provider(string id) =>
        AiProviderCatalog.Find(id) ?? throw new InvalidOperationException($"No provider '{id}'.");

    private static AiProviderInstance Instance(string providerId, string key, string baseUrl = "") =>
        new() { ProviderId = providerId, ApiKey = key, BaseUrl = baseUrl };

    /// <summary>An agent instance pointed at a configured provider, and the settings holding both.</summary>
    private static (AppSettings Settings, AiAgentInstance Instance) Configured(string agentId,
        string providerId, string key)
    {
        var provider = new AiProviderInstance { ProviderId = providerId, ApiKey = key };
        var instance = AiAgentCatalog.SeedInstanceFor(Agent(agentId));
        instance.ProviderInstanceId = provider.Id;

        var settings = new AppSettings();
        settings.AiProviderInstances.Add(provider);
        settings.AiAgentInstances.Add(instance);
        return (settings, instance);
    }

    // ── A provider that does not answer ──────────────────────────────────────────────────────────

    /// <summary>
    /// A timeout is answered, not thrown.
    /// </summary>
    /// <remarks><c>HttpClient.Timeout</c> arrives as a <c>TaskCanceledException</c>, which is an
    /// <c>OperationCanceledException</c> — so a catch filtered on that type alone let the one failure
    /// this layer exists to absorb escape onto the UI thread. Nothing above catches it: Test has a
    /// <c>try/finally</c>, the two model lists have nothing at all.</remarks>
    [Fact]
    public async Task An_address_that_never_answers_is_reported_rather_than_thrown()
    {
        using var _ = new ThrowingHttp(new TaskCanceledException("The request was canceled due to the "
            + "configured HttpClient.Timeout of 30 seconds elapsing."));

        var provider = Provider("ollama");
        var instance = Instance("ollama", key: "", baseUrl: "192.0.2.1:11434");

        Assert.False((await provider.TestAsync(instance)).Ok);
        Assert.Empty(await provider.ModelsAsync(instance));
        Assert.Null(await ((ILocalAiProvider)provider).FirstLoadedModelAsync(instance));
        Assert.False(await ((ILocalAiProvider)provider).IsServingAsync(new Uri("http://192.0.2.1:11434/")));
    }

    /// <summary>
    /// The unreachable local server refuses the launch instead of starting one with no model.
    /// </summary>
    /// <remarks>The sentinel's whole promise: a session that cannot be told which model was loaded must
    /// not quietly run on whatever the CLI would pick, against an address chosen for the model that was
    /// asked for.</remarks>
    [Fact]
    public async Task A_local_server_that_never_answers_stops_the_launch_with_a_sentence()
    {
        using var _ = new ThrowingHttp(new TaskCanceledException("timed out"));

        var (settings, instance) = Configured("pi", "ollama", key: "");
        instance.Model = AiModelChoice.FirstLoaded;

        var (model, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("pi"), instance);

        Assert.Null(model);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    /// <summary>A caller who really did cancel still gets the cancellation it asked for.</summary>
    /// <remarks>The reason the filter asks the token rather than the exception type: swallowing every
    /// <c>OperationCanceledException</c> would turn a cancelled call into an empty answer, which reads
    /// as "the provider serves nothing".</remarks>
    [Fact]
    public async Task A_cancelled_call_is_still_a_cancellation()
    {
        using var _ = new ThrowingHttp(new TaskCanceledException("cancelled"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Provider("ollama").ModelsAsync(
                Instance("ollama", key: "", baseUrl: "192.0.2.1:11434"), cancelled.Token));
    }

    /// <summary>Every request fails the way an unreachable address does.</summary>
    private sealed class ThrowingHttp : IDisposable
    {
        public ThrowingHttp(Exception failure) =>
            AiProvider.HandlerFactory = () => new FailingHandler(failure);

        public void Dispose() => AiProvider.HandlerFactory = null;

        private sealed class FailingHandler(Exception failure) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromException<HttpResponseMessage>(failure);
        }
    }

    /// <summary>
    /// One canned reply for every request this provider layer makes, for as long as it is not disposed.
    /// </summary>
    /// <remarks>A handler rather than a client, because the base address and the timeout are exactly the
    /// two per-instance things worth seeing applied. The seam is restored on disposal, so one test
    /// cannot leave the next one talking to a stub.</remarks>
    private sealed class StubHttp : IDisposable
    {
        public StubHttp(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            AiProvider.HandlerFactory = () => new CannedHandler(body, status);

        public void Dispose() => AiProvider.HandlerFactory = null;

        private sealed class CannedHandler(string body, HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
        }
    }
}
