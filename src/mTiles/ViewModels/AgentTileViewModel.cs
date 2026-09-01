using System.Diagnostics;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.Services.Shells;

namespace mTiles.ViewModels;

/// <summary>
/// An AI agent in a tile: the terminal tile, with its commands coming from an <see cref="IAiAgent"/>
/// and an <see cref="AiAgentInstance"/> instead of from a shell profile the user had to write.
/// </summary>
/// <remarks>
/// <para>Derived rather than parallel, and that is the whole point of the split: everything a shell
/// tile does — the theme, the activity light, the clipboard registration, the launch chain, the
/// header's actions — an agent tile does identically. What differs is two answers, so two members are
/// overridden: where the commands come from, and what the layout calls this kind.</para>
/// <para><b>The instance is read at every launch, not captured at construction.</b> An instance whose
/// model or provider is changed in Settings takes effect on the next restart of the tile, which is the
/// same rule a shell profile already follows — and the reason a tile stores an id rather than a copy.
/// </para>
/// </remarks>
public sealed class AgentTileViewModel : TerminalTileViewModel, IDescribedTile
{
    private readonly IAiAgent _agent;
    private readonly SettingsService _settings;
    private readonly Action? _requestSave;

    /// <summary>The id captured from an agent that names its own session, and the tile identity it was
    /// captured under.</summary>
    /// <remarks>The pair, rather than the id alone, is what makes "New session" work on a captured
    /// agent: that command replaces the leaf's <c>TileId</c> and restarts, and an id remembered without
    /// the identity it belongs to would then resume the conversation the user has just asked to leave.
    /// </remarks>
    private string _capturedSessionId;
    private string _capturedForTileId;

    /// <summary>The capture in flight, so a tile closed while one is running does not leave a process
    /// behind — and so a second launch cannot race the first one's answer into the layout.</summary>
    private CancellationTokenSource? _capturing;

    /// <inheritdoc />
    public override string KindId => TileKindIds.Agent;

    /// <summary>Which configured way of running an agent this tile is. Stored in the layout, looked up
    /// in settings at every launch.</summary>
    /// <remarks>Written only by <see cref="SwitchTo"/>, never by a plain setter: three things have to
    /// happen together with it — the substitution is put down, the captured conversation is dropped when
    /// the account changes, and the layout is asked to be written — and a setter is an invitation to do
    /// one of them and not the others.</remarks>
    public string InstanceId { get; private set; }

    /// <summary>Which agent it runs, so a tile whose instance has been deleted can still be shown for
    /// what it was.</summary>
    public string AgentId => _agent.Id;

    /// <summary>What the layout asked for, when this tile could not be built as it — otherwise null.
    /// </summary>
    /// <remarks>Read by <c>AgentTileKind.Save</c>, which writes the requested ids rather than these
    /// ones: see <see cref="AgentSubstitution"/>. Cleared by <see cref="SwitchTo"/>, and only there: a
    /// user who points the tile at another instance has answered the question the notice was asking, and
    /// a substitution left standing would have <c>Save</c> write the old requested id over their
    /// choice.</remarks>
    public AgentSubstitution? Substitution { get; private set; }

    /// <summary>
    /// The conversation this tile resumes.
    /// </summary>
    /// <remarks>For the two strategies where we choose it, it is the tile's own identity as
    /// <em>the agent</em> spells it — opencode's <c>ses_</c> prefix is not this tile's business, and a
    /// bare GUID handed to it threw before the tile could launch — and nothing has to be written down.
    /// For the one where the agent chooses, it is whatever was captured under <em>this</em> identity —
    /// empty until it has been, which every agent reads as "start a fresh one".</remarks>
    public string SessionId =>
        NamesItsOwnSession
            ? (_capturedForTileId == TileId ? _capturedSessionId : "")
            : _agent.SessionIdForTile(TileId);

