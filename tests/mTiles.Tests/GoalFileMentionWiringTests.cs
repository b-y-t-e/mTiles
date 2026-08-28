using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using mTiles.Models;
using mTiles.Services;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Every box a goal is typed into offers the <c>@</c> file suggestions.
/// </summary>
/// <remarks>
/// The view model's own tests say what the suggestions do; nothing there can say whether the markup
/// hands them to a box, and a box wired to nothing looks exactly like a box with nothing to suggest.
/// The answer box is the one worth pinning: it lives inside a data template whose data context is one
/// question, so it reaches the tile's mentions out of the template — the binding shape that has already
/// failed silently once in this view.
/// </remarks>
public class GoalFileMentionWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mtiles-mention-ui-" + Guid.NewGuid());

    public GoalFileMentionWiringTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory */ }
    }

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GoalFileMentionWiringTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private GoalTileViewModel Tile() =>
        new(_dir, new SettingsService(Path.Combine(_dir, "settings.json")));

    private static GoalTileView Shown(GoalTileViewModel vm)
    {
        var view = new GoalTileView { DataContext = vm };
        var window = new Window { Content = view, Width = 620, Height = 480 };

        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        });

        window.Show();
        Pump();
        return view;
    }

    private static void Pump()
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static TextBox Box(GoalTileView view, string name) =>
        view.GetVisualDescendants().OfType<TextBox>().First(b => b.Name == name);

    [Theory]
    [InlineData("InputBox")]
    [InlineData("PlanBox")]
    public void The_composer_and_the_plan_box_offer_the_tiles_files(string name)
    {
        OnUiThread(() =>
        {
            using var vm = Tile();
            var view = Shown(vm);

            Assert.Same(vm.FileMentions, FileMentionBehavior.GetMentions(Box(view, name)));
        });
    }

    /// <summary>
    /// An answer box reaches the tile's mentions out of the template it lives in.
    /// </summary>
    /// <remarks>
    /// The row is built from the template by hand, as the transcript's rows are in
    /// <see cref="GoalAskPanelTests"/> and for the same reason: item containers are created lazily and
    /// this headless window never lays the question list out far enough to create one. It is then hosted
    /// in a plain user control carrying the tile's view model, which is exactly what the binding claims
    /// to look for — the nearest user control, and a goal tile hanging off it.
    /// </remarks>
    [Fact]
    public void So_does_an_answer_box_inside_a_question()
    {
        OnUiThread(() =>
        {
            using var vm = Tile();
            var question = new GoalQuestionAnswer(1, new GoalQuestion { Question = "Which file?" });

            // Asking is what puts the question panel on screen, and a panel that is not on screen has
            // no list to take the template from.
            vm.CurrentPhase = GoalPhase.Clarify;
            vm.Questions.Add(question);

            var questions = Shown(vm).GetVisualDescendants().OfType<ItemsControl>()
                .First(i => i.Name == "QuestionList");
            var row = questions.ItemTemplate!.Build(question)!;
            row.DataContext = question;

            var host = new Window { Content = new UserControl { DataContext = vm, Content = row } };
            host.Show();
            Pump();

            var answer = row.GetLogicalDescendants().OfType<TextBox>()
                .First(b => b.Classes.Contains("ask-field"));

            Assert.Same(vm.FileMentions, FileMentionBehavior.GetMentions(answer));
        });
    }
}
