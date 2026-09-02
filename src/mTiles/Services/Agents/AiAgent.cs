using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;
using mTiles.Services.Providers;
using mTiles.Services.Shells;

namespace mTiles.Services.Agents;

/// <summary>
/// What every agent shares: the defaults that are true of all five, and the two derivations that would
/// otherwise be copied into each of them.
/// </summary>
/// <remarks>
/// A base class rather than default interface members, for one reason: <see cref="EffortFlagFor"/> and
/// <see cref="BehaviourFlagFor"/> are <em>derived</em> from the argv fragments, and a derivation that
/// lives in the interface cannot be seen by anyone holding the concrete type — which is how the tests
/// read them. Codex overrides both, because a config key is not the first token of its own fragment.
/// </remarks>
public abstract class AiAgent : IAiAgent
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string BinaryName { get; }
    public abstract string? InstallUrl { get; }
    public abstract SessionStrategy SessionStrategy { get; }
    public abstract IReadOnlyList<ApiFlavor> ConsumesApiFlavors { get; }

    /// <summary>
    /// The agent's own pair of commands: the one that resumes <paramref name="sessionId"/>, and the one
    /// to try when it does not work. Nothing about the instance's configuration belongs here.
    /// </summary>
    /// <remarks>What an agent implements instead of <see cref="Interactive"/>, for the reason
    /// <see cref="Configure"/> exists instead of <see cref="EnvFor"/>: the instance's effort, behaviour
    /// and extra arguments have to reach the command line of every agent, and a rule six classes have to
    /// remember is one five of them did forget.</remarks>
    protected abstract LaunchScripts Resume(string sessionId);

    public abstract IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage);
    public abstract IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage);
    public abstract IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage);
    public abstract IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage);
    public abstract void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto, AiEffort effort = AiEffort.High,
        string model = "");

    /// <summary>Nothing, for an agent with no way of being told a model on its command line.</summary>
    /// <remarks>The default is the honest answer for an agent nothing has been measured about, and it
    /// is what <see cref="AcceptsModel"/> reads — so an agent that cannot carry a model is one that
    /// says so rather than one that quietly runs on its own.</remarks>
    public virtual IReadOnlyList<string> ModelArgs(string model, AiUsage usage) => [];

    /// <inheritdoc />
    /// <remarks>The id as the instance stores it, which is right for every agent that is pointed at a
    /// service by its address rather than by its name. The two that keep a registry override this.
    /// </remarks>
    public virtual string QualifiedModel(AgentRuntime runtime) => runtime.RequestedModel;

    /// <inheritdoc />
    /// <remarks>True unless the agent says otherwise: every agent measured so far either takes a base
    /// URL from the environment or can be handed a configuration file, and an agent nothing is known
    /// about is better offered and seen to fail than hidden on a guess.</remarks>
    public virtual bool SupportsCustomEndpoint => true;

    /// <inheritdoc />
    /// <remarks>The interface default that is not: the default answers live here, on the one class
    /// every agent derives from, because a default on the interface member itself is never overridden
    /// by an answer declared only on a derived class — interface mapping stops at the class that
    /// lists the interface, and Claude Code's auto-compact gate answered false to the very caller it
    /// was written for. Virtual here, override in the agent.</remarks>
    public virtual bool UsesModelContextWindow => false;

    /// <inheritdoc />
    /// <remarks>Virtual for the reason <see cref="UsesModelContextWindow"/> gives.</remarks>
    public virtual bool UsesFastModel => false;

    /// <inheritdoc />
    /// <remarks>False by default: a slot read through the environment exists wherever the CLI does.
    /// </remarks>
    public virtual bool FastModelNeedsDeclaredEndpoint => false;

    /// <inheritdoc />
    /// <remarks>False for the agents pointed at a service by address: the model is then just a model.
    /// </remarks>
    public virtual bool NamesProviderInModel => false;

    /// <inheritdoc />
    /// <remarks><b>Not virtual, and for the reason <see cref="EnvFor"/> is not.</b> The sign-in's
    /// directories are made here for every agent, and an agent that overrode the whole method could
    /// drop that without anything noticing — so what an agent overrides is <see cref="Prepare"/>, which
    /// only ever adds. This is also the moment that exists so a getter does not write files, which is
    /// why the directories are made here and not in <see cref="EnvFor"/>: that one is reached through
    /// <c>AgentTileViewModel.LaunchEnvironment</c>, a property read twice a launch and again by any
    /// debugger watching it.</remarks>
    public void PrepareToLaunch(AgentRuntime runtime)
    {
        // Owner-only, and before the CLI is pointed at it. Created by the Settings form alone, it is
        // missing on every machine the settings file is imported into and after any manual tidy-up -
        // and the CLI then makes it itself, at whatever the umask says, which is where it writes a
        // refresh token. The answer is deliberately not checked: a launch is not the place to refuse a
        // tile over a directory the CLI may well create for itself, and Ensure has said so in the log.
        if (runtime.SignIn is { } signIn) AiSignInStore.Ensure(signIn, this);

        Prepare(runtime);
    }

    /// <summary>What this agent has to write down before a launch. Nothing, for all but one.</summary>
    protected virtual void Prepare(AgentRuntime runtime)
    {
    }

    /// <summary>
    /// <c>provider/model</c> for an agent whose registry names providers, or the bare id where no
    /// provider is configured.
    /// </summary>
    /// <remarks><para>Shared by opencode and pi because they take the same shape, and put here rather
    /// than duplicated so that the one rule with a measurement behind it has one home.</para>
    /// <para>An instance with no provider is left alone: it runs on whatever the CLI is already set up
    /// with, and prefixing a model with a provider that was never chosen would invent a pairing.</para>
    /// <para>A model that already carries a slash is <b>still</b> prefixed, and that is not a mistake —
    /// <c>z-ai/glm-5.3-flash</c> is one model id in OpenRouter's catalogue, so the qualified form is
    /// <c>openrouter/z-ai/glm-5.3-flash</c>. Trying to be clever about an existing slash would break
    /// every namespaced id these services actually publish.</para></remarks>
    protected static string WithProviderPrefix(AgentRuntime runtime)
    {
        var model = runtime.RequestedModel;
        if (model.Length == 0 || runtime.Provider is not { } provider) return model;

        return $"{provider.CatalogueId}/{model}";
    }

    /// <summary>
    /// The provider's own key, under the name that provider's key is read from.
    /// </summary>
    /// <remarks><b>Not <c>OPENAI_API_KEY</c> for everything.</b> These CLIs decide which service they
    /// are talking to from <em>which variable is set</em>, so one pair of OpenAI-shaped variables
    /// authenticated every instance against api.openai.com whatever its row said — measured through
    /// <c>opencode auth list</c>, which reported our <c>OPENAI_API_KEY</c> as the OpenAI provider.
    /// <para>Nothing for a service with no key: a local server needs an address, and an address is not
    /// something these agents take from the environment at all — see each agent for what it does
    /// instead.</para></remarks>
    protected static void ApplyProviderKey(IDictionary<string, string?> environment,
        AgentRuntime runtime, bool setKey = true)
    {
        // Nothing at all for an instance that has chosen no account: it runs on whatever the CLI is set
        // up with, and removing that setup is not what "the agent's own account" asks for.
        //
        // A *sign-in* is a choice, though, and gets the same clearing as a provider — the same bug by
        // the other branch, otherwise. An instance on a subscription names no provider, so returning
        // here left a globally exported OPENAI_API_KEY visible to opencode as a second account, and
        // QualifiedModel has no provider to prefix the model with either, so nothing breaks the tie.
        if (runtime.Provider is null && runtime.SignIn is null) return;

        var keep = runtime.Provider?.KeyEnvironmentVariable;

        // Nothing is *cleared* where the provider is declared rather than selected by key: opencode is
        // given a config file naming it, and `<provider>/<model>` on the command line says which one to
        // use, so an inherited key adds nothing to disambiguate - it only keeps the user's other
        // providers working in that tile, which is the whole reason that file merges their own
        // configuration in. Clearing here and merging there were two comments arguing opposite ways
        // about one launch. A sign-in is the exception: there the inherited key is what would
        // authenticate instead of the login.
        //
        // It skips the clearing alone, and that distinction is the whole of the fix: written as an
        // early return it also skipped the *setting* below, so an OpenRouter instance given a gateway
        // address - the case this branch was added for - had its own key left out of the environment
        // while OpenCodeProviderConfig wrote `{env:OPENROUTER_API_KEY}` into the document counting on
        // it being there. opencode resolved that to nothing and authenticated only on a machine where
        // the user happened to export the key globally.
        var clearTheOthers = !runtime.NeedsDeclaredEndpoint || runtime.SignIn is not null;

        // Every other service's variable is removed, and this is the half that was missing. These CLIs
        // decide which provider is in play from *which key variable is set* - the sentence is in this
        // method's own summary - so on a machine exporting a global OPENAI_API_KEY, setting
        // OPENROUTER_API_KEY beside it leaves both visible and nothing choosing between them. With no
        // model on the instance there is no `--model provider/...` to break the tie either, which is
        // the state every seeded instance starts in. A null value unsets, which is exactly what
        // ClaudeAgent already relies on for ANTHROPIC_API_KEY.
        if (clearTheOthers)
        {
            foreach (var variable in AiProviderCatalog.All
                         .Select(provider => provider.KeyEnvironmentVariable)
                         .OfType<string>()
                         .Distinct(StringComparer.Ordinal))
            {
                if (!string.Equals(variable, keep, StringComparison.Ordinal))
                    environment[variable] = null;
            }
        }

        // Set only for the agents that read it. Claude Code authenticates through ANTHROPIC_AUTH_TOKEN
        // and codex takes the key as OPENAI_API_KEY whatever the provider is; exporting the provider's
        // own variable for them put a secret into the tile's shell - and every program the user runs in
        // it - that nothing there reads.
        if (setKey && keep is not null && runtime.ApiKey.Length > 0)
            environment[keep] = runtime.ApiKey;
    }

    /// <summary>
    /// Whether a model set on the instance reaches this agent at all.
    /// </summary>
    /// <remarks>Derived from <see cref="ModelArgs"/> rather than declared, for the reason
    /// <see cref="EffortFlagFor"/> is derived: an agent that grows a flag must not have to remember a
    /// second place. Claude Code overrides it, because its model goes through the environment and no
    /// fragment would show it here.</remarks>
    public virtual bool AcceptsModel => ModelArgs("a-model", AiUsage.Interactive).Count > 0;

    /// <summary>Nothing this application would offer to run on somebody's machine, unless the agent
    /// says otherwise.</summary>
    public virtual InstallPlan? InstallPlan => null;

    /// <inheritdoc />
    public virtual bool SupportsStreaming => false;

    /// <inheritdoc />
    public virtual bool AcceptsPromptOnStdin => false;

    /// <summary>In front of the prompt when the prompt is the last argument, otherwise at the end.
    /// </summary>
    /// <remarks>An option after a positional argument is a parse this application does not get to
    /// decide — it is somebody else's CLI — so the extras go before it. Recognised by value rather than
    /// by a remembered index, because the value is what was handed in and cannot drift. An agent whose
    /// prompt is a flag's value overrides this.</remarks>
    public virtual int ExtraArgsIndex(IReadOnlyList<string> arguments, string prompt) =>
        arguments.Count > 0 && arguments[^1] == prompt ? arguments.Count - 1 : arguments.Count;

    /// <summary>
    /// The flag to blame is the first token of what was actually passed, and nothing when nothing was.
    /// </summary>
    /// <remarks>Derived rather than declared, so a flag cannot be renamed in
    /// <see cref="IAiAgent.EffortArgs"/> and left stale in the matcher — which would send a user to a
    /// setting that has nothing to do with the failure. An agent whose fragment does not begin with the
    /// token a refusal would name (codex, whose <c>-c</c> is the same token for every key) overrides
    /// this.</remarks>
    public virtual string? EffortFlagFor(AiEffort effort, AiUsage usage) =>
        EffortArgs(effort, usage).FirstOrDefault();

    /// <inheritdoc cref="EffortFlagFor" />
    public virtual string? BehaviourFlagFor(AiBehaviour behaviour, AiUsage usage) =>
        BehaviourArgs(behaviour, usage).FirstOrDefault();

    /// <summary>
    /// What the agent runs, with what the instance was configured to run it as.
    /// </summary>
    /// <remarks><b>Not virtual, and that is the same rule as <see cref="EnvFor"/>.</b> An agent answers
    /// <see cref="Resume"/>, which is only about the session; the instance's default effort, default
    /// behaviour and <see cref="AiAgentInstance.ExtraArgs"/> are appended here, so an agent cannot
    /// launch a tile on the CLI's own defaults by forgetting them. Both commands get them: the fallback
    /// is the same session under a different route, not a lesser one.
    /// <para>Fitted to what this agent supports interactively first
    /// (<see cref="AiProcessRunner.Fit"/>), so a mode the CLI has no flag for rounds down rather than
    /// reaching its command line and failing the launch.</para>
    /// <para><b>The session id is quoted before <see cref="Resume"/> ever sees it.</b> What comes back
    /// is a script handed whole to <c>powershell -Command</c> / <c>bash -c</c>, and the id is not this
    /// application's to trust: it is <c>TileNode.TileId</c> read out of a layout file anybody can edit
    /// or a backup can corrupt, or — for a captured agent — whatever string the CLI printed as its
    /// conversation id. Unquoted, a <c>;</c> in either of those is a second command running in the
    /// user's repository. Quoted here rather than in six <see cref="Resume"/> bodies, for the reason
    /// this method is not virtual at all.</para></remarks>
    public LaunchScripts Interactive(AgentRuntime runtime, string sessionId, IShellTerminal shell)
    {
        var commands = Resume(ForCommandLine(sessionId, shell));
        var arguments = InteractiveArguments(runtime);

        return arguments.Count == 0
            ? commands
            : commands with
            {
                Startup = Append(commands.Startup, arguments, shell),
                Fallback = Append(commands.Fallback, arguments, shell),
            };
    }

    /// <summary>Everything the instance adds to an interactive command line, in the order it is added.
    /// </summary>
    private IReadOnlyList<string> InteractiveArguments(AgentRuntime runtime)
    {
        var instance = runtime.Instance;
        var (behaviour, effort) = AiProcessRunner.Fit(this, AiUsage.Interactive,
            instance.DefaultBehaviour, instance.DefaultEffort, instance);

        return
        [
            .. BehaviourArgs(behaviour, AiUsage.Interactive),
            .. EffortArgs(effort, AiUsage.Interactive),
            // The resolved model rather than the stored one: a sentinel on a command line is a model
            // name no provider has.
            .. ModelArgs(QualifiedModel(runtime), AiUsage.Interactive),
            .. instance.ExtraArgs.Where(argument => !string.IsNullOrWhiteSpace(argument)),
        ];
    }

    /// <summary>
    /// The arguments after the command, quoted for the shell this tile runs — because this is a script
    /// typed into one, not an argv this application hands to <c>Process.Start</c>.
    /// </summary>
    /// <remarks>Quoted by the shell itself (<see cref="IShellTerminal.Quote"/>) rather than here: the
    /// rules are not shared. A <c>\"</c> escape is not one PowerShell recognises, and inside its double
    /// quotes a <c>$</c> interpolates and a backtick escapes — so an
    /// <see cref="AiAgentInstance.ExtraArgs"/> entry carrying any of the three came out mangled or
    /// partly executed. Only what needs it is quoted: a flag wrapped in quotes reads as a mistake to
    /// anyone looking at the scrollback, and every flag these agents take is quote-free.</remarks>
    private static string? Append(string? command, IReadOnlyList<string> arguments,
        IShellTerminal shell) =>
        command is null
            ? null
            : $"{command} {string.Join(' ', arguments.Select(argument => Quoted(argument, shell)))}";

    /// <summary>
    /// The session id as it may appear in a shell script: itself when it is quote-free, quoted when it
    /// is not, and empty when there is none.
    /// </summary>
    /// <remarks>The empty case is passed through rather than quoted, because an empty id is the answer
    /// two agents branch on — <c>codex</c> and <c>agy</c> start a plain session on it — and <c>''</c>
    /// is not empty to them. An ordinary id (a GUID, or opencode's <c>ses_</c> and one) is quote-free
    /// and comes out unchanged, so the scrollback still reads as the command a user would type.
    /// </remarks>
    private static string ForCommandLine(string sessionId, IShellTerminal shell) =>
        sessionId.Length == 0 ? sessionId : Quoted(sessionId, shell);

    /// <inheritdoc cref="Append"/>
    private static string Quoted(string argument, IShellTerminal shell) =>
        argument.Length > 0 && argument.All(IsQuoteFree) ? argument : shell.Quote(argument);

    /// <summary>A character every shell in the catalog leaves alone, so an argument made only of them
    /// is passed through unquoted.</summary>
    /// <remarks>An allow-list rather than a list of what to escape: the next shell added brings its own
    /// metacharacters, and a rule stated the other way round would already be wrong for it.</remarks>
    private static bool IsQuoteFree(char character) =>
        char.IsAsciiLetterOrDigit(character) || "._-/:=@+,".Contains(character);

    /// <summary>
    /// What the agent asks for, and then what the user asked for on top of it.
    /// </summary>
    /// <remarks><b>Not virtual, and the order is why.</b> The instance's own
    /// <see cref="AiAgentInstance.ExtraEnv"/> is merged last, so a variable the user set by hand wins
    /// over one this application worked out — including setting one back that the agent wanted removed.
    /// An agent free to override the whole method could drop that rule without anything noticing, so
    /// what an agent overrides is <see cref="Configure"/>, which only ever contributes.</remarks>
    public IReadOnlyDictionary<string, string?> EnvFor(AgentRuntime runtime)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);

        // First, so that an agent's own Configure and then the user's ExtraEnv can both still overrule
        // it — and here rather than in Configure for the same reason this method is not virtual: an
        // agent that forgot the line would run every one of its tiles on the default account whatever
        // the row said, which is a different subscription being billed with nothing on screen saying so.
        if (runtime.SignIn is { } signIn)
            foreach (var (name, value) in SignInEnv(AiSignInStore.DirectoryFor(signIn)))
                environment[name] = value;

        Configure(environment, runtime);

        foreach (var (name, value) in runtime.Instance.ExtraEnv)
            environment[name] = value;

        return environment;
    }

    /// <summary>Most CLIs hold one login. The three that do not say so for themselves.</summary>
    public virtual bool SupportsSignIns => false;

    /// <inheritdoc />
    public virtual IReadOnlyDictionary<string, string?> SignInEnv(string configDirectory) =>
        new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    /// Whether anything at all was written into that directory, which is the most an agent whose
    /// credential file this application has not measured can honestly answer.
    /// </summary>
    /// <remarks>Deliberately not "the directory exists": the New sign-in step <em>creates</em> it, so
    /// existence would report a brand-new row as logged in and send the user to a tile that cannot
    /// authenticate. An agent that knows its own file overrides this and says who.
    /// The enumeration itself never throws out of here: a row naming a directory it cannot read
    /// (<c>ConfigDirectory</c> is typed by hand) is answered "not signed in", the same recovery every
    /// override offers — these are read while a Settings page is being drawn.</remarks>
    public virtual SignInStatus ReadSignIn(string? configDirectory)
    {
        if (configDirectory is not { Length: > 0 } directory || !Directory.Exists(directory))
            return SignInStatus.NotSignedIn;

        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any()
                ? SignInStatus.SignedInAnonymously
                : SignInStatus.NotSignedIn;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SignInStatus.NotSignedIn;
        }
    }

    /// <summary>Nothing, unless an agent publishes its allowance.</summary>
    /// <remarks>The default is <c>null</c> rather than an empty report, and the distinction is the one
    /// <see cref="IAiAgent.UsageAsync"/> spells out: three of these five CLIs have no endpoint and no
    /// file that says how much is left, and a card reading "0%" for them would be a figure this
    /// application invented.</remarks>
    public virtual Task<AiUsageReport?> UsageAsync(AiSignIn? signIn, CancellationToken ct = default) =>
        Task.FromResult<AiUsageReport?>(null);

    /// <inheritdoc />
    /// <remarks>Null by default, which is the answer for an agent that reports no usage at all and for
    /// one that cannot tell its logins apart on disk. It costs the round one extra call at worst, and
    /// that is the same call being made today.</remarks>
    public virtual string? UsageAccountKeyFor(AiSignIn? signIn) => null;

    /// <summary>The key one account's readings are filed under.</summary>
    /// <remarks>The agent's id and the sign-in's, never a name: nothing makes a sign-in's name unique
    /// and the user can retype it, so a renamed row would either start a fresh history or adopt
    /// somebody else's. Here rather than on each agent that answers usage, so two of them cannot file
    /// the same account under two keys.</remarks>
    protected string UsageSourceId(AiSignIn? signIn) => signIn is null ? Id : $"{Id}:{signIn.Id}";

    /// <summary>What the card is titled: the CLI, and which of its logins where there is more than
    /// one.</summary>
    protected string UsageSourceName(AiSignIn? signIn) =>
        signIn?.Name is { Length: > 0 } named ? $"{DisplayName} · {named}" : DisplayName;

    /// <summary>
    /// A file or directory named the one way, so that two rows reading it are recognisably one account.
    /// </summary>
    /// <remarks><para><b>The default account and a sign-in can be the same login, and routinely are.</b>
    /// A machine that exports <c>CLAUDE_CONFIG_DIR</c> — which is exactly what an mTiles sign-in sets for
    /// the tiles it launches — has its default account <em>in</em> that sign-in's own directory, so the
    /// two are read from one file and the tile drew the same figures twice under two names.</para>
    /// <para>The path and not the credential: what is being asked is whether two rows point at one
    /// login on this disk, and comparing tokens would mean holding somebody's refresh token to answer a
    /// question about a file name. Case-insensitively on Windows, where the two spellings are one file.
    /// </para></remarks>
    protected static string? UsageAccountKey(string? path)
    {
        if (path is not { Length: > 0 }) return null;

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // A path this cannot canonicalise is one this cannot compare, and the honest answer to that
            // is "cannot say" - which shows both rows rather than merging two accounts on a guess.
            return null;
        }
    }

    /// <summary>Reads one string out of a JSON file, or null for anything that did not work.</summary>
    /// <remarks><para>Shared by the agents that can name their account, and it never throws: these
    /// files belong to somebody else's CLI, they are read while a Settings page is being drawn, and
    /// every failure — absent, locked, half-written, a format that has moved — means the same thing on
    /// screen. A row that says "not signed in" is recoverable; a dialog with a stack trace in it is
    /// not.</para>
    /// <para>Takes a path <em>through</em> the document so that the one field wanted is the one field
    /// read: these files hold a refresh token beside it.</para>
    /// <para>The document is <b>walked, not parsed</b>: <c>.claude.json</c> carries a Claude Code
    /// installation's per-project history beside <c>oauthAccount</c> and grows into the megabytes, and
    /// a DOM of the whole file — several times its size in memory, on the UI thread, once per sign-in
    /// row and once per account-chooser rebuild — is more than naming who a row belongs to may cost.
    /// The reader is fed the file a chunk at a time and stops at the answer, at the point the path
    /// provably cannot continue (the matched object ended, or a segment turned out not to be an
    /// object), or at the end of the file; everything else is skipped token by token, and only what
    /// the path reaches is ever decoded.</para></remarks>
    protected static string? ReadJsonString(string path, params string[] propertyPath)
    {
        try
        {
            if (!File.Exists(path) || propertyPath.Length == 0) return null;

            return WalkJsonPath(path, propertyPath);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not read {0}: {1}", path, ex.Message);
            return null;
        }
    }

    /// <summary>What the token straight after a matched property name has to be: the object the rest
    /// of the path lives in, or — on the last segment — the answer itself.</summary>
    private enum Expected { Nothing, Object, Answer }

    private static string? WalkJsonPath(string path, string[] propertyPath)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096);

        var buffer = new byte[8 * 1024];
        var filled = 0;
        var final = false;
        var state = new JsonReaderState();

        // Depth is counted by hand because what a property name may match depends on where in the
        // tree it sits: a property of the object matched so far sits exactly one level inside it,
        // which is what keeps a name merely *passing through* — inside a value being skipped, or
        // inside an object already left behind, the way the DOM read's TryGetProperty could not
        // reach it — from answering in the field's place or restarting the search.
        var depth = 0;
        var matched = 0;
        var expected = Expected.Nothing;

        while (true)
        {
            var reader = new Utf8JsonReader(buffer.AsSpan(0, filled), final, state);

            while (reader.Read())
            {
                if (expected != Expected.Nothing)
                {
                    if (expected == Expected.Answer)
                        return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

                    expected = Expected.Nothing;
                    if (reader.TokenType != JsonTokenType.StartObject) return null;
                    depth++;
                    continue;
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        depth++;
                        break;

                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        depth--;
                        break;

                    case JsonTokenType.PropertyName:
                        // matched cannot overflow propertyPath here: every increment arms `expected`,
                        // and the answer it asks for is consumed before anything else can match.
                        if (depth == matched + 1 && reader.ValueTextEquals(propertyPath[matched]))
                        {
                            matched++;
                            expected = matched == propertyPath.Length
                                ? Expected.Answer
                                : Expected.Object;
                        }
                        break;
                }
            }

            if (final) return null;

            // The reader stopped inside an unfinished token: what it consumed is dead, so the rest is
            // moved to the front and the next read carries on after it. The saved state describes
            // that token, so it has to still be at the front of the span the next reader is handed.
            state = reader.CurrentState;
            var consumed = (int)reader.BytesConsumed;
            Array.Copy(buffer, consumed, buffer, 0, filled - consumed);
            filled -= consumed;

            if (filled == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);

            var read = stream.Read(buffer.AsSpan(filled));
            if (read == 0) final = true;
            else filled += read;
        }
    }

    /// <summary>
    /// The variables this agent needs in order to run against the configured provider.
    /// </summary>
    /// <remarks>Nothing by default, which is the right answer for an instance with no provider: an
    /// agent left alone uses whatever it was configured with, and that is the case a first run is in.
    /// A <c>null</c> value <b>unsets</b> the variable — the half that lets an instance authenticate
    /// some other way on a machine that exports a global key.</remarks>
    protected virtual void Configure(IDictionary<string, string?> environment, AgentRuntime runtime) { }

    /// <summary>The tile's own identity, which is what an agent that takes an id outright wants.
    /// </summary>
    public virtual string SessionIdForTile(string tileId) => tileId;

    /// <inheritdoc />
    public virtual bool CapturesWhileRunning => false;

    /// <summary>Nothing to capture, which is the answer for every agent that lets us name the session
    /// ourselves.</summary>
    public virtual Task<string?> CaptureSessionAsync(AiAgentInstance instance,
        SessionCaptureRequest request, CancellationToken ct) =>
        Task.FromResult<string?>(null);

    /// <summary>One line of output, unparsed. What an agent that says nothing structured about itself
    /// produces, which is four of the five.</summary>
    public virtual IReadOnlyList<AiOutputChunk> ParseLine(string line) =>
        string.IsNullOrWhiteSpace(line) ? [] : [new AiOutputChunk { Content = line }];

    /// <summary>The canonical levels, for an agent whose own scale is the canonical one.</summary>
    protected static IReadOnlyList<AiEffort> FullEffortScale { get; } =
        [AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.XHigh, AiEffort.Max, AiEffort.ToolDefault];
}
