using System.Text;
using Avalonia.Controls.Templates;
using mTiles.Models;
using mTiles.Services;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Which control draws a message, and what the copy button gets — the two halves of "a review is rows
/// now" that the user actually sees.
/// </summary>
public class GoalMessageRenderingTests
{
    private static GoalMessage Message(GoalMessageRole role, string text, bool markdown = false,
        params GoalFinding[] findings) =>
        new() { Role = role, Text = text, Markdown = markdown, Findings = [..findings] };

    private static GoalFinding Finding(GoalSeverity severity, string title, string detail = "") =>
        new() { Severity = severity, Title = title, Detail = detail };

    /// <summary>
    /// Findings outrank markdown, and the order is the decision rather than a coincidence of how the
    /// conditional was written.
    /// </summary>
    /// <remarks>
    /// A review is the one message that is both: written by the tool, so it is marked as markdown, and
    /// carrying rows, which markdown cannot draw. Asked the other way round the rows would never
    /// appear on the one message type they exist for.
    /// </remarks>
    [Fact]
    public void A_review_is_drawn_as_findings_even_though_it_is_also_markdown()
    {
        var template = new GoalMessageTemplate
        {
            Findings = new FuncDataTemplate<GoalMessage>((_, _) => new Avalonia.Controls.TextBlock { Tag = "findings" }),
            Markdown = new FuncDataTemplate<GoalMessage>((_, _) => new Avalonia.Controls.TextBlock { Tag = "markdown" }),
            Plain = new FuncDataTemplate<GoalMessage>((_, _) => new Avalonia.Controls.TextBlock { Tag = "plain" }),
        };

        Assert.Equal("findings", Built(template,
            Message(GoalMessageRole.Assistant, "Goal not met", markdown: true,
                Finding(GoalSeverity.Error, "Broken"))));

        Assert.Equal("markdown", Built(template, Message(GoalMessageRole.Assistant, "# plan", markdown: true)));
        Assert.Equal("plain", Built(template, Message(GoalMessageRole.User, "a goal")));

        // Markdown is only ever the tool's own words: anything this application composed keeps its
        // columns, which are made of spaces.
        Assert.Equal("plain", Built(template, Message(GoalMessageRole.System, "1. a", markdown: true)));
    }

    /// <summary>
    /// A round of questions outranks all three of the others.
    /// </summary>
    /// <remarks>
    /// The same decision the findings make and for the same reason: the message is composed here, so
    /// every other answer is false and the order only decides which false one is reached. It is asked
    /// first because it is the newest, and because a round carries neither findings nor markdown — so
    /// getting the order wrong here has no symptom until somebody adds a message that is both.
    /// </remarks>
    [Fact]
    public void A_round_of_questions_is_drawn_as_questions()
    {
        var template = new GoalMessageTemplate
        {
            Questions = new FuncDataTemplate<GoalMessage>((_, _) => new Avalonia.Controls.TextBlock { Tag = "questions" }),
            Findings = new FuncDataTemplate<GoalMessage>((_, _) => new Avalonia.Controls.TextBlock { Tag = "findings" }),
            Markdown = new FuncDataTemplate<GoalMessage>((_, _) => new Avalonia.Controls.TextBlock { Tag = "markdown" }),
            Plain = new FuncDataTemplate<GoalMessage>((_, _) => new Avalonia.Controls.TextBlock { Tag = "plain" }),
        };

        var round = Message(GoalMessageRole.Assistant, "1. Which file?", markdown: true);
        round.Questions = [new GoalQuestion { Question = "Which file?", Answer = "appsettings.json" }];

        Assert.Equal("questions", Built(template, round));

        // A goal file written before rounds were kept has the same text and no questions of its own.
        // It falls through to the plain template and is drawn as the numbered paragraph it always was.
        Assert.Equal("plain", Built(template, Message(GoalMessageRole.Assistant, "1. Which file?")));
    }

