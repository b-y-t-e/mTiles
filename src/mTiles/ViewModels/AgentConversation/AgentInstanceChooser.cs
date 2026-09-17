using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Agents.Sessions;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// The strip's list of agents a conversation can be pointed at: what it offers, which entries are refused and
/// why, and which one is shown as running.
/// </summary>
/// <remarks>
/// <para>It decides nothing about the conversation. What runs, which agent holds the conversation and what a
/// pick then does are the tile's, handed in as questions and a callback, so a change to how the list looks and
/// a change to the binding rule land in two different classes.</para>
/// <para><b>Nothing is left out of the list.</b> An instance of another agent stops being pickable once the
/// conversation has something in it, and says why, rather than vanishing from under the pointer.</para>
/// </remarks>
public sealed partial class AgentInstanceChooser : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly Func<AiAgentInstance> _current;
    private readonly Func<AiAgentInstance, bool> _isRunning;
    private readonly Func<string?> _heldAgentId;
    private readonly Action<Action> _post;
    private readonly Action<AiAgentInstance> _picked;
    private bool _drawing;
    private bool _disposed;
    private string? _drawnInstances;
    private string? _drawnHeldAgentId;

    [ObservableProperty] private AgentInstanceOption? _selected;

    /// <param name="settings">Where the instances are configured.</param>
    /// <param name="current">The tile's instance, listed even when Settings no longer holds it.</param>
    /// <param name="isRunning">Whether an instance is what actually runs, and so is shown selected.</param>
    /// <param name="heldAgentId">The agent the conversation is bound to, or null while nothing has been said.</param>
    /// <param name="post">Runs on the thread the tile draws on.</param>
    /// <param name="picked">A pickable entry the user chose.</param>
    public AgentInstanceChooser(SettingsService settings, Func<AiAgentInstance> current,
        Func<AiAgentInstance, bool> isRunning, Func<string?> heldAgentId, Action<Action> post,
        Action<AiAgentInstance> picked)
    {
        _settings = settings;
        _current = current;
        _isRunning = isRunning;
        _heldAgentId = heldAgentId;
        _post = post;
        _picked = picked;
        Draw();
        settings.SettingsChanged += OnSettingsChanged;
    }

    public ObservableCollection<AgentInstanceOption> Options { get; } = [];

    public bool HasOptions => Options.Count > 1;

    /// <summary>What the strip's control says at rest.</summary>
    public string SelectedLabel => Selected?.Label ?? "Agent";

    /// <summary>Which row the list marks as the current one.</summary>
    /// <remarks>The instance's id and not the option object: the list is rebuilt whenever Settings changes,
    /// so an object held across that rebuild is a different instance from the one now in the list.</remarks>
    public string SelectedKey => Selected?.Instance.Id ?? "";

    /// <summary>Rebuilds the list, in step with what is configured and with what the conversation is bound to.</summary>
    public void Draw()
    {
        var held = _heldAgentId();
        WhileDrawing(() =>
        {
            Options.Clear();
            foreach (var instance in ConversationalInstances())
                Options.Add(OptionFor(instance, held));

            Selected = RunningOption();
            OnPropertyChanged(nameof(HasOptions));
        });
        _drawnHeldAgentId = held;
        _drawnInstances = InstancesFingerprint();
    }

    /// <summary>Redraws the list only when the agent the conversation is bound to has moved.</summary>
    /// <remarks>The conversation is drawn once a frame for the whole of an agent's reply, and a rebuild is a
    /// reset of the list: an open chooser flickers and loses its highlight. Every pass would also ask
    /// <see cref="AiAgentCatalog.IsAvailable"/>, whose locate answer is cached for thirty seconds — so in a
    /// drawing loop that is a scan of <c>PATH</c> on the UI thread twice a minute. An instance added or renamed
    /// in Settings arrives on <see cref="OnSettingsChanged"/> instead.</remarks>
    public void DrawIfBindingChanged()
    {
        if (_drawnHeldAgentId != _heldAgentId()) Draw();
    }

    /// <summary>Puts the selection back on what is actually running, leaving the list as it stands.</summary>
    public void RestoreSelection() => WhileDrawing(() => Selected = RunningOption());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.SettingsChanged -= OnSettingsChanged;
    }

    partial void OnSelectedChanged(AgentInstanceOption? value)
    {
        OnPropertyChanged(nameof(SelectedLabel));
        OnPropertyChanged(nameof(SelectedKey));
        if (_drawing || value is null) return;
        // The running entry is handed on too: picked back while another pick waits its turn, it is what the user
        // meant last, and the tile only learns that if it is told — the switch itself then finds nothing to do.
        if (!_isRunning(value.Instance) && !value.IsPickable && !IsPickableNow(value.Instance))
        {
            RestoreSelection();
            return;
        }

        _picked(value.Instance);
    }

    /// <summary>An instance added, renamed or deleted in Settings, drawn on the tile's thread.</summary>
    /// <remarks>Raised for every keystroke in that dialog, so the list is rebuilt only when what it lists could
    /// have moved: a rebuild resets an open chooser and asks for availability, whose cache expires into a scan of
    /// <c>PATH</c>.</remarks>
    private void OnSettingsChanged() => _post(() =>
    {
        if (!_disposed && InstancesFingerprint() != _drawnInstances) Draw();
    });

    /// <summary>Every configured instance that can be held as a conversation, and the tile's own first when
    /// Settings no longer lists it.</summary>
    private List<AiAgentInstance> ConversationalInstances()
    {
        var instances = _settings.Settings.AiAgentInstances
            .Where(i => AiAgentCatalog.Find(i.AgentId) is IConversationalAgent)
            .ToList();
        var current = _current();
        if (instances.All(i => i.Id != current.Id)) instances.Insert(0, current);
        return instances;
    }

    /// <summary>What the entries are drawn from: the instances and the accounts they rely on.</summary>
    private string InstancesFingerprint()
    {
        var settings = _settings.Settings;
        return string.Join("|",
            settings.AiAgentInstances.Select(i => $"{i.Id},{i.Name},{i.AgentId},{i.ApiAccountId},{i.SignInId}")
                .Concat(settings.AiProviderInstances.Select(p => p.Id))
                .Concat(settings.AiSignIns.Select(s => s.Id)));
    }

    /// <summary>One entry: pickable, or dimmed with the reason it is refused.</summary>
    private AgentInstanceOption OptionFor(AiAgentInstance instance, string? heldAgentId)
    {
        var agentName = AgentName(instance.AgentId);
        if (!AiAgentCatalog.IsAvailable(instance, _settings.Settings))
            return new AgentInstanceOption(instance, agentName, IsPickable: false,
                AgentAvailability.Problem(instance, _settings.Settings));

        return heldAgentId is null || instance.AgentId == heldAgentId
            ? new AgentInstanceOption(instance, agentName, IsPickable: true, Reason: null)
            : new AgentInstanceOption(instance, agentName, IsPickable: false,
                $"This conversation is held with {AgentName(heldAgentId)}. Start a new conversation to switch agent.");
    }

    private static string AgentName(string agentId) => AiAgentCatalog.Find(agentId)?.DisplayName ?? agentId;

    /// <summary>The entry for what runs, or none while a substitute stands in without running.</summary>
    private AgentInstanceOption? RunningOption() => Options.FirstOrDefault(o => _isRunning(o.Instance));

    /// <summary>Asks again whether an entry drawn as refused can be picked now.</summary>
    /// <remarks>What the entry says was true when the list was drawn, and availability moves without Settings
    /// changing: a CLI installed while the tile is open would otherwise stay refused as not installed for the
    /// life of the tile. Asked on the pick rather than on every settings change, since the answer costs a scan
    /// of <c>PATH</c>.</remarks>
    private bool IsPickableNow(AiAgentInstance instance) => OptionFor(instance, _heldAgentId()).IsPickable;

    /// <summary>Changes the list or its selection without the change reading as the user's pick.</summary>
    private void WhileDrawing(Action change)
    {
        _drawing = true;
        try
        {
            change();
        }
        finally
        {
            _drawing = false;
        }
    }
}
