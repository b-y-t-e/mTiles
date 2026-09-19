using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// One thing in the conversation's timeline, as the view binds to it.
/// </summary>
/// <remarks>
/// <b>Updated in place, not replaced.</b> A streaming message changes a hundred times a second, and a
/// view model replaced on every change is a control rebuilt on every change — the text flickers and a
/// selection in it is lost. <see cref="TimelineSync"/> hands each view model the newer record of its own
/// entry and replaces it only when the entry has become a different kind of thing.
/// </remarks>
public abstract partial class TimelineItemViewModel : ObservableObject
{
    public string Id { get; protected set; } = "";

    /// <summary>The record this was last drawn from.</summary>
    public object? Source { get; protected set; }

    /// <summary>Whether this view model can show <paramref name="entry"/> by updating itself.</summary>
    public abstract bool CanShow(object entry);

    public abstract void Update(object entry);
}

/// <summary>A message from the user or the assistant.</summary>
public sealed partial class MessageItemViewModel : TimelineItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isStreaming;

    public MessageItemViewModel(MessageEntry entry)
    {
        Role = entry.Role;
        Update(entry);
    }

    public MessageRole Role { get; }
    public bool IsUser => Role == MessageRole.User;
    public bool IsAssistant => Role == MessageRole.Assistant;

    /// <summary>The images sent with the message. They never change after it is sent.</summary>
    public IReadOnlyList<ImageAttachment> Images { get; private set; } = [];

    public bool HasImages => Images.Count > 0;
    public bool HasText => Text.Length > 0;

    public override bool CanShow(object entry) => entry is MessageEntry message && message.Role == Role;

    public override void Update(object entry)
    {
        var message = (MessageEntry)entry;
        Id = message.Id;
        Source = message;
        Text = message.Text;
        IsStreaming = message.IsStreaming;
        // By reference, never by count: a row is reused for the message that now stands in its place, and
        // one carrying as many images as the last would otherwise go on showing the old conversation's.
        if (!ReferenceEquals(Images, message.Images))
        {
            Images = message.Images;
            OnPropertyChanged(nameof(Images));
            OnPropertyChanged(nameof(HasImages));
        }

        OnPropertyChanged(nameof(HasText));
    }
}

/// <summary>
/// Everything the agent did between two messages — the unit that collapses.
/// </summary>
/// <remarks>
/// <para>Open while anything in it is still running, and closed once it has all finished, which is
/// t3code's rule and the one that keeps a long turn readable: the reply is what is read, and the work
/// behind it is one line until somebody asks for it. A group the user opened or closed by hand stays as
/// they left it.</para>
/// </remarks>
public sealed partial class WorkGroupItemViewModel : TimelineItemViewModel
{
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isRunning;
    private bool _userChoseExpansion;

    public WorkGroupItemViewModel(WorkGroupEntry entry) => Update(entry);

    public ObservableCollection<WorkItemViewModel> Items { get; } = [];

    public override bool CanShow(object entry) => entry is WorkGroupEntry;

    public override void Update(object entry)
    {
        var group = (WorkGroupEntry)entry;
        Id = group.Id;
        Source = group;
        TimelineSync.Sync(Items, group.Items, WorkItemViewModel.Create);

        var tools = group.Items.OfType<ToolCallItem>().ToList();
        IsRunning = tools.Any(tool => tool.State == ToolCallState.Running);
        Summary = SummaryOf(group.Items);
        if (!_userChoseExpansion) IsExpanded = IsRunning;
    }

    [RelayCommand]
    private void Toggle()
    {
        _userChoseExpansion = true;
        IsExpanded = !IsExpanded;
    }

    /// <summary>"3 commands · 2 edits · 1 failed" — what the work was, in the fewest words.</summary>
    internal static string SummaryOf(IEnumerable<WorkItem> items)
    {
        var list = items.ToList();
        var tools = list.OfType<ToolCallItem>().ToList();
        var parts = new List<string>();

        void Count(int n, string one, string many)
        {
            if (n > 0) parts.Add($"{n} {(n == 1 ? one : many)}");
        }

        Count(tools.Count(t => t.Kind == ToolKind.Command), "command", "commands");
        Count(tools.Count(t => t.Kind == ToolKind.FileChange), "edit", "edits");
        Count(tools.Count(t => t.Kind == ToolKind.FileRead), "read", "reads");
        Count(tools.Count(t => t.Kind is ToolKind.Search or ToolKind.WebFetch), "search", "searches");
        Count(tools.Count(t => t.Kind is ToolKind.Mcp or ToolKind.SubAgent or ToolKind.Other), "other tool", "other tools");
        Count(list.OfType<ReasoningItem>().Count(), "thought", "thoughts");
        Count(tools.Count(t => t.State is ToolCallState.Failed), "failed", "failed");
        Count(tools.Count(t => t.State is ToolCallState.Declined), "declined", "declined");

        return parts.Count > 0 ? string.Join(" · ", parts) : "Working";
    }
}

