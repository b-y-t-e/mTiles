using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// How an instance's provider actually reaches the CLI that runs on it.
/// </summary>
/// <remarks>
/// <para><b>Every agent measured 2026-08-31, and the answers are not the same shape.</b> Claude Code is
/// pointed at a service by <em>address</em> — <c>ANTHROPIC_BASE_URL</c> redirects it and the model id
/// travels bare. opencode and pi are pointed at one by <em>name</em>: each keeps a registry, decides
/// which provider is available from which key variable is set, and validates <c>provider/model</c>
/// against a catalogue before opening a socket.</para>
/// <para>This file exists because that difference was not modelled at all: both were given
/// <c>OPENAI_BASE_URL</c> and <c>OPENAI_API_KEY</c> and a bare model, so an instance configured for
/// OpenRouter authenticated against api.openai.com and its model was refused outright
/// (<c>ProviderModelNotFoundError</c>) — a pairing offered, stored, launched and ignored.</para>
/// </remarks>
public class AgentProviderRoutingTests : IDisposable
{
    // Every test in here can reach AppPaths - a sign-in's directory is derived from it, and
    // EnvFor now creates one. Without this the suite wrote into a live installation.
    private readonly TempAppData _appData = new();

    public void Dispose() => _appData.Dispose();

    // ── The model, spelled the way each CLI wants it ──────────────────────────────────────────────

    /// <summary>
    /// opencode and pi want <c>provider/model</c>; everyone else takes the id as stored.
    /// </summary>
    /// <remarks>The namespaced id is the interesting row: <c>z-ai/glm-5.3-flash</c> is <em>one</em>
    /// model name in OpenRouter's catalogue, so the qualified form has two slashes in it. Trying to be
    /// clever about an existing slash would break every id these services actually publish.</remarks>
    [Theory]
    [InlineData("opencode", "openrouter/z-ai/glm-5.3-flash")]
    [InlineData("pi", "openrouter/z-ai/glm-5.3-flash")]
    [InlineData("claude", "z-ai/glm-5.3-flash")]
    [InlineData("codex", "z-ai/glm-5.3-flash")]
    public void The_model_is_spelled_the_way_the_agent_expects(string agentId, string expected)
    {
        var (settings, instance) = Configured(agentId, "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        Assert.Equal(expected, Agent(agentId).QualifiedModel(AgentRuntime.For(settings, instance)));
    }

    /// <summary>
    /// A bare model on an agent that names its provider in the model is refused by a sentence.
    /// </summary>
    /// <remarks>With no provider there is nothing to prefix it with, so what is typed reaches the
    /// command line verbatim — and <c>opencode --model gpt-5</c> answers <c>ProviderModelNotFoundError</c>
    /// before a socket is opened. The sign-in path is the one that made this reachable in the UI: a
    /// subscription has no catalogue to complete from either, so the qualified form was something the
    /// user simply had to know.</remarks>
    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void A_model_with_no_provider_to_qualify_it_is_refused(string agentId)
    {
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(Agent(agentId));
        instance.Model = "gpt-5";

        var problem = AgentAvailability.Problem(instance, settings);

        Assert.NotNull(problem);
        Assert.Contains("provider/model", problem);

        // Already qualified, and nothing to say. Nor for an instance naming no model at all: the CLI
        // then chooses within its own provider, which is what "the agent's own account" asks for.
        instance.Model = "openrouter/gpt-5";
        Assert.Null(AgentAvailability.Problem(instance, settings));

        instance.Model = "";
        Assert.Null(AgentAvailability.Problem(instance, settings));
    }

    /// <summary>Claude Code takes a bare id, so nothing is said about one.</summary>
    [Fact]
    public void An_agent_pointed_at_a_service_by_address_takes_a_bare_model()
    {
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(Agent("claude"));
        instance.Model = "claude-opus-5";

        Assert.Null(AgentAvailability.Problem(instance, settings));
    }

    /// <summary>
    /// Write merges the user's own configuration, not only Document does.
    /// </summary>
    /// <remarks><b>The merge was tested only through <c>Document(theirs:)</c></b> — the path that
    /// actually finds their file had no test at all, because it read the real home directory and one
    /// could not be written without touching the developer's own. <c>HomeOverride</c> is that seam, and
    /// it is what stops these tests copying a machine's opencode key into a generated file.</remarks>
    [Fact]
    public void The_generated_config_is_written_with_the_users_own_settings_merged()
    {
        using var appData = new TempAppData();
        var theirs = Path.Combine(appData.Root, ".config", "opencode");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "opencode.json"),
            """{ "model": "openrouter/theirs", "mcp": { "their-server": {} } }""");

