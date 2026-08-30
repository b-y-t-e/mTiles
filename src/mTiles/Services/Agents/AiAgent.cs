using System.Diagnostics;
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
            .. ModelArgs(runtime.RequestedModel, AiUsage.Interactive),
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
        Configure(environment, runtime);

        foreach (var (name, value) in runtime.Instance.ExtraEnv)
            environment[name] = value;

        return environment;
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
