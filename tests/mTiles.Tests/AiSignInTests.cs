using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A second subscription: which agents can hold one, what reaches the process, and what happens to an
/// instance whose sign-in has gone.
/// </summary>
/// <remarks>
/// <para>The environment tables are somebody else's CLI, measured on 2026-08-30 against Claude Code
/// 2.1.251, codex-cli 0.141.0 and opencode 1.18.18 — each pointed at an empty directory and each shown
/// to read <em>none</em> of its default one (<c>Not logged in</c>, <c>401 Unauthorized</c>, and
/// <c>0 credentials</c> respectively). Pinned here so a spelling that moves surfaces as a failing build
/// rather than as a run billed to the wrong account.</para>
/// <para>Nothing here signs in or reads a real credential file: what is under test is which directory
/// the launch is pointed at, which is the part this application owns.</para>
/// </remarks>
public class AiSignInTests : IDisposable
{
    // Every test in here can reach AppPaths - a sign-in's directory is derived from it, and
    // EnvFor now creates one. Without this the suite wrote into a live installation.
    private readonly TempAppData _appData = new();

    public void Dispose() => _appData.Dispose();

    // ── Which agents can hold a second login ─────────────────────────────────────────────────────

    /// <summary>
    /// Four can, one cannot, and the one that cannot says so rather than being given a variable.
    /// </summary>
    /// <remarks><b>pi was on the wrong side of this list for a day.</b> It was recorded as having no
    /// way to relocate its credentials, from a reading of <c>--help</c> that stopped too early;
    /// <c>PI_CODING_AGENT_DIR</c> is in there and works — with <c>OPENROUTER_API_KEY</c> removed from
    /// the environment, <c>pi auth check --provider openrouter</c> answers <c>not_ready</c> against a
    /// fresh directory and <c>ready</c> against the default one. The lesson is in the assertion: a
    /// negative here is a claim about somebody else's CLI, and it has to be run rather than read.
    /// <para>agy remains a negative, and that one was established by searching the binary for any
    /// <c>*_HOME</c>, <c>*_DIR</c> or <c>*_CONFIG</c> variable and finding none — it keeps its state in
    /// <c>~/.gemini</c> and switches Google accounts itself, in
    /// <c>google_accounts.json</c>.</para></remarks>
    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("opencode", true)]
    [InlineData("pi", true)]
    [InlineData("agy", false)]
    public void Only_the_agents_that_can_relocate_their_credentials_offer_sign_ins(
        string agentId, bool expected) =>
        Assert.Equal(expected, Agent(agentId).SupportsSignIns);

    /// <summary>The variables each of the three is moved with.</summary>
    /// <remarks>opencode is why this is a whole block rather than a name and a value: it has no
    /// variable of its own, it is moved by somebody else's — <c>XDG_DATA_HOME</c> — and the value is
    /// not the directory it was handed but <c>&lt;dir&gt;/data</c> underneath it. It began as both XDG
    /// variables and was narrowed to this one on 2026-08-31, which is what the assertions below
    /// pin.</remarks>
    [Fact]
    public void Each_agent_is_pointed_at_a_directory_the_way_its_own_cli_expects()
    {
        Assert.Equal(new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = "/d" },
            Agent("claude").SignInEnv("/d"));

        Assert.Equal(new Dictionary<string, string?> { ["CODEX_HOME"] = "/d" },
            Agent("codex").SignInEnv("/d"));

        // opencode moves its *credentials* and nothing else: XDG_DATA_HOME alone answers
        // "0 credentials", and redirecting XDG_CONFIG_HOME as well took the user's own opencode.json
        // away from the tile - no default model, no MCP servers, no instructions.
        var opencode = Agent("opencode").SignInEnv("/d");
        Assert.Equal(Path.Combine("/d", "data"), opencode["XDG_DATA_HOME"]);
        Assert.False(opencode.ContainsKey("XDG_CONFIG_HOME"));
    }

    /// <summary>An agent that cannot hold a second login contributes nothing to the environment.</summary>
    [Fact]
    public void An_agent_without_sign_ins_asks_for_no_variable() =>
        Assert.Empty(Agent("agy").SignInEnv("/d"));

    /// <summary>pi's own variable, which is a correction — see the theory above.</summary>
    [Fact]
    public void Pi_is_moved_with_its_config_directory() =>
        Assert.Equal(new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = "/d" },
            Agent("pi").SignInEnv("/d"));

    // ── What reaches the process ─────────────────────────────────────────────────────────────────

    /// <summary>An instance on a sign-in launches pointed at that sign-in's directory.</summary>
    [Fact]
    public void An_instance_on_a_sign_in_is_pointed_at_its_directory()
    {
        var (settings, instance, signIn) = WithSignIn("claude");

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal(AiSignInStore.DirectoryFor(signIn), environment["CLAUDE_CONFIG_DIR"]);
    }

    /// <summary>
    /// The default account sets <b>nothing</b> — never the variable pointed at the CLI's own directory.
    /// </summary>
    /// <remarks>Measured on Claude Code 2.1.251: with <c>CLAUDE_CONFIG_DIR</c> set it keeps
    /// <c>.claude.json</c> inside that directory, while by default it keeps it at <c>~/.claude.json</c>
    /// and puts only the credentials in <c>~/.claude</c>. So "helpfully" pointing the variable at
    /// <c>~/.claude</c> yields a session that is logged in and has lost its projects, its MCP servers
    /// and its history — a half-configured account wearing the real one's face.</remarks>
    [Fact]
    public void The_default_account_sets_no_variable_at_all()
    {
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(Agent("claude"));

        Assert.Empty(Agent("claude").EnvFor(AgentRuntime.For(settings, instance)));
    }

    /// <summary>
    /// A sign-in and a provider are one slot, and a file holding both resolves to the sign-in alone.
    /// </summary>
    /// <remarks>The chooser cannot produce this, and a hand-edited <c>settings.json</c> or an older
    /// build can. Left unresolved it is the worst combination available: the CLI pointed at one
    /// subscription's directory while authenticating with somebody else's key, so the work is billed to
    /// the provider while every row on screen names the subscription.</remarks>
    [Fact]
    public void An_instance_carrying_both_runs_as_the_sign_in_and_not_the_provider()
    {
        var (settings, instance, signIn) = WithSignIn("claude");
        var provider = new AiProviderInstance { ProviderId = "openrouter", ApiKey = "sk-test" };
        settings.AiProviderInstances.Add(provider);
        instance.ApiAccountId = provider.Id;

        var environment = Agent("claude").EnvFor(AgentRuntime.For(settings, instance));

        Assert.Equal(AiSignInStore.DirectoryFor(signIn), environment["CLAUDE_CONFIG_DIR"]);

        // Removed rather than merely not set, which is the stronger answer and the one this needs: a
        // null unsets, so a machine exporting either of them globally cannot have this login run on
        // somebody else's token or at somebody else's address.
        Assert.Null(environment["ANTHROPIC_BASE_URL"]);
        Assert.Null(environment["ANTHROPIC_AUTH_TOKEN"]);
    }

    /// <summary>
    /// A sign-in removes the variables that would point the CLI at another account.
    /// </summary>
    /// <remarks><b>Neither of these is any provider's key variable</b>, so the clearing every
    /// configured account gets did not cover them: an instance on a subscription names no provider, so
    /// the branch that sets an address never runs for it, and a machine exporting
    /// <c>ANTHROPIC_AUTH_TOKEN</c> or <c>OPENAI_BASE_URL</c> globally had the login run on somebody
    /// else's token, or against somebody else's gateway, with the row saying otherwise.</remarks>
    [Theory]
    [InlineData("claude", "ANTHROPIC_AUTH_TOKEN")]
    [InlineData("claude", "ANTHROPIC_BASE_URL")]
    [InlineData("codex", "OPENAI_BASE_URL")]
    [InlineData("codex", "OPENAI_API_KEY")]
    public void A_sign_in_clears_what_would_point_the_tool_elsewhere(string agentId, string variable)
    {
        var (settings, instance, _) = WithSignIn(agentId);

        var environment = Agent(agentId).EnvFor(AgentRuntime.For(settings, instance));

        Assert.True(environment.ContainsKey(variable), $"{variable} is left inherited");
        Assert.Null(environment[variable]);
    }

    /// <summary>
    /// An instance on no account at all is left alone, including these.
    /// </summary>
    /// <remarks>"The agent's own configuration" is a choice somebody made, and a globally exported
    /// endpoint is part of it. Removing it would be this application overruling a decision it was never
    /// asked about.</remarks>
    [Fact]
    public void An_instance_on_no_account_keeps_the_machines_own_environment()
    {
        var settings = new AppSettings();
        var instance = AiAgentCatalog.SeedInstanceFor(Agent("claude"));

        Assert.Empty(Agent("claude").EnvFor(AgentRuntime.For(settings, instance)));
    }

    /// <summary>A sign-in belonging to another agent is not this agent's to use.</summary>
    /// <remarks>A Claude Code login means nothing to codex: different file, different variable. Stored
    /// by a hand edit or left behind by changing an instance's agent, it must resolve to nothing rather
    /// than to a directory codex would then create a second, empty profile in.</remarks>
    [Fact]
    public void A_sign_in_belonging_to_another_agent_is_ignored()
    {
        var (settings, instance, signIn) = WithSignIn("claude");
        signIn.AgentId = "codex";

        Assert.Null(AgentRuntime.For(settings, instance).SignIn);
        Assert.Empty(Agent("claude").EnvFor(AgentRuntime.For(settings, instance)));
    }

    /// <summary>What the user set by hand still wins, which is the rule <c>EnvFor</c> exists to keep.
    /// </summary>
    [Fact]
    public void The_users_own_variables_still_win_over_the_sign_in()
    {
        var (settings, instance, _) = WithSignIn("claude");
        instance.ExtraEnv["CLAUDE_CONFIG_DIR"] = "/mine";

        Assert.Equal("/mine",
            Agent("claude").EnvFor(AgentRuntime.For(settings, instance))["CLAUDE_CONFIG_DIR"]);
    }

    // ── Where the directory is ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Derived from the id, so nothing machine-specific is written to <c>settings.json</c>.
    /// </summary>
    /// <remarks>An absolute path under one user's <c>%APPDATA%</c> is a directory that does not exist
    /// on the machine the exported file is imported into, and a row pointing at nothing reads as a lost
    /// login rather than as a login that was never carried across.</remarks>
    [Fact]
    public void A_sign_in_directory_is_derived_from_its_id()
    {
        var signIn = new AiSignIn { Id = "abc123", AgentId = "claude" };

        var directory = AiSignInStore.DirectoryFor(signIn);

        Assert.EndsWith(Path.Combine("claude", "abc123"), directory);
        Assert.StartsWith(mTiles.Services.AppPaths.GetAgentAccountsDirectory(), directory);
    }

    /// <summary>A path typed by hand is used verbatim — that is somebody pointing at a directory that
    /// already exists, and rewriting it would be deciding we know better about a location we did not
    /// choose.</summary>
    [Fact]
    public void A_directory_given_by_hand_is_used_as_written()
    {
        var signIn = new AiSignIn { AgentId = "claude", ConfigDirectory = "/somewhere/else" };

        Assert.Equal("/somewhere/else", AiSignInStore.DirectoryFor(signIn));
    }

    /// <summary>
    /// A separator in an id cannot walk the directory out of the accounts folder.
    /// </summary>
    /// <remarks>Ids are generated, so nothing legitimate is ever replaced — but <c>settings.json</c> is
    /// hand-editable and this value becomes a path.</remarks>
    [Fact]
    public void An_id_with_a_separator_in_it_stays_inside_the_accounts_directory()
    {
        var signIn = new AiSignIn { Id = "../../escape", AgentId = "claude" };

        var directory = AiSignInStore.DirectoryFor(signIn);

        Assert.StartsWith(mTiles.Services.AppPaths.GetAgentAccountsDirectory(), directory);
        Assert.DoesNotContain("..", directory);
    }

    // ── An instance whose sign-in has gone ───────────────────────────────────────────────────────

    /// <summary>
    /// A deleted sign-in makes the instance unavailable rather than silently the default account.
    /// </summary>
    /// <remarks>The same rule a deleted provider follows, and for the same reason: it would launch, on
    /// another subscription, with nothing on screen saying so.</remarks>
    [Fact]
    public void An_instance_whose_sign_in_is_gone_is_not_offered()
    {
        var (settings, instance, signIn) = WithSignIn("claude");
        settings.AiSignIns.Remove(signIn);

        Assert.False(AiAgentCatalog.IsAvailable(instance, settings));
    }

    /// <summary>And a tile handed that instance anyway is told why, rather than starting quietly.</summary>
    /// <remarks>A layout hands a tile its stored instance without anybody choosing, which is the one
    /// path no chooser filters — see <c>AgentModelResolver</c>.</remarks>
    [Fact]
    public async Task A_launch_on_a_missing_sign_in_is_refused_with_a_sentence()
    {
        var (settings, instance, signIn) = WithSignIn("claude");
        settings.AiSignIns.Remove(signIn);

        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("claude"), instance);

        Assert.NotNull(problem);
        Assert.Contains("sign-in", problem);
    }

    /// <summary>An instance on a live sign-in is offered.</summary>
    [Fact]
    public async Task An_instance_on_a_sign_in_that_exists_is_offered()
    {
        var (settings, instance, _) = WithSignIn("claude");

        // Availability also asks whether the CLI is installed, which this machine may not have — so the
        // assertion is that nothing about the sign-in stands in the way, not that the row is runnable.
        var (_, problem) = await AgentModelResolver.ResolveAsync(settings, Agent("claude"), instance);

        Assert.Null(problem);
    }

    // ── Reading a directory ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A directory the CLI has never written to is "not signed in", not "signed in".
    /// </summary>
    /// <remarks>The New sign-in step <em>creates</em> the directory, so answering on existence would
    /// report a brand-new row as logged in and send the user to a tile that cannot authenticate.
    /// </remarks>
    [Fact]
    public void An_empty_directory_reads_as_not_signed_in()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(Agent("claude").ReadSignIn(directory).SignedIn);
            Assert.False(Agent("codex").ReadSignIn(directory).SignedIn);
            Assert.False(Agent("opencode").ReadSignIn(directory).SignedIn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Claude Code's credentials say logged in; its settings say as whom.
    /// </summary>
    /// <remarks>Both files are read, and the credentials one is what decides: a configuration directory
    /// holding settings and no credentials is a signed-<em>out</em> account, and reporting it as signed
    /// in would hide the one thing the row exists to show. The token beside these two fields is never
    /// touched.</remarks>
    [Fact]
    public void A_claude_directory_names_the_account_it_is_logged_into()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".credentials.json"),
                """{"claudeAiOauth":{"accessToken":"secret","subscriptionType":"max"}}""");
            File.WriteAllText(Path.Combine(directory, ".claude.json"),
                """{"oauthAccount":{"emailAddress":"a@b.c"}}""");

            var status = Agent("claude").ReadSignIn(directory);

            Assert.True(status.SignedIn);
            Assert.Contains("a@b.c", status.Detail);
            Assert.Contains("Max", status.Detail);
            Assert.DoesNotContain("secret", status.Detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A <c>.claude.json</c> that grew large is still read, whatever stands between the start of the
    /// file and the field.
    /// </summary>
    /// <remarks>The file carries a Claude Code installation's per-project history and grows into the
    /// megabytes, and <c>oauthAccount</c> sits wherever the CLI last wrote it — not necessarily near
    /// the front. A long string and a long property name stand in for that history, both sized to
    /// cross the reader's buffer boundary, because a walk that cannot resume inside an unfinished
    /// token would answer null and drop the account's name.</remarks>
    [Fact]
    public void A_claude_json_that_grew_large_is_still_read_to_the_field()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".credentials.json"),
                """{"claudeAiOauth":{"subscriptionType":"max"}}""");
            var history = new string('x', 200_000);
            var longName = new string('k', 20_000);
            File.WriteAllText(Path.Combine(directory, ".claude.json"),
                "{\"" + longName + "\":\"" + history
                + "\",\"oauthAccount\":{\"emailAddress\":\"late@b.c\"}}");

            var status = Agent("claude").ReadSignIn(directory);

            Assert.Contains("late@b.c", status.Detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Naming the account does not read the whole of a large <c>.claude.json</c>.
    /// </summary>
    /// <remarks><b>This is the reason <c>ReadJsonString</c> walks instead of parsing.</b> The file is
    /// read once per sign-in row and once per account-chooser rebuild, on the thread drawing the
    /// Settings page, and the per-project history it carries is exactly the part the answer does not
    /// need. The field sits at the front and a megabyte of history behind it, and the whole read's
    /// allocation is bounded: the <c>ReadAllBytes</c>-plus-<c>JsonDocument</c> read that was here
    /// before costs the file itself plus a DOM of several times its size and cannot come near it.</remarks>
    [Fact]
    public void Naming_the_account_does_not_read_the_whole_of_a_large_claude_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".credentials.json"),
                """{"claudeAiOauth":{"subscriptionType":"max"}}""");
            var history = new string('h', 1024 * 1024);
            File.WriteAllText(Path.Combine(directory, ".claude.json"),
                "{\"oauthAccount\":{\"emailAddress\":\"early@b.c\"},"
                + "\"projects\":{\"p\":{\"history\":\"" + history + "\"}}}");

            // The first read pays for JIT, not the measurement.
            Agent("claude").ReadSignIn(directory);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var status = Agent("claude").ReadSignIn(directory);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Contains("early@b.c", status.Detail);
            Assert.True(allocated < 256 * 1024,
                $"naming the account allocated {allocated:N0} B — the whole file was read");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// An <c>emailAddress</c> merely passing through the file is not mistaken for the path's own.
    /// </summary>
    /// <remarks>The depth rule is what keeps the walk equivalent to the DOM read it replaced: a
    /// property inside a value being skipped through sits deeper than the object the path has
    /// reached, so it can neither answer nor restart the search — and a walk that searched by name
    /// alone would take this file's wrong address for the account.</remarks>
    [Fact]
    public void An_emailAddress_passing_through_is_not_mistaken_for_the_paths_own()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".credentials.json"),
                """{"claudeAiOauth":{"subscriptionType":"max"}}""");
            File.WriteAllText(Path.Combine(directory, ".claude.json"),
                """{"decoy":{"emailAddress":"wrong@b.c"},"oauthAccount":{"emailAddress":"right@b.c"}}""");

            var status = Agent("claude").ReadSignIn(directory);

            Assert.Contains("right@b.c", status.Detail);
            Assert.DoesNotContain("wrong@b.c", status.Detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A settings file whose shape cannot give the answer is still a login, and still gives no name.
    /// </summary>
    /// <remarks>The same answers the DOM read gave, pinned because the walk replaced it: a root that
    /// is not an object, a segment that is not an object, a key that is missing and a value that is
    /// not a string all mean "signed in, unnamed" — and none of them may invent a name, least of all
    /// one belonging to another part of the file.</remarks>
    [Theory]
    [InlineData("[]")]
    [InlineData("""{"oauthAccount":"a@b.c"}""")]
    [InlineData("""{"oauthAccount":{"other":"a@b.c"}}""")]
    [InlineData("""{"oauthAccount":{"emailAddress":42}}""")]
    public void A_settings_file_the_answer_cannot_come_from_still_says_logged_in(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            // Deliberately a credentials file that names no plan, so the detail can only come from
            // the settings file — an invented one would show here.
            File.WriteAllText(Path.Combine(directory, ".credentials.json"), """{"somethingElse":{}}""");
            File.WriteAllText(Path.Combine(directory, ".claude.json"), json);

            var status = Agent("claude").ReadSignIn(directory);

            Assert.True(status.SignedIn);
            Assert.Equal("", status.Detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Settings without credentials is signed out, which is the half that decides.</summary>
    [Fact]
    public void A_claude_directory_with_no_credentials_is_signed_out()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".claude.json"),
                """{"oauthAccount":{"emailAddress":"a@b.c"}}""");

            Assert.False(Agent("claude").ReadSignIn(directory).SignedIn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A codex directory says which of the two ways it authenticates.
    /// </summary>
    /// <remarks>Measured 2026-08-30 against codex-cli 0.141.0: <c>auth_mode</c> is <c>chatgpt</c> for a
    /// subscription login. It is the one distinction worth a row — a sign-in spending a plan and one
    /// spending an API balance are different bills — and it is readable without touching the bearer
    /// token beside it, which is why the address is not shown.</remarks>
    [Fact]
    public void A_codex_directory_says_whether_it_is_on_a_subscription()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "auth.json"),
                """{"auth_mode":"chatgpt","tokens":{"refresh_token":"secret"}}""");

            var status = Agent("codex").ReadSignIn(directory);

            Assert.True(status.SignedIn);
            Assert.Contains("ChatGPT", status.Detail);
            Assert.DoesNotContain("secret", status.Detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A codex credentials file it cannot parse is still a login, not an offer to make one.
    /// </summary>
    /// <remarks>The opposite of Claude Code's rule, and deliberately: there the field read <em>is</em>
    /// the evidence of a login, here the file's existence is and the field only describes it. A format
    /// that moves must not turn a working account into a row saying "not signed in".</remarks>
    [Fact]
    public void A_codex_file_that_cannot_be_parsed_is_still_signed_in()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "auth.json"), "{ not json");

            Assert.True(Agent("codex").ReadSignIn(directory).SignedIn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Credentials that name no subscription are still credentials.
    /// </summary>
    /// <remarks>The file's existence says logged in; its contents only say who. Reading a field first
    /// and answering "not signed in" when it was missing put a Sign in button over a working login —
    /// a subscription is not the only way to authenticate, and this is the policy codex already had.
    /// </remarks>
    [Fact]
    public void A_claude_directory_with_credentials_but_no_plan_is_still_signed_in()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".credentials.json"), """{"somethingElse":{}}""");

            Assert.True(Agent("claude").ReadSignIn(directory).SignedIn);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A file that is not JSON at all is still a login, and never an exception.
    /// </summary>
    /// <remarks><b>This asserted the opposite.</b> These files belong to somebody else's CLI and are
    /// read while a Settings page is being drawn, so a half-written one must not be a dialog with a
    /// stack trace in it — but it must not be an offer to sign in over a working account either. The
    /// file's existence is the evidence; its contents only say who, and a format that moves takes the
    /// detail with it and nothing else. codex already read it this way, and the two had drifted.
    /// </remarks>
    [Fact]
    public void An_unreadable_credentials_file_is_still_signed_in()
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, ".credentials.json"), "{ not json");

            var status = Agent("claude").ReadSignIn(directory);

            Assert.True(status.SignedIn);
            Assert.Equal("", status.Detail);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Removing the row leaves the login on disk.
    /// </summary>
    /// <remarks><b>The one invariant here whose breach cannot be undone.</b> That directory holds the
    /// CLI's refresh token and the whole conversation history that came with the account, and neither
    /// is ours; the confirmation says where it stays. Asserted rather than assumed because "there is no
    /// delete call in that method" is a fact about today's code, and this is the kind of tidying a
    /// later change makes without noticing what it is tidying.</remarks>
    [Fact]
    public async Task Removing_a_sign_in_leaves_its_directory_alone()
    {
        // The directory is derived from AppPaths, which TempSettings does not redirect - without this
        // the test would create and delete folders inside the developer's own installation.
        using var appData = new TempAppData();
        using var settings = new TempSettings();
        var signIn = new AiSignIn { AgentId = "claude", Name = "Work" };
        settings.Service.Settings.AiSignIns.Add(signIn);

        var directory = AiSignInStore.DirectoryFor(signIn);
        Directory.CreateDirectory(directory);
        var credentials = Path.Combine(directory, ".credentials.json");
        await File.WriteAllTextAsync(credentials, "{}");

        try
        {
            var page = new SettingsViewModel(settings.Service) { ConfirmAction = _ => Task.FromResult(true) };
            page.SelectTabCommand.Execute(SettingsTabs.Ai);
            var row = page.SignIns.Single(r => r.SignIn.Id == signIn.Id);

            await page.DeleteSignInCommand.ExecuteAsync(row);

            Assert.Empty(settings.Service.Settings.AiSignIns);
            Assert.True(File.Exists(credentials), "the login was deleted with the row");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The derived directory really is inside the application's own, not somewhere else.
    /// </summary>
    /// <remarks>Which is also what makes the seam above worth having: everything this class writes is
    /// under one root, so one override redirects all of it.</remarks>
    [Fact]
    public void A_sign_in_directory_is_inside_the_application_directory()
    {
        using var appData = new TempAppData();

        var directory = AiSignInStore.DirectoryFor(new AiSignIn { AgentId = "claude" });

        Assert.StartsWith(appData.Root, directory);
    }

    /// <summary>
    /// The status of every agent's directory is read from the path its own launch is pointed at.
    /// </summary>
    /// <remarks><b>The dependency this pins is silent when it breaks.</b> opencode and pi build their
    /// credential paths by hand (<c>&lt;dir&gt;/data/opencode/auth.json</c>, <c>&lt;dir&gt;/auth.json</c>)
    /// and those have to agree with what <see cref="IAiAgent.SignInEnv"/> sets: disagree, and every row
    /// says "not signed in" over a working login while the launch works perfectly. Only claude and
    /// codex were covered.</remarks>
    [Theory]
    [InlineData("claude", ".credentials.json")]
    [InlineData("codex", "auth.json")]
    [InlineData("opencode", "data/opencode/auth.json")]
    [InlineData("pi", "auth.json")]
    public void Each_agent_reads_its_status_from_the_directory_it_is_launched_with(
        string agentId, string relativePath)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(Agent(agentId).ReadSignIn(directory).SignedIn);

            var file = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{}");

            Assert.True(Agent(agentId).ReadSignIn(directory).SignedIn,
                $"{agentId} does not read the directory its own SignInEnv points at");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A launch makes the directory it points the CLI at — every one of them.
    /// </summary>
    /// <remarks><para>It used to be created by the Settings form alone, so a settings file imported
    /// onto another machine, a directory tidied away by hand, or a fresh clone of somebody's
    /// configuration left the CLI to make it itself — at whatever the umask says, which is where it
    /// then writes a refresh token. Owner-only was the whole point of having a method for it.</para>
    /// <para>opencode is the row that matters: <c>XDG_DATA_HOME</c> is <c>&lt;root&gt;/data</c>, and
    /// <c>auth.json</c> lands in <em>that</em> directory rather than in the one this store names.
    /// </para></remarks>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void A_launch_creates_every_directory_it_points_the_tool_at(string agentId)
    {
        using var appData = new TempAppData();
        var (settings, instance, signIn) = WithSignIn(agentId);
        var agent = Agent(agentId);

        var runtime = AgentRuntime.For(settings, instance, agent: agent);

        // The moment, not the property: EnvFor is reached through a getter that a launch reads twice
        // and a debugger reads again, which is the whole reason PrepareToLaunch exists.
        agent.PrepareToLaunch(runtime);
        var environment = agent.EnvFor(runtime);

        Assert.True(Directory.Exists(AiSignInStore.DirectoryFor(signIn)));
        foreach (var (name, value) in agent.SignInEnv(AiSignInStore.DirectoryFor(signIn)))
        {
            Assert.Equal(value, environment[name]);
            Assert.True(Directory.Exists(value), $"{agentId} is pointed at a directory {name} that "
                + "does not exist");
        }
    }

    /// <summary>
    /// Null asks about the CLI's own default location, and every agent answers about a different place.
    /// </summary>
    /// <remarks><para>Nothing calls it that way today — every caller has a sign-in and passes its
    /// directory — so this is what keeps the contract in <c>IAiAgent.ReadSignIn</c> from rotting. It is
    /// the branch the AI page will need the moment the default account's row wants to say who it is,
    /// and the one place the two layouts differ: Claude Code keeps <c>.claude.json</c> beside
    /// <c>~/.claude</c> rather than inside a relocated directory, which is why asking the wrong one
    /// reports a working login as signed out.</para>
    /// <para>Asserted as "does not answer about the sign-in directory", not as a status: whether this
    /// machine is logged in is not something a test may decide.</para></remarks>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void The_default_account_is_read_from_the_tools_own_location(string agentId)
    {
        using var appData = new TempAppData();
        var signIn = new AiSignIn { AgentId = agentId, Name = "Work" };
        var directory = AiSignInStore.DirectoryFor(signIn);
        Assert.True(AiSignInStore.Ensure(signIn, Agent(agentId)));

        // The relocated one is empty, so it certainly is not signed in; the default one is whatever
        // this machine happens to be, and answering at all is the assertion.
        Assert.False(Agent(agentId).ReadSignIn(directory).SignedIn);
        Assert.NotNull(Agent(agentId).ReadSignIn(null).Detail);
    }

    /// <summary>
    /// An id read back empty is replaced, because it names a credential directory.
    /// </summary>
    /// <remarks><c>SafePathComponent.Of("")</c> answers <c>unnamed</c>, so two such rows would share
    /// one directory — which the form creates and the CLI writes a refresh token into. A property
    /// initialiser does not survive deserialisation, and a null in the file arrives as an empty string.
    /// </remarks>
    [Fact]
    public void A_sign_in_read_back_without_an_id_is_given_one()
    {
        var read = System.Text.Json.JsonSerializer.Deserialize<AiSignIn>(
            """{"Id":"","AgentId":"claude","Name":"Work"}""", mTiles.Services.JsonDefaults.SettingsOptions)!;

        Assert.NotEmpty(read.Id);
        Assert.DoesNotContain("unnamed", AiSignInStore.DirectoryFor(read));

        // A stored one is kept as it is: this is a tidying, not a rewrite of anybody's file.
        var kept = System.Text.Json.JsonSerializer.Deserialize<AiSignIn>(
            """{"Id":"s1","AgentId":"claude"}""", mTiles.Services.JsonDefaults.SettingsOptions)!;

        Assert.Equal("s1", kept.Id);
    }

    private static IAiAgent Agent(string id) =>
        AiAgentCatalog.Find(id) ?? throw new InvalidOperationException($"No agent '{id}'.");

    /// <summary>An instance running as a sign-in, and the settings holding both.</summary>
    private static (AppSettings Settings, AiAgentInstance Instance, AiSignIn SignIn) WithSignIn(
        string agentId)
    {
        var signIn = new AiSignIn { AgentId = agentId, Name = "Work" };
        var instance = AiAgentCatalog.SeedInstanceFor(Agent(agentId));
        instance.SignInId = signIn.Id;

        var settings = new AppSettings();
        settings.AiSignIns.Add(signIn);
        settings.AiAgentInstances.Add(instance);
        return (settings, instance, signIn);
    }
}
