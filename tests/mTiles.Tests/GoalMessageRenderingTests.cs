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
}