/// <summary>One thing inside a work group.</summary>
public abstract partial class WorkItemViewModel : TimelineItemViewModel
{
    public static WorkItemViewModel Create(object entry) => entry switch
    {
        ToolCallItem tool => new ToolCallItemViewModel(tool),
        ReasoningItem thought => new ReasoningItemViewModel(thought),
        DecisionItem decision => new DecisionItemViewModel(decision),
        _ => throw new ArgumentException($"No view model for {entry.GetType().Name}."),
    };
}

/// <summary>One tool call.</summary>
public sealed partial class ToolCallItemViewModel : WorkItemViewModel
{
    /// <summary>The output shown inline is its end: a build log's last lines are the ones that say how it
    /// went, and a whole log in a conversation is a wall.</summary>
    private const int OutputTail = 4000;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ToolKind _kind;
    [ObservableProperty] private ToolCallState _state;
    [ObservableProperty] private string? _command;
    [ObservableProperty] private string? _paths;
    [ObservableProperty] private string? _query;
    [ObservableProperty] private string? _input;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private IReadOnlyList<DiffLine> _diff = [];
    [ObservableProperty] private bool _isExpanded;

    public ToolCallItemViewModel(ToolCallItem tool) => Update(tool);

    public bool IsRunning => State == ToolCallState.Running;
    public bool IsFailed => State is ToolCallState.Failed or ToolCallState.Abandoned;
    public bool IsDeclined => State == ToolCallState.Declined;
    public bool HasOutput => Output.Length > 0;
    public bool HasDiff => Diff.Count > 0;
    public bool HasDetail => HasOutput || HasDiff || Command is not null || Paths is not null || Input is not null;

    public override bool CanShow(object entry) => entry is ToolCallItem;

    public override void Update(object entry)
    {
        var tool = (ToolCallItem)entry;
        var previous = Source as ToolCallItem;
        Id = tool.Id;
        Source = tool;
        Title = tool.Title;
        Name = tool.Name;
        Kind = tool.Kind;
        State = tool.State;
        Command = tool.Detail.Command;
        Paths = tool.Detail.Paths is { Count: > 0 } paths ? string.Join("\n", paths) : null;
        Query = tool.Detail.Query;
        Input = tool.Detail.Input;
        Output = tool.Output.Length > OutputTail ? "…" + tool.Output[^OutputTail..] : tool.Output;
        if (!ReferenceEquals(previous?.Detail.Diff, tool.Detail.Diff)) Diff = DiffLines.Parse(tool.Detail.Diff);

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsDeclined));
        OnPropertyChanged(nameof(HasOutput));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(HasDetail));
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>What the model thought, collapsed to its first line until opened.</summary>
public sealed partial class ReasoningItemViewModel : WorkItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isExpanded;

    public ReasoningItemViewModel(ReasoningItem thought) => Update(thought);

    public string FirstLine => Text.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line : "Thinking";

    public override bool CanShow(object entry) => entry is ReasoningItem;

    public override void Update(object entry)
    {
        var thought = (ReasoningItem)entry;
        Id = thought.Id;
        Source = thought;
        Text = thought.Text;
        OnPropertyChanged(nameof(FirstLine));
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>How an approval was answered.</summary>
public sealed partial class DecisionItemViewModel : WorkItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _allowed;

    public DecisionItemViewModel(DecisionItem decision) => Update(decision);

    public override bool CanShow(object entry) => entry is DecisionItem;

    public override void Update(object entry)
    {
        var decision = (DecisionItem)entry;
        Id = decision.Id;
        Source = decision;
        Allowed = decision.Decision is ApprovalDecision.Accept or ApprovalDecision.AcceptForSession;
        Text = decision.Decision switch
        {
            ApprovalDecision.Accept => $"Allowed: {decision.Title}",
            ApprovalDecision.AcceptForSession => $"Allowed for this session: {decision.Title}",
            ApprovalDecision.Decline => $"Denied: {decision.Title}",
            _ => $"Stopped at: {decision.Title}",
        };
    }
}

/// <summary>A plan the agent wrote for approval.</summary>
public sealed partial class PlanProposalItemViewModel : TimelineItemViewModel
{
    [ObservableProperty] private string _markdown = "";

    public PlanProposalItemViewModel(ProposedPlanEntry entry) => Update(entry);

