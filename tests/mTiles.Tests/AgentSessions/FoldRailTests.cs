using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mTiles.AgentSessions.Conversation;
using mTiles.AgentSessions.Events;
using mTiles.Services.Agents;
using mTiles.ViewModels.AgentConversation;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// The handle that folds a block away, in the two places a block is drawn.
/// </summary>
/// <remarks>
/// <para><b>Its width is the whole of it.</b> The rail is sized in characters of the terminal's face
/// (<see cref="MonospaceWidth"/>), and that measurement answered zero for two months: it measured a
/// space, and <c>FormattedText.Width</c> is the width excluding trailing whitespace. A zero-width
/// button is on screen, is hit-testable by nothing, and looks exactly like a feature nobody built — so
/// what is asserted here is a width, not a control's presence — measured with no font resource in
/// the application at all, which is the second half of the same fault: the measurement gave up
/// silently where <c>TerminalFontFamily</c> was not there to be found.</para>
/// </remarks>
public class FoldRailTests
{
    [Fact]
    public void A_patch_and_a_command_are_both_folded_by_a_rail_wide_enough_to_hit()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FoldRailTests).Assembly);
        session.Dispatch(() =>
        {
            using var settings = new TempSettings();
            var agent = AiAgentCatalog.Find("claude")!;
            var instance = AiAgentCatalog.SeedInstanceFor(agent);
            instance.ApiAccountId = "no-such-account";
            instance.MaxContextTokens = 200_000;
            var vm = new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service,
                new mTiles.AgentSessions.Storage.SqliteConversationStore(
                    Path.Combine(Path.GetTempPath(), $"mtiles-rail-{Guid.NewGuid():N}.db")),
                instance, agent, () => "tile", post: action => action());

            var state = ConversationReducer.Replay(
            [
                new ToolStarted("c", ToolKind.Command, "Bash", "dotnet build", new ToolDetail(Command: "dotnet build")),
                new ToolCompleted("c", ToolStatus.Completed, "done", ToolDetail.Empty),
                new CheckpointCaptured("c1", "c0", [new ChangedFile("a.cs", FileChangeKind.Modified, 1, 1)]),
            ]);

            var view = new AgentConversationTileView { DataContext = vm };
            var app = Avalonia.Application.Current!;
            var theme = new Avalonia.Themes.Fluent.FluentTheme();
            var tokens = new ResourceInclude(new Uri("avares://mTiles/Styles/"))
                { Source = new Uri("avares://mTiles/Styles/AppTheme.axaml") };
            var pickers = new StyleInclude(new Uri("avares://mTiles.Controls/Themes/"))
                { Source = new Uri("avares://mTiles.Controls/Themes/Picker.axaml") };
            app.Styles.Add(theme);
            app.Styles.Add(pickers);
            app.Resources.MergedDictionaries.Add(tokens);
            // Deliberately no TerminalFontFamily and no TermFontBase: they are written into the
            // application's resources at startup by App.ApplyFontResources, and neither the XAML
            // previewer nor a view hosted on its own ever reaches that. The rail has to be wide
            // enough to hit there too, which is what MonospaceWidth's own defaults are for.

            var window = new Window { Content = view, Width = 700, Height = 900 };
            try
            {
                window.Show();
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (vm.IsStarting && DateTime.UtcNow < deadline)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(10);
                }

                vm.Draw(state);
                Layout();

                // Both blocks open: the command's output, and the file's patch under the turn's summary.
                vm.Timeline.OfType<WorkGroupItemViewModel>().Single().ToggleCommand.Execute(null);
                var group = vm.Timeline.OfType<WorkGroupItemViewModel>().Single();
                group.Items.OfType<ToolCallItemViewModel>().Single().ToggleCommand.Execute(null);
                // Opened by hand rather than through the command: the command also reads the patch out
                // of git, which this temporary directory is not, and the read then fails as the window
                // closes. What is under test is the rail, not the diff.
                var turn = vm.Timeline.OfType<CheckpointItemViewModel>().Single();
                // The list of changed files is folded until somebody asks for it, so the rail under
                // it has nothing to stand beside until the turn is opened.
                turn.ToggleDiffCommand.Execute(null);
                var file = turn.Files.Single();
                file.IsExpanded = true;
                Layout();

                var rails = view.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("diff-rail") && b.IsVisible).ToList();
                Assert.Equal(2, rails.Count);
                foreach (var rail in rails)
                {
                    Assert.True(rail.Bounds.Width >= 8,
                        $"The rail is {rail.Bounds.Width}px wide — nothing can be aimed at it.");
                    var mark = rail.GetVisualDescendants().OfType<Border>()
                        .First(b => b.Classes.Contains("diff-rail-mark"));
                    Assert.True(mark.Bounds.Width > 0, "The rail's mark is invisible.");
                }

                // And it folds what it stands beside.
                rails[0].Command!.Execute(rails[0].CommandParameter);
                Layout();
                Assert.False(group.Items.OfType<ToolCallItemViewModel>().Single().IsExpanded);
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

            void Layout()
            {
                for (var pass = 0; pass < 3; pass++)
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                }
            }
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}
