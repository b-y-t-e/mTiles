using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.AgentSessions.Events;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>An approval the agent is waiting on, with one button per answer it offers.</summary>
public sealed partial class ApprovalRequestViewModel : ObservableObject
{
    public ApprovalRequestViewModel(ApprovalRequested request, Func<string, ApprovalDecision, Task> answer)
    {
        Request = request;
        Options = [.. request.Options.Select(option => new ApprovalOptionViewModel(option,
            () => answer(request.RequestId, option.Decision)))];
        Detail = DiffLines.Parse(request.Detail).Any(line => line.Kind is DiffLineKind.Added or DiffLineKind.Removed)
            ? DiffLines.Parse(request.Detail)
            : [];
    }

    public ApprovalRequested Request { get; }
    public string Title => Request.Title;

    /// <summary>The detail as plain text, where it is not a diff.</summary>
    public string? Text => Detail.Count > 0 ? null : Request.Detail;

    /// <summary>The detail as a diff, where it is one.</summary>
    public IReadOnlyList<DiffLine> Detail { get; }

    /// <summary>The patch as the viewer that draws every message here reads it.</summary>
    public string DetailMarkdown => DiffMarkdown.For(Detail);

    public bool HasDiff => Detail.Count > 0;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public IReadOnlyList<ApprovalOptionViewModel> Options { get; }

    public string KindLabel => Request.Kind switch
    {
        ApprovalKind.Command => "Run a command?",
        ApprovalKind.FileChange => "Change files?",
        ApprovalKind.FileRead => "Read files?",
        _ => "Allow?",
    };
}

/// <summary>One answer to an approval.</summary>
public sealed partial class ApprovalOptionViewModel(ApprovalOption option, Func<Task> answer) : ObservableObject
{
    public string Label => option.Label;
    public bool IsPrimary => option.Decision == ApprovalDecision.Accept;
    public bool IsDanger => option.Decision == ApprovalDecision.Cancel;

    [RelayCommand]
    private Task ChooseAsync() => answer();
}

/// <summary>A round of questions the agent is waiting on.</summary>
public sealed partial class QuestionRoundViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>?, Task> _answer;

    public QuestionRoundViewModel(QuestionsAsked round,
        Func<string, IReadOnlyDictionary<string, IReadOnlyList<string>>?, Task> answer)
    {
        Round = round;
        _answer = answer;
        Questions = [.. round.Questions.Select((q, index) => new QuestionViewModel(q, index + 1))];
    }

    public QuestionsAsked Round { get; }
    public ObservableCollection<QuestionViewModel> Questions { get; }

    [RelayCommand]
    private Task SubmitAsync() =>
        _answer(Round.RequestId, Questions.ToDictionary(q => q.Question.Id, q => q.Answer()));

    [RelayCommand]
    private Task DismissAsync() => _answer(Round.RequestId, null);
}

/// <summary>One question, with its choices and a field for an answer of one's own.</summary>
public sealed partial class QuestionViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    private string _customAnswer = "";

    public QuestionViewModel(UserQuestion question, int number = 1)
    {
        Question = question;
        Number = number;
        Options = [.. question.Options.Select(o => new QuestionChoiceViewModel(o, this))];
    }

    public UserQuestion Question { get; }
    public int Number { get; }

    /// <summary>The question's place in its round, in its own column — the Goal tile's marker.</summary>
    public string Marker => $"{Number}.";

    /// <summary>What the copy button beside the question takes: the question, what it offers, and the answer
    /// so far — a question is a thing taken next door to look something up with.</summary>
    /// <remarks>Raised as the answer is typed or a choice is picked, because the button is bound to this
    /// string: bound to the question itself, the converter ran once and the clipboard kept the empty
    /// answer it was realised with.</remarks>
    public string CopyText => string.Join(Environment.NewLine, new[]
    {
        Header, Text,
        Options.Count > 0 ? string.Join(" / ", Options.Select(o => o.Label)) : null,
        Answer() is { Count: > 0 } answer ? "> " + string.Join(", ", answer) : null,
    }.Where(line => !string.IsNullOrWhiteSpace(line)));

    public string Text => Question.Text;
    public string? Header => Question.Header;
    public bool HasHeader => !string.IsNullOrWhiteSpace(Question.Header);
    public bool AllowsCustomAnswer => Question.AllowsCustomAnswer;
    public IReadOnlyList<QuestionChoiceViewModel> Options { get; }

    /// <summary>The chosen labels, and whatever was typed after them.</summary>
    public IReadOnlyList<string> Answer()
    {
        var chosen = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
        if (!string.IsNullOrWhiteSpace(CustomAnswer)) chosen.Add(CustomAnswer.Trim());
        return chosen;
    }

    /// <summary>A single-choice question keeps one choice at a time.</summary>
    internal void Selected(QuestionChoiceViewModel choice)
    {
        OnPropertyChanged(nameof(CopyText));
        if (Question.MultiSelect || !choice.IsSelected) return;
        foreach (var other in Options.Where(o => !ReferenceEquals(o, choice))) other.IsSelected = false;
    }
}

/// <summary>One choice a question offers.</summary>
public sealed partial class QuestionChoiceViewModel(QuestionOption option, QuestionViewModel owner) : ObservableObject
{
    [ObservableProperty] private bool _isSelected;

    public string Label => option.Label;
    public string? Description => option.Description;
    public bool HasDescription => !string.IsNullOrWhiteSpace(option.Description);

    partial void OnIsSelectedChanged(bool value) => owner.Selected(this);
}