    /// <summary>
    /// The record of a round says what was asked and what was answered, both.
    /// </summary>
    /// <remarks>
    /// It is the message's own text, so it is what the clipboard gets and what anything that cannot
    /// draw the rows falls back to. A question left blank still appears: it was asked, and a round of
    /// two answered once is a round of two.
    /// </remarks>
    [Fact]
    public void A_recorded_round_carries_the_questions_and_the_answers()
    {
        var text = GoalTranscript.Answered(
        [
            new GoalQuestion
            {
                Question = "Which file holds the port?",
                Why = "There are two candidates.",
                Options = ["appsettings.json", "launchSettings.json"],
                Answer = "appsettings.json",
            },
            new GoalQuestion { Question = "Sync or async?" },
        ]);

        Assert.StartsWith("1. Which file holds the port?", text);
        Assert.Contains("There are two candidates.", text);
        Assert.Contains("launchSettings.json", text);
        Assert.Contains("appsettings.json", text);
        Assert.Contains("2. Sync or async?", text);

        // The answered one is marked with the transcript's own "you said" glyph; the unanswered one
        // carries no answer line at all rather than an empty one.
        Assert.Contains("\u276F appsettings.json", text);
        Assert.EndsWith("2. Sync or async?", text);
    }

    /// <summary>One question copied on its own reads as the same block, without the number — which is a
    /// position in a round, and what is copied out of a round is pasted somewhere the round is not.
    /// </summary>
    [Fact]
    public void Copying_one_question_takes_its_reason_its_offers_and_its_answer()
    {
        var copied = GoalTranscript.Copyable(new GoalQuestion
        {
            Question = "Which file holds the port?",
            Why = "There are two candidates.",
            Options = ["appsettings.json", "launchSettings.json"],
            Answer = "appsettings.json",
        });

        Assert.StartsWith("Which file holds the port?", copied);
        Assert.Contains("There are two candidates.", copied);
        Assert.Contains("launchSettings.json", copied);
        Assert.Contains("\u276F appsettings.json", copied);
        Assert.DoesNotContain("1.", copied);
    }

    /// <summary>
    /// One finding copied on its own reads exactly as it does inside the review it came from.
    /// </summary>
    /// <remarks>
    /// The two are built by one method, which is the whole of the guarantee: a second spelling of a
    /// finding is how the row and the clipboard come to disagree about what the reviewer said. The lone
    /// copy loses the blank line that separates two findings and nothing else.
    /// </remarks>
    [Fact]
    public void Copying_one_finding_reads_as_it_does_inside_the_review()
    {
        var finding = Finding(GoalSeverity.Error, "Total ignores discounts", "Sum() runs before ApplyDiscount().");

        var alone = GoalTranscript.Copyable(finding);
        var whole = GoalTranscript.Copyable(Message(GoalMessageRole.Assistant, "Goal not met", true, finding));

        Assert.Contains("Total ignores discounts", alone);
        Assert.Contains("Sum() runs before ApplyDiscount().", alone);
        Assert.DoesNotContain("Goal not met", alone);
        Assert.EndsWith(alone, whole);
    }

    private static string? Built(GoalMessageTemplate template, GoalMessage message) =>
        (template.Build(message) as Avalonia.Controls.TextBlock)?.Tag as string;

    /// <summary>
    /// The clipboard gets the defects the verdict is counting, not just the verdict.
    /// </summary>
    /// <remarks>
    /// The transcript draws findings as rows, so a review message's own <c>Text</c> is only its head —
    /// "Goal not met · 2 errors". Copying that alone hands somebody a count with the things it counted
    /// missing, which is the half they wanted.
    /// </remarks>
    [Fact]
    public void Copying_a_review_takes_its_findings_with_it()
    {
        var message = Message(GoalMessageRole.Assistant, "Goal not met · 1 error", markdown: true,
            Finding(GoalSeverity.Error, "Total ignores discounts", "Sum() runs before ApplyDiscount()."));

        var copied = GoalTranscript.Copyable(message);

        Assert.StartsWith("Goal not met · 1 error", copied);
        Assert.Contains("Total ignores discounts", copied);
        Assert.Contains("Sum() runs before ApplyDiscount().", copied);
    }

    [Fact]
    public void Copying_an_ordinary_message_is_exactly_its_text()
    {
        var message = Message(GoalMessageRole.User, "make the tests pass");

        Assert.Equal("make the tests pass", GoalTranscript.Copyable(message));
    }

    /// <summary>What a goal file written before findings became rows holds: the whole review flattened
    /// into the text, and no findings of its own. It is copied as it stands.</summary>
    [Fact]
    public void An_older_message_that_carries_its_findings_in_its_text_is_copied_unchanged()
    {
        var message = Message(GoalMessageRole.Assistant,
            "Goal not met · 1 error\n\nerror\n  Total ignores discounts", markdown: true);

        Assert.Equal(message.Text, GoalTranscript.Copyable(message));
    }

