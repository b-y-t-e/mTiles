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
    [Fact]
    public void Agent_composer() => Ui.Run(() =>
    {
        using var settings = new TempSettings();
        using var vm = ConversationTiles.New(settings);

        Press(new AgentConversationTileView { DataContext = vm }, () => vm.Draft, emptyBeforeEnter: false);
    });

    [Fact]
    public void Goal_composer() => Ui.Run(() =>
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
        // Without a theme a TextBox has no presenter and edits nothing.
        using var theme = new HeadlessTheme();
        var window = new Window { Content = view, Width = 700, Height = 700 };
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
        }
    }

    private static int LineBreaks(string text) => text.Replace("\r\n", "\n").Count(c => c == '\n');
}