    /// <summary>Whether this tile's session id is the agent's own answer rather than ours.</summary>
    /// <remarks>Which is what makes it worth writing down: the two strategies where we choose the id
    /// derive it from the tile's identity at every launch, so a stored copy could only ever disagree —
    /// and, handed to a different agent, would be an id it has never seen.</remarks>
    public bool NamesItsOwnSession => _agent.SessionStrategy == SessionStrategy.CapturedAfterStart;

    public AgentTileViewModel(string workingDirectory, ShellInstallation? shell,
        SettingsService settingsService, IAiAgent agent, string instanceId,
        string? sessionId = null, Func<string>? tileId = null, Action? requestSave = null,
        AgentSubstitution? substitution = null)
        : base(workingDirectory, shell, settingsService, tileId: tileId)
    {
        _agent = agent;
        _settings = settingsService;
        _requestSave = requestSave;
        InstanceId = instanceId;
        Substitution = substitution;
        // Said at construction rather than at the launch: it is an answer about the layout this tile was
        // restored from, and one the user can put down.
        if (substitution is not null) LaunchNotice = substitution.Notice;
        _capturedSessionId = sessionId ?? "";
        // The identity the stored id belongs to is the one this tile is loading under: a layout only
        // ever carries the two together.
        _capturedForTileId = TileId;
        // Claimed at construction, not only when captured: a layout reopened brings its session back
        // without anything capturing it, and a neighbouring codex tile starting a moment later must not
        // be able to adopt the conversation this tile is already showing.
        CapturedSessions.Claim(_capturedSessionId, TileId);
    }

    /// <summary>The instance as settings define it <em>now</em>, or the agent's seeded one when the
    /// user has deleted it — an instance that is gone must leave a working tile, not a dead one.</summary>
    private AiAgentInstance Instance =>
        _settings.Settings.AiAgentInstances.FirstOrDefault(i => i.Id == InstanceId)
        ?? AiAgentCatalog.SeedInstanceFor(_agent);

    /// <summary>The instances this tile could be switched to, the one it is running included.</summary>
    /// <remarks>
    /// <para><b>The same agent, and nothing else.</b> Another instance of the same
    /// <see cref="IAiAgent"/> is the same program with another account, model or set of flags — the same
    /// session strategy, the same resume commands, the same shape of environment. Another <em>agent</em>
    /// is another program working in somebody's repository, which is the failure
    /// <see cref="AgentSubstitution"/> exists to announce rather than something to offer as a menu item.
    /// </para>
    /// <para>Filtered by <c>AiAgentCatalog.IsAvailable</c>, the rule the tile chooser and the Goal
    /// tile's list already hide on, so a pairing <c>AgentModelResolver</c> would refuse the launch of
    /// cannot be picked here either. Read from settings at the moment it is asked for: instances are
    /// added, renamed and deleted while the tile lives.</para>
    /// </remarks>
    public IReadOnlyList<AiAgentInstance> SwitchTargets =>
        [.. _settings.Settings.AiAgentInstances.Where(
            instance => instance.AgentId == AgentId
                        && AiAgentCatalog.IsAvailable(instance, _settings.Settings))];

    /// <summary>
    /// What the user is agreeing to, or null when there is nothing to switch to.
    /// </summary>
    /// <remarks>Switching kills whatever the shell is running, so it is asked about like any other
    /// destructive action — and the sentence names the part the user cannot see coming: the account is
    /// where the CLI keeps its conversations, so changing it is what changes which conversation the tile
    /// comes back to. On an agent that names its own session that loss is one way; on the other two the
    /// id is derived from the tile's identity at every launch, so switching back finds the old
    /// conversation again.</remarks>
    public string? ConfirmationForSwitchTo(string instanceId)
    {
        if (Target(instanceId) is not { } target) return null;

        var question = $"Run this tile as \"{target.Name}\"? Whatever it is running now is stopped.";
        if (target.SignInId == Instance.SignInId) return question;

        return question + (NamesItsOwnSession
            ? " It is a different account, so the current conversation will not be resumed."
            : " It is a different account, so a new conversation starts — switching back reopens this one.");
    }