    public override bool CanShow(object entry) => entry is ProposedPlanEntry;

    public override void Update(object entry)
    {
        var plan = (ProposedPlanEntry)entry;
        Id = plan.Id;
        Source = plan;
        Markdown = plan.Markdown;
    }
}

/// <summary>An answered round of questions, kept where it was asked.</summary>
public sealed partial class QuestionsRecordItemViewModel : TimelineItemViewModel
{
    public QuestionsRecordItemViewModel(QuestionsEntry entry) => Update(entry);

    public ObservableCollection<AnsweredQuestion> Answers { get; } = [];
    public bool WasDismissed { get; private set; }

    public override bool CanShow(object entry) => entry is QuestionsEntry;

    public override void Update(object entry)
    {
        var round = (QuestionsEntry)entry;
        Id = round.Id;
        Source = round;
        WasDismissed = round.Answers is null;
        Answers.Clear();
        foreach (var question in round.Questions)
        {
            var answer = round.Answers?.GetValueOrDefault(question.Id) is { Count: > 0 } chosen
                ? string.Join(", ", chosen)
                : "—";
            Answers.Add(new AnsweredQuestion(question.Text, answer));
        }

        OnPropertyChanged(nameof(WasDismissed));
    }
}

/// <summary>One question and what was answered.</summary>
public sealed record AnsweredQuestion(string Question, string Answer);

/// <summary>What a turn changed on disk, with its diff on request and a way back.</summary>
public sealed partial class CheckpointItemViewModel : TimelineItemViewModel
{
    private readonly Func<CheckpointEntry, string?, Task<string>> _loadDiff;
    private readonly Func<CheckpointEntry, Task> _restore;

    [ObservableProperty] private bool _restored;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private IReadOnlyList<DiffLine> _diff = [];
    [ObservableProperty] private bool _isLoadingDiff;

    public CheckpointItemViewModel(CheckpointEntry entry,
        Func<CheckpointEntry, string?, Task<string>> loadDiff, Func<CheckpointEntry, Task> restore)
    {
        _loadDiff = loadDiff;
        _restore = restore;
        Update(entry);
    }

    public ObservableCollection<ChangedFileViewModel> Files { get; } = [];

    public string Summary
    {
        get
        {
            var count = ((CheckpointEntry)Source!).Files.Count;
            return $"{count} {(count == 1 ? "file" : "files")} changed";
        }
    }

    // The totals are two strings rather than part of the summary so each can wear its own colour.
    public string Additions => $"+{((CheckpointEntry)Source!).Files.Sum(f => f.Additions)}";
    public string Deletions => $"−{((CheckpointEntry)Source!).Files.Sum(f => f.Deletions)}";

    public override bool CanShow(object entry) => entry is CheckpointEntry;

    public override void Update(object entry)
    {
        var checkpoint = (CheckpointEntry)entry;
        Id = checkpoint.Id;
        Source = checkpoint;
        Restored = checkpoint.Restored;
        if (Files.Count != checkpoint.Files.Count)
        {
            Files.Clear();
            foreach (var file in checkpoint.Files) Files.Add(new ChangedFileViewModel(file));
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Additions));
        OnPropertyChanged(nameof(Deletions));
    }

    [RelayCommand]
    private async Task ToggleDiffAsync()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded || Diff.Count > 0) return;

        IsLoadingDiff = true;
        try
        {
            Diff = DiffLines.Parse(await _loadDiff((CheckpointEntry)Source!, null));
        }
        finally
        {
            IsLoadingDiff = false;
        }
    }

    [RelayCommand]
    private Task RestoreAsync() => _restore((CheckpointEntry)Source!);
}

/// <summary>One file a turn changed.</summary>
public sealed class ChangedFileViewModel(ChangedFile file)
{
    public string Path => file.Path;
    public string Additions => $"+{file.Additions}";
    public string Deletions => $"−{file.Deletions}";
    public string Marker => file.Kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        _ => "M",
    };
}

/// <summary>Something said by the session rather than by the agent.</summary>
public sealed partial class NoticeItemViewModel : TimelineItemViewModel
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private NoticeLevel _level;

    public NoticeItemViewModel(NoticeEntry entry) => Update(entry);

    public bool IsError => Level == NoticeLevel.Error;
    public bool IsWarning => Level == NoticeLevel.Warning;

    public override bool CanShow(object entry) => entry is NoticeEntry;

    public override void Update(object entry)
    {
        var notice = (NoticeEntry)entry;
        Id = notice.Id;
        Source = notice;
        Text = notice.Text;
        Level = notice.Level;
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsWarning));
    }
}