    /// <summary>A record of the shape the head takes, so the two halves stay joinable: the copy is the
    /// head, then the findings, with the same builder production uses.</summary>
    [Fact]
    public void The_head_and_the_findings_join_into_what_the_clipboard_gets()
    {
        var review = new GoalReviewResult
        {
            GoalMet = false, WasStructured = true,
            Findings = [Finding(GoalSeverity.Blocker, "Token in the log")],
        };

        var head = GoalTranscript.ReviewHead(review);
        var sb = new StringBuilder(head);
        GoalTranscript.AppendFindings(sb, GoalTranscript.InOrder(review.Findings));

        var message = Message(GoalMessageRole.Assistant, head, markdown: true,
            Finding(GoalSeverity.Blocker, "Token in the log"));

        Assert.Equal(sb.ToString(), GoalTranscript.Copyable(message));
    }

    /// <summary>
    /// The two buttons under a review's verdict hand over the list and not the bookkeeping.
    /// </summary>
    /// <remarks>
    /// Copying the whole list is the same text as copying every row of it one at a time, which is what
    /// makes the bulk button worth having rather than a second spelling of the review. The verdict line
    /// stays out: what is being pasted somewhere else is the defects, and this tile's counts in the
    /// middle of them are noise where they land.
    /// </remarks>
    [Fact]
    public void Copying_a_whole_list_of_findings_reads_as_the_rows_do()
    {
        var first = Finding(GoalSeverity.Error, "Total ignores discounts", "Sum() runs first.");
        var second = Finding(GoalSeverity.Suggestion, "Rename the local");

        var both = GoalTranscript.Copyable([first, second]);

        Assert.StartsWith(GoalTranscript.Copyable(first), both);
        Assert.EndsWith(GoalTranscript.Copyable(second), both);
        Assert.DoesNotContain("Goal not met", both);
    }

    /// <summary>
    /// The shape a copied finding lands in, pinned: a bracketed severity and its place on one line,
    /// then the title, then the detail, with a blank line between two of them.
    /// </summary>
    /// <remarks>
    /// This text is only ever read somewhere this application has no say over — an issue, a message,
    /// another tool's prompt — so the things that make a monospace column (a padded severity, a
    /// two-space indent under it) are noise there, and <c>suggest</c> is a word with its ending cut
    /// off. Written down because it is the sort of formatting that gets tidied back the other way by
    /// somebody reading only the transcript.
    /// </remarks>
    [Fact]
    public void A_copied_finding_reads_as_a_tag_a_title_and_its_detail()
    {
        var copied = GoalTranscript.Copyable([
            new GoalFinding
            {
                Severity = GoalSeverity.Suggestion,
                File = "src/Basket.cs", Line = 12, Category = "naming",
                Title = "Rename the local", Detail = "It is called x.\nTwice.",
            },
            Finding(GoalSeverity.Error, "Total ignores discounts"),
        ]);

        Assert.Equal(
            "[suggestion] src/Basket.cs:12 [naming]\n" +
            "Rename the local\n" +
            "It is called x.\n" +
            "Twice.\n" +
            "\n" +
            "[error]\n" +
            "Total ignores discounts",
            copied);
    }

    /// <summary>
    /// Which of the two bulk buttons a review offers, and what "problems" counts.
    /// </summary>
    /// <remarks>
    /// Both are hidden where they would repeat something already on screen: one finding already has its
    /// own copy button, and a list that is all problems — or none — would put two buttons side by side
    /// that hand over identical text.
    /// </remarks>
    [Theory]
    // findings, expected problem count, all-button, problems-button
    [InlineData(new[] { GoalSeverity.Error }, 1, false, false)]
    [InlineData(new[] { GoalSeverity.Error, GoalSeverity.Blocker }, 2, true, false)]
    [InlineData(new[] { GoalSeverity.Suggestion, GoalSeverity.Suggestion }, 0, true, false)]
    [InlineData(new[] { GoalSeverity.Warning, GoalSeverity.Suggestion }, 1, true, true)]
    public void A_review_offers_the_bulk_buttons_that_say_something_new(
        GoalSeverity[] severities, int problems, bool canCopyAll, bool canCopyProblems)
    {
        var message = Message(GoalMessageRole.Assistant, "Goal not met", markdown: true,
            [..severities.Select(s => Finding(s, s.ToString()))]);

        Assert.Equal(problems, message.ProblemCount);
        Assert.Equal(canCopyAll, message.CanCopyAllFindings);
        Assert.Equal(canCopyProblems, message.CanCopyProblems);
    }
}