    /// <summary>
    /// Points the tile at another instance of the same agent.
    /// </summary>
    /// <remarks>
    /// <para>Everything the tile runs on is derived from <see cref="Instance"/> at every launch — the
    /// runtime, the environment, the commands, the model — so this is the whole of the switch, and the
    /// restart that follows it is the caller's. The layout is asked for straight away: a choice nobody
    /// writes down is one the next start of mTiles does not honour.</para>
    /// <para><b>Nothing else can be reached from here.</b> An id that is not an available instance of
    /// this agent is refused rather than resolved onto something near it, which is the difference
    /// between this and the fallback chain in <c>AgentTileKind.Resolve</c>: that one is rescuing a tile
    /// nobody is choosing for, and this one is the user choosing.</para>
    /// </remarks>
    public void SwitchTo(string instanceId)
    {
        if (Target(instanceId) is not { } target) return;

        // Read before the change, because Instance answers from the id below.
        var accountChanged = target.SignInId != Instance.SignInId;

        InstanceId = target.Id;
        ClearSubstitution();
        if (accountChanged) ForgetCapturedSession();

        OnPropertyChanged(nameof(HeaderNote));
        _requestSave?.Invoke();
    }

    /// <summary>The instance a switch would land on, or null when the switch means nothing.</summary>
    /// <remarks>The same agent is the whole of the constraint here, and availability deliberately is not
    /// part of it: what a machine has installed decides what <see cref="SwitchTargets"/> offers, exactly
    /// as it decides what the tile chooser and the Goal tile's list offer, and the launch is where an
    /// instance that cannot be run says so by name (<c>AgentModelResolver</c>). Repeating the filter
    /// here would make the tile's own bookkeeping depend on a fact about the machine that it is not the
    /// one reporting.</remarks>
    private AiAgentInstance? Target(string instanceId) =>
        instanceId == InstanceId
            ? null
            : _settings.Settings.AiAgentInstances.FirstOrDefault(
                instance => instance.Id == instanceId && instance.AgentId == AgentId);

    /// <summary>Puts down the report of a substitution the user has just overruled.</summary>
    /// <remarks>The notice only if it is still the one the substitution put there: the user may have
    /// dismissed it, and a launch may have replaced it with something of its own.</remarks>
    private void ClearSubstitution()
    {
        if (Substitution is not { } substitution) return;

        if (LaunchNotice == substitution.Notice) LaunchNotice = "";
        Substitution = null;
    }

    /// <summary>Forgets the conversation captured under the account the tile is leaving.</summary>
    /// <remarks>The same reset <see cref="ReleaseSessionOfPreviousIdentity"/> performs, keyed on the
    /// account rather than on the tile's identity — two independent triggers, both of which have to
    /// exist. A captured id is only meaningful inside its own <c>CODEX_HOME</c> / <c>~/.gemini</c>:
    /// handed to the new account, <c>codex resume &lt;unknown&gt;</c> stops on an interactive picker
    /// nobody knows the tile is waiting for, and <c>agy --conversation &lt;unknown&gt;</c> warns,
    /// silently starts a different conversation and exits 0.</remarks>
    private void ForgetCapturedSession()
    {
        if (!NamesItsOwnSession) return;

        CancelCapture();
        CapturedSessions.ReleaseAllOf(_capturedForTileId);
        _capturedSessionId = "";
        _capturedForTileId = TileId;
    }

    /// <inheritdoc />
    /// <remarks>Through the runtime rather than the instance alone, because the model belongs on the
    /// command line of the four agents that are told one that way — and it is the <em>resolved</em>
    /// model, settled by <see cref="PrepareForLaunchAsync"/> a moment earlier.</remarks>
    public override LaunchScripts ResolveCurrentScripts() => _agent.Interactive(Runtime, SessionId, Shell.Shell);

    /// <summary>The instance, its provider and the model this launch settled on.</summary>
    private AgentRuntime Runtime =>
        AgentRuntime.For(_settings.Settings, Instance, _resolvedModel, _agent,
            _autoCompactWindow, _maxContextTokens);

