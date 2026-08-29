using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using mTiles.Models;

namespace mTiles.ViewModels;

/// <summary>
/// One clarifying question with the box its answer is typed into.
/// </summary>
/// <remarks>
/// <para>The questions used to arrive as a block of text in the transcript and be answered by editing a
/// numbered skeleton in the composer. That works, and it asks the reader to do the filing: match answer
/// three to question three, in a column that wraps, while the conversation above scrolls away. A
/// question that owns its own box cannot be answered against the wrong number.</para>
/// <para>A view model of its own, beside <see cref="GoalCriteriaEditor"/> and <see cref="GoalBadge"/>
/// rather than inside the tile's, because it holds one question's worth of state and nothing else. It
/// is also the whole of what a test needs to ask about option handling.</para>
/// </remarks>
public sealed partial class GoalQuestionAnswer : ObservableObject
{
    /// <param name="answered">Told whenever the box changes, so what the user has typed reaches the
    /// file. Optional, because the option-handling rules are worth testing without one.</param>
    public GoalQuestionAnswer(int number, GoalQuestion question, Action<string>? answered = null)
    {
        Number = number;
        Question = question.Question;
        Why = question.Why;
        _answer = question.Answer;
        _answered = answered;
        Options =
        [
            ..question.Options
                .Select(o => o.ReplaceLineEndings(" ").Trim())
                .Where(o => o.Length > 0)
                .Select(o => new GoalOption(o, new RelayCommand(() => Take(o))))
        ];
    }

    /// <summary>Its position, from one — the number the answer is filed under when the answers go back
    /// to the tool.</summary>
    public int Number { get; }

    public string Marker => $"{Number}.";
    public string Question { get; }
    public string Why { get; }

    /// <summary>
    /// The answers the tool offered, each carrying the command that takes it.
    /// </summary>
    /// <remarks>
    /// The command is on the option rather than on the question, and that is a decision about the
    /// markup: a chip bound to a command on its parent has to walk out of its own <c>ItemsControl</c>
    /// to find it, which nothing compiles and nothing can check — it fails as a chip that does nothing
    /// when clicked. Handing each option its own command removes the walk, and with it the failure.
    /// </remarks>
    public IReadOnlyList<GoalOption> Options { get; }

    public bool HasWhy => Why.Length > 0;
    public bool HasOptions => Options.Count > 0;

    [ObservableProperty] private string _answer = "";

    private readonly Action<string>? _answered;

    partial void OnAnswerChanged(string value) => _answered?.Invoke(value);

    /// <summary>
    /// This question and what has been typed against it, as the model the record is kept in.
    /// </summary>
    /// <remarks>
    /// A copy rather than the engine's own object. The round is written into the transcript at the
    /// moment it is answered and the pending set is cleared immediately afterwards; handing the record
    /// the live object would leave a message in a saved conversation pointing at something the next
    /// round is free to reuse. It is also what the copy button on a single question is built from, so
    /// the clipboard and the record are made the same way.
    /// </remarks>
    public GoalQuestion Snapshot() => new()
    {
        Question = Question,
        Why = Why,
        Answer = Answer.Trim(),
        Options = [..Options.Select(o => o.Text)],
    };

    /// <summary>
    /// Takes one of the offered answers into the box.
    /// </summary>
    /// <remarks>
    /// It replaces an empty box, and an answer that is itself one of the options — changing your mind
    /// between two offers is the common case and should not need a selection first. Anything else is
    /// text the user typed, and it is appended to rather than overwritten: the options are suggestions,
    /// and a suggestion that deletes a sentence somebody wrote is not a suggestion.
    /// </remarks>
    private void Take(string option)
    {
        var typed = Answer.Trim();
        Answer = typed.Length == 0 || Options.Any(o => o.Text == typed) ? option : $"{typed} {option}";
    }
}

/// <summary>One offered answer, and the command that puts it in the box.</summary>
public sealed record GoalOption(string Text, ICommand Use);
