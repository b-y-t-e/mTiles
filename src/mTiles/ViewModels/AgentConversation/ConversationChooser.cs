using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.AgentSessions.Storage;
using mTiles.Services.Agents;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// The tile's list of conversations held in this workspace: what it offers, which entries are refused and
/// why, and which one is shown as open.
/// </summary>
/// <remarks>
/// <para>It decides nothing, the same division <see cref="AgentInstanceChooser"/> keeps: whether an entry can
/// be picked and what a pick then does are the tile's, handed in as a question and a callback, so a change to
/// how the list looks and a change to the rules land in two different classes.</para>
/// <para><b>Read on demand, never on a timer.</b> The list is a query across every conversation in the
/// directory and the answer only changes when somebody says something, so it is refreshed when the list is
/// opened and after the tile itself moves — not while an agent is replying, which is the one time the tile is
/// redrawing every frame.</para>
/// <para><b>The open conversation is always in the list, whether or not the store has it.</b> A tile opened on
/// a conversation nobody has said anything in has no row of its own yet, and a chooser whose selection is
/// empty reads as a tile that has lost its place.</para>
/// </remarks>
public sealed partial class ConversationChooser : ObservableObject
{
    private readonly IConversationStore _store;
    private readonly string _workingDirectory;
    private readonly Func<string> _current;
    private readonly Func<string> _currentAgentId;
    private readonly Func<ConversationSummary, string?> _refusal;
    private readonly Action<Action> _post;
    private readonly Action<ConversationSummary> _picked;
    private readonly Action _startNew;
    private bool _drawing;
    private int _latestRefresh;

    [ObservableProperty] private ConversationOption? _selected;

    /// <param name="store">Where the conversations are.</param>
    /// <param name="workingDirectory">Which of them are this workspace's.</param>
    /// <param name="current">The conversation this tile is showing.</param>
    /// <param name="currentAgentId">Which agent that is, for the row the store cannot describe yet.</param>
    /// <param name="refusal">Why an entry cannot be picked, or null when it can.</param>
    /// <param name="post">Runs on the thread the tile draws on.</param>
    /// <param name="picked">A pickable entry the user chose.</param>
    /// <param name="startNew">The user chose the row that starts a conversation.</param>
    public ConversationChooser(IConversationStore store, string workingDirectory, Func<string> current,
        Func<string> currentAgentId, Func<ConversationSummary, string?> refusal, Action<Action> post,
        Action<ConversationSummary> picked, Action startNew)
    {
        _startNew = startNew;
        _store = store;
        _workingDirectory = workingDirectory;
        _current = current;
        _currentAgentId = currentAgentId;
        _refusal = refusal;
        _post = post;
        _picked = picked;
        Draw([]);
    }

    /// <summary>
    /// The conversations to choose between, the open one included.
    /// </summary>
    /// <remarks>Always drawn, even when it holds only the open conversation: unlike the agent beside it, which
    /// the header already names, this is the only thing on screen that says <i>which</i> conversation this is.</remarks>
    public ObservableCollection<ConversationOption> Options { get; } = [];

    /// <summary>Reads the store off the caller's thread and redraws the list on the tile's.</summary>
    /// <remarks>A failed read leaves the list as it stands rather than emptying it: the conversation on screen
    /// is open and unaffected, and a chooser that empties itself says the work is gone.
    /// <para><b>Only the latest read is drawn.</b> Refreshes are fired without waiting and finish in any order,
    /// so a read begun before a delete that lands after the read begun after it would put the deleted
    /// conversation back as a row that opens an empty one under the id the user has just removed.</para></remarks>
    public async Task RefreshAsync()
    {
        var refresh = Interlocked.Increment(ref _latestRefresh);
        IReadOnlyList<ConversationSummary> stored;
        try
        {
            stored = await Task.Run(() => _store.List(_workingDirectory));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"[AgentConversation] Listing the conversations in {_workingDirectory} failed: {ex}");
            return;
        }

        _post(() =>
        {
            if (IsLatestRefresh(refresh)) Draw(stored);
        });
    }

    private bool IsLatestRefresh(int refresh) => refresh == Volatile.Read(ref _latestRefresh);

    /// <summary>Rebuilds the list from what the store answered, with the open conversation always in it.</summary>
    public void Draw(IReadOnlyList<ConversationSummary> stored)
    {
        var current = _current();
        // An empty id is a tile that has not been given one yet, not a conversation: synthesizing a row for
        // it would put a nameless entry at the top of the list until the next draw.
        var known = current.Length == 0 || stored.Any(c => c.Id == current)
            ? stored
            : [new ConversationSummary(current, _currentAgentId(), DateTimeOffset.Now, null), .. stored];

        WhileDrawing(() =>
        {
            Options.Clear();
            // First, because it is the one row here that is an action rather than a place, and because a list
            // grows: at the bottom it would move every time a conversation is added.
            Options.Add(ConversationOption.New);
            foreach (var summary in known) Options.Add(OptionFor(summary, current));
            Selected = Options.FirstOrDefault(option => option.IsCurrent);
        });
    }

    /// <summary>Puts the selection back on the open conversation, leaving the list as it stands.</summary>
    public void RestoreSelection() =>
        WhileDrawing(() => Selected = Options.FirstOrDefault(option => option.IsCurrent));

    partial void OnSelectedChanged(ConversationOption? value)
    {
        if (_drawing || value is null || value.IsCurrent) return;
        if (value.IsNew)
        {
            // Put back first: this row is an action, and left selected it would name the open conversation as
            // "New conversation" until the start finished and redrew the list.
            RestoreSelection();
            _startNew();
            return;
        }

        if (!value.IsPickable)
        {
            RestoreSelection();
            return;
        }

        _picked(value.Summary);
    }

    private ConversationOption OptionFor(ConversationSummary summary, string current)
    {
        var agentName = AiAgentCatalog.Find(summary.AgentId)?.DisplayName ?? summary.AgentId;
        if (summary.Id == current) return new ConversationOption(summary, agentName, true, true, null);
        var reason = _refusal(summary);
        return new ConversationOption(summary, agentName, false, reason is null, reason);
    }

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