    /// <summary>
    /// Which agent this tile is, and on what — the line beside its name.
    /// </summary>
    /// <remarks>
    /// <para><b>The instance's name first, the CLI's only as a fallback.</b> The instance is the thing
    /// the user configured and named, and its name is what the Settings row and the chooser both show;
    /// falling straight through to the CLI would name the program rather than the configuration, which
    /// is the distinction the whole instance model exists to make. Two tiles both called
    /// <c>Agent#N</c> may be a subscription and an API key on the same binary.</para>
    /// <para><b>The model is shortened, and only for display.</b> Provider ids are namespaced
    /// (<c>z-ai/glm-5.3-flash</c>) and the header is the narrowest place in the application, so the
    /// vendor is dropped from a line that is already the second thing to give way — the full name is a
    /// tooltip away and unchanged everywhere it is stored or sent. Empty for an instance that names no
    /// model: "whatever the agent picks" is not a model, and printing the word for it would fill the
    /// scarcest line on screen with the absence of information.</para>
    /// <para>Read live rather than captured at construction, so that editing the instance in Settings
    /// and restarting the tile redraws this without anything having to notice.</para>
    /// </remarks>
    public string HeaderNote
    {
        get
        {
            var instance = Instance;
            var name = instance.Name.Length > 0 ? instance.Name : _agent.DisplayName;
            var model = ShortModel(_resolvedModel ?? instance.Model);

            return model.Length > 0 ? $"{name} · {model}" : name;
        }
    }

    /// <summary>The part of a model id that tells one model from another.</summary>
    /// <remarks>The sentinel is not a name and never reaches here as one — but it can be the stored
    /// value before a launch has resolved it, and <c>__first_loaded__</c> in a header is worse than
    /// nothing.</remarks>
    private static string ShortModel(string model) =>
        model.Length == 0 || model == AiModelChoice.FirstLoaded
            ? ""
            : model[(model.LastIndexOf('/') + 1)..];

    /// <summary>
    /// The model this launch settled on, when the instance asked for whatever the server had loaded.
    /// </summary>
    /// <remarks>Held for one launch and re-resolved at the next, which is the whole point of the
    /// sentinel: a name written down here would mean changing the model in LM Studio no longer changed
    /// it for this tile.</remarks>
    private string? _resolvedModel;

    /// <summary>The auto-compact window resolved for this launch together with the model - already
    /// reduced by the <see cref="ModelContextWindow"/> rule, whose whole answer it is.</summary>
    /// <remarks>Null sets nothing: an instance with no model, a provider that did not say, or a model
    /// whose window is too small for the compaction variable's minimum all leave the CLI on its own
    /// assumption. Read by <see cref="Runtime"/> - and reset with the model, so a launch that fails
    /// does not hand the next one a window settled for the model before it.</remarks>
    private long? _autoCompactWindow;

    /// <summary>The assumed window resolved beside it - the model's context at 100%, for
    /// <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c>. Same rules, same reset, same null.</summary>
    private long? _maxContextTokens;

    /// <inheritdoc />
    /// <remarks>The provider's address and key, and the model resolved above — never the startup
    /// script, which is typed into a live prompt and kept in the shell's history.</remarks>
    public override IReadOnlyDictionary<string, string?>? LaunchEnvironment => _agent.EnvFor(Runtime);

