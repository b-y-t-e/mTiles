using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>The chooser's keyboard: what typing, the arrows, Enter and Escape do to a row of cards.</summary>
public class ChooserKeyboardTests
{
    private sealed class Chooser
    {
        public readonly TextBox Filter = new();
        public readonly Button Back;
        public readonly List<Button> Cards = [];
        public readonly List<string> Picked = [];
        public int Left;
        public readonly ChooserKeyboard Keyboard;

        public Chooser(params string[] labels)
        {
            var pick = new RelayCommand<string>(label => Picked.Add(label!));
            Back = new Button { Tag = "Back", Command = pick, CommandParameter = "Back" };
            Cards.Add(Back);
            foreach (var label in labels)
                Cards.Add(new Button { Tag = label, Command = pick, CommandParameter = label });
            Keyboard = new ChooserKeyboard(Filter, () => Cards, Back, () => Left++);
            Keyboard.Apply();
            new Window { Content = new StackPanel { Children = { Filter } } }.Show();
        }

        /// <summary>Types into the filter; its <c>TextChanged</c> is raised from the dispatcher.</summary>
        public void Type(string text)
        {
            Filter.Text = text;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        public Button Current => Cards.Single(card => card.Classes.Contains("current"));

        public void Press(Key key) =>
            Filter.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
    }

    [Fact]
    public void The_highlight_starts_on_the_first_answer_and_never_on_back() => Ui.Run(() =>
    {
        var chooser = new Chooser("PowerShell", "Git Bash");

        Assert.Equal("PowerShell", chooser.Current.Tag);
    });

    [Fact]
    public void Enter_picks_the_first_answer_rather_than_going_back() => Ui.Run(() =>
    {
        var chooser = new Chooser("PowerShell", "Git Bash");

        chooser.Press(Key.Enter);

        Assert.Equal(["PowerShell"], chooser.Picked);
    });

    [Fact]
    public void Typing_narrows_the_cards_and_back_is_never_filtered_out() => Ui.Run(() =>
    {
        var chooser = new Chooser("PowerShell", "Git Bash");

        chooser.Type("bash");

        Assert.True(chooser.Back.IsVisible);
        Assert.False(chooser.Cards[1].IsVisible);
        Assert.Equal("Git Bash", chooser.Current.Tag);
        chooser.Press(Key.Enter);
        Assert.Equal(["Git Bash"], chooser.Picked);
    });

    [Fact]
    public void Escape_clears_the_filter_first_and_leaves_only_once_it_is_empty() => Ui.Run(() =>
    {
        var chooser = new Chooser("PowerShell");
        chooser.Type("pow");

        chooser.Press(Key.Escape);
        Assert.Equal("", chooser.Filter.Text);
        Assert.Equal(0, chooser.Left);

        chooser.Press(Key.Escape);
        Assert.Equal(1, chooser.Left);
    });

    [Fact]
    public void Left_and_right_move_between_cards_only_while_the_filter_is_empty() => Ui.Run(() =>
    {
        var chooser = new Chooser("PowerShell", "Pwsh");

        chooser.Press(Key.Right);
        Assert.Equal("Pwsh", chooser.Current.Tag);

        // With text in the filter the arrow is the caret's, and the highlight stays where typing put it.
        chooser.Type("p");
        chooser.Press(Key.Right);
        Assert.Equal("PowerShell", chooser.Current.Tag);
    });

}
