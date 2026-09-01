using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using mTiles.Services.Shells;

namespace mTiles.ViewModels;

/// <summary>
/// The AI page: the agent instances a tile can be created from, and the providers they authenticate
/// through.
/// </summary>
/// <remarks>
/// <para>The last piece of the agents work, and the one that made the rest reachable: an instance's
/// provider, key, model, behaviour and effort could all be stored, launched through and tested, and
/// none of them could be typed anywhere — the only way to configure one was to edit
/// <c>settings.json</c> by hand.</para>
/// <para><b>Two lists, one form at a time</b>, on the overlay the manual database connection already
/// uses: these forms are taller than the dialog, and a row that grows into one pushes the list it came
/// from off screen with Save below the fold.</para>
/// <para><b>Installing shows the plan and runs it in a terminal</b> — never silently. It writes outside
/// every directory this application owns, sometimes with elevation, and the only honest place for that
/// is a tile the user can read afterwards.</para>
/// </remarks>
public partial class SettingsViewModel
{
    public bool IsAiTab => SelectedTab == SettingsTabs.Ai;

    public ObservableCollection<AiAgentInstanceViewModel> AgentInstances { get; } = [];
    public ObservableCollection<AiProviderInstanceViewModel> ProviderInstances { get; } = [];
    public ObservableCollection<AiSignInViewModel> SignIns { get; } = [];

    /// <summary>Runs an agent's install command where the user can see it — wired to the workspace,
    /// because a plan shown and then run out of sight is the thing this exists to prevent.</summary>
    /// <remarks>Answers whether it ran: a workspace has to be open for there to be a tile to run it in,
    /// and an install that quietly did nothing is worse than one that says where it would have gone.
    /// </remarks>
    public Func<InstallPlan, Task<bool>>? RunInstallPlan { get; set; }

    /// <summary>Asks for a file to write settings to, and one to read them from.</summary>
    public Func<string, Task<string?>>? BrowseSaveFile { get; set; }
    public Func<Task<string?>>? BrowseOpenFile { get; set; }

    private void LoadAiInstances()
    {
        AgentInstances.Clear();
        foreach (var instance in _settingsService.Settings.AiAgentInstances)
            AgentInstances.Add(new AiAgentInstanceViewModel(instance, _settingsService.Settings));

        ProviderInstances.Clear();
        foreach (var instance in _settingsService.Settings.AiProviderInstances)
            ProviderInstances.Add(new AiProviderInstanceViewModel(instance));

        SignIns.Clear();
        foreach (var signIn in _settingsService.Settings.AiSignIns)
            SignIns.Add(new AiSignInViewModel(signIn));
        OnPropertyChanged(nameof(ShowsSignIns));
        // Both read SignInAgentNames, which goes through AiAgentCatalog.Locate and its thirty-second
        // cache: installing a CLI with Settings open left Add hidden until the dialog was reopened.
        OnPropertyChanged(nameof(CanAddSignIn));

        OnPropertyChanged(nameof(HasNoProviderInstances));
        OnPropertyChanged(nameof(HasNoSignIns));
    }

    /// <summary>Whether anything has been set up at all — an empty list is an empty state, not a list of
    /// nothing.</summary>
    public bool HasNoProviderInstances => ProviderInstances.Count == 0;

    /// <summary>Whether a second login has ever been set up. Empty is the ordinary state: one account
    /// per CLI is what everybody starts with.</summary>
    public bool HasNoSignIns => SignIns.Count == 0;

    // ─────────────────────────── Agent instances ───────────────────────────

    [ObservableProperty] private bool _isEditingAgentInstance;
    [ObservableProperty] private string _editAgentName = "";
    [ObservableProperty] private string _editAgentAgentName = "";
    [ObservableProperty] private AccountChoice? _editAgentAccount = AccountChoice.Default;
    [ObservableProperty] private string _editAgentModel = "";
    [ObservableProperty] private string _editAgentFastModel = "";
    [ObservableProperty] private string _editAgentAutoCompact = "";
    [ObservableProperty] private string _editAgentMaxContext = "";
    [ObservableProperty] private string _editAgentBehaviour = "";
    [ObservableProperty] private string _editAgentEffort = "";
    [ObservableProperty] private string _editAgentExtraArgs = "";
    private AiAgentInstance? _editingAgentInstance;

    /// <summary>Whether the agent form can be saved: it needs a name, for the reason the provider
    /// form does - nothing else identifies the row, and nothing can invent one. A typed auto-compact
    /// window or context window has to be a number, or Save says nothing about why it did nothing.</summary>
    public bool CanSaveAgentInstance =>
        EditAgentName.Trim().Length > 0
        && EditAgentAutoCompactIsValid && EditAgentMaxContextIsValid;

    /// <summary>Whether the auto-compact field is either empty or a positive token count.</summary>
    public bool EditAgentAutoCompactIsValid =>
        EditAgentAutoCompact.Trim().Length == 0 || ParseTokens(EditAgentAutoCompact) is not null;

    /// <summary>Whether the max-context field is either empty or a positive token count.</summary>
    public bool EditAgentMaxContextIsValid =>
        EditAgentMaxContext.Trim().Length == 0 || ParseTokens(EditAgentMaxContext) is not null;

    private static long? ParseTokens(string text) =>
        long.TryParse(text.Trim(), out var value) && value > 0 ? value : null;

    partial void OnEditAgentNameChanged(string value) =>
        OnPropertyChanged(nameof(CanSaveAgentInstance));

    partial void OnEditAgentMaxContextChanged(string value) =>
        OnPropertyChanged(nameof(CanSaveAgentInstance));

    partial void OnEditAgentAutoCompactChanged(string value) =>
        OnPropertyChanged(nameof(CanSaveAgentInstance));

    /// <summary>The agents that can be named, by display name — the same spelling the row shows.</summary>
    public static IReadOnlyList<string> AgentNames { get; } =
        [.. AiAgentCatalog.All.Select(agent => agent.DisplayName)];

    /// <summary>The behaviours this instance can actually be set to.</summary>
    /// <remarks>The agent's own list for an interactive tile — not the Goal tile's reduced one, because
    /// an instance's default applies to an agent tile too, where there <em>is</em> somebody to ask, and
    /// not the whole vocabulary either. Offering a mode the agent has no gate for is a row promising a
    /// restriction that does not exist: <c>plan</c> on pi, which has no permission gate at all, is
    /// stored, rounded away by <see cref="AiBehaviours.RoundDown"/> and the agent runs unrestricted
    /// regardless.</remarks>
    public ObservableCollection<string> BehaviourLabels { get; } = [.. AiBehaviours.Labels];

    /// <summary>The effort levels this instance can actually be set to.</summary>
    /// <remarks>The agent's own list, narrowed by what the chosen model says it accepts
    /// (<see cref="AiProviderCatalog.NarrowEfforts"/>). A level the model refuses is a run that fails
    /// on a flag the user never typed, and silence from the provider narrows nothing — which is why the
    /// list is usually the whole scale.</remarks>
    public ObservableCollection<string> EffortLabels { get; } = [.. AiEfforts.Labels];

    /// <summary>What the provider last said it serves, kept whole — the ids fill the model field's
    /// suggestions and the efforts narrow the chooser above.</summary>
    private IReadOnlyList<AiModelInfo> _agentModels = [];

    /// <summary>The agent the form names, or null while it names one this build does not have.</summary>
    private IAiAgent? AgentBeingEdited =>
        AiAgentCatalog.All.FirstOrDefault(agent => agent.DisplayName == EditAgentAgentName);

    private void RefreshBehaviourLabels()
    {
        var allowed = AgentBeingEdited is { } agent
            ? agent.SupportedBehaviours(
                _editingAgentInstance ?? AiAgentCatalog.SeedInstanceFor(agent), AiUsage.Interactive)
            : AiBehaviours.All;

        BehaviourLabels.Clear();
        foreach (var behaviour in AiBehaviours.All.Where(allowed.Contains))
            BehaviourLabels.Add(AiBehaviours.Label(behaviour));

        // A stored mode this agent has no gate for falls to the tool's own default rather than staying
        // selected in a chooser that no longer offers it — the same rule the effort chooser follows.
        if (!BehaviourLabels.Contains(EditAgentBehaviour))
            EditAgentBehaviour = AiBehaviours.Label(AiBehaviour.ToolDefault);
    }

    private void RefreshEffortLabels()
    {
        var agentEfforts = AgentBeingEdited is { } agent
            ? agent.SupportedEfforts(
                _editingAgentInstance ?? AiAgentCatalog.SeedInstanceFor(agent), AiUsage.Interactive)
            : AiEfforts.All;

        var model = _agentModels.FirstOrDefault(info => info.Id == EditAgentModel.Trim());
        var allowed = AiProviderCatalog.NarrowEfforts(agentEfforts, model?.SupportedEfforts);

        EffortLabels.Clear();
        foreach (var effort in allowed)
            EffortLabels.Add(AiEfforts.Label(effort));

        // A stored level the agent or the model no longer accepts falls to the tool's own default
        // rather than staying selected in a chooser that no longer offers it.
        if (!EffortLabels.Contains(EditAgentEffort))
            EditAgentEffort = AiEfforts.Label(AiEffort.ToolDefault);
    }