    /// <summary>
    /// Works out which model this launch runs on, and refuses the launch when it cannot.
    /// </summary>
    /// <remarks>
    /// <para><b>A model that cannot be resolved fails the launch</b> rather than leaving the agent to
    /// pick one for itself. The user asked for whatever the server had loaded; starting the session on
    /// something else — with the reason in a log file — is exactly the silent substitution the sentinel
    /// exists to prevent, and it is invisible precisely when it matters, because the tile looks like it
    /// worked.</para>
    /// <para>A model on an agent that has no way of being told one is the same fault by the other
    /// route, and it is said here for the same reason: the setting is on the instance's row and does
    /// nothing at all.</para>
    /// </remarks>
    private async Task ResolveModelAsync()
    {
        var instance = Instance;
        _resolvedModel = null;
        _autoCompactWindow = null;
        _maxContextTokens = null;
        LaunchProblem = "";

        var (model, problem) =
            await AgentModelResolver.ResolveAsync(_settings.Settings, _agent, instance);

        if (problem is not null)
        {
            LaunchProblem = problem;
            // The header still showed the model the *previous* launch settled on, over a launch that is
            // not happening: _resolvedModel was cleared above and nothing said so.
            OnPropertyChanged(nameof(HeaderNote));
            Trace.TraceWarning("Tile {0} was not launched: {1}", TileId, problem);
            return;
        }

        _resolvedModel = model ?? "";

        // The windows travel with the model and only for the agent that reads them
        // (ModelContextWindow gates on that), so no provider is asked for an agent that sets nothing
        // from the answer.
        if (await ModelContextWindow.ResolveAsync(
                _settings.Settings, _agent, instance, _resolvedModel) is { } windows)
        {
            _autoCompactWindow = windows.AutoCompactWindow;
            _maxContextTokens = windows.MaxContextTokens;
        }

        // The header shows the model this launch settled on, and until now that was the instance's
        // stored value — which for the "first loaded" sentinel is not a model name at all. Announced
        // rather than pushed at the view, so the tile stays a view model that knows nothing about
        // headers.
        OnPropertyChanged(nameof(HeaderNote));
    }

    /// <summary>
    /// Brings the conversation into being for an agent that has to be asked before it is resumed.
    /// </summary>
    /// <remarks>agy's capture is a real (cheap) call that <em>creates</em> a conversation, so it has to
    /// happen before the command line that resumes it is written — afterwards the tile would be showing
    /// one conversation and remembering another. Once per tile: an id already captured under this
    /// identity is not asked for again.</remarks>
    public override async Task PrepareForLaunchAsync()
    {
        ReleaseSessionOfPreviousIdentity();

        // First, because the environment the commands run with is read straight after they are
        // resolved: a model settled afterwards would reach the tile one launch late.
        await ResolveModelAsync();

        // Nothing is launched with a problem standing, so nothing is created for it either: agy's
        // pre-create is a model call, and making a conversation for a session that is not going to
        // start is a call the user pays for twice.
        if (HasLaunchProblem) return;

        // After the model, and after the problem check. opencode's generated document declares the one
        // model the launch will ask for, so writing it any earlier declares the wrong one: before the
        // first resolve `Runtime` still carries the sentinel, which RequestedModel reports as no model
        // at all, and on later launches it carries the answer from the launch before. Either way the
        // command line then names a model the document does not - which is the
        // ProviderModelNotFoundError the document exists to prevent. And after the check because a
        // launch that is not going to happen has nothing to prepare.
        _agent.PrepareToLaunch(Runtime);

        if (_agent.CapturesWhileRunning) return;
        await CaptureAsync(DateTimeOffset.UtcNow, retryFor: TimeSpan.Zero);
    }

    /// <inheritdoc />
    /// <remarks>Nothing is awaited here — the launcher has a terminal to hand back — and the retry is
    /// what a file-based capture needs: codex writes its rollout a moment after it starts, and asking
    /// once would answer "no session" for every tile.</remarks>
    public override void OnLaunched(DateTimeOffset startedAt)
    {
        if (!_agent.CapturesWhileRunning) return;
        _ = CaptureAsync(startedAt, RetryFor);
    }

