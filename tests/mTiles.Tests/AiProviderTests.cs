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
            ["anthropic", "lmstudio", "openrouter", "zai"],
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

    /// <summary>
    /// The OpenAI-compatible agents are given the provider's <em>own</em> key variable, and no address.
    /// </summary>
    /// <remarks><b>This asserted the opposite until 2026-08-31</b>, when the pairing was actually run:
    /// opencode and pi take no base URL from the environment at all. They keep a registry of providers
    /// and decide which one is in play from which key variable is set, so <c>OPENAI_API_KEY</c> for an
    /// OpenRouter instance authenticated the run against api.openai.com — reported as the OpenAI
    /// provider by <c>opencode auth list</c> — while the row on screen said OpenRouter. See
    /// <c>AgentProviderRoutingTests</c> for the whole of the corrected behaviour.</remarks>
    [Fact]
    public void An_openai_compatible_agent_is_given_the_providers_own_key()
    {
        var (settings, instance) = Configured("opencode", "openrouter", key: "sk-test");

        var environment = Agent("opencode").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("sk-test", environment["OPENROUTER_API_KEY"]);
        Assert.False(environment.ContainsKey("OPENAI_BASE_URL"));

        // Present and null: the block *removes* the other services' variables rather than leaving an
        // inherited one beside ours - see AgentProviderRoutingTests for why that half matters most.
        Assert.Null(environment["OPENAI_API_KEY"]);
    }

    /// <summary>
    /// OpenRouter's two shapes are two addresses, and Claude Code gets the shorter one.
    /// </summary>
    /// <remarks>The provider serves the Anthropic shape at <c>api/v1/messages</c> and Claude Code
    /// appends <c>/v1/messages</c> itself, so handing it the same <c>api/v1</c> an OpenAI-shaped client
    /// wants produced a doubled version and a 404. Both halves are asserted together: the fix is that
    /// they differ, so a test of one alone would pass on the bug.
    /// <para>Read off the endpoints rather than out of an environment block, because only one of the
    /// two is delivered that way — see <c>AgentProviderRoutingTests</c>.</para></remarks>
    [Fact]
    public void Openrouter_gives_claude_an_api_root_and_the_openai_agents_a_versioned_one()
    {
        var (settings, claude) = Configured("claude", "openrouter", key: "sk-test");
        var runtime = AgentRuntime.For(settings, claude);

        Assert.Equal("https://openrouter.ai/api",
            Agent("claude").EnvFor(runtime)["ANTHROPIC_BASE_URL"]);
        Assert.Equal("https://openrouter.ai/api/v1",
            runtime.EndpointFor(ApiFlavor.OpenAiChatCompletions)?.ToString());
    }

    /// <summary>An address the user typed replaces the provider's own, port and all.</summary>
    /// <remarks>Asserted through the endpoint the provider answers with, which is what every route to a
    /// server is built from — the agents differ in how they are told it, and one of them is told
    /// nothing at all.</remarks>
    [Fact]
    public void A_local_server_is_reached_where_the_user_said_it_was()
    {
        var (settings, instance) = Configured("pi", "lmstudio", key: "");
        settings.AiProviderInstances[0].BaseUrl = "192.168.1.10";

        var endpoint = AgentRuntime.For(settings, instance).EndpointFor(ApiFlavor.OpenAiChatCompletions);

        Assert.Equal("http://192.168.1.10:1234/v1", endpoint?.ToString());
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

    // ── Claude Code against a local server ───────────────────────────────────────────────────────

    /// <summary>
    /// LM Studio gets the API root for Claude Code and the versioned path for an OpenAI client.
    /// </summary>
    /// <remarks>Claude Code appends <c>/v1/messages</c> itself, so handing it the same <c>/v1</c> the
    /// other agents get produces a doubled version and a 404 — the mistake already paid for once
    /// against OpenRouter. Both halves are asserted together: the fix is that they differ, so a test of
    /// one alone would pass on the bug.</remarks>
    [Fact]
    public void Lm_studio_serves_claude_at_the_root_and_the_openai_agents_under_v1()
    {
        var provider = Provider("lmstudio");
        var instance = Instance("lmstudio", key: "", baseUrl: "127.0.0.1:1234");

        Assert.Equal("http://127.0.0.1:1234/",
            provider.EndpointFor(ApiFlavor.Anthropic, instance)?.ToString());
        Assert.Equal("http://127.0.0.1:1234/v1",
            provider.EndpointFor(ApiFlavor.OpenAiChatCompletions, instance)?.ToString());
    }

    /// <summary>
    /// A keyless provider still gets a non-empty token, because an empty one is a refusal.
    /// </summary>
    /// <remarks>Measured 2026-08-31 against Claude Code 2.1.251 and a running LM Studio:
    /// <c>ANTHROPIC_AUTH_TOKEN=""</c> fails with "Not logged in · Please run /login" before a request
    /// is made, while any non-empty value goes straight through — the server has no authentication and
    /// ignores it. Without this, every local-server pairing was configurable, offered, and dead on
    /// launch.</remarks>
    [Fact]
    public void Claude_gets_a_placeholder_token_where_the_provider_needs_no_key()
    {
        var (settings, instance) = Configured("claude", "lmstudio", key: "");

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.NotNull(environment["ANTHROPIC_AUTH_TOKEN"]);
        Assert.NotEmpty(environment["ANTHROPIC_AUTH_TOKEN"]!);
    }

    /// <summary>Ollama is not given the same, because it does not serve it.</summary>
    /// <remarks>Measured the same day: its <c>/v1/messages</c> answers 404. The flavors are what each
    /// server actually serves, and a pairing offered and then failed is worse than one never
    /// offered.</remarks>
    [Fact]
    public void Ollama_is_not_offered_to_claude() =>
        Assert.False(AiProviderCatalog.IsCompatible(Agent("claude"), Provider("ollama")));

    /// <summary>
    /// An address that was typed and cannot be read is a failure, not a fallback.
    /// </summary>
    /// <remarks><b>Empty and unparseable stopped being the same answer</b> the day the local providers
    /// gained a default of their own: <c>Parse(...) ?? DefaultBaseUrl</c> then sent a typo to this
    /// machine, so a Test could pass and a tile run against localhost while the row showed another
    /// address. Asserted through <c>BaseUrlFor</c> rather than <c>ProviderEndpoint.Parse</c>, because
    /// the fallback is what this is about.</remarks>
    [Theory]
    [InlineData("", "http://localhost:1234/")]
    [InlineData("   ", "http://localhost:1234/")]
    [InlineData("192.168.1.10", "http://192.168.1.10:1234/")]
    [InlineData("192.168.1.10:abc", null)]
    [InlineData("ftp://box.local", null)]
    public void A_typed_address_that_cannot_be_read_does_not_fall_back(string typed, string? expected)
    {
        var endpoint = AgentRuntime
            .For(SettingsWith("lmstudio", typed), InstanceOn("lmstudio"))
            .EndpointFor(ApiFlavor.OpenAiChatCompletions);

        // The endpoint is the base with /v1 on it, so an answer at all means the base resolved.
        Assert.Equal(expected is null, endpoint is null);
    }

    private static AppSettings SettingsWith(string providerId, string baseUrl)
    {
        var settings = new AppSettings();
        settings.AiProviderInstances.Add(
            new AiProviderInstance { Id = "p", ProviderId = providerId, BaseUrl = baseUrl });
        return settings;
    }

    private static AiAgentInstance InstanceOn(string providerId) =>
        new() { AgentId = "opencode", ApiAccountId = "p" };

    // ── Which ports a scan looks on ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A provider that can be moved is looked for on every port it turns up on.
    /// </summary>
    /// <remarks>Measured 2026-08-31: an LM Studio serving a loaded model on 8080 was invisible to
    /// Discover, which probed 1234 and nothing else. Whether 8080 is that build's default or somebody's
    /// choice is not knowable from the config file — which is the argument for probing both rather than
    /// for picking a winner.</remarks>
    [Fact]
    public void Lm_studio_is_looked_for_on_both_ports_it_turns_up_on() =>
        Assert.Equal([1234, 8080], Local("lmstudio").DiscoveryPorts);

    /// <summary>
    /// The one port stays the one an address is built from, and is among the ones scanned.
    /// </summary>
    /// <remarks><para>Two questions, one number each way round: <c>DefaultPort</c> is what a bare host
    /// means and has to be single, while a scan may look wider. A provider whose default was not in its
    /// own scan list would be discoverable nowhere it could actually be typed.</para>
    /// <para><b>Only the local ones are asked</b>, and openrouter used to be a row here — which is the
    /// whole of why the property moved to <c>ILocalAiProvider</c>: on a hosted provider it resolved to
    /// <c>[443]</c>, an answer no caller wants and none reads.</para></remarks>
    [Theory]
    [InlineData("lmstudio")]
    [InlineData("ollama")]
    public void Every_local_provider_scans_the_port_a_bare_host_would_mean(string providerId)
    {
        var provider = Local(providerId);

        Assert.Contains(provider.DefaultPort, provider.DiscoveryPorts);
    }

    /// <summary>The ports are asked of the servers that can be scanned for, and of nothing else.
    /// </summary>
    private static ILocalAiProvider Local(string id) =>
        Provider(id) as ILocalAiProvider
        ?? throw new InvalidOperationException($"'{id}' is not a local provider.");

    // ── A local provider with an empty address ───────────────────────────────────────────────────

    /// <summary>
    /// Empty means this machine, on the port the tool documents.
    /// </summary>
    /// <remarks>Both local providers used to answer null here, on the reasoning that a server on
    /// somebody's machine has no address until they say where it is. That is true of another machine
    /// and wrong about the case that happens: <c>GetJsonAsync</c> returned before a socket was opened,
    /// the empty model list that came back was indistinguishable from an unreachable server, and the
    /// user was sent to check a server that was running — from a form whose placeholder had told them
    /// the field could be left blank.</remarks>
    [Theory]
    [InlineData("lmstudio", "http://localhost:1234/")]
    [InlineData("ollama", "http://localhost:11434/")]
    public void A_local_provider_with_an_empty_address_means_this_machine(
        string providerId, string expected)
    {
        // Through the endpoint an agent would actually be given, which is the thing that was empty:
        // the flavor route is what a launch reads, and it is built from the same base address.
        var provider = Provider(providerId);
        var endpoint = provider.EndpointFor(ApiFlavor.OpenAiChatCompletions,
            Instance(providerId, key: "", baseUrl: ""));

        Assert.NotNull(endpoint);
        Assert.StartsWith(expected, endpoint.ToString());
    }

    /// <summary>
    /// And one that was asked and did not answer names the address it tried.
    /// </summary>
    /// <remarks>The port is the whole of this bug in practice: LM Studio's default is 1234 and it will
    /// happily be configured onto another, at which point "nothing answered there" is a sentence with
    /// no "there" in it — the address field the user is looking at may well be empty.</remarks>
    [Fact]
    public async Task A_local_provider_that_does_not_answer_names_where_it_looked()
    {
        using var http = new StubHttp("", HttpStatusCode.NotFound);

        var check = await Provider("lmstudio")
            .TestAsync(Instance("lmstudio", key: "", baseUrl: "127.0.0.1:8080"));

        Assert.False(check.Ok);
        Assert.Contains("8080", check.Message);
    }

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
        instance.ApiAccountId = provider.Id;

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
    /// A typed address that cannot be read is named, rather than the key being blamed for it.
    /// </summary>
    /// <remarks>No request is made at all in that case, so every provider answered its own "the key was
    /// not accepted" — and the first thing a user does with that is rotate a key that works. One
    /// provider is enough: the short-circuit is the same line in all six.</remarks>
    [Fact]
    public async Task A_test_names_an_address_it_could_not_read()
    {
        // No stub: the point is that nothing is sent.
        var instance = Instance("openrouter", "sk-test");
        instance.BaseUrl = "192.168.1.10:abc";

        var check = await new OpenRouterProvider().TestAsync(instance);

        Assert.False(check.Ok);
        Assert.Contains("192.168.1.10:abc", check.Message);
        Assert.Contains("not an address this can read", check.Message);
        Assert.DoesNotContain("key", check.Message);
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
