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
        Questions = [.. round.Questions.Select(q => new QuestionViewModel(q))];
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
    [ObservableProperty] private string _customAnswer = "";

    public QuestionViewModel(UserQuestion question)
    {
        Question = question;
        Options = [.. question.Options.Select(o => new QuestionChoiceViewModel(o, this))];
    }

    public UserQuestion Question { get; }
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
