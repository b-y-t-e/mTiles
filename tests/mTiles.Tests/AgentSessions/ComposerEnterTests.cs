using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests.AgentSessions;

/// <summary>
/// Enter sends and Shift+Enter breaks the line, in both composers, pressed as a keyboard presses it.
/// </summary>
/// <remarks>A multi-line TextBox takes Enter in its own class handler and marks it handled, so a handler
/// wired in the markup (bubble, handled events skipped) never sees it: both keys inserted a line break.
/// Only a real key press through the window shows that; calling the handler directly passes.</remarks>
public class ComposerEnterTests
{
    private static readonly Avalonia.Themes.Fluent.FluentTheme Theme = new();

    [Fact]
    public void Agent_composer() => OnUiThread(() =>
    {
        using var settings = new TempSettings();
        var agent = AiAgentCatalog.Find("claude")!;
        using var vm = new AgentConversationTileViewModel(Path.GetTempPath(), settings.Service,
            new mTiles.AgentSessions.Storage.SqliteConversationStore(
                Path.Combine(Path.GetTempPath(), $"mtiles-enter-{Guid.NewGuid():N}.db")),
            AiAgentCatalog.SeedInstanceFor(agent), agent, () => "tile", post: action => action());

        Press(new AgentConversationTileView { DataContext = vm }, () => vm.Draft, emptyBeforeEnter: false);
    });

    [Fact]
    public void Goal_composer() => OnUiThread(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "mtiles-enter-goal-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        using var vm = new GoalTileViewModel(dir, new SettingsService(Path.Combine(dir, "settings.json")));

        // Enter on a typed goal would start a run, so the box is emptied first: Enter on an empty Goal
        // composer sends nothing, and must not break the line either.
        Press(new GoalTileView { DataContext = vm }, () => vm.InputText, emptyBeforeEnter: true);
    });

    private static void Press(Control view, Func<string> text, bool emptyBeforeEnter)
    {
        // Control templates on the application: without a theme a TextBox has no presenter and edits nothing.
        var app = Avalonia.Application.Current!;
        app.Styles.Add(Theme);
        var window = new Window { Content = view, Width = 700, Height = 700 };
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        });
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var box = view.FindControl<TextBox>("InputBox")!;
            box.Focus();
            box.Text = "one";
            box.CaretIndex = 3;

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, LineBreaks(text()));

            if (emptyBeforeEnter) box.Text = "";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(emptyBeforeEnter ? 0 : 1, LineBreaks(text()));
        }
        finally
        {
            window.Close();
            app.Styles.Remove(Theme);
        }
    }

    private static int LineBreaks(string text) => text.Replace("\r\n", "\n").Count(c => c == '\n');

    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ComposerEnterTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
