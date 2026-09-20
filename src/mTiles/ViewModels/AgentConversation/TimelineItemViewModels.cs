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

    /// <summary>Whether anything is open, which is what the summary's own chevron reports.</summary>
    /// <remarks>Derived from the files rather than kept beside them: with a flag of its own the header
    /// would go on pointing down after the last file was folded away by its own row.</remarks>
    public bool IsExpanded => Files.Any(f => f.IsExpanded);

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
        OnPropertyChanged(nameof(IsExpanded));
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

    private void OnFileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChangedFileViewModel.IsExpanded)) OnPropertyChanged(nameof(IsExpanded));
    }

    /// <summary>Open every file, or — where anything is already open — fold them all away.</summary>
    /// <remarks>The turn's whole diff used to be a separate thing the header opened, drawn under a list
    /// of the same files: two routes to the same lines, and on a turn touching five files the one that
    /// answered "what happened to this file" was the one that made you scroll. So the header is the
    /// same question asked of all of them at once, and there is one place a line of a diff is drawn.</remarks>
    [RelayCommand]
    private async Task ToggleDiffAsync()
    {
        if (IsExpanded)
        {
            foreach (var file in Files) file.Collapse();
            return;
        }

        await ExpandFilesWithinBudgetAsync();
    }

    /// <summary>How many files are read at once, and how many lines one press may draw.</summary>
    /// <remarks><see cref="DiffLines.MaxLines"/> guards a non-virtualising list and is per file, so
    /// asked of every file at once it multiplies by the number of files — a turn touching twenty of
    /// them would draw thirty thousand rows and spawn twenty git processes on one click. The batch
    /// keeps the round trips few without running the whole turn in parallel, and the budget is the
    /// same cap the old turn-wide diff had; the files past it stay folded and open one by one from
    /// their own rows. Reading and showing are two steps for exactly that reason: a batch read in
    /// parallel and then opened row by row is counted against the budget between files rather than
    /// between batches, which is what keeps four large files from drawing four times the cap. One
    /// file may still carry it past the line, because how long a diff is is not known until it has
    /// been read.</remarks>
    private const int FilesReadTogether = 4;
    private const int ExpandAllLineBudget = DiffLines.MaxLines;

    private async Task ExpandFilesWithinBudgetAsync()
    {
        var pending = Files.Where(f => !f.IsExpanded).ToList();
        var drawn = Files.Where(f => f.IsExpanded).Sum(f => f.Diff.Count);

        for (var i = 0; i < pending.Count && drawn < ExpandAllLineBudget; i += FilesReadTogether)
        {
            var batch = pending.Skip(i).Take(FilesReadTogether).ToList();
            await Task.WhenAll(batch.Select(f => f.LoadAsync()));

            foreach (var file in batch)
            {
                if (drawn >= ExpandAllLineBudget) break;
                file.Show();
                drawn += file.Diff.Count;
            }
        }
    }

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
    private IReadOnlyList<DiffLine> _diff = [];

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
        if (!IsExpanded) return ExpandAsync();
        Collapse();
        return Task.CompletedTask;
    }

    public void Collapse() => IsExpanded = false;

    /// <summary>Whether this row is showing that very file.</summary>
    public bool Describes(ChangedFile file) => _file == file;

    /// <summary>Open this file, reading its diff the first time and never again.</summary>
    /// <remarks>Open first and read second: the row is what the "loading" line is drawn inside, so a
    /// row opened after its read is a press that does nothing until the diff arrives.</remarks>
    public Task ExpandAsync()
    {
        Show();
        return LoadAsync();
    }

    /// <summary>Show what has been read, without reading anything.</summary>
    public void Show() => IsExpanded = true;

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
            // Numbered: this is git's own patch of the whole file, so the gutter is the file's own
            // line numbers. A tool's diff is a fragment and is read unnumbered.
            Diff = DiffLines.ParseFilePatch(await _loadDiff(_file));
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
