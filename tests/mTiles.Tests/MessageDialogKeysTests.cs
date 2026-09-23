using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mTiles.Views;
using System.Linq;
using Xunit;

namespace mTiles.Tests;

/// <summary>What a bare letter does to a confirmation, and when.</summary>
/// <remarks>
/// <para>The dialog appears under somebody's typing, so the key that answers it is held back for
/// <see cref="MessageDialog.SettlingTime"/> — see that field for the argument. These pin both halves:
/// held back at first, accepted afterwards, and the letter that is accepted is the letter the button
/// underlines.</para>
/// <para><b>Nothing here waits and nothing here leaves a dialog open.</b> The settling window is set
/// on the dialog itself and each test closes the window it opened on the same call stack: a real delay
/// yields the dispatcher with a dialog on screen, and <c>ModalScope</c> is process-wide, so whatever
/// ran meanwhile was told something was being asked of the user. Per dialog rather than per process,
/// because test classes run in parallel and a shared window would refuse another class's keypress.</para>
/// </remarks>
public class MessageDialogKeysTests
{
    [Fact]
    public void A_key_in_flight_does_not_answer_the_dialog() => Ui.Run(() =>
        {
            var (window, asked) = Ask(settling: TimeSpan.FromSeconds(30));

            Press(window, Key.N);

            // Still open, still unanswered: the keystroke landed inside the settling window.
            Assert.False(asked.IsCompleted);
            Assert.Single(window.GetVisualDescendants().OfType<MessageDialog>());

            Close(window);
        });

    [Theory]
    [InlineData(Key.Y, true)]
    [InlineData(Key.N, false)]
    public void A_deliberate_key_answers_it(Key key, bool expected) => Ui.Run(() =>
        {
            var (window, asked) = Ask();

            Press(window, key);

            Assert.Equal(expected, Answer(asked));
            Close(window);
        });

    [Fact]
    public void The_letter_is_taken_from_the_label_the_caller_wrote() => Ui.Run(() =>
        {
            var (window, asked) = Ask(confirmText: "Discard", cancelText: "Keep");

            // Not Y: nothing on this dialog is spelled "Yes", which is why the key is derived.
            Press(window, Key.Y);
            Assert.False(asked.IsCompleted);

            Press(window, Key.D);

            Assert.True(Answer(asked));
            Close(window);
        });

    [Fact]
    public void A_modified_letter_is_somebody_elses_gesture() => Ui.Run(() =>
        {
            var (window, asked) = Ask();

            Press(window, Key.Y, RawInputModifiers.Control);

            Assert.False(asked.IsCompleted);
            Close(window);
        });

    [Fact]
    public void The_key_is_underlined_without_holding_Alt() => Ui.Run(() =>
    {
        var (window, _) = Ask();

        var dialog = window.GetVisualDescendants().OfType<MessageDialog>().Single();
        var marks = dialog.GetVisualDescendants().OfType<AccessText>().ToList();

        // Avalonia reveals access keys only while Alt is held; the bare letter answers too, so a
        // mark nobody sees is a shortcut nobody knows about.
        Assert.NotEmpty(marks);
        Assert.All(marks, m => Assert.True(m.ShowAccessKey));

        // And the mark was read as a mark rather than drawn as a character: a string handed to the
        // Button theme this application uses arrives as a plain TextBlock reading "_Yes", underscore
        // and all, with no access key registered anywhere.
        // Cancel is drawn first, so N comes before Y.
        Assert.Equal(new[] { "N", "Y" }, marks.Select(m => m.AccessKey));

        Close(window);
    });

    /// <summary>Opens a dialog and hands it the settling window this test wants of it.</summary>
    private static (Window Window, Task<bool> Asked) Ask(
        string confirmText = "Yes", string cancelText = "No", TimeSpan? settling = null)
    {
        var host = new OverlayHost();
        var window = new Window { Content = new Panel { Children = { host } }, Width = 500, Height = 400 };
        window.Show();
        window.UpdateLayout();

        var asked = MessageDialog.ConfirmAsync(window, "Confirm", "Really?",
            whenUnavailable: false, confirmText: confirmText, cancelText: cancelText);
        window.UpdateLayout();

        window.GetVisualDescendants().OfType<MessageDialog>().Single().SettlingTime =
            settling ?? TimeSpan.Zero;
        return (window, asked);
    }

    /// <summary>The answer, which by now is in hand — the dialog closes on the keystroke's own stack.</summary>
    private static bool Answer(Task<bool> asked)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(asked.IsCompleted);
        return asked.Result;
    }

    /// <summary>Takes the window down and lets the teardown its detach posts run.</summary>
    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Press(Window window, Key key,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(ToPhysical(key), modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static PhysicalKey ToPhysical(Key key) => key switch
    {
        Key.Y => PhysicalKey.Y,
        Key.N => PhysicalKey.N,
        Key.D => PhysicalKey.D,
        _ => PhysicalKey.None,
    };

}