    partial void OnEditAgentAgentNameChanged(string value)
    {
        RefreshAccountChoices();
        RefreshBehaviourLabels();
        RefreshEffortLabels();
        OnPropertyChanged(nameof(ModelHint));
        OnPropertyChanged(nameof(ShowsAutoCompact));
        OnPropertyChanged(nameof(ShowsMaxContext));
        OnPropertyChanged(nameof(ShowsFastModel));
        OnPropertyChanged(nameof(FastModelHint));
        _ = UpdateModelContextAsync(fastModel: false);
        _ = UpdateModelContextAsync(fastModel: true);
    }

    /// <summary>Whether the Auto-compact field is shown: Claude Code's alone.</summary>
    /// <remarks>The field is the manual <c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c>, and only Claude Code
    /// reads that — on the other five it would save and do nothing, so it is hidden rather than
    /// offered. <b>The context readouts under the model fields are a different question and are shown
    /// for every agent</b> (<see cref="HasModelContext"/>): a model's window is a fact about the model
    /// whatever reads it, and Claude Code is only the one agent that spends it on an environment
    /// variable. This remark is not that rule — the two used to be written as one, and a reader
    /// trusting the sentence over the XAML would have hidden the readouts everywhere.</remarks>
    public bool ShowsAutoCompact => AgentBeingEdited is { UsesModelContextWindow: true };

    /// <summary>Whether the Max-context field is shown: the same gate as Auto-compact, because it is
    /// the same CLI that reads the variable it names.</summary>
    public bool ShowsMaxContext => AgentBeingEdited is { UsesModelContextWindow: true };

    /// <summary>Whether the agent being edited has a slot for the small, frequent calls, and this
    /// launch would read it.</summary>
    /// <remarks>codex, pi and agy answer their small calls with the main model or their own pick and
    /// offer no setting for one — measured 2026-08-31 in each binary and its documentation — so the
    /// form hides the field on them rather than showing one that would save and do nothing, the rule
    /// <see cref="ShowsAutoCompact"/> already applies.
    /// <para><b>And the account is half of the question.</b> opencode's slot lives in the generated
    /// provider document, which is written only where an endpoint is declared — a local server, or a
    /// hosted provider given an address of its own. On a hosted provider at its published address a
    /// typed fast model would save and do nothing, so the field stands down there too
    /// (<see cref="IAiAgent.FastModelNeedsDeclaredEndpoint"/> is the agent's own answer to which slots
    /// work that way). The two that reach it name their fallbacks differently, which is what
    /// <see cref="FastModelHint"/> says.</para></remarks>
    public bool ShowsFastModel =>
        AgentBeingEdited is { UsesFastModel: true } agent
        && (!agent.FastModelNeedsDeclaredEndpoint || TheAccountDeclaresAnEndpoint);

    /// <summary>Whether the chosen account is one a provider document is written for — a local server,
    /// or a hosted provider the user has given an address of its own.</summary>
    /// <remarks>The one rule <c>AgentRuntime.NeedsDeclaredEndpoint</c> asks about a launch, asked here
    /// through the same <see cref="AgentRuntime.DeclaresEndpoint"/> about the account the form holds —
    /// so a third case added there is added here, and the field cannot stay visible over an account
    /// the launch stopped writing for.</remarks>
    private bool TheAccountDeclaresAnEndpoint =>
        EditAgentAccount is { Kind: AccountKind.Provider } account
        && ProviderInstances.FirstOrDefault(p => p.Instance.Id == account.Id) is { } row
        && AgentRuntime.DeclaresEndpoint(row.Provider, row.Instance);

    /// <summary>What the fast-model field asks for, which is not the same question for the two agents
    /// that have one — nor the same answer for every account.</summary>
    /// <remarks>Claude Code's empty field follows the Model field <em>where a provider is chosen</em>;
    /// on its own account or a subscription the CLI's own small model exists and answers, and the
    /// fallback deliberately does not run there. opencode's own slot falls back to a cheap model picked
    /// from the provider's catalogue — and takes <b>the bare model id</b>, because the generated
    /// document writes the <c>provider/</c> prefix itself (<c>OpenCodeProviderConfig.Document</c>); a
    /// hint asking for <c>provider/model</c> here would be followed to the letter into a slot opencode
    /// then silently discards. The sibling Model field is the opposite case, and its own hint says so:
    /// there the qualifier is written by the user because nothing writes it for them. Only these two
    /// agents reach the field — the others are hidden outright by <see cref="ShowsFastModel"/>.</remarks>
    public string FastModelHint =>
        AgentBeingEdited is not { UsesFastModel: true } agent
            ? ""
            : agent.FastModelNeedsDeclaredEndpoint
                ? "the model's own name — the provider is added for you — empty: opencode's own cheap pick"
                : EditAgentAccount is { Kind: AccountKind.Provider }
                    ? "for the small, frequent calls — empty: same as Model"
                    : "for the small, frequent calls — empty: the CLI's own small model";

    /// <summary>What the model field asks for, which is not the same question for every agent.</summary>
    /// <remarks>opencode and pi are told which service to use by the model's <em>name</em>, so on an
    /// instance with no provider to prefix it with — a sign-in, or the CLI's own account — a bare id is
    /// refused before a request is made. One placeholder for both cases said "empty = the agent's own
    /// choice" and left the qualified form as something the user had to know already, on the very
    /// account the Sign-ins section has just started encouraging.</remarks>
    public string ModelHint =>
        AgentBeingEdited is { NamesProviderInModel: true }
        && EditAgentAccount is not { Kind: AccountKind.Provider }
            ? "provider/model, e.g. openrouter/z-ai/glm-5.3-flash"
            : "empty = the agent's own choice";

    /// <summary>The warning under an empty model field, or empty while there is nothing to warn about.
    /// </summary>
    /// <remarks>An API key points the CLI at a service that serves <em>your</em> catalogue, and an
    /// empty model field hands the launch no model at all — so the CLI asks for its own default, which
    /// the provider may not serve (a launch that succeeds and a run that fails) or may serve at a
    /// price nobody chose. On a sign-in the CLI's default is the account's own answer, so there is
    /// nothing to warn about. Read beside <see cref="ModelContextDisplay"/>, which fills the same slot
    /// the moment a model is named: the two are never shown together.</remarks>
    public string ModelEmptyWarning =>
        EditAgentAccount is { Kind: AccountKind.Provider }
        && EditAgentModel.Trim().Length == 0
            ? "No model set: the CLI's own default model will be requested from this provider — "
              + "it may not be served there, and it may cost more."
            : "";

    public bool HasModelEmptyWarning => ModelEmptyWarning.Length > 0;

    private void NotifyModelEmptyWarning()
    {
        OnPropertyChanged(nameof(ModelEmptyWarning));
        OnPropertyChanged(nameof(HasModelEmptyWarning));
    }

    partial void OnEditAgentModelChanged(string value)
    {
        RefreshEffortLabels();
        NotifyModelEmptyWarning();
        _ = UpdateModelContextAsync(fastModel: false);
    }

    partial void OnEditAgentFastModelChanged(string value) =>
        _ = UpdateModelContextAsync(fastModel: true);

    // ─── Context readouts and the auto-compact fallback ───

    /// <summary>What the model field holds, said in tokens, or empty while nothing is known.</summary>
    /// <remarks>Shown under the field the moment the model is one the provider describes: it is the
    /// number the empty auto-compact field would be worked out from, and seeing it is what makes the
    /// fallback legible instead of a promise.</remarks>
    public string ModelContextDisplay => _modelContextDisplay;
    private string _modelContextDisplay = "";

    /// <summary>The same answer for the fast model, which is asked separately and may be a different
    /// model with a different window.</summary>
    public string FastModelContextDisplay => _fastModelContextDisplay;
    private string _fastModelContextDisplay = "";

    public bool HasModelContext => ModelContextDisplay.Length > 0;
    public bool HasFastModelContext => FastModelContextDisplay.Length > 0;

    /// <summary>Cancels each field's lookup in flight, so a late answer cannot land on a form that has
    /// moved on to another instance.</summary>
    private CancellationTokenSource? _modelContextCts;
    private CancellationTokenSource? _fastModelContextCts;

