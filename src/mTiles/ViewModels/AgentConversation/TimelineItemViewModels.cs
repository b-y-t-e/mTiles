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
    /// <summary>
    /// What to write on the rule above this item, where the work moved to another account here.
    /// </summary>
    /// <remarks>
    /// <para><b>On the item rather than an item of its own.</b> <see cref="TimelineSync"/> matches view
    /// models to records by position — the reducer only appends — so a separator inserted between them
    /// would shift every index after it and redraw the conversation from the seam down.</para>
    /// <para>Null on all but the first entry of a new stretch, which is what makes it a seam and not a
    /// label on every line: a conversation that never changed agent or login carries none at all.</para>
    /// </remarks>
    [ObservableProperty] private string? _seam;

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
/// <para>Open for the whole of the turn it belongs to and folded once that turn is over, which is
/// t3code's rule and the one that keeps a long turn readable: the reply is what is read, and the work
/// behind it is one line until somebody asks for it. The turn is the conversation's answer, so it is
/// told (<see cref="FollowTurn"/>). A group the user opened or closed by hand stays as they left it.</para>
/// </remarks>
public sealed partial class WorkGroupItemViewModel : TimelineItemViewModel
{
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private string _summary = "";
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

        Summary = SummaryOf(group.Items);
    }

    /// <summary>Open for as long as this group is the work of a turn that is still going.</summary>
    /// <remarks>Whether a turn is still going is the conversation's answer and never this group's, which
    /// is why it is told rather than worked out from the tools in it: nothing is running between one
    /// tool finishing and the next being started, and a tool fast enough to start and finish between two
    /// draws is a group that never has a running tool at all — read that way, both fold the list shut
    /// and open again under the reader in the middle of a turn. A group the user opened or closed by
    /// hand is left as they left it, and a group replayed out of the store is folded, because no turn of
    /// this session is holding it open.</remarks>
    public void FollowTurn(bool isTheLiveTurnsWork)
    {
        if (_userChoseExpansion) return;
        IsExpanded = isTheLiveTurnsWork;
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffMarkdown))]
    private IReadOnlyList<DiffLine> _diff = [];

    [ObservableProperty] private bool _isExpanded;

    public ToolCallItemViewModel(ToolCallItem tool) => Update(tool);

    /// <summary>The patch as the viewer that draws every message here reads it.</summary>
    public string DiffMarkdown => AgentConversation.DiffMarkdown.For(Diff);

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
    private readonly Func<CheckpointEntry, ChangedFile?, Task<string>> _loadDiff;
    private readonly Func<CheckpointEntry, Task> _restore;

    [ObservableProperty] private bool _restored;

    public CheckpointItemViewModel(CheckpointEntry entry,
        Func<CheckpointEntry, ChangedFile?, Task<string>> loadDiff, Func<CheckpointEntry, Task> restore)
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

    /// <summary>Whether the list of changed files is showing. Folded, until somebody asks.</summary>
    /// <remarks>It used to open itself, which on a turn of thirty files put a page of paths between
    /// the reply and whatever was said next — and the line above it already says how many files
    /// changed and by how much, which is the whole of what most readers want from it.</remarks>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>What pressing the summary does, said in the words of what is on screen now.</summary>
    public string FoldTip => IsExpanded
        ? AnyFileIsOpen ? "Hide the changed files and their diffs" : "Hide the changed files"
        : "Show the changed files";

    public override bool CanShow(object entry) => entry is CheckpointEntry;

    public override void Update(object entry)
    {
        var checkpoint = (CheckpointEntry)entry;
        Id = checkpoint.Id;
        Source = checkpoint;
        Restored = checkpoint.Restored;
        if (!FilesAlreadyShow(checkpoint.Files))
        {
            foreach (var file in Files) file.PropertyChanged -= OnFileChanged;
            Files.Clear();
            foreach (var file in checkpoint.Files)
            {
                // The checkpoint is read at the moment the diff is asked for, not captured here: this
                // view model is updated in place as the entry is rewritten, and a file row holding the
                // entry it was built from would go on asking about a checkpoint that has moved on.
                var row = new ChangedFileViewModel(file, changed => _loadDiff((CheckpointEntry)Source!, changed));
                row.PropertyChanged += OnFileChanged;
                Files.Add(row);
            }
        }

        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Additions));
        OnPropertyChanged(nameof(Deletions));
        OnPropertyChanged(nameof(AnyFileIsOpen));
        OnPropertyChanged(nameof(FoldTip));
    }

    /// <summary>Whether the rows on screen are already these very files.</summary>
    /// <remarks>The whole file, not how many there are: this view model is reused for whatever
    /// checkpoint lands in its place — the same position in another conversation — and two turns
    /// touching the same number of files would otherwise keep the old rows. Those name the previous
    /// conversation's paths, and a row still open shows that turn's patch; asked to open, it hands git
    /// a path this checkpoint has never heard of and comes back with nothing. <see cref="ChangedFile"/>
    /// is a record, so one comparison covers the path, the kind, the counts and a rename's old name.</remarks>
    private bool FilesAlreadyShow(IReadOnlyList<ChangedFile> files) =>
        Files.Count == files.Count && !Files.Where((row, i) => !row.Describes(files[i])).Any();

    /// <summary>Whether any file under the summary has its diff open.</summary>
    /// <remarks>What the summary's tooltip says folding the list away would hide. A property rather
    /// than a count in the markup, because it has to be raised again when a row is folded from its own
    /// chevron — which is what <see cref="OnFileChanged"/> is for.</remarks>
    public bool AnyFileIsOpen => Files.Any(f => f.IsExpanded);

    private void OnFileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChangedFileViewModel.IsExpanded)) return;
        OnPropertyChanged(nameof(AnyFileIsOpen));
        OnPropertyChanged(nameof(FoldTip));
    }

    /// <summary>Show or hide the list of changed files. Reads nothing and closes nothing.</summary>
    /// <remarks>
    /// <para>A file's own diff stays as it was left: hidden with the list and there again when the list
    /// comes back. Folding a summary away is a statement about the room on screen, not about what one
    /// wants to see inside each file.</para>
    /// <para>It used to open every file's diff at once, which is the trouble with answering two
    /// questions with one mark: on a turn touching twenty files that is tens of thousands of rows in a
    /// list that does not virtualise and twenty git processes on one press, so a budget stopped it part
    /// way down — and then the rows past it were folded for a reason nothing on screen gave. "What
    /// happened to this file" is the rows' own question, and it is the only one they answer now.</para>
    /// </remarks>
    [RelayCommand]
    private void ToggleDiff() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(FoldTip));

    [RelayCommand]
    private Task RestoreAsync() => _restore((CheckpointEntry)Source!);
}

