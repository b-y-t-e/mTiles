using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>Where the keyboard goes when a tile is activated, and which presses leave it where they land.
/// </summary>
public class FocusTargetsTests
{
    private sealed class AnsweringView : UserControl, IFocusTargetView
    {
        public readonly Button Other = new() { Content = "first" };
        public readonly TextBox Target = new();

        public AnsweringView() => Content = new StackPanel { Children = { Other, Target } };

        public InputElement? PreferredFocusTarget => Target;
    }

    [Fact]
    public void A_view_that_answers_gets_its_own_target() => InWindow(() => new AnsweringView(), (view, card) =>
        Assert.Same(view.Target, FocusTargets.Resolve(view, card)));

    [Fact]
    public void A_target_that_cannot_take_the_keyboard_falls_to_the_card_not_to_another_control() =>
        InWindow(() => new AnsweringView(), (view, card) =>
        {
            view.Target.IsEnabled = false;

            Assert.Same(card, FocusTargets.Resolve(view, card));
        });

    [Fact]
    public void A_view_that_does_not_answer_gets_its_first_focusable_element() =>
        InWindow(() => new UserControl { Content = new TextBox() }, (view, card) =>
            Assert.Same(view.Content, FocusTargets.Resolve(view, card)));

    [Fact]
    public void A_press_on_a_button_keeps_the_keyboard_where_it_went_even_if_the_button_is_not_focusable() =>
        InWindow(() => new Button { Focusable = false, Content = new TextBlock() }, (button, _) =>
        {
            var tile = (Avalonia.Visual)button.Parent!;

            Assert.True(FocusTargets.PressTakesKeyboard(button.Content, tile));
        });

    [Fact]
    public void A_press_on_the_background_or_a_label_is_sent_to_the_target()
    {
        var tile = new Border();
        var label = new TextBlock();
        tile.Child = new StackPanel { Children = { label } };

        Assert.False(FocusTargets.PressTakesKeyboard(label, tile));
        Assert.False(FocusTargets.PressTakesKeyboard(tile, tile));
    }

    [Fact]
    public void A_press_on_a_disabled_field_is_sent_to_the_target()
    {
        var tile = new Border();
        var field = new TextBox { IsEnabled = false };
        tile.Child = field;

        Assert.False(FocusTargets.PressTakesKeyboard(field, tile));
    }

    [Fact]
    public void A_press_inside_a_popup_the_tile_opened_keeps_the_keyboard_in_the_popup()
    {
        var tile = new Border();
        var categoryLabel = new TextBlock();
        _ = new Border { Child = categoryLabel }; // the popup's own root, never under the tile

        Assert.True(FocusTargets.PressTakesKeyboard(categoryLabel, tile));
    }

    /// <summary>Lays <paramref name="view"/> out in a window beside a focusable card standing in for the
    /// tile's own.</summary>
    private static void InWindow<TView>(Func<TView> build, Action<TView, InputElement> body) where TView : Control
    {
        Ui.Run(() =>
        {
            var view = build();
            var card = new Border { Focusable = true };
            var window = new Window { Content = new StackPanel { Children = { card, view } } };
            window.Show();
            try { body(view, card); }
            finally { window.Close(); }
        });
    }
}