    /// <summary>How long a capture that reads a file keeps looking, and how often.</summary>
    /// <remarks>Measured against nothing but patience: a tile that fails to capture still works, it
    /// just starts a fresh conversation next time. Long enough to cover a cold start of the agent,
    /// short enough that a tile the user closed is not still polling minutes later.</remarks>
    private static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks the agent for the id of the session it is running, and writes it into the layout.
    /// </summary>
    /// <remarks>Every failure ends as "no session id": a capture is an optimisation on top of a tile
    /// that works without one, and the cost of getting it wrong is a conversation the next launch does
    /// not resume — never a tile that does not start.</remarks>
    private async Task CaptureAsync(DateTimeOffset startedAt, TimeSpan retryFor)
    {
        if (SessionId is { Length: > 0 }) return;
        if (AiAgentCatalog.Locate(_agent) is not { } executablePath) return;

        var capturing = new CancellationTokenSource();
        Interlocked.Exchange(ref _capturing, capturing)?.Cancel();

        // Read once, before the first await: the tile this capture is for is the tile it started under,
        // and a "New session" taken while it runs must not have the answer land under the new identity.
        var capturedFor = TileId;
        var instance = Instance;
        // Read here too, and for the same reason: a capture that creates a conversation must create it
        // against the provider, key and ExtraEnv this tile's own session runs with.
        var environment = LaunchEnvironment;
        var deadline = DateTimeOffset.UtcNow + retryFor;

        try
        {
            while (true)
            {
                var id = await _agent.CaptureSessionAsync(instance,
                    new SessionCaptureRequest(executablePath, WorkingDirectory, startedAt, capturedFor,
                        environment),
                    capturing.Token);

                if (id is { Length: > 0 })
                {
                    Remember(id, capturedFor);
                    return;
                }

                if (DateTimeOffset.UtcNow >= deadline) return;
                await Task.Delay(RetryEvery, capturing.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // The tile was closed, or a second launch took over. Neither is worth a word.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Capturing the session of {0} in tile {1} failed, so it will start a new "
                + "conversation next time: {2}", _agent.Id, capturedFor, ex.Message);
        }
        finally
        {
            Interlocked.CompareExchange(ref _capturing, null, capturing);
            capturing.Dispose();
        }
    }

    /// <summary>Gives up the session held under the identity this tile has just left.</summary>
    /// <remarks>"New session" replaces the leaf's <c>TileId</c> and relaunches, so the claim made under
    /// the old one has no holder any more: nothing else releases it, and left behind it would keep the
    /// abandoned conversation unavailable to every other tile for the rest of the run — while the
    /// dictionary grew by an entry each time the command was used. Released at the launch that follows
    /// the change, which is the first moment this tile can see that its identity moved.</remarks>
    private void ReleaseSessionOfPreviousIdentity()
    {
        if (_capturedForTileId == TileId) return;

        CapturedSessions.ReleaseAllOf(_capturedForTileId);
        _capturedSessionId = "";
        _capturedForTileId = TileId;
    }

    /// <summary>Keeps a captured id, and asks for the layout to be written.</summary>
    /// <remarks>The save is the point: a captured id nobody writes down is a conversation lost at the
    /// next restart, which is the one thing this strategy costs that the other two do not.</remarks>
    private void Remember(string sessionId, string capturedFor)
    {
        if (capturedFor != TileId) return;

        _capturedSessionId = sessionId;
        _capturedForTileId = capturedFor;
        CapturedSessions.Claim(sessionId, capturedFor);
        _requestSave?.Invoke();
    }

    /// <inheritdoc />
    /// <remarks>The claim goes with the tile: a session nobody is showing any more is one the next codex
    /// tile in this workspace may legitimately be handed.</remarks>
    protected override void OnDisposing()
    {
        CancelCapture();
        CapturedSessions.ReleaseAllOf(TileId);
        // And whatever it held before its last change of identity, for a tile closed after "New
        // session" without ever completing the launch that would have released it.
        CapturedSessions.ReleaseAllOf(_capturedForTileId);
    }

    /// <summary>Stops a capture that is still running, whatever state its token source is in.</summary>
    /// <remarks>The capture's own <c>finally</c> clears the field and then disposes the source, so a
    /// dispose landing between those two steps reads a live reference to an already-disposed
    /// <see cref="CancellationTokenSource"/> and <c>Cancel</c> throws. Unhandled, that would take the two
    /// <see cref="CapturedSessions.ReleaseAllOf(string)"/> calls below with it and leave the captured id
    /// held by a tile that no longer exists — the lost conversation the register exists to prevent.</remarks>
    private void CancelCapture()
    {
        try
        {
            Interlocked.Exchange(ref _capturing, null)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The capture had already finished with it.
        }
    }
}