/// <summary>One file a turn changed, with its own diff under it on request.</summary>
public sealed partial class ChangedFileViewModel : ObservableObject
{
    private readonly ChangedFile _file;
    private readonly Func<ChangedFile, Task<string>> _loadDiff;

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNothingToShow))]
    [NotifyPropertyChangedFor(nameof(DiffMarkdown))]
    private IReadOnlyList<DiffLine> _diff = [];

    /// <summary>The patch as the viewer that draws every message here reads it.</summary>
    public string DiffMarkdown => AgentConversation.DiffMarkdown.For(Diff);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNothingToShow))]
    private bool _isLoadingDiff;

    /// <summary>Whether the diff has been asked for, whatever came back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNothingToShow))]
    private bool _wasRead;

    public ChangedFileViewModel(ChangedFile file, Func<ChangedFile, Task<string>> loadDiff)
    {
        _file = file;
        _loadDiff = loadDiff;
    }

    /// <summary>The patch as git wrote it, for the button that copies the whole of it.</summary>
    /// <remarks>Kept beside the parsed lines rather than rebuilt from them: the rows on screen have had
    /// their markers taken off and their header dropped, so putting them back together would produce
    /// something close to a patch and not one — and what somebody copies a diff for is to apply it or
    /// to paste it somewhere that reads diffs.</remarks>
    [ObservableProperty] private string _patchText = "";

    /// <summary>Whether the row has been read and has no patch to draw.</summary>
    /// <remarks>An empty answer is what a git that could not be asked gives back — the checkpoints run
    /// git without throwing, so a ref that is gone or a repository somebody else has locked comes back
    /// as no output at all. Drawn as a blank row it is indistinguishable from a file nothing happened
    /// to, which is the one thing a row in this list promises is not the case.</remarks>
    public bool HasNothingToShow => WasRead && !IsLoadingDiff && Diff.Count == 0;

    public string Path => _file.Path;
    public string Additions => $"+{_file.Additions}";
    public string Deletions => $"−{_file.Deletions}";
    public string Marker => _file.Kind switch
    {
        FileChangeKind.Added => "A",
        FileChangeKind.Deleted => "D",
        FileChangeKind.Renamed => "R",
        _ => "M",
    };

    [RelayCommand]
    private Task ToggleAsync()
    {
        if (IsExpanded)
        {
            IsExpanded = false;
            return Task.CompletedTask;
        }

        // Open first and read second: the row is what the "loading" line is drawn inside, so a row
        // opened after its read is a press that does nothing until the diff arrives.
        IsExpanded = true;
        return LoadAsync();
    }

    /// <summary>Whether this row is showing that very file.</summary>
    public bool Describes(ChangedFile file) => _file == file;

    /// <summary>Read this file's diff, the first time and never again.</summary>
    /// <remarks>A checkpoint's diff is what the turn did and cannot change afterwards, so the read is
    /// kept: folding a file away and opening it again is free, which is what makes flicking through
    /// five files bearable.</remarks>
    public async Task LoadAsync()
    {
        // Read once, not "read until something comes back": an empty answer is an answer, and judging
        // on the lines drawn would spawn a fresh git process every time such a row was folded and
        // opened again.
        if (WasRead || IsLoadingDiff) return;

        IsLoadingDiff = true;
        try
        {
            // The file itself, not its path: a rename has two names and only the file knows both.
            // Read as a file's own patch, so the header naming the file is dropped — the row above it
            // already says which file this is. A tool's diff is a fragment and keeps its header.
            var patch = await _loadDiff(_file);
            PatchText = patch;
            Diff = DiffLines.ParseFilePatch(patch);
        }
        finally
        {
            IsLoadingDiff = false;
            WasRead = true;
        }
    }
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

