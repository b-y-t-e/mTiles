using mTiles.AgentSessions.Events;
using mTiles.ViewModels.AgentConversation;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// What the copy button beside one question hands over.
/// </summary>
/// <remarks>The whole of the question, and that now includes the reason under each offered answer: an
/// offered answer is a button, so what it says cannot be selected on screen the way the question itself
/// can — and whenever the decision is about behaviour, those descriptions are the content of the
/// question rather than a gloss on it.</remarks>
public class QuestionCopyTests
{
    [Fact]
    public void A_question_is_copied_with_the_reason_under_each_answer()
    {
        var question = new QuestionViewModel(new UserQuestion("q", "Scope", "Where do I start?",
        [
            new QuestionOption("Restore and finish", "Three conflicts, then the build."),
            new QuestionOption("Backend only"),
        ], MultiSelect: false, AllowsCustomAnswer: true), number: 2);

        var copied = question.CopyText;

        Assert.Contains("Scope", copied);
        Assert.Contains("Where do I start?", copied);
        Assert.Contains("- Restore and finish: Three conflicts, then the build.", copied);
        Assert.Contains("- Backend only", copied);
        Assert.DoesNotContain(">", copied);
    }

    /// <summary>The answer so far travels with it — a question is taken next door to work something out.</summary>
    [Fact]
    public void What_has_been_answered_so_far_goes_too()
    {
        var question = new QuestionViewModel(new UserQuestion("q", null, "Which?",
            [new QuestionOption("Red")], MultiSelect: false, AllowsCustomAnswer: true));

        question.Options[0].IsSelected = true;
        question.CustomAnswer = "or blue";

        Assert.Contains("> Red, or blue", question.CopyText);
    }
}
