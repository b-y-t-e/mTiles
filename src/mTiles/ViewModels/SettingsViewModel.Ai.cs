using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Providers;

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

        OnPropertyChanged(nameof(HasNoProviderInstances));
    }

    /// <summary>Whether anything has been set up at all — an empty list is an empty state, not a list of
    /// nothing.</summary>
    public bool HasNoProviderInstances => ProviderInstances.Count == 0;

    // ─────────────────────────── Agent instances ───────────────────────────

    [ObservableProperty] private bool _isEditingAgentInstance;
    [ObservableProperty] private string _editAgentName = "";
    [ObservableProperty] private string _editAgentAgentName = "";
    [ObservableProperty] private ProviderChoice? _editAgentProvider = ProviderChoice.OwnAccount;
    [ObservableProperty] private string _editAgentModel = "";
    [ObservableProperty] private string _editAgentFastModel = "";
    [ObservableProperty] private string _editAgentBehaviour = "";
    [ObservableProperty] private string _editAgentEffort = "";
    [ObservableProperty] private string _editAgentExtraArgs = "";
    private AiAgentInstance? _editingAgentInstance;

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
        RefreshProviderChoices();
        RefreshBehaviourLabels();
        RefreshEffortLabels();
    }

    partial void OnEditAgentModelChanged(string value) => RefreshEffortLabels();

    /// <summary>What an instance's provider may be: the configured ones, plus the agent's own account.
    /// </summary>
    /// <remarks>Rebuilt whenever a form opens rather than held, because the provider list is edited on
    /// the same page and a stale choice here is a launch that finds no provider.</remarks>
    public ObservableCollection<ProviderChoice> ProviderChoices { get; } = [];

    /// <summary>What "no provider" is called on screen. A word rather than a blank row, which reads as
    /// an unfinished form.</summary>
    public const string OwnAccountChoice = ProviderChoice.OwnAccountLabel;

    /// <summary>
    /// Rebuilds the chooser for the agent the form currently names, leaving out every provider that
    /// agent cannot speak to.
    /// </summary>
    /// <remarks>A pairing that cannot work must not be offered: stored, it makes the instance
    /// unavailable everywhere (<c>AiAgentCatalog.IsAvailable</c>) — gone from the Agent tile's chooser
    /// and from the Goal tile's list — with nothing on screen saying why. Depends on the agent, so it
    /// is rebuilt when the agent changes and not only when the form opens.</remarks>
    private void RefreshProviderChoices()
    {
        var agent = AgentBeingEdited;
        var chosen = EditAgentProvider?.Id;

        ProviderChoices.Clear();
        ProviderChoices.Add(ProviderChoice.OwnAccount);
        foreach (var provider in ProviderInstances)
        {
            // A provider kind this build does not have cannot be judged, so it is still offered — the
            // alternative is hiding a row the user configured on the strength of not recognising it.
            if (agent is not null && provider.Provider is { } kind
                && !AiProviderCatalog.IsCompatible(agent, kind))
                continue;

            ProviderChoices.Add(new ProviderChoice(provider.Instance.Id, provider.Name));
        }

        if (chosen is not null && ProviderChoices.All(choice => choice.Id != chosen))
            EditAgentProvider = ProviderChoice.OwnAccount;
    }

    /// <summary>Finds the chooser's entry for a stored provider id — the row itself, so the combo shows
    /// the one that was saved rather than the first that happens to share its name.</summary>
    private ProviderChoice ChoiceFor(string providerInstanceId) =>
        ProviderChoices.FirstOrDefault(choice => choice.Id == providerInstanceId)
        ?? ProviderChoice.OwnAccount;

    [RelayCommand]
    private void AddAgentInstance()
    {
        var agent = AiAgentCatalog.All[0];
        BeginAgentEditing(new AiAgentInstance { AgentId = agent.Id, Name = agent.DisplayName });
    }

    [RelayCommand]
    private void EditAgentInstance(AiAgentInstanceViewModel row) => BeginAgentEditing(row.Instance);

    private void BeginAgentEditing(AiAgentInstance instance)
    {
        _editingAgentInstance = instance;
        _agentModels = [];
        RefreshProviderChoices();

        EditAgentName = instance.Name;
        EditAgentAgentName = AiAgentCatalog.Find(instance.AgentId)?.DisplayName ?? AgentNames[0];
        EditAgentProvider = ChoiceFor(instance.ProviderInstanceId);
        EditAgentModel = instance.Model;
        EditAgentFastModel = instance.FastModel;
        EditAgentBehaviour = AiBehaviours.Label(instance.DefaultBehaviour);
        EditAgentEffort = AiEfforts.Label(instance.DefaultEffort);
        RefreshBehaviourLabels();
        RefreshEffortLabels();
        // One per line, because an argument may contain spaces and splitting on them is how a path with
        // one in it becomes two arguments neither of which exists.
        EditAgentExtraArgs = string.Join('\n', instance.ExtraArgs);

        BeginEditing(ref _isEditingAgentInstance);
    }

    [RelayCommand]
    private async Task SaveAgentInstanceAsync()
    {
        if (_editingAgentInstance is not { } instance) return;

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
        instance.ProviderInstanceId = EditAgentProvider?.Id ?? "";
        instance.Model = EditAgentModel.Trim();
        instance.FastModel = EditAgentFastModel.Trim();
        instance.DefaultBehaviour = behaviour;
        instance.DefaultEffort = AiEfforts.FromLabel(EditAgentEffort);
        instance.ExtraArgs = [.. EditAgentExtraArgs
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        var list = _settingsService.Settings.AiAgentInstances;
        if (!list.Contains(instance)) list.Add(instance);

        _settingsService.NotifyChanged();
        CloseAgentForm();
        LoadAiInstances();
    }

    [RelayCommand]
    private void CancelEditAgentInstance() => CloseAgentForm();

    private void CloseAgentForm()
    {
        IsEditingAgentInstance = false;
        _editingAgentInstance = null;
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

        var agreed = ConfirmAction != null && await ConfirmAction(
            $"Install {row.AgentName}?\n\n{plan.CommandLine}\n\n{plan.Note}\n\n"
            + "It will run in a terminal tile in the current workspace.");
        if (!agreed) return;

        if (RunInstallPlan is not { } run || !await run(plan))
        {
            await ShowProblemAsync("Install",
                $"Open a workspace first — the command runs in a tile there.\n\n{plan.CommandLine}");
        }
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
    [RelayCommand]
    private async Task LoadAgentModelsAsync()
    {
        var configured = ProviderInstances
            .FirstOrDefault(p => p.Instance.Id == EditAgentProvider?.Id);

        if (configured?.Provider is not { } provider)
        {
            _agentModels = [];
            ModelSuggestions.Clear();
            RefreshEffortLabels();
            return;
        }

        _agentModels = await provider.ModelsAsync(configured.Instance);

        ModelSuggestions.Clear();
        if (provider is ILocalAiProvider)
            ModelSuggestions.Add(AiModelChoice.FirstLoaded);
        foreach (var model in _agentModels)
            ModelSuggestions.Add(model.Id);

        // What the models say about effort is the other half of the same answer: asking for the list
        // and then offering a level the chosen one refuses is a launch that fails on our own flag.
        RefreshEffortLabels();
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
    public bool EditProviderIsLocal => EditedProvider is { IsLocal: true };

    private IAiProvider? EditedProvider =>
        AiProviderCatalog.All.FirstOrDefault(p => p.DisplayName == EditProviderKind);

    partial void OnEditProviderKindChanged(string value)
    {
        OnPropertyChanged(nameof(EditProviderNeedsKey));
        OnPropertyChanged(nameof(ShowsProviderKeyWarning));
        OnPropertyChanged(nameof(EditProviderIsLocal));
    }

    [RelayCommand]
    private void AddProviderInstance()
    {
        var provider = AiProviderCatalog.All[0];
        BeginProviderEditing(
            new AiProviderInstance { ProviderId = provider.Id, Name = provider.DisplayName });
    }

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

        BeginEditing(ref _isEditingProviderInstance);
    }

    [RelayCommand]
    private void SaveProviderInstance()
    {
        if (_editingProviderInstance is not { } instance) return;

        ApplyProviderForm(instance);

        var list = _settingsService.Settings.AiProviderInstances;
        if (!list.Contains(instance)) list.Add(instance);

        _settingsService.NotifyChanged();
        CloseProviderForm();
        LoadAiInstances();
    }

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
            .Count(instance => instance.ProviderInstanceId == row.Instance.Id);

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