/// <summary>Where the work was handed to another agent, with what it was handed shown on request.</summary>
/// <remarks>
/// <para><b>The brief is folded, not hidden.</b> It is a page of Markdown nobody wants between two messages
/// every time they scroll past — and it is also the only account there is of what the next agent was
/// actually told, so it has to be reachable from the conversation rather than from a log.</para>
/// <para>Not drawn as a notice: a notice says something went wrong or is worth knowing, and this is a thing
/// the user did. It is also the one entry whose own account differs from the account of everything after
/// it, which is what the seam below it is drawn from.</para>
/// </remarks>
public sealed partial class HandoverItemViewModel : TimelineItemViewModel
{
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _brief = "";
    [ObservableProperty] private bool _isBriefShown;

    public HandoverItemViewModel(HandoverEntry entry) => Update(entry);

    public string ToggleLabel => IsBriefShown ? "Hide what it was told" : "Show what it was told";

    public override bool CanShow(object entry) => entry is HandoverEntry;

    public override void Update(object entry)
    {
        var handover = (HandoverEntry)entry;
        Id = handover.Id;
        Source = handover;
        Headline = handover.From is { } from
            ? $"The work was handed from {StoredSessionPolicy.AccountLabel(from)} to " +
              $"{StoredSessionPolicy.AccountLabel(handover.To)}."
            : $"The work was handed to {StoredSessionPolicy.AccountLabel(handover.To)}.";
        Brief = handover.Brief;
    }

    [RelayCommand]
    private void ToggleBrief()
    {
        IsBriefShown = !IsBriefShown;
        OnPropertyChanged(nameof(ToggleLabel));
    }
}