    /// <summary>
    /// Says what context window the model in one of the two fields has, as far as anybody can say.
    /// </summary>
    /// <remarks><para>Three answers, in order. The list already fetched for the account
    /// (<see cref="FetchAgentModelsAsync"/>) carries the window for OpenRouter and LM Studio, and
    /// answering from it is free and instant. A model the list does not describe — Ollama's listing
    /// names models and says nothing else — is asked directly, once, debounced: this fires on every
    /// keystroke of an <c>AutoCompleteBox</c>, and a request per keystroke against somebody's server
    /// is not how the answer is owed. Nobody at all — no provider chosen, a provider that will not
    /// say — leaves the readout empty rather than guessing.</para>
    /// <para>Not awaited: it runs from a property setter on the UI thread, and everything below
    /// already answers rather than throws.</para></remarks>
    private async Task UpdateModelContextAsync(bool fastModel)
    {
        // Cancelled first, before any early return below. Every path here — a cleared field, a hidden
        // fast field, an answer straight off the model list — replaces the lookup in flight, because
        // the answers race: a lookup started for one model answering after the field moved on would
        // otherwise pass the cancellation test and write its number under a field that no longer asks
        // for it. The exchange is on the field for this form, so opening another instance's form also
        // cancels what this one has in the air.
        var source = new CancellationTokenSource();
        var field = fastModel ? ref _fastModelContextCts : ref _modelContextCts;
        var previous = Interlocked.Exchange(ref field, source);
        previous?.Cancel();

        var model = (fastModel ? EditAgentFastModel : EditAgentModel).Trim();
        var property = fastModel ? nameof(FastModelContextDisplay) : nameof(ModelContextDisplay);
        var hasProperty = fastModel ? nameof(HasFastModelContext) : nameof(HasModelContext);

        void Display(string text)
        {
            if (fastModel) _fastModelContextDisplay = text; else _modelContextDisplay = text;
            OnPropertyChanged(property);
            OnPropertyChanged(hasProperty);
        }

        Display("");

        // The fast field is hidden on the agents that have no slot for one (ShowsFastModel), and a
        // context readout under a field that is not there is a number with no question. Cleared first,
        // so switching agents cannot leave the previous form's answer behind it.
        if (fastModel && !ShowsFastModel) return;

        if (model.Length == 0) return;

        // Free first: whatever the account's model list already says, said now.
        var listed = _agentModels.FirstOrDefault(info =>
            string.Equals(info.Id, model, StringComparison.OrdinalIgnoreCase))?.ContextWindowTokens;
        if (listed is { } known)
        {
            Display(ContextSentence(known));
            return;
        }

        var configured = EditAgentAccount is { Kind: AccountKind.Provider } account
            ? ProviderInstances.FirstOrDefault(p => p.Instance.Id == account.Id)
            : null;
        if (configured?.Provider is not { } provider)
            return;

        try
        {
            // The debounce: one lookup for the model the field settles on, not one per keystroke.
            await Task.Delay(400, source.Token);

            var tokens = await provider.ContextWindowAsync(configured.Instance, model, source.Token);
            if (source.Token.IsCancellationRequested) return;

            Display(tokens is { } answer ? ContextSentence(answer) : "");
        }
        catch (OperationCanceledException) when (source.Token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string ContextSentence(long tokens) =>
        $"{tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens context";

    /// <summary>Cancels the model fetch in flight, so a late answer cannot land on a form that has
    /// moved on.</summary>
    /// <remarks>The same pattern as <c>_gitDetectCts</c>, and needed for the same reason once the fetch
    /// stopped being a button press: two quick changes of account, or closing the form mid-flight, let
    /// the slower reply overwrite <c>ModelSuggestions</c> for an account nobody has selected — and
    /// <c>RefreshEffortLabels</c> would then narrow the effort levels by a stranger's model list. With
    /// the List button the timing belonged to the user; now it belongs here.</remarks>
    private CancellationTokenSource? _agentModelsCts;

    /// <summary>
    /// Choosing an account fetches what it serves.
    /// </summary>
    /// <remarks><b>This is what the List button was for, and why there is no longer one.</b> A button
    /// that has to be pressed before a field can suggest anything is a step the user has to know about;
    /// worse, an <c>AutoCompleteBox</c> gives no sign that a list exists, so an unpressed List looked
    /// exactly like a provider with nothing to offer. Fetching on the one event that changes the answer
    /// — which account this runs as — costs one call per choice and makes the field simply work.
    /// <para>Not awaited, and it must not be: this runs from a property setter on the UI thread while a
    /// form is being opened. The call is already failure-tolerant (an unreachable provider answers with
    /// an empty list), so the worst case is the field suggesting nothing, which is where it started.
    /// </para></remarks>
    partial void OnEditAgentAccountChanged(AccountChoice? value)
    {
        // The hints depend on it too: with a provider chosen the qualifier is written for the user
        // (AiAgent.WithProviderPrefix), and asking them for it as well would be the form contradicting
        // what the launch does — and the fast-model field's visibility is an account question for the
        // agent whose slot lives in a generated document.
        OnPropertyChanged(nameof(ModelHint));
        OnPropertyChanged(nameof(FastModelHint));
        OnPropertyChanged(nameof(ShowsFastModel));
        NotifyModelEmptyWarning();
        _ = LoadAgentModelsAsync();
    }

    /// <summary>What an instance's provider may be: the configured ones, plus the agent's own account.
    /// </summary>
    /// <remarks>Rebuilt whenever a form opens rather than held, because the provider list is edited on
    /// the same page and a stale choice here is a launch that finds no provider.</remarks>
    public ObservableCollection<AccountChoice> AccountChoices { get; } = [];

    /// <summary>
    /// Rebuilds the chooser for the agent the form currently names, leaving out every provider that
    /// agent cannot speak to.
    /// </summary>
    /// <remarks>A pairing that cannot work must not be offered: stored, it makes the instance
    /// unavailable everywhere (<c>AiAgentCatalog.IsAvailable</c>) — gone from the Agent tile's chooser
    /// and from the Goal tile's list — with nothing on screen saying why. Depends on the agent, so it
    /// is rebuilt when the agent changes and not only when the form opens.</remarks>
    private void RefreshAccountChoices(string? restore = null)
    {
        var agent = AgentBeingEdited;
        // The caller's answer where it has one: opening a form knows which account the instance names,
        // and without it the rebuild guesses from the *previous* form's selection - so the account the
        // instance actually stores was assigned a second time, and a form opened on one instance could
        // sit for a moment showing another's account. (It does not make the open a single fetch:
        // assigning the agent name raises its own rebuild first, and that one's fetch is cancelled by
        // this one. Cheap, and the cancellation is what makes it safe.)
        var chosen = restore ?? EditAgentAccount?.Id;

        AccountChoices.Clear();
        AccountChoices.Add(AccountChoice.Default);

        // The subscriptions first, because they are the answer that needs nothing typed. Only this
        // agent's: a Claude Code login means nothing to codex, which keeps its credentials in another
        // file under another variable, so offering one across would be a pairing that cannot work.
        // Reads the CLI's own files, on this thread, once per sign-in per rebuild. The read is walked,
        // not parsed into a DOM (see ReadJsonString): .claude.json carries a per-project history that
        // grows into the megabytes, and it is read here for every row on every rebuild. What would
        // still have to change for a heavier read is not this line but the page's loading model, since
        // a row would then need a "not yet known" state.
        if (agent is { SupportsSignIns: true })
        {
            foreach (var signIn in AiSignInStore.For(_settingsService.Settings, agent.Id))
                AccountChoices.Add(AccountChoice.For(signIn,
                    agent.ReadSignIn(AiSignInStore.DirectoryFor(signIn))));
        }

        foreach (var provider in ProviderInstances)
        {
            // A provider kind this build does not have cannot be judged, so it is still offered — the
            // alternative is hiding a row the user configured on the strength of not recognising it.
            //
            // Everything else is judged by the one rule, not by half of it: compatibility alone left
            // pi + LM Studio in the list, and an instance saved from it was refused the moment it
            // existed. "The chooser hides and the row explains" only holds while the chooser hides on
            // the same sentence the row shows.
            //
            // The instance goes with it, because "can this agent be pointed there" depends on the
            // address typed into this one: a hosted provider given a gateway needs what a local server
            // needs. Without it the chooser offered pi + OpenRouter-via-a-gateway and the row saved
            // from it was UNAVAILABLE the moment it existed.
            if (agent is not null && provider.Provider is { } kind
                && !AgentAvailability.CanPair(agent, kind, provider.Instance))
                continue;

            AccountChoices.Add(AccountChoice.For(provider.Instance, provider.Name));
        }

        // What the instance stores, when the list above has nothing for it: the account is deleted,
        // belongs to another tool, or is one this agent cannot be pointed at. Added rather than
        // silently dropped, because the chooser lists what can be chosen *now* while the instance
        // records what was chosen *then* - and this is exactly the instance whose row carries the
        // UNAVAILABLE chip explaining it. Falling back to Default meant opening that row's own form and
        // saving it - a rename is enough - replaced the configuration with "the agent's own account",
        // which is both a different subscription and the end of the evidence.
        // Only for `restore`, which is what the *instance* stores, and never for a selection carried
        // over from the field: changing the agent to one that cannot use the account on screen is
        // somebody making a new pairing, and there the choice is rightly dropped rather than kept as a
        // combination that cannot work.
        if (restore is { Length: > 0 } && AccountChoices.All(choice => choice.Id != restore))
            AccountChoices.Add(StoredButUnusable(restore));

        // Rebuilding the list clears the combo's SelectedItem and the binding writes that null back
        // here, so the selection has to be restored either way - not only when it was lost. Restored
        // *first*: an account that is still on the list is still the answer, and without this line
        // changing the agent between two services that both work emptied the field and the next Save
        // stored "the agent's own account" instead.
        EditAgentAccount = chosen is null
            ? AccountChoice.Default
            : AccountChoices.FirstOrDefault(choice => choice.Id == chosen) ?? AccountChoice.Default;

        OnPropertyChanged(nameof(HasNoAccountToChoose));
        OnPropertyChanged(nameof(NoAccountNote));
    }

    /// <summary>
    /// The entry standing for an account that is stored and cannot be offered.
    /// </summary>
    /// <remarks>Which kind it is comes from the instance being edited and not from a lookup, because
    /// the commonest case is the one where the lookup finds nothing: a sign-in that has been removed.
    /// The name is filled in where the account still exists — a provider this agent cannot speak to, a
    /// sign-in belonging to another tool — so the row says which one it is holding.</remarks>
    private AccountChoice StoredButUnusable(string id)
    {
        var settings = _settingsService.Settings;

        if (_editingAgentInstance is { } instance && instance.SignInId == id)
            return AccountChoice.Unusable(AccountKind.SignIn, id, AiSignInStore.Find(settings, id)?.Name);

        return AccountChoice.Unusable(AccountKind.Provider, id,
            AiProviderCatalog.FindInstance(settings, id)?.Name);
    }

    /// <summary>
    /// Whether this agent has nothing to authenticate as but the account it is already logged into.
    /// </summary>
    /// <remarks><b>A chooser with one entry is a question the user cannot answer</b>, and here it looks
    /// exactly like a chooser whose other entries are missing for a reason nobody can see: an agent is
    /// only offered the providers whose API it can actually speak, so a configured OpenRouter key is
    /// absent from a local-only agent's list and a configured LM Studio is absent from Claude Code's.
    /// The row that could say so is this one — the same argument as the instance row's INCOMPATIBLE
    /// chip, one level earlier.</remarks>
    public bool HasNoAccountToChoose => AccountChoices.Count <= 1;

    /// <summary>What to do about it, in the words of the section that fixes it.</summary>
    public string NoAccountNote =>
        AgentBeingEdited is { } agent
            ? $"Only {agent.DisplayName}'s own account. Add an API key under Providers"
              + (agent.SupportsSignIns ? ", or a second subscription under Sign-ins" : "")
              + $" — {agent.DisplayName} is offered the ones whose API it can speak."
            : "";

    /// <summary>Which account id this instance stores — a sign-in or a provider, never both.</summary>
    /// <remarks>Handed to <c>RefreshAccountChoices</c> so the rebuild selects it directly. The chooser
    /// is keyed by id and never by label, because nothing makes an account's name unique.</remarks>
    private static string StoredAccountId(AiAgentInstance instance) =>
        instance.SignInId.Length > 0 ? instance.SignInId : instance.ApiAccountId;

    /// <summary>
    /// A new agent instance, unnamed.
    /// </summary>
    /// <remarks><b>Seeded with the agent's own display name it was a name nobody chose</b>, and one
    /// that stopped being true the moment Agent or Account changed — nothing rewrites a field the user
    /// may have typed in, and nothing can tell a default from a deliberate answer that matches it. The
    /// name is what the tile chooser and the Goal tile's list identify the row by, and a second
    /// instance of the same agent arrived spelled identically to the first. The seeded per-agent
    /// instances keep their names: those are rows nobody was asked about, and a blank one would be
    /// worse than an obvious one.</remarks>
    [RelayCommand]
    private void AddAgentInstance() =>
        BeginAgentEditing(new AiAgentInstance { AgentId = AiAgentCatalog.All[0].Id });

    [RelayCommand]
    private void EditAgentInstance(AiAgentInstanceViewModel row) => BeginAgentEditing(row.Instance);

    private void BeginAgentEditing(AiAgentInstance instance)
    {
        // First, and the order is load-bearing in both directions. It has to run - without it
        // IsEditingAgentInstance never becomes true and the overlay never appears - and it has to run
        // *before* the instance is remembered, because putting the other forms down also leaves this
        // one, and leaving it clears the instance and cancels the model fetch that filling the form
        // below is about to start.
        BeginEditing(ref _isEditingAgentInstance);

        _editingAgentInstance = instance;
        _agentModels = [];
        // And the suggestions with them: the fetch only runs when an account is chosen, so a form
        // opened on "the agent's own account" was completing against the catalogue left by whichever
        // form was open before it.
        ModelSuggestions.Clear();

        EditAgentName = instance.Name;

        // Three steps and the order is the whole of it. The agent first, because which accounts exist
        // is a question about *this* agent; then the list, unconditionally, because assigning the same
        // agent name as last time raises no change and would leave the previous form's list in place;
        // then the selection, because rebuilding the list clears the combo's SelectedItem and writes
        // that null straight back here. Built first and selected second, the form opened on an account
        // it then immediately forgot, and showed an empty box over a list that had the right entry in
        // it.
        EditAgentAgentName = AiAgentCatalog.Find(instance.AgentId)?.DisplayName ?? AgentNames[0];
        RefreshAccountChoices(StoredAccountId(instance));
        EditAgentModel = instance.Model;
        EditAgentFastModel = instance.FastModel;
        EditAgentBehaviour = AiBehaviours.Label(instance.DefaultBehaviour);
        EditAgentEffort = AiEfforts.Label(instance.DefaultEffort);
        RefreshBehaviourLabels();
        RefreshEffortLabels();
        // One per line, because an argument may contain spaces and splitting on them is how a path with
        // one in it becomes two arguments neither of which exists.
        EditAgentExtraArgs = string.Join('\n', instance.ExtraArgs);
        EditAgentAutoCompact = instance.AutoCompactWindow is { } window
            ? window.ToString(CultureInfo.InvariantCulture)
            : "";
        EditAgentMaxContext = instance.MaxContextTokens is { } context
            ? context.ToString(CultureInfo.InvariantCulture)
            : "";

        // Asked for outright, and unconditionally - the same rule the model fetch below is given, for
        // the same reason. Assigning the same model text as the last form's raises no change and so
        // starts no lookup, and both readouts would show whatever that form's model had answered.
        _ = UpdateModelContextAsync(fastModel: false);
        _ = UpdateModelContextAsync(fastModel: true);

        // Asked for outright, and unconditionally - the same rule the agent step above is given, for
        // the same reason. AccountChoice is a record, so RefreshAccountChoices assigning the account
        // this instance already names raises no change and starts no fetch, while ModelSuggestions has
        // just been cleared: the model field then completes against nothing, and the List button that
        // used to be the manual way back is gone. It appeared to work only because clearing the list
        // makes the combo write a null back through the binding - the trap GoalTileViewModel documents
        // - so the feature depended on a control being on screen, and every test of this page runs
        // without one.
        _ = LoadAgentModelsAsync();
    }

    [RelayCommand]
    private async Task SaveAgentInstanceAsync()
    {
        if (_editingAgentInstance is not { } instance || !CanSaveAgentInstance) return;

        var behaviour = AiBehaviours.FromLabel(EditAgentBehaviour);

        // Asked once, before it is stored, and for the same reason the Goal tile asks: this is the
        // largest single grant on the page — it applies wherever the instance is used — and the first
        // place it is noticed is a run that has already happened. No dialog means no.
        if (behaviour == AiBehaviour.BypassPermissions
            && instance.DefaultBehaviour != AiBehaviour.BypassPermissions)
        {
            var agreed = ConfirmAction != null && await ConfirmAction(
                "Run this agent with no permission checks at all?\n\n"
                + "It will edit, create and delete files and run commands without asking, in every "
                + "tile and every goal that uses this instance.");

            if (!agreed) return;
        }

        instance.Name = EditAgentName.Trim();
        instance.AgentId = AiAgentCatalog.All
            .FirstOrDefault(agent => agent.DisplayName == EditAgentAgentName)?.Id ?? instance.AgentId;
        // Exactly one of the two, always: they are one chooser, and an instance carrying both would
        // point the CLI at a subscription's directory and hand it a provider's key at the same time.
        var account = EditAgentAccount ?? AccountChoice.Default;
        instance.ApiAccountId = account.Kind == AccountKind.Provider ? account.Id : "";
        instance.SignInId = account.Kind == AccountKind.SignIn ? account.Id : "";
        instance.Model = EditAgentModel.Trim();
        instance.FastModel = EditAgentFastModel.Trim();
        instance.DefaultBehaviour = behaviour;
        instance.DefaultEffort = AiEfforts.FromLabel(EditAgentEffort);
        instance.ExtraArgs = [.. EditAgentExtraArgs
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        // Empty is the fallback, not a zero: the launch works the window out from the model's context.
        instance.AutoCompactWindow = ParseTokens(EditAgentAutoCompact);
        instance.MaxContextTokens = ParseTokens(EditAgentMaxContext);

        var list = _settingsService.Settings.AiAgentInstances;
        if (!list.Contains(instance)) list.Add(instance);

        _settingsService.NotifyChanged();
        CloseAgentForm();
        LoadAiInstances();
    }

    [RelayCommand]
    private void CancelEditAgentInstance() => CloseAgentForm();

    /// <summary>
    /// Everything that has to stop when the agent form is left, by whichever route.
    /// </summary>
    /// <remarks>The promise made where <c>_agentModelsCts</c> is declared. Separate from
    /// <see cref="CloseAgentForm"/> because there are two ways to leave — cancelling it, and opening a
    /// different form over it — and only the first went through that method.</remarks>
    private void LeaveAgentForm()
    {
        Interlocked.Exchange(ref _agentModelsCts, null)?.Cancel();
        Interlocked.Exchange(ref _modelContextCts, null)?.Cancel();
        Interlocked.Exchange(ref _fastModelContextCts, null)?.Cancel();
        _editingAgentInstance = null;
    }

    private void CloseAgentForm()
    {
        LeaveAgentForm();

        IsEditingAgentInstance = false;
        OnPropertyChanged(nameof(IsEditingAnything));
    }

    [RelayCommand]
    private async Task DeleteAgentInstanceAsync(AiAgentInstanceViewModel row)
    {
        // An unwired dialog says no, as everywhere on this dialog: a tile pointed at this instance
        // keeps working on the agent's own configuration, which is a change the user cannot see from
        // here and would not have chosen.
        var agreed = ConfirmAction != null
                     && await ConfirmAction($"Delete the agent instance \"{row.Name}\"?");
        if (!agreed) return;

        _settingsService.Settings.AiAgentInstances.Remove(row.Instance);
        _settingsService.NotifyChanged();
        LoadAiInstances();
    }

    /// <summary>
    /// Shows what installing an agent would run, and runs it where it can be watched.
    /// </summary>
    /// <remarks>The plan is in the question, not beside it: an install writes outside every directory
    /// this application owns and sometimes asks for elevation, so agreeing to it has to mean agreeing to
    /// a command the user has read.</remarks>
    [RelayCommand]
    private async Task InstallAgentAsync(AiAgentInstanceViewModel row)
    {
        if (row.InstallPlan is not { } plan) return;

        if (!await ConfirmedAsync($"Install {row.AgentName}?", plan)) return;

        if (RunInstallPlan is not { } run || !await run(plan))
        {
            await ShowProblemAsync("Install",
                $"Open a workspace first — the command runs in a tile there.\n\n{plan.CommandLine}");
        }
    }

    /// <summary>Opens the agent's own page in the browser.</summary>
    [RelayCommand]
    private void OpenAgentUrl(AiAgentInstanceViewModel row)
    {
        if (row.InstallUrl is not { } url) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // ─────────────────────────── Sign-ins ───────────────────────────

    /// <summary>The agents that can hold more than one login, for the New sign-in form's chooser.
    /// </summary>
    /// <remarks><para>Measured, not assumed — <c>IAiAgent.SupportsSignIns</c>. One of the five answers
    /// no, and offering a row the user could name, log into and never actually run as would be a second
    /// account that silently is the first.</para>
    /// <para><b>Installed as well as capable.</b> It was capable alone, so the section appeared on a
    /// machine with none of these CLIs on it — offering to set up a second login for a tool that is not
    /// there, and contradicting the sentence in <c>CLAUDE.md</c> that says it hides itself. Located the
    /// same way the tile chooser locates one, cache and all.</para></remarks>
    public static IReadOnlyList<string> SignInAgentNames =>
        [.. AiAgentCatalog.All
            .Where(agent => agent.SupportsSignIns && AiAgentCatalog.Locate(agent) is not null)
            .Select(agent => agent.DisplayName)];

    public bool CanAddSignIn => SignInAgentNames.Count > 0;

    /// <summary>
    /// The same list, plus the tool the sign-in being edited is already for.
    /// </summary>
    /// <remarks><para>A CLI that has dropped off <c>PATH</c> is filtered out of
    /// <see cref="SignInAgentNames"/> — which is right for adding one, and wrong for a row that
    /// exists: its selection was then not in the list, so the combo showed the Tool field empty and the
    /// sign-in read as unassigned. The same reason its rows stay reachable at all
    /// (<see cref="ShowsSignIns"/>).</para>
    /// <para><b>Held rather than computed from the current selection</b>, which is the ordering rule
    /// <c>RefreshAccountChoices</c> and <c>GoalTileViewModel</c> already follow: rebuilding a combo's
    /// <c>ItemsSource</c> clears its <c>SelectedItem</c> and the binding writes that null straight
    /// back. Derived from the selection, raising it after the tool was chosen emptied the field again —
    /// and an empty Tool saved an empty <c>AgentId</c>, which no form could then put right.</para>
    /// </remarks>
    public IReadOnlyList<string> SignInAgentChoices => _signInAgentChoices;

    private IReadOnlyList<string> _signInAgentChoices = [];

    /// <summary>Whether the Sign-ins section is shown at all.</summary>
    /// <remarks><b>Not the same question as whether one can be added.</b> Tying the section to that
    /// alone meant a CLI dropping off <c>PATH</c> — a reinstall, another shell profile — took the
    /// existing rows with it: they could not be renamed or removed, while <c>AgentAvailability</c> went
    /// on judging instances by them. A row that exists is a row the user must be able to reach.
    /// </remarks>
    public bool ShowsSignIns => CanAddSignIn || SignIns.Count > 0;

    [ObservableProperty] private bool _isEditingSignIn;
    [ObservableProperty] private string _editSignInName = "";
    /// <summary>
    /// Which tool the sign-in is for, by display name.
    /// </summary>
    /// <remarks>Written by hand because a <c>ComboBox</c> writes <c>null</c> into
    /// <c>SelectedItem</c> whenever the selection is not in its list, and a generated setter would take
    /// it into a property the rest of this file treats as a string. The list is
    /// <see cref="SignInAgentChoices"/> for the other half of the same problem.</remarks>
    public string EditSignInAgentName
    {
        get => _editSignInAgentName;
        set
        {
            if (!SetProperty(ref _editSignInAgentName, value ?? "")) return;

            // Saving needs a tool as much as it needs a name, and this is the field that answers it.
            OnPropertyChanged(nameof(CanSaveSignIn));
        }
    }

    private string _editSignInAgentName = "";
    private AiSignIn? _editingSignIn;

    [RelayCommand]
    private void AddSignIn()
    {
        if (SignInAgentNames.Count == 0) return;
        BeginSignInEditing(new AiSignIn());
    }

    [RelayCommand]
    private void EditSignIn(AiSignInViewModel row) => BeginSignInEditing(row.SignIn);

    /// <summary>
    /// Whether the tool can still be chosen — only while the sign-in is new.
    /// </summary>
    /// <remarks><b>Changing it afterwards breaks two things at once.</b> The directory is derived from
    /// the sign-in's <em>id</em> and not its agent, so the credentials the old CLI wrote stay in a
    /// directory now handed to a different one; and every instance running as this sign-in becomes
    /// unavailable, because a login belongs to one tool (<c>AgentAvailability</c>). The button that
    /// opens this form is already labelled Rename, which is what the form is for.</remarks>
    public bool CanChooseSignInAgent =>
        _editingSignIn is { } signIn && !_settingsService.Settings.AiSignIns.Contains(signIn);

    private void BeginSignInEditing(AiSignIn signIn)
    {
        _editingSignIn = signIn;
        EditSignInName = signIn.Name;

        // The list first and the selection second, which is the order BeginAgentEditing spells out for
        // the same reason: the other way round the combo answers the new ItemsSource by writing null
        // over the tool that had just been chosen.
        //
        // A stored row whose agent this build does not have shows the id it stores, not the first tool
        // on the list. Substituting one there was silent - the field is disabled for a stored row, so
        // nothing on screen said the tool had changed - and Save then wrote it, which moved the
        // directory the login is in (AiSignInStore.DirectoryFor is built from the agent's id and the
        // sign-in's) and orphaned a refresh token. Renaming is described as harmless, and it was losing
        // the login. This is exactly the row AgentAvailability keeps reachable so it can be renamed or
        // removed after a Velopack rollback.
        var tool = AiAgentCatalog.Find(signIn.AgentId)?.DisplayName
                   ?? (CanChooseSignInAgent ? SignInAgentNames.FirstOrDefault() ?? "" : signIn.AgentId);
        _signInAgentChoices = tool.Length > 0 && !SignInAgentNames.Contains(tool)
            ? [tool, .. SignInAgentNames]
            : SignInAgentNames;
        OnPropertyChanged(nameof(SignInAgentChoices));

        EditSignInAgentName = tool;
        OnPropertyChanged(nameof(CanChooseSignInAgent));

        BeginEditing(ref _isEditingSignIn);
    }

    /// <summary>Whether the sign-in form can be saved: it needs a name.</summary>
    /// <remarks><para>The same rule as the agent and provider forms, and it was missed here — which
    /// showed as two unnamed sign-ins arriving in the account chooser as two identical rows. The
    /// <c>"Unnamed"</c> fallback on the row and the empty label in <c>AccountChoice.For</c> hide that
    /// rather than preventing it: a name is the only thing distinguishing two logins to the same
    /// tool.</para>
    /// <para><b>And a tool, while one can still be chosen.</b> The tool is fixed once the row
    /// exists, so one saved without it is a row that can only be deleted: no agent, no Sign in button,
    /// and a directory under a placeholder name. A blank name is at least fixable.</para>
    /// <para>Asked only <em>while</em> it can be chosen, because a stored row for an agent this build
    /// does not have has no resolvable tool and must still be renameable — that is the whole reason its
    /// row is kept reachable.</para></remarks>
    public bool CanSaveSignIn =>
        EditSignInName.Trim().Length > 0
        && (!CanChooseSignInAgent || SignInAgentBeingEdited is not null);

    /// <summary>Which tool the name in the form stands for, or null when it stands for none.</summary>
    private IAiAgent? SignInAgentBeingEdited =>
        AiAgentCatalog.All.FirstOrDefault(agent => agent.DisplayName == EditSignInAgentName);

    partial void OnEditSignInNameChanged(string value) =>
        OnPropertyChanged(nameof(CanSaveSignIn));

    /// <summary>
    /// What the tile is for, said before it opens.
    /// </summary>
    /// <remarks><b>The plan's note was being built and thrown away.</b> The route a plan takes to a tile
    /// carries the command and nothing else, so a sign-in opened a tile with the environment set, an
    /// empty prompt and no sign anywhere that the user was meant to type the tool's own login command —
    /// while the row went on saying "not signed in". The install path asks the same way, for the same
    /// reason: what the tile is about to do is in the question rather than beside it.</remarks>
    private async Task<bool> ConfirmedAsync(string title, InstallPlan plan) =>
        ConfirmAction != null && await ConfirmAction($"{title}\n\n{plan.CommandLine}\n\n{plan.Note}\n\n"
            + "It will run in a terminal tile in the current workspace.");

    /// <summary>
    /// Stores the sign-in and makes its directory, which is the whole of setting one up here.
    /// </summary>
    /// <remarks>The login itself is not ours to perform: it is an OAuth flow in the CLI's own terminal,
    /// which is what the row's Sign in button opens. All this does is give it somewhere to put the
    /// answer.</remarks>
    [RelayCommand]
    private async Task SaveSignInAsync()
    {
        if (_editingSignIn is not { } signIn || !CanSaveSignIn) return;

        signIn.Name = EditSignInName.Trim();

        // The tool only while the form is allowed to offer one, which is only while the row is new.
        // Written unconditionally it moved a stored row to whatever tool the field happened to show -
        // and for an agent this build does not have, the field shows something the user never chose.
        // The directory is composed from this id, so the change leaves the login where it was and
        // points the row at an empty directory belonging to another CLI.
        if (CanChooseSignInAgent) signIn.AgentId = SignInAgentBeingEdited!.Id;

        var agent = AiAgentCatalog.Find(signIn.AgentId);

        // Its answer is used, as it is on the Sign in button: a row stored against a directory that
        // could not be made is a login the user will be sent to perform and cannot.
        if (!AiSignInStore.Ensure(signIn, agent))
        {
            await ShowProblemAsync("Sign-in",
                $"Could not create {AiSignInStore.DirectoryFor(signIn)}, so this sign-in was not added.");
            return;
        }

        var list = _settingsService.Settings.AiSignIns;
        if (!list.Contains(signIn)) list.Add(signIn);

        _settingsService.NotifyChanged();
        CloseSignInForm();
        LoadAiInstances();
    }

    [RelayCommand]
    private void CancelEditSignIn() => CloseSignInForm();

    private void CloseSignInForm()
    {
        IsEditingSignIn = false;
        _editingSignIn = null;
        OnPropertyChanged(nameof(IsEditingAnything));
    }

    /// <summary>
    /// Opens a tile signed in to nothing yet, so the user can run the CLI's own login command.
    /// </summary>
    /// <remarks>Through the same <see cref="RunInstallPlan"/> route an install takes, and for the same
    /// reason: it needs a terminal in the current workspace, and an OAuth flow prints a URL somebody has
    /// to read. The command is the bare CLI — its login is a command typed inside it, not a flag.
    /// </remarks>
    [RelayCommand]
    private async Task SignInAsync(AiSignInViewModel row)
    {
        if (row.Agent is not { } agent) return;

        var directory = AiSignInStore.DirectoryFor(row.SignIn);
        // With the agent, which is what creates the directory the CLI is really pointed at — for
        // opencode that is <root>/data, where auth.json lands. This is the one path that sends somebody
        // off to log in, so it is the last place to leave that directory to the umask.
        if (!AiSignInStore.Ensure(row.SignIn, row.Agent))
        {
            await ShowProblemAsync("Sign in", $"Could not create {directory}.");
            return;
        }

        // Through the startup script rather than the process environment, which is the opposite of the
        // rule a launch follows — and deliberately. What goes in here is a directory path: the rule
        // exists because a script is typed into a live prompt and lands in the scrollback and the
        // shell's history, which is fatal for a key and harmless for a location the user just named.
        // The tile that runs it is a plain terminal, and plumbing an environment block through the tile
        // kind for this one command would be a second route to keep in step with the first.
        var shell = ShellTerminalCatalog.ResolveDefault(_settingsService.Settings).Shell;
        var command = shell.WithEnv(agent.SignInEnv(directory), agent.BinaryName);

        // Not an InstallPlan in spirit, but exactly one in shape: a command line, a note, and a tile to
        // run it in. A second type for the same three fields would be two things to keep in step.
        var plan = new InstallPlan(command, [],
            $"Signs in as a separate account, kept in {directory}. "
            + "Run the tool's own login command in the tile that opens — /login for Claude Code.");

        // Asked, so that the note is read rather than written and discarded: the route a plan takes to
        // a tile carries the command alone, and without this the tile opened on an empty prompt with
        // nothing saying what to type into it.
        if (!await ConfirmedAsync($"Sign in as \"{row.Name}\"?", plan)) return;

        if (RunInstallPlan is not { } run || !await run(plan))
            await ShowProblemAsync("Sign in", "Open a workspace first — the tool runs in a tile there.");
    }

    /// <summary>
    /// Removes the row. <b>Never the directory.</b>
    /// </summary>
    /// <remarks>What is in there is the CLI's refresh token and the whole conversation history that came
    /// with the account, neither of which this application put there — and "writing to the user's disk
    /// asks first" cannot be honoured for a deletion the user has no way to undo. The confirmation says
    /// where it stays, so removing a row by mistake costs one row.</remarks>
    [RelayCommand]
    private async Task DeleteSignInAsync(AiSignInViewModel row)
    {
        var used = _settingsService.Settings.AiAgentInstances
            .Count(instance => instance.SignInId == row.SignIn.Id);

        var agreed = ConfirmAction != null && await ConfirmAction(
            $"Remove the sign-in \"{row.Name}\"?"
            + (used > 0
                ? $"\n\n{used} agent instance{(used == 1 ? "" : "s")} run as it and will stop being "
                  + "offered until you point them at another account."
                : "")
            // What it says has to be what happens. It used to promise that adding the sign-in again
            // brought the login back, and it does not: the directory is named after the sign-in's id
            // and a new row gets a new one, so the old login stays where it is and nothing in the form
            // can point at it. Naming the path is the part that is true and useful.
            + $"\n\nThe login itself is not deleted - it stays at "
            + $"{AiSignInStore.DirectoryFor(row.SignIn)}. A sign-in added later gets a directory of its "
            + "own, so it will have to be logged into again.");
        if (!agreed) return;

        _settingsService.Settings.AiSignIns.Remove(row.SignIn);
        _settingsService.NotifyChanged();
        LoadAiInstances();
    }

    // ─────────────────────────── Provider instances ───────────────────────────

    [ObservableProperty] private bool _isEditingProviderInstance;
    [ObservableProperty] private string _editProviderName = "";
    [ObservableProperty] private string _editProviderKind = "";
    [ObservableProperty] private string _editProviderBaseUrl = "";
    [ObservableProperty] private string _editProviderApiKey = "";
    [ObservableProperty] private int _editProviderTimeout = AiProviderInstance.DefaultTimeoutSeconds;
    [ObservableProperty] private string _editProviderTestResult = "";
    [ObservableProperty] private bool _isTestingProvider;
    private AiProviderInstance? _editingProviderInstance;

    /// <summary>The models a provider says it serves, for the model field to complete against.</summary>
    /// <remarks><b>A completion list rather than a drop-down</b>: <c>opencode models</c> answered with
    /// 374 entries here, and a combo box of those is not a chooser. Shared by both forms, because both
    /// ask the same provider the same question.</remarks>
    public ObservableCollection<string> ModelSuggestions { get; } = [];

    /// <summary>Fills the agent form's model suggestions from the provider that instance authenticates
    /// through.</summary>
    /// <remarks>The sentinel is offered first, and only where it means something: a hosted service
    /// cannot say which model is loaded, so listing it there would be a choice that fails at launch.
    /// </remarks>
    private async Task LoadAgentModelsAsync()
    {
        using var cancelling = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _agentModelsCts, cancelling);
        var token = cancelling.Token;

        try
        {
            previous?.Cancel();
        }
        // The one it cancels is owned by the call that made it, and that call disposes it as it leaves.
        // Nothing here can be sure it has not: the fetch awaits an HTTP layer that does not capture a
        // context, so the earlier call's continuation - and its dispose - can run on the pool while
        // this one is between the exchange and the cancel. A disposed source is a fetch that has
        // already finished, so there is nothing left to cancel and this is the whole of the handling.
        // Outside the try below deliberately: that one is about the *await*, and a throw from here
        // would leave this fire-and-forget task unobserved, which is the ERROR in the log the catch
        // there exists to avoid.
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await FetchAgentModelsAsync(token);
        }
        // Ours, and expected: a newer choice cancelled this one. Swallowed here because the caller is a
        // property setter that discards the task - unobserved, the exception reaches
        // TaskScheduler.UnobservedTaskException and is logged as an ERROR for something working exactly
        // as designed.
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            // Only ever clears its own, so a later fetch that has already taken the field keeps it.
            Interlocked.CompareExchange(ref _agentModelsCts, null, cancelling);
        }
    }

    /// <summary>The fetch itself, which is free to throw when it is cancelled.</summary>
    /// <remarks>Split from the command so that <c>using</c> and the catch above sit together, and so
    /// that the source is disposed after <em>its own</em> work is finished rather than while an
    /// in-flight request still holds its token — disposing a source a live <c>HttpClient</c> is
    /// registered against is how a cancellation becomes an <c>ObjectDisposedException</c>.</remarks>
    private async Task FetchAgentModelsAsync(CancellationToken token)
    {
        // Only a provider has a catalogue to ask. A subscription serves whatever the plan includes and
        // has no endpoint for it, so the button stands down rather than answering with an empty list.
        var configured = EditAgentAccount is { Kind: AccountKind.Provider } account
            ? ProviderInstances.FirstOrDefault(p => p.Instance.Id == account.Id)
            : null;

        if (configured?.Provider is not { } provider)
        {
            _agentModels = [];
            ModelSuggestions.Clear();
            RefreshEffortLabels();
            // And the context readouts go with the list: this is the branch where the account stopped
            // being a provider — switched to Default, or to a sign-in — which is exactly the moment
            // nothing is computed from them any more. Every other path of this method clears them
            // first; this one was the exception.
            _ = UpdateModelContextAsync(fastModel: false);
            _ = UpdateModelContextAsync(fastModel: true);
            return;
        }

        token.ThrowIfCancellationRequested();

        var models = await provider.ModelsAsync(configured.Instance, token);

        // Read once, after the await: whoever started a later fetch owns the form now, and a reply that
        // arrives after them describes an account that is no longer chosen.
        if (token.IsCancellationRequested) return;
        _agentModels = models;

        ModelSuggestions.Clear();
        if (provider is ILocalAiProvider)
            ModelSuggestions.Add(AiModelChoice.FirstLoaded);
        foreach (var model in _agentModels)
            ModelSuggestions.Add(model.Id);

        // Deliberately nothing here opens the list. It used to, because List had been pressed and had
        // to visibly do something; the fetch now happens because the account changed, and a popup
        // unfurling over a form somebody is still filling in would be the application taking the
        // keyboard for a list nobody asked to see.

        // What the models say about effort is the other half of the same answer: asking for the list
        // and then offering a level the chosen one refuses is a launch that fails on our own flag.
        RefreshEffortLabels();

        // And what they say about context is the third: the list is where the readouts answer from
        // when they can, and the fields may already have text in them that arrived before this reply.
        _ = UpdateModelContextAsync(fastModel: false);
        _ = UpdateModelContextAsync(fastModel: true);
    }

    public static IReadOnlyList<string> ProviderKinds { get; } =
        [.. AiProviderCatalog.All.Select(provider => provider.DisplayName)];

    /// <summary>Whether the provider being edited needs a key at all — a local server has no
    /// authentication, and an empty field there is the answer rather than an omission.</summary>
    public bool EditProviderNeedsKey => EditedProvider is not { NeedsApiKey: false };

    /// <summary>Whether the key field needs the plain-text warning under it — only where there is a key
    /// to warn about and a platform that cannot encrypt it.</summary>
    public bool ShowsProviderKeyWarning => EditProviderNeedsKey && SecretStorage.HasWarning;

    /// <summary>Whether the provider being edited is one this network might be running, which is what
    /// makes Discover worth offering.</summary>
    /// <remarks>Asked of the interface rather than the flag: Discover walks addresses looking for a
    /// server, and the one provider that is local without being one to discover is CCS — its address is
    /// fixed and published, and a sweep would find either it or nothing.</remarks>
    public bool EditProviderIsLocal => EditedProvider is ILocalAiProvider;

    /// <summary>Whether the provider being edited is CCS, whose row carries its own setup flow.</summary>
    /// <remarks><b>Always offered, never hidden for being uninstalled.</b> LM Studio and Ollama stand in
    /// the Service list while they are not running, and hiding the one entry somebody could otherwise
    /// install would be the list telling them the option does not exist. The form reacts to state
    /// instead — an Install button while the CLI is missing, an Auth button while the proxy's account
    /// is not signed in.</remarks>
    public bool EditProviderIsCcs => EditedProvider is CcsProvider;

    /// <summary>Whether CCS itself is missing from this machine.</summary>
    public bool ShowCcsInstall => EditProviderIsCcs && !CcsProvider.IsInstalled;

    /// <summary>Whether the proxy's Codex account still needs its one-time login.</summary>
    public bool ShowCcsAuth => EditProviderIsCcs && CcsProvider.IsInstalled && !CcsProvider.HasCodexAuth;

    /// <summary>Whether nothing on the CCS row is waiting for the user any more.</summary>
    /// <remarks>A property rather than a <c>!</c>-and-<c>&amp;</c> expression in the binding, which a
    /// compiled binding cannot read — and the failure was a silent one: the assembly's precompiled XAML
    /// was left half-written, and every view in it answered "not found" at load.</remarks>
    public bool CcsIsSetUp => EditProviderIsCcs && CcsProvider.IsInstalled && CcsProvider.HasCodexAuth;

    /// <summary>Re-reads what this machine says about CCS, after anything that could have changed it.
    /// </summary>
    /// <remarks>The install and the login both happen in a tile this dialog does not own, so their
    /// effect arrives whenever the user gets round to it — the properties are asked again when the form
    /// opens or the kind changes, which is the moment the answer could have moved.</remarks>
    private void RefreshCcsState()
    {
        OnPropertyChanged(nameof(EditProviderIsCcs));
        OnPropertyChanged(nameof(ShowCcsInstall));
        OnPropertyChanged(nameof(ShowCcsAuth));
        OnPropertyChanged(nameof(CcsIsSetUp));
    }

    /// <summary>
    /// What the address field says while it is empty.
    /// </summary>
    /// <remarks><b>Empty means the same thing for both kinds — but only a local one can usefully name
    /// what that is.</b> "The service's own address" is all that can be said about a hosted API; for a
    /// server on this machine the actual address is short, and printing it is the difference between a
    /// user knowing the field is optional and a user guessing what happens if they leave it.</remarks>
    public string ProviderAddressHint =>
        EditedProvider is { IsLocal: true, DefaultPort: var port }
            ? $"empty = localhost:{port} — or type where the server is"
            : "empty = the service's own address";

    /// <summary>What the form says where no key field is shown — the provider's own sentence, so the
    /// row and the form cannot say two different things about the same fact.</summary>
    public string EditProviderNoKeyNote => EditedProvider?.NoKeyNote ?? "";

    private IAiProvider? EditedProvider =>
        AiProviderCatalog.All.FirstOrDefault(p => p.DisplayName == EditProviderKind);

    partial void OnEditProviderKindChanged(string value)
    {
        OnPropertyChanged(nameof(EditProviderNeedsKey));
        OnPropertyChanged(nameof(ShowsProviderKeyWarning));
        OnPropertyChanged(nameof(EditProviderIsLocal));
        OnPropertyChanged(nameof(ProviderAddressHint));
        OnPropertyChanged(nameof(EditProviderNoKeyNote));
        RefreshCcsState();
    }

    /// <summary>
    /// A new provider row, unnamed.
    /// </summary>
    /// <remarks><b>Seeded with the first provider's display name, it was a name nobody chose and one
    /// that then stopped being true.</b> The form opened saying "Anthropic", the user picked LM Studio
    /// from Service, and the name stayed — because nothing rewrites a field somebody may have typed in,
    /// and nothing can tell a default apart from a deliberate answer that happens to match it. The
    /// result was a row called Anthropic pointing at a local server. Empty asks the question instead,
    /// and <see cref="CanSaveProviderInstance"/> makes it a real one.</remarks>
    [RelayCommand]
    private void AddProviderInstance() =>
        BeginProviderEditing(new AiProviderInstance { ProviderId = AiProviderCatalog.All[0].Id });

    [RelayCommand]
    private void EditProviderInstance(AiProviderInstanceViewModel row) =>
        BeginProviderEditing(row.Instance);

    private void BeginProviderEditing(AiProviderInstance instance)
    {
        _editingProviderInstance = instance;
        ModelSuggestions.Clear();
        EditProviderTestResult = "";

        EditProviderName = instance.Name;
        EditProviderKind =
            AiProviderCatalog.Find(instance.ProviderId)?.DisplayName ?? ProviderKinds[0];
        EditProviderBaseUrl = instance.BaseUrl;
        EditProviderApiKey = instance.ApiKey;
        EditProviderTimeout = instance.TimeoutSeconds;

        // The kind may be unchanged from the last time this form was open, in which case the
        // ObservableProperty setter raises nothing — and what this machine says about CCS may still
        // have moved between the two opens.
        RefreshCcsState();

        BeginEditing(ref _isEditingProviderInstance);
    }

    [RelayCommand]
    private void SaveProviderInstance()
    {
        if (_editingProviderInstance is not { } instance || !CanSaveProviderInstance) return;

        ApplyProviderForm(instance);

        var list = _settingsService.Settings.AiProviderInstances;
        if (!list.Contains(instance)) list.Add(instance);

        _settingsService.NotifyChanged();
        CloseProviderForm();
        LoadAiInstances();
    }

    /// <summary>
    /// Whether the provider form can be saved: it needs a name.
    /// </summary>
    /// <remarks><b>The name is the only field that cannot be defaulted.</b> The address has the
    /// service's own, the key is genuinely optional on a local server, and the timeout has a number —
    /// but nothing makes an instance's name unique and nothing can invent one, and the name is what
    /// every chooser identifies the row by. Two rows spelled the same are two rows the user cannot tell
    /// apart in the account chooser, which is the mistake a seeded default was quietly making for
    /// them.</remarks>
    public bool CanSaveProviderInstance => EditProviderName.Trim().Length > 0;

    partial void OnEditProviderNameChanged(string value) =>
        OnPropertyChanged(nameof(CanSaveProviderInstance));

    /// <summary>The form as an instance — what Save stores and what Test and the model list ask about.
    /// </summary>
    /// <remarks>The unsaved form, deliberately: testing what is stored rather than what is on screen
    /// would answer about the old address every time somebody corrects one.</remarks>
    private void ApplyProviderForm(AiProviderInstance instance)
    {
        instance.Name = EditProviderName.Trim();
        instance.ProviderId = EditedProvider?.Id ?? instance.ProviderId;
        instance.BaseUrl = EditProviderBaseUrl.Trim();
        instance.ApiKey = EditProviderApiKey;
        instance.TimeoutSeconds = EditProviderTimeout;
    }

    private AiProviderInstance FormAsInstance()
    {
        var probe = new AiProviderInstance();
        ApplyProviderForm(probe);
        return probe;
    }

    [RelayCommand]
    private void CancelEditProviderInstance() => CloseProviderForm();

    private void CloseProviderForm()
    {
        IsEditingProviderInstance = false;
        _editingProviderInstance = null;
        OnPropertyChanged(nameof(IsEditingAnything));
    }

    [RelayCommand]
    private async Task DeleteProviderInstanceAsync(AiProviderInstanceViewModel row)
    {
        var used = _settingsService.Settings.AiAgentInstances
            .Count(instance => instance.ApiAccountId == row.Instance.Id);

        // Says what it costs before it is agreed to: an agent instance whose provider is gone is not
        // offered anywhere, so deleting one row can take three tiles' worth of choices with it.
        var agreed = ConfirmAction != null && await ConfirmAction(
            $"Delete the provider \"{row.Name}\"?"
            + (used > 0
                ? $"\n\n{used} agent instance{(used == 1 ? "" : "s")} authenticate through it and will "
                  + "stop being offered until you point them somewhere else."
                : ""));
        if (!agreed) return;

        _settingsService.Settings.AiProviderInstances.Remove(row.Instance);
        _settingsService.NotifyChanged();
        LoadAiInstances();
    }

    /// <summary>Asks the provider being edited whether it is there.</summary>
    /// <remarks>Answers rather than throws — every provider's own call does, because a test button that
    /// throws is a dialog with a stack trace in it.</remarks>
    [RelayCommand]
    private async Task TestProviderAsync()
    {
        if (IsTestingProvider || EditedProvider is not { } provider) return;

        IsTestingProvider = true;
        EditProviderTestResult = "";
        try
        {
            var check = await provider.TestAsync(FormAsInstance());
            EditProviderTestResult = check.Ok
                ? check.Balance is { } balance
                    ? $"{check.Message} · {balance:0.##} left"
                    : check.Message
                : check.Message;
        }
        finally
        {
            IsTestingProvider = false;
        }
    }

    /// <summary>Fills the model field's suggestions from the provider itself.</summary>
    [RelayCommand]
    private async Task LoadProviderModelsAsync()
    {
        if (EditedProvider is not { } provider) return;

        ModelSuggestions.Clear();
        var models = await provider.ModelsAsync(FormAsInstance());
        foreach (var model in models)
            ModelSuggestions.Add(model.Id);

        EditProviderTestResult = models.Count > 0
            ? $"{models.Count} model{(models.Count == 1 ? "" : "s")}"
            // Not an error: a provider that answers with nothing is the usual outcome of a local server
            // with nothing loaded, and saying "0 models" is the fact rather than a failure.
            : "No models — the provider answered with an empty list.";
    }

    /// <summary>Whether Discover looks past this machine.</summary>
    /// <remarks>Off by default and asked for by hand: loopback answers nearly every time and costs one
    /// call, while a sweep of a corporate subnet is slow and — unasked — looks like reconnaissance.
    /// </remarks>
    [ObservableProperty] private bool _searchLocalNetwork;

    /// <summary>
    /// Looks for this provider on this machine and this network, and fills the address in.
    /// </summary>
    /// <remarks><b>On demand and never on a timer</b>, and it verifies by protocol rather than by port —
    /// see <c>LocalProviderDiscovery</c>. It usually finds nothing: Ollama binds <c>127.0.0.1</c> unless
    /// told otherwise and LM Studio has to be asked to serve on the local network, so the empty answer
    /// says so rather than looking like a failure.</remarks>
    [RelayCommand]
    private async Task DiscoverProviderAsync()
    {
        if (EditedProvider is not ILocalAiProvider local) return;

        IsTestingProvider = true;
        EditProviderTestResult = "Looking…";
        try
        {
            var found = await LocalProviderDiscovery.FindAsync(local, SearchLocalNetwork);
            if (found.Count > 0)
            {
                EditProviderBaseUrl = found[0].ToString();
                EditProviderTestResult = found.Count == 1
                    ? "Found one."
                    : $"Found {found.Count}; using the first.";
            }
            else
            {
                EditProviderTestResult =
                    "Nothing answered. Ollama listens on this machine only unless OLLAMA_HOST is set, "
                    + "and LM Studio needs \"Serve on Local Network\".";
            }
        }
        finally
        {
            IsTestingProvider = false;
        }
    }

    /// <summary>Installs CCS in a visible tile, through the route every agent install takes.</summary>
    /// <remarks>Shown only while <c>ccs</c> is missing; once it is on the machine the button stands down
    /// and the Auth one takes its place. Nothing is re-read here: the route answers as soon as the tile
    /// opens, so the state the buttons follow is re-checked when the form next opens — which is when
    /// the answer could have moved.</remarks>
    [RelayCommand]
    private async Task InstallCcsAsync()
    {
        if (!await ConfirmedAsync("Install CCS?", CcsProvider.Install)) return;

        if (RunInstallPlan is not { } run || !await run(CcsProvider.Install))
        {
            await ShowProblemAsync("Install CCS",
                "Open a workspace first — the install runs in a terminal tile there.");
        }
    }

    /// <summary>Signs the CCS proxy in to a Codex account — the one OAuth step nothing here can do
    /// silently, and the only one the whole setup needs.</summary>
    /// <remarks>Through the same route a sign-in row takes, and for the same reason: the URL the login
    /// prints has to be read by somebody. The command travels through the startup script rather than
    /// the environment, which is harmless for a CLI invocation and is where a login command belongs.
    /// After it, the proxy refreshes its own token — nothing here ever touches it. The button's state
    /// is re-checked when the form next opens, not here: the route answers as soon as the tile opens,
    /// before anybody has logged in.</remarks>
    [RelayCommand]
    private async Task AuthCcsCodexAsync()
    {
        var shell = ShellTerminalCatalog.ResolveDefault(_settingsService.Settings).Shell;
        var command = shell.Invoke(CcsProvider.CommandName, CcsProvider.AuthArguments);

        // Not an InstallPlan in spirit, but exactly one in shape — the rule the Sign in button already
        // lives by: a command line, a note, and a tile to run it in.
        var plan = new InstallPlan(command, [],
            "Runs \"ccs codex --auth\" — the proxy's one-time login to a Codex account. A browser "
            + "opens, the token lands in the proxy's own directory, and it refreshes itself from then on.");

        if (!await ConfirmedAsync("Sign the CCS proxy in to Codex?", plan)) return;

        if (RunInstallPlan is not { } run || !await run(plan))
        {
            await ShowProblemAsync("Auth Codex",
                "Open a workspace first — the login runs in a terminal tile there.");
        }
    }

    // ─────────────────────────── Export and import ───────────────────────────

    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        if (BrowseSaveFile is not { } browse) return;

        // Said before the file exists, because afterwards the user has already shared it.
        var agreed = ConfirmAction != null
                     && await ConfirmAction($"Export settings?\n\n{SettingsPortability.SecretsWarning}");
        if (!agreed) return;

        if (await browse(SettingsPortability.SuggestedFileName) is not { Length: > 0 } path) return;

        try
        {
            SettingsPortability.Export(_settingsService.Settings, path);
        }
        catch (Exception ex)
        {
            await ShowProblemAsync("Export", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        if (BrowseOpenFile is not { } browse) return;
        if (await browse() is not { Length: > 0 } path) return;

        var imported = SettingsPortability.Import(path, out var problem);
        if (imported is null)
        {
            await ShowProblemAsync("Import", $"That file could not be read: {problem}");
            return;
        }

        var agreed = ConfirmAction != null && await ConfirmAction(
            "Replace your settings with this file?\n\n"
            + "Everything on this dialog is replaced. API keys and database passwords already set up "
            + "here are kept, because an exported file carries none.");
        if (!agreed) return;

        _settingsService.Replace(imported);
        ReloadFromSettings();
    }

    /// <summary>Puts the whole dialog back in step with settings that were replaced underneath it.
    /// </summary>
    /// <remarks>
    /// <para>Every other page writes as you type, so nothing else ever needed this: an import is the
    /// one change that arrives from outside the form the user is looking at.</para>
    /// <para><em>Every</em> page, not the three the import obviously touches. The pages that save as you
    /// type are exactly the ones that cannot afford to be left stale: a Speech tab still showing the old
    /// shortcut writes it straight back over the imported one the first time any control on it is
    /// touched, and the user sees nothing to say that happened. Speech and Phone are reloaded through
    /// the same <c>Initialize*</c> the constructor uses, which writes the backing fields rather than the
    /// properties for that very reason — so the notification is raised here, once, for everything.</para>
    /// </remarks>
    private void ReloadFromSettings()
    {
        var s = _settingsService.Settings;
        ColorThemeName = s.ColorThemeName;
        TerminalFontFamily = s.TerminalFontFamily;
        TerminalFontSize = s.TerminalFontSize;
        FontFamily = s.FontFamily;
        FontSize = s.FontSize;
        GitIgnoreWorkspaceDir = s.GitIgnoreWorkspaceDir;
        GitPath = s.GitPath;
        LoadDefaultShell();
        LoadAiInstances();
        LoadDatabaseForm();
        LoadManualConnections();

        InitializeSpeech(Dictation);
        SelectSpeechModelFromSettings();
        InitializePhone();

        // With a null name, for the reason the speech wizard gives: listing the properties by hand means
        // a list that has to be kept in step with three methods that assign two dozen of them between
        // them, and it will not be.
        OnPropertyChanged((string?)null);
    }

    private async Task ShowProblemAsync(string title, string message)
    {
        if (ShowError is { } show)
        {
            await show(title, message);
            return;
        }

        Trace.TraceWarning("{0} failed: {1}", title, message);
    }
}
