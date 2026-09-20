using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Services.Agents;
using mTiles.ViewModels.AgentConversation;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// The conversation view, laid out with one of everything it can be asked to draw.
/// </summary>
/// <remarks>Compiled bindings catch a misspelled property; they do not catch a template that throws when it
/// is realised, a converter handed a type it did not expect, or a block that never becomes visible. This
/// lays the whole view out in the headless renderer with every kind of entry, pending approval and question
/// present at once.</remarks>
public class AgentConversationViewTests
{
    internal const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Fact]
    public void Every_kind_of_entry_lays_out()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AgentConversationViewTests).Assembly);
        session.Dispatch(() =>
        {
            using var settings = new TempSettings();
            var agent = AiAgentCatalog.Find("claude")!;
            // An account that does not exist refuses the launch before anything is spawned. Left to start, the
            // tile runs the real CLI, which a test has no business doing and a CI runner does not have.
            var instance = AiAgentCatalog.SeedInstanceFor(agent);
            instance.ApiAccountId = "no-such-account";
            // A window of its own, so no lookup of one runs: its answer redraws the host's conversation — empty
            // here, since the state under test is drawn straight onto the tile — over all seven entries.
            instance.MaxContextTokens = 200_000;
            var vm = new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service,
                new mTiles.AgentSessions.Storage.SqliteConversationStore(
                    Path.Combine(Path.GetTempPath(), $"mtiles-view-{Guid.NewGuid():N}.db")),
                instance, agent, () => "tile", post: action => action());

            var state = ConversationReducer.Replay(
            [
                new UserMessageAdded("u", "Fix the build", [new ImageAttachment("image/png", OnePixelPng, "shot.png")]),
                new SessionOptionsReported([new SessionOption("opus", "Opus")],
                    [new SessionOption("Plan", "plan"), new SessionOption("Auto", "auto")],
                    [new SessionOption("Low", "low"), new SessionOption("High", "high")]),
                new SessionConfigured("opus", "Auto", "token", "High"),
                new TurnStarted { TurnId = "t" },
                new ReasoningDelta("r", "Let me look.\nThen fix."),
                new ToolStarted("c", ToolKind.Command, "Bash", "dotnet build", new ToolDetail(Command: "dotnet build")),
                new ToolCompleted("c", ToolStatus.Failed, "error CS1002", new ToolDetail(ExitCode: 1)),
                new ToolStarted("e", ToolKind.FileChange, "Edit", "Edit a.cs", new ToolDetail(Paths: ["a.cs"], Diff: "--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-x\n+y")),
                new ApprovalRequested("a0", ApprovalKind.Command, "rm", null, "c", [new ApprovalOption(ApprovalDecision.Accept, "Allow")]),
                new ApprovalResolved("a0", ApprovalDecision.Accept),
                new AssistantTextDelta("m", "**Fixed** the build."),
                new PlanProposed("1. Build\n2. Test"),
                new PlanUpdated(null, [new PlanStep("Build", PlanStepStatus.Completed), new PlanStep("Test", PlanStepStatus.InProgress)]),
                new QuestionsAsked("q0", [new UserQuestion("x", "Scope", "Which?", [new QuestionOption("All")], false, true)]),
                new QuestionsAnswered("q0", new Dictionary<string, IReadOnlyList<string>> { ["x"] = ["All"] }),
                new CheckpointCaptured("c1", "c0", [new ChangedFile("a.cs", FileChangeKind.Modified, 1, 1)]),
                new NoticeRaised(NoticeLevel.Warning, "Retrying…"),
                new UsageUpdated(new TokenUsage(42_000, 200_000, CostUsd: 0.12m)),
                new ApprovalRequested("a1", ApprovalKind.FileChange, "Edit b.cs", "--- a/b.cs\n+++ b/b.cs\n-a\n+b", null,
                    [new ApprovalOption(ApprovalDecision.Accept, "Allow"), new ApprovalOption(ApprovalDecision.Cancel, "Stop")]),
                new QuestionsAsked("q1", [new UserQuestion("y", null, "Proceed?", [new QuestionOption("Yes", "go on"), new QuestionOption("No")], true, true)]),
            ]);
            var view = new AgentConversationTileView { DataContext = vm };
            // Control templates on the application, never the window: a theme added to a window leaves an
            // ItemsControl untemplated (GoalFindingsDialogTests measured it). Taken off again at the end,
            // because the test application is shared by the whole assembly.
            var app = Avalonia.Application.Current!;
            var theme = new Avalonia.Themes.Fluent.FluentTheme();
            var tokens = new ResourceInclude(new Uri("avares://mTiles/Styles/"))
            {
                Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
            };
            // The composer's model, effort and permission controls come from mTiles.Controls, and an
            // untemplated control draws nothing at all — which would let this test pass over a composer
            // that is, on screen, an empty row.
            var pickers = new StyleInclude(new Uri("avares://mTiles.Controls/Themes/"))
            {
                Source = new Uri("avares://mTiles.Controls/Themes/Picker.axaml"),
            };
            app.Styles.Add(theme);
            app.Styles.Add(pickers);
            app.Resources.MergedDictionaries.Add(tokens);

            var window = new Window { Content = view, Width = 700, Height = 900 };
            try
            {
            window.Show();

            // Showing the view starts the tile, which reads its (empty) stored conversation off the UI thread
            // and draws it; the state under test is drawn over it once that start has finished.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (vm.IsStarting && DateTime.UtcNow < deadline)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            vm.Draw(state);
            for (var pass = 0; pass < 3; pass++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            Assert.Equal(7, vm.Timeline.Count);
            Assert.Single(vm.PendingApprovals);
            Assert.NotNull(vm.PendingQuestions);
            Assert.Equal("42k / 200k tokens · $0.12", vm.UsageText);
            Assert.Equal(mTiles.Models.TileActivity.Blocked, vm.Activity);

            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Equal(("Auto", "High"), (vm.SelectedMode?.Id, vm.SelectedEffort?.Id));
            Assert.Equal("opus", vm.Model);
            var images = view.GetVisualDescendants().OfType<Avalonia.Controls.Image>().ToList();
            Assert.Contains(images, image => image.Source is not null);
            Assert.True(texts.Contains("Edit b.cs"),
                "Drawn: " + string.Join(" | ", texts.Where(t => !string.IsNullOrEmpty(t))) +
                " || controls: " + string.Join(",", view.GetVisualDescendants().Select(v => v.GetType().Name).Distinct()));
            Assert.Contains("Proceed?", texts);

            // The Goal tile's controls, not copies: the @ suggestions on the composer and on the answer box
            // (which reaches the tile's mentions out of a question's template), and offered answers drawn as
            // its full-width rows.
            var boxes = view.GetVisualDescendants().OfType<TextBox>().ToList();
            Assert.Same(vm.FileMentions, FileMentionBehavior.GetMentions(boxes.Single(b => b.Name == "InputBox")));
            Assert.Same(vm.FileMentions, FileMentionBehavior.GetMentions(boxes.Single(b => b.Classes.Contains("ask-field"))));
            Assert.Equal(2, view.GetVisualDescendants().OfType<ToggleButton>().Count(b => b.Classes.Contains("ask-option")));
            Assert.True(texts.Contains("1 file changed") && texts.Contains("+1") && texts.Contains("−1"),
                "Drawn: " + string.Join(" | ", texts.Where(t => !string.IsNullOrEmpty(t))));

            // The waiting row is a Border of its own, and a type selector matches the exact type: without
            // `:is(Border).row` it loses the padding that lines it up with the messages it stands in for.
            var waiting = view.GetVisualDescendants().OfType<WaitingRow>().Single();
            Assert.Equal(new Avalonia.Thickness(10, 6), waiting.Padding);

            // The copy button beside a question takes the answer as it stands, not the empty one the template
            // was realised with.
            var answer = boxes.Single(b => b.Classes.Contains("ask-field"));
            answer.Text = "the third option";
            Dispatcher.UIThread.RunJobs();
            // Not diff-copy, which wears the same class for the same look: a file's patch has one too.
            var copy = view.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("item-copy") && !b.Classes.Contains("diff-copy"));
            Assert.Contains("the third option", CopyButton.GetText(copy));

            // The composer does not scroll with the conversation. It is the one place you act from, and
            // scrolling back to re-read a turn must not take it off the bottom of the tile — the same
            // rule GoalAskPanelTests pins one tile along, which is why the pair stays in step.
            var scroller = view.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "ChatScroll");
            var composer = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("composer"));
            Assert.DoesNotContain(scroller, composer.GetVisualAncestors());
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(composer));


            // What you typed sits on the right, at most three quarters across; what the agent said runs
            // the full width from the left. The column and the span are the whole of it — see
            // Views/BubbleLayout.cs — so they are what is asserted rather than a measured position.
            var rows = view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row")).ToList();
            var yours = rows.Single(b => b.Classes.Contains("row-user"));
            Assert.Equal(1, Grid.GetColumn(yours));
            Assert.Equal(1, Grid.GetColumnSpan(yours));
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, yours.HorizontalAlignment);

            var theirs = rows.First(b => !b.Classes.Contains("row-user") && b is not WaitingRow);
            Assert.Equal(0, Grid.GetColumn(theirs));
            Assert.Equal(2, Grid.GetColumnSpan(theirs));

            // And your own gutter glyph is gone with it: two things saying who spoke is one of them
            // being ignored, and the one costing a column of a bubble's width is the one to drop.
            Assert.Empty(yours.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Classes.Contains("gutter") && t.IsVisible));

            // Compact stands at the end of the line the figure is on — on the context bar, not among the
            // composer's pickers, which say what the next message runs as. Drawn only where the session
            // has a route for it: this one reports none, so it is there and invisible.
            var compact = view.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("context-action"));
            var contextBar = view.GetVisualDescendants().OfType<Border>()
                .Single(b => b.Classes.Contains("context-bar"));
            Assert.Contains(contextBar, compact.GetVisualAncestors());
            Assert.DoesNotContain(composer, compact.GetVisualAncestors());
            Assert.False(compact.IsVisible);

            vm.Draw(ConversationReducer.Replay(
            [
                new SessionStateChanged(AgentSessionState.Ready),
                new SessionOptionsReported([], [], []) { CanCompact = true },
                new UsageUpdated(new TokenUsage(180_000, 200_000)),
            ]));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(compact.IsVisible);
            // The colour never travels without the sentence that says what it means.
            Assert.True(compact.Classes.Contains("tight"));
            Assert.Contains("nearly full", (string)ToolTip.GetTip(compact)!);
            }
            finally
            {
                window.Close();
                vm.Dispose();
                app.Styles.Remove(pickers);
                app.Styles.Remove(theme);
                app.Resources.MergedDictionaries.Remove(tokens);
            }

            return Task.FromResult(true);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
