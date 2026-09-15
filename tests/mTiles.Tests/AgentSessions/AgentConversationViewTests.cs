using Avalonia.Controls;
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
    [Fact]
    public void Every_kind_of_entry_lays_out()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AgentConversationViewTests).Assembly);
        session.Dispatch(() =>
        {
            using var settings = new TempSettings();
            var agent = AiAgentCatalog.Find("claude")!;
            var vm = new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service,
                new mTiles.AgentSessions.Storage.SqliteConversationStore(
                    Path.Combine(Path.GetTempPath(), $"mtiles-view-{Guid.NewGuid():N}.db")),
                AiAgentCatalog.SeedInstanceFor(agent), agent, () => "tile", post: action => action());

            var state = ConversationReducer.Replay(
            [
                new UserMessageAdded("u", "Fix the build", []),
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
            app.Styles.Add(theme);
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
            Assert.True(texts.Contains("Edit b.cs"),
                "Drawn: " + string.Join(" | ", texts.Where(t => !string.IsNullOrEmpty(t))) +
                " || controls: " + string.Join(",", view.GetVisualDescendants().Select(v => v.GetType().Name).Distinct()));
            Assert.Contains("Proceed?", texts);
            Assert.True(texts.Contains("1 file changed  +1 −1"),
                "Drawn: " + string.Join(" | ", texts.Where(t => !string.IsNullOrEmpty(t))));
            }
            finally
            {
                window.Close();
                vm.Dispose();
                app.Styles.Remove(theme);
                app.Resources.MergedDictionaries.Remove(tokens);
            }

            return Task.FromResult(true);
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