        var (settings, instance) = Configured("opencode", "lmstudio", key: "");
        instance.Model = "gemma";
        var runtime = AgentRuntime.For(settings, instance, "gemma");

        Agent("opencode").PrepareToLaunch(runtime);

        var written = File.ReadAllText(OpenCodeProviderConfig.PathFor(instance.Id));
        Assert.Contains("openrouter/theirs", written);
        Assert.Contains("their-server", written);
        Assert.Contains("lmstudio", written);
    }

    /// <summary>
    /// The chooser refuses the pairing the row would refuse, address included.
    /// </summary>
    /// <remarks>Compatibility and <c>IsLocal</c> are not the whole question: a hosted provider with a
    /// gateway typed into it needs somewhere to put an address exactly as a local server does, so pi +
    /// "OpenRouter via my gateway" was offered and the instance saved from it was unavailable at
    /// once — the drift both methods' remarks promise cannot happen.</remarks>
    [Fact]
    public void The_chooser_and_the_row_agree_about_an_address_typed_into_a_hosted_provider()
    {
        var pi = Agent("pi");
        var openrouter = AiProviderCatalog.Find("openrouter")!;
        var published = new AiProviderInstance { ProviderId = "openrouter", ApiKey = "sk-test" };
        var gateway = new AiProviderInstance
        {
            ProviderId = "openrouter", ApiKey = "sk-test", BaseUrl = "https://gateway.example.com/v1",
        };

        Assert.True(AgentAvailability.CanPair(pi, openrouter, published));
        Assert.False(AgentAvailability.CanPair(pi, openrouter, gateway));

        // And the agent that can carry one is offered both.
        var opencode = Agent("opencode");
        Assert.True(AgentAvailability.CanPair(opencode, openrouter, gateway));
    }

    /// <summary>
    /// An agent this build does not have is refused out loud, whatever account it names.
    /// </summary>
    /// <remarks>Narrowed to instances naming one, an instance on the CLI's own account was hidden by
    /// <c>AiAgentCatalog.IsAvailable</c> — which fails at the lookup, before any account — while its
    /// row said nothing and showed NOT INSTALLED, which is a different claim.</remarks>
    [Fact]
    public void An_agent_this_build_does_not_have_is_explained_with_or_without_an_account()
    {
        var settings = new AppSettings();
        var instance = new AiAgentInstance { AgentId = "from-a-newer-build", Name = "Mine" };

        Assert.False(AiAgentCatalog.IsAvailable(instance, settings));
        Assert.Contains("does not have", AgentAvailability.Problem(instance, settings));
    }

    /// <summary>
    /// A provider entry in the user's own config that is not an object does not cost them the file.
    /// </summary>
    /// <remarks>Their file is not validated against a schema and is read best effort, so
    /// <c>"provider": { "foo": 3 }</c> is a thing that arrives. <c>JsonNode</c>'s indexer throws on a
    /// value, and the throw was from the loop that only <em>logs</em> — caught by <c>Write</c>, which
    /// then discarded the document and left the launch with no <c>OPENCODE_CONFIG</c> at all: the
    /// <c>ProviderModelNotFoundError</c> this file exists to prevent, caused by a line that reports.
    /// </remarks>
    [Theory]
    [InlineData("""{ "provider": { "foo": 3 } }""")]
    [InlineData("""{ "provider": { "foo": [1, 2] } }""")]
    [InlineData("""{ "provider": { "foo": { "options": 7 } } }""")]
    [InlineData("""{ "provider": 3 }""")]
    public void A_provider_entry_that_is_not_an_object_does_not_stop_the_config_being_written(
        string theirs)
    {
        using var appData = new TempAppData();
        var directory = Path.Combine(appData.Root, ".config", "opencode");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "opencode.json"), theirs);

        var (settings, instance) = Configured("opencode", "lmstudio", key: "");
        instance.Model = "gemma";

        Agent("opencode").PrepareToLaunch(AgentRuntime.For(settings, instance, "gemma"));

        var path = OpenCodeProviderConfig.PathFor(instance.Id);
        Assert.True(File.Exists(path), "the generated config was discarded");
        Assert.Contains("lmstudio", File.ReadAllText(path));
    }

    /// <summary>An instance on no provider is left alone: there is no provider to name.</summary>
    /// <remarks>Prefixing here would invent a pairing nobody chose — and "the agent's own account" is
    /// the state every seeded instance starts in.</remarks>
    [Fact]
    public void An_instance_with_no_provider_keeps_its_model_bare()
    {
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(Agent("opencode"));
        instance.Model = "some-model";

        Assert.Equal("some-model", Agent("opencode").QualifiedModel(AgentRuntime.For(settings, instance)));
    }

    /// <summary>An instance naming no model still names none, prefix or not.</summary>
    [Fact]
    public void No_model_stays_no_model()
    {
        var (settings, instance) = Configured("opencode", "openrouter", "sk-test");
        instance.Model = "";

        Assert.Equal("", Agent("opencode").QualifiedModel(AgentRuntime.For(settings, instance)));
    }

    // ── The key, under the name that service's key is read from ──────────────────────────────────

    /// <summary>
    /// The provider's own variable, not <c>OPENAI_API_KEY</c> for everything.
    /// </summary>
    /// <remarks>This is the half that sent the work to the wrong company: measured through
    /// <c>opencode auth list</c>, our <c>OPENAI_API_KEY</c> was reported as the <b>OpenAI</b> provider,
    /// so every instance authenticated against api.openai.com whatever its row said.</remarks>
    [Theory]
    [InlineData("opencode", "openrouter", "OPENROUTER_API_KEY")]
    [InlineData("pi", "openrouter", "OPENROUTER_API_KEY")]
    [InlineData("opencode", "openai", "OPENAI_API_KEY")]
    [InlineData("pi", "zai", "ZAI_API_KEY")]
    public void The_key_goes_under_the_providers_own_variable(
        string agentId, string providerId, string variable)
    {
        var (settings, instance) = Configured(agentId, providerId, "sk-test");

        var environment = Agent(agentId).EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("sk-test", environment[variable]);
        Assert.False(environment.ContainsKey("OPENAI_BASE_URL"));
    }

    /// <summary>Claude Code is unaffected: it really is pointed at an address.</summary>
    /// <remarks>The two shapes have to be able to coexist, and this is what says the change did not
    /// spread to the agent it was right for.</remarks>
    [Fact]
    public void Claude_is_still_pointed_at_an_address()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("https://openrouter.ai/api", environment["ANTHROPIC_BASE_URL"]);
        Assert.Equal("sk-test", environment["ANTHROPIC_AUTH_TOKEN"]);
    }

    /// <summary>
    /// Every other service's key variable is <b>removed</b>, not merely left unset.
    /// </summary>
    /// <remarks>The half that was missing, and the one that mattered: these CLIs choose a provider from
    /// which key variable is set, so on a machine exporting a global <c>OPENAI_API_KEY</c> an OpenRouter
    /// instance showed the CLI two accounts and nothing to choose between them — and with no model on
    /// the instance, which is how every seeded one starts, there is no <c>provider/model</c> to break
    /// the tie either. A null value unsets, exactly as <c>ClaudeAgent</c> already relies on for
    /// <c>ANTHROPIC_API_KEY</c>.</remarks>
    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void The_other_services_key_variables_are_unset(string agentId)
    {
        var (settings, instance) = Configured(agentId, "openrouter", key: "sk-test");

        var environment = Agent(agentId).EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("sk-test", environment["OPENROUTER_API_KEY"]);
        foreach (var variable in new[] { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "ZAI_API_KEY" })
        {
            Assert.True(environment.ContainsKey(variable), $"{variable} is not removed");
            Assert.Null(environment[variable]);
        }
    }

    /// <summary>An instance on no provider removes nothing.</summary>
    /// <remarks>"The agent's own account" means the CLI's own setup, and taking its keys away is not
    /// what that asks for.</remarks>
    [Fact]
    public void An_instance_on_no_provider_leaves_every_key_alone()
    {
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(Agent("opencode"));

        Assert.Empty(Agent("opencode").EnvFor(AgentRuntime.For(settings, instance)));
    }

    /// <summary>
    /// A declared provider leaves the user's other keys alone.
    /// </summary>
    /// <remarks><b>This asserted the opposite, and the two halves were arguing.</b> Clearing exists to
    /// stop an inherited key <em>selecting</em> a service — but a local server is named in a generated
    /// config file and picked by <c>lmstudio/&lt;model&gt;</c> on the command line, so nothing is
    /// ambiguous, and removing the keys broke exactly the providers whose configuration
    /// <c>OpenCodeProviderConfig</c> merges in so that they keep working in that tile.</remarks>
    [Fact]
    public void A_declared_provider_leaves_the_other_keys_alone()
    {
        var (settings, instance) = Configured("opencode", "lmstudio", key: "");

        var environment = Agent("opencode").EnvFor(AgentRuntime.For(settings, instance));

        Assert.False(environment.ContainsKey("OPENROUTER_API_KEY"));
        Assert.False(environment.ContainsKey("OPENAI_API_KEY"));
    }

    /// <summary>
    /// The small, frequent calls run on the row's model, not on the CLI's own default.
    /// </summary>
    /// <remarks>Claude Code's own default small model is an Anthropic id, which a third-party
    /// provider does not serve — with the variable unset those calls failed while the real ones
    /// worked. On a provider, an empty Fast model therefore falls back to the same model the real
    /// calls run on; on the CLI's own account the default exists and the field left empty leaves it
    /// alone (see <c>ModelContextWindowTests</c>).</remarks>
    [Fact]
    public void Claude_small_calls_follow_the_row_model_when_no_fast_model_is_named()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("z-ai/glm-5.3-flash", environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
    }

    /// <summary>A Fast model named on the instance still wins over that fallback.</summary>
    [Fact]
    public void A_named_fast_model_still_wins()
    {
        var (settings, instance) = Configured("claude", "openrouter", "sk-test");
        instance.Model = "z-ai/glm-5.3-flash";
        instance.FastModel = "z-ai/glm-5.3-air";

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal("z-ai/glm-5.3-air", environment["ANTHROPIC_DEFAULT_HAIKU_MODEL"]);
    }

    /// <summary>
    /// A sign-in belongs to the agent that runs, not to the id the instance stores.
    /// </summary>
    /// <remarks><b>The other half of the substitution fix, and it was left behind.</b> After a rollback
    /// a stand-in agent kept a sign-in belonging to the agent the instance names, so
    /// <c>CLAUDE_CONFIG_DIR</c> pointed at another tool's credential directory — and Claude Code would
    /// have written its own login into it.</remarks>
    [Fact]
    public void A_stand_in_agent_does_not_inherit_another_tools_sign_in()
    {
        var settings = new AppSettings();
        var signIn = new AiSignIn { AgentId = "an-agent-from-the-future", Name = "Work" };
        var instance = new AiAgentInstance
        {
            AgentId = "an-agent-from-the-future", Name = "Future", SignInId = signIn.Id,
        };
        settings.AiSignIns.Add(signIn);
        settings.AiAgentInstances.Add(instance);

        // Claude Code stands in for an agent this build does not have.
        var runtime = AgentRuntime.For(settings, instance, model: null, agent: Agent("claude"));

        Assert.Null(runtime.SignIn);
        Assert.False(Agent("claude").EnvFor(runtime).ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    /// <summary>And the same question asked of availability answers the same way.</summary>
    [Fact]
    public void A_sign_in_for_another_agent_is_named_by_availability()
    {
        var settings = new AppSettings();
        var signIn = new AiSignIn { AgentId = "codex", Name = "Work" };
        var instance = new AiAgentInstance { AgentId = "claude", Name = "Mine", SignInId = signIn.Id };
        settings.AiSignIns.Add(signIn);

        var problem = AgentAvailability.Problem(instance, settings, Agent("claude"));

        Assert.NotNull(problem);
        Assert.Contains("Codex", problem);
    }

    /// <summary>An instance carrying both accounts is named by availability, not only by the runtime.
    /// </summary>
    [Fact]
    public void An_instance_carrying_both_accounts_is_named_by_availability()
    {
        var (settings, instance) = Configured("claude", "openrouter", key: "sk-test");
        var signIn = new AiSignIn { AgentId = "claude", Name = "Work" };
        settings.AiSignIns.Add(signIn);
        instance.SignInId = signIn.Id;

        Assert.Contains("both", AgentAvailability.Problem(instance, settings)!);
    }

    // ── A server the registry has never heard of ─────────────────────────────────────────────────

    /// <summary>
    /// opencode is handed a generated config file naming the local server.
    /// </summary>
    /// <remarks>The only route in: an address in the environment reaches opencode by no path at all.
    /// The provider id in the document and the prefix on the model both come from
    /// <c>IAiProvider.CatalogueId</c>, so they cannot disagree.</remarks>
    [Fact]
    public void Opencode_is_given_a_config_file_for_a_local_server()
    {
        var (settings, instance) = Configured("opencode", "lmstudio", key: "");
        instance.Model = "google/gemma-4-12b";

        using var appData = new TempAppData();
        var runtime = AgentRuntime.For(settings, instance);

        // Prepared first: the variable names the file only once it is really there, so that a write
        // that failed cannot point opencode at a missing config.
        Agent("opencode").PrepareToLaunch(runtime);
        var environment = Agent("opencode").EnvFor(runtime);

        Assert.True(environment.ContainsKey("OPENCODE_CONFIG"));
        Assert.Equal("lmstudio/google/gemma-4-12b", Agent("opencode").QualifiedModel(runtime));
    }

    /// <summary>
    /// Building the environment writes nothing; preparing the launch does.
    /// </summary>
    /// <remarks>The environment is reached through a <em>property</em> that the launch reads twice, so
    /// writing from there made the file twice per launch and again on any later read. The path is a pure
    /// function of the instance's id, which is what lets the two be separated at all.</remarks>
    [Fact]
    public void The_config_file_is_written_by_preparing_and_not_by_reading_the_environment()
    {
        // Its own application directory, because this writes a real file: without it the test drops one
        // into the developer's installation and only tidies it away when it passes.
        using var appData = new TempAppData();

        var (settings, instance) = Configured("opencode", "lmstudio", key: "");
        var runtime = AgentRuntime.For(settings, instance);
        var path = OpenCodeProviderConfig.PathFor(instance.Id);

        try
        {
            var before = Agent("opencode").EnvFor(runtime);

            Assert.False(File.Exists(path), "reading the environment wrote a file");
            // And nothing is named while it does not exist: opencode pointed at a missing config
            // answers ProviderModelNotFoundError, which is the error the file exists to prevent.
            Assert.False(before.ContainsKey("OPENCODE_CONFIG"));

            Agent("opencode").PrepareToLaunch(runtime);

            Assert.True(File.Exists(path));
            Assert.Equal(path, Agent("opencode").EnvFor(runtime)["OPENCODE_CONFIG"]);

            // And the instance's fast model reaches the file, not just the document builder: the slot
            // is written where a provider document is, so the whole route — instance to Write to
            // Document — is what is pinned here.
            instance.FastModel = "qwen3-4b";
            Agent("opencode").PrepareToLaunch(runtime);
            Assert.Contains("small_model", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A sign-in clears the other services' keys too — the same rule by the other branch.
    /// </summary>
    /// <remarks>An instance on a subscription names no provider, so the early return meant a globally
    /// exported <c>OPENAI_API_KEY</c> stayed visible to opencode as a second account; and with no
    /// provider there is no prefix on the model to break the tie either.</remarks>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void A_sign_in_clears_the_hosted_keys(string agentId)
    {
        var settings = new AppSettings();
        var signIn = new AiSignIn { AgentId = agentId, Name = "Work" };
        var instance = AiAgentCatalog.SeedInstanceFor(Agent(agentId));
        instance.SignInId = signIn.Id;
        settings.AiSignIns.Add(signIn);
        settings.AiAgentInstances.Add(instance);

        var environment = Agent(agentId).EnvFor(AgentRuntime.For(settings, instance));

        // Every service's key, because the point is that nothing inherited survives to authenticate
        // instead of the login this instance names.
        foreach (var variable in new[]
                 { "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "OPENROUTER_API_KEY", "ZAI_API_KEY" })
        {
            Assert.True(environment.ContainsKey(variable), $"{agentId} leaves {variable} inherited");
            Assert.Null(environment[variable]);
        }
    }

    /// <summary>The document says what opencode's schema asks for.</summary>
    /// <remarks>Pinned because it is somebody else's format: when it moves, this fails rather than a
    /// tile silently starting on the wrong provider.</remarks>
    [Fact]
    public void The_generated_config_declares_the_provider_and_its_address()
    {
        var document = OpenCodeProviderConfig.Document(
            "lmstudio", "LM Studio", new Uri("http://localhost:1234/v1"), "google/gemma-4-12b");

        Assert.Contains("\"lmstudio\"", document);
        Assert.Contains("@ai-sdk/openai-compatible", document);
        Assert.Contains("http://localhost:1234/v1", document);
        Assert.Contains("google/gemma-4-12b", document);
    }

    /// <summary>
    /// A named fast model goes into opencode's own <c>small_model</c> slot, qualified and listed.
    /// </summary>
    /// <remarks>Qualified because the slot is spelled <c>provider/model</c> and resolves against the
    /// provider declared here; listed because opencode resolves the slot through the same catalogue,
    /// and measured in the binary (1.18.18) a <c>small_model</c> the provider does not declare is
    /// silently discarded rather than refused — the field set and doing nothing.</remarks>
    [Fact]
    public void The_generated_config_declares_the_fast_model()
    {
        var document = OpenCodeProviderConfig.Document(
            "lmstudio", "LM Studio", new Uri("http://localhost:1234/v1"), "google/gemma-4-12b",
            smallModel: "qwen3-4b");

        var parsed = System.Text.Json.Nodes.JsonNode.Parse(document)!.AsObject();
        Assert.Equal("lmstudio/qwen3-4b", parsed["small_model"]!.GetValue<string>());
        Assert.NotNull(parsed["provider"]!["lmstudio"]!["models"]!["qwen3-4b"]);
    }

    /// <summary>No fast model named, no slot written — opencode's own pick answers.</summary>
    [Fact]
    public void An_empty_fast_model_writes_no_small_model_slot()
    {
        var document = OpenCodeProviderConfig.Document(
            "lmstudio", "LM Studio", new Uri("http://localhost:1234/v1"), "gemma");

        var parsed = System.Text.Json.Nodes.JsonNode.Parse(document)!.AsObject();
        Assert.False(parsed.ContainsKey("small_model"));
    }

    /// <summary>Only the agents whose CLI has a small-model slot offer the field.</summary>
    /// <remarks>Measured 2026-08-31: Claude Code's <c>ANTHROPIC_DEFAULT_HAIKU_MODEL</c> (the
    /// <c>ANTHROPIC_SMALL_FAST_MODEL</c> spelling is deprecated in its favour), opencode's
    /// <c>small_model</c>; codex, pi and agy answered their small calls with the main model or their
    /// own pick and offer no setting for one — the form hides the field on them.</remarks>
    [Theory]
    [InlineData("claude", true)]
    [InlineData("opencode", true)]
    [InlineData("codex", false)]
    [InlineData("pi", false)]
    [InlineData("agy", false)]
    public void Only_the_agents_with_a_small_model_slot_offer_the_field(string agentId, bool has)
    {
        Assert.Equal(has, Agent(agentId) is { UsesFastModel: true });
    }

    /// <summary>
    /// The generated document keeps whatever the user already had in theirs.
    /// </summary>
    /// <remarks><c>OPENCODE_CONFIG</c> names <em>the</em> config file rather than an extra one, so a
    /// document holding only our provider is a tile that has silently lost the user's default model,
    /// their MCP servers, their agents and their instructions. This is the one place this application
    /// writes a configuration file for somebody else's tool.</remarks>
    [Fact]
    public void The_generated_config_keeps_the_users_own_settings()
    {
        var theirs = System.Text.Json.Nodes.JsonNode.Parse("""
            { "model": "openrouter/theirs", "mcp": { "their-server": {} },
              "provider": { "openrouter": { "name": "Theirs" } } }
            """)!.AsObject();

        var document = OpenCodeProviderConfig.Document(
            "lmstudio", "LM Studio", new Uri("http://localhost:1234/v1"), "gemma", theirs);

        Assert.Contains("openrouter/theirs", document);
        Assert.Contains("their-server", document);
        // Their other provider survives beside ours, and only the key we own is replaced.
        Assert.Contains("\"Theirs\"", document);
        Assert.Contains("\"lmstudio\"", document);
        Assert.Contains("http://localhost:1234/v1", document);
    }

    /// <summary>
    /// A hosted provider with an address of its own is declared too — a gateway is not the published
    /// service.
    /// </summary>
    /// <remarks><b>Silently lost before this.</b> The condition was "the provider has no key", which is
    /// only the local case: an opencode instance on "OpenRouter via my gateway" was offered by the
    /// chooser, launched with nothing but <c>OPENROUTER_API_KEY</c>, and ran against openrouter.ai
    /// while the address the user had typed did nothing at all.</remarks>
    [Fact]
    public void A_hosted_provider_with_its_own_address_is_declared_as_well()
    {
        var (settings, instance) = Configured("opencode", "openrouter", key: "sk-test");
        settings.AiProviderInstances[0].BaseUrl = "https://gateway.example.com/v1";

        var runtime = AgentRuntime.For(settings, instance);

        // The instance form asks the shared rule about the account it holds, so both roads — the
        // launch and the field's visibility — answer from one statement of the two cases.
        Assert.True(AgentRuntime.DeclaresEndpoint(
            AiProviderCatalog.Find("openrouter"), settings.AiProviderInstances[0]));
        Assert.True(runtime.NeedsDeclaredEndpoint);
        Assert.True(OpenCodeProviderConfig.IsNeededFor(runtime));

        var document = OpenCodeProviderConfig.Document("openrouter", "OpenRouter",
            new Uri("https://gateway.example.com/v1"), "some/model",
            keyVariable: "OPENROUTER_API_KEY");

        Assert.Contains("gateway.example.com", document);
        // A reference, never the key itself: opencode resolves {env:NAME}, so the secret stays in the
        // process environment instead of being copied to a file that outlives the launch.
        Assert.Contains("{env:OPENROUTER_API_KEY}", document);
        Assert.DoesNotContain("sk-test", document);

        // And the variable the document refers to is actually put there. The two halves were tested
        // apart, so nothing noticed that ApplyProviderKey returned early for exactly this runtime -
        // leaving opencode with an unresolvable {env:...} and a login that only worked on a machine
        // where the user happened to export the key globally.
        var environment = Agent("opencode").EnvFor(runtime);
        Assert.Equal("sk-test", environment["OPENROUTER_API_KEY"]);
    }

    /// <summary>
    /// An address that was typed and cannot be read is refused by a sentence.
    /// </summary>
    /// <remarks><c>BaseUrlFor</c> answers null for it rather than falling back to this machine, and
    /// every consumer reads that null as "there is nowhere to call" and then says nothing:
    /// <c>ClaudeAgent.Configure</c> sets neither the base URL nor a token, so the tile came up on the
    /// user's real subscription while the row named a gateway.</remarks>
    [Fact]
    public async Task An_address_that_cannot_be_read_stops_the_launch()
    {
        var (settings, instance) = Configured("claude", "openrouter", key: "sk-test");
        settings.AiProviderInstances[0].BaseUrl = "ftp://gateway:not-a-port";

        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("claude"), instance);

        Assert.NotNull(problem);
        Assert.Contains("not an address this can read", problem);
    }

    /// <summary>
    /// An agent that cannot be given an address is refused that instance, not just a local one.
    /// </summary>
    /// <remarks>The refusal used to ask <c>provider.IsLocal</c>, so a gateway on a hosted provider
    /// passed it and then ran against the published address instead — quietly, because the pairing
    /// itself is perfectly legal.</remarks>
    [Fact]
    public async Task Pi_is_refused_a_hosted_provider_with_an_address_of_its_own()
    {
        var (settings, instance) = Configured("pi", "openrouter", key: "sk-test");
        instance.Model = "some/model";
        settings.AiProviderInstances[0].BaseUrl = "https://gateway.example.com/v1";

        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("pi"), instance);

        Assert.NotNull(problem);
        Assert.Contains("address of its own", problem);
    }

    /// <summary>The same instance without an address is fine: pi can reach a service by name.</summary>
    [Fact]
    public async Task Pi_on_a_hosted_provider_at_its_published_address_is_allowed()
    {
        var (settings, instance) = Configured("pi", "openrouter", key: "sk-test");
        instance.Model = "some/model";

        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("pi"), instance);

        Assert.Null(problem);
    }

    /// <summary>
    /// An instance naming a sign-in <em>and</em> a provider says so instead of quietly dropping one.
    /// </summary>
    /// <remarks>The chooser cannot produce it — it writes one and clears the other — but
    /// <c>settings.json</c> is hand-editable and an older build wrote only the provider field.
    /// <c>AgentRuntime.For</c> resolves it by dropping the provider; unsaid, the row claimed the
    /// subscription while a configured provider stopped being used and the next Save deleted it.
    /// </remarks>
    [Fact]
    public async Task An_instance_naming_both_accounts_is_told_it_names_both()
    {
        var (settings, instance) = Configured("claude", "openrouter", key: "sk-test");
        var signIn = new AiSignIn { AgentId = "claude", Name = "Work" };
        settings.AiSignIns.Add(signIn);
        instance.SignInId = signIn.Id;

        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("claude"), instance);

        Assert.NotNull(problem);
        Assert.Contains("both", problem);
    }

    /// <summary>A hosted provider needs no file: its registry entry already exists.</summary>
    [Fact]
    public void A_hosted_provider_needs_no_config_file()
    {
        var (settings, instance) = Configured("opencode", "openrouter", "sk-test");

        Assert.False(OpenCodeProviderConfig.IsNeededFor(AgentRuntime.For(settings, instance)));
    }

    /// <summary>
    /// pi cannot be pointed at a local server, and the launch says so rather than starting.
    /// </summary>
    /// <remarks>Speaking the same wire format is not the same as having somewhere to put an address:
    /// opencode and pi both speak <c>/v1/chat/completions</c> and only one has a way to be told where.
    /// Left unsaid, the tile ran on pi's own default provider — <c>google</c> — with nothing on screen
    /// saying so.</remarks>
    [Fact]
    public async Task Pi_on_a_local_server_is_refused_with_a_sentence()
    {
        var (settings, instance) = Configured("pi", "lmstudio", key: "");

        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("pi"), instance);

        Assert.NotNull(problem);
        Assert.Contains("LM Studio", problem);
    }

    /// <summary>opencode on the same server is not refused, because it has a route.</summary>
    [Fact]
    public async Task Opencode_on_a_local_server_is_allowed()
    {
        var (settings, instance) = Configured("opencode", "lmstudio", key: "");
        instance.Model = "some/model";

        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("opencode"), instance);

        Assert.Null(problem);
    }

    /// <summary>Which agents can be pointed at an address of their own.</summary>
    [Theory]
    [InlineData("claude", true)]
    [InlineData("opencode", true)]
    [InlineData("pi", false)]
    public void Only_some_agents_can_be_pointed_at_a_server_of_their_own(string agentId, bool expected) =>
        Assert.Equal(expected, Agent(agentId).SupportsCustomEndpoint);

    /// <summary>
    /// A tile running a stand-in agent is judged by the stand-in, not by the id it was configured with.
    /// </summary>
    /// <remarks><b>Otherwise a Velopack rollback kills the tile.</b> <c>AgentTileKind.WithAgent</c>
    /// substitutes an agent this build does have and says so through <c>AgentSubstitution</c> — a
    /// dismissible notice over a tile that is running. Asking availability by the instance's own id
    /// answered "this build does not have that agent", which became a launch problem and a dead tile
    /// carrying two messages at once.</remarks>
    [Fact]
    public async Task A_substituted_agent_is_judged_by_the_agent_that_is_running()
    {
        var (settings, instance) = Configured("claude", "openrouter", key: "sk-test");
        instance.Model = "some/model";
        // What a layout written by a newer build looks like here.
        instance.AgentId = "an-agent-from-the-future";

        // By the instance's own id there is nothing to judge, so the chooser hides it and says why.
        Assert.NotNull(AgentAvailability.Problem(instance, settings));

        // By the agent actually standing in, the account is checked instead - and it is fine.
        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("claude"), instance);

        Assert.Null(problem);
    }

    private static IAiAgent Agent(string id) =>
        AiAgentCatalog.Find(id) ?? throw new InvalidOperationException($"No agent '{id}'.");

    private static (AppSettings Settings, AiAgentInstance Instance) Configured(
        string agentId, string providerId, string key)
    {
        var provider = new AiProviderInstance { ProviderId = providerId, ApiKey = key };
        var instance = AiAgentCatalog.SeedInstanceFor(Agent(agentId));
        instance.ApiAccountId = provider.Id;

        var settings = new AppSettings();
        settings.AiProviderInstances.Add(provider);
        settings.AiAgentInstances.Add(instance);
        return (settings, instance);
    }
}
