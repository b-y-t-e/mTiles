using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using mTiles.Models;
using mTiles.ViewModels;
using mTiles.Views;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The tile border while it is being spoken into: which of the two states is showing.
/// </summary>
/// <remarks>
/// Worth a test because the failure is invisible in code review — a class name that does not match the
/// selector, or a border left visible after the transcript arrives, both compile and both leave the
/// user with an indicator that lies about whether the microphone is open.
/// </remarks>
public class DictationIndicatorTests
{
    private static void OnUiThread(Action body)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DictationIndicatorTests).Assembly);
        session.Dispatch(() => { body(); return Task.FromResult(true); }, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static (LeafTileNodeViewModel Leaf, Border Indicator, LeafTileView View) Build()
    {
        var leaf = new LeafTileNodeViewModel(TileContentType.Empty, null, "", new TileActivationScope());
        var view = new LeafTileView { DataContext = leaf };
        var window = new Window { Content = view, Width = 400, Height = 300 };

        // The colour tokens, so the outline's brush is something a test can tell apart. They are a
        // resource dictionary rather than styles — the two live in different collections, and only the
        // resources are needed here.
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://mTiles/Styles/"))
        {
            Source = new Uri("avares://mTiles/Styles/AppTheme.axaml"),
        });

        window.Show();

        return (leaf, view.FindControl<Border>("DictationBorder")!, view);
    }

    /// <summary>What the card's edge is painted with right now, by token name.</summary>
    private static object? OutlineBrush(LeafTileView view) =>
        view.FindControl<Border>("TileCard")!.BorderBrush;

    private static object? Token(LeafTileView view, string key) =>
        view.FindResource(key);

    [Fact]
    public void The_border_is_hidden_until_something_is_being_dictated()
        => OnUiThread(() =>
        {
            var (_, indicator, _) = Build();

            Assert.False(indicator.IsVisible);
            Assert.DoesNotContain("recording", indicator.Classes);
            Assert.DoesNotContain("processing", indicator.Classes);
        });

    [Fact]
    public void Recording_and_transcribing_are_shown_differently_and_never_at_once()
        => OnUiThread(() =>
        {
            var (leaf, indicator, _) = Build();

            leaf.IsRecordingDictation = true;
            Assert.True(indicator.IsVisible);
            Assert.Contains("recording", indicator.Classes);
            Assert.DoesNotContain("processing", indicator.Classes);

            // The microphone closes and the words go off to be worked out.
            leaf.IsRecordingDictation = false;
            leaf.IsTranscribingDictation = true;
            Assert.True(indicator.IsVisible);
            Assert.Contains("processing", indicator.Classes);
            Assert.DoesNotContain("recording", indicator.Classes);

            leaf.IsTranscribingDictation = false;
            Assert.False(indicator.IsVisible);
        });

    /// <summary>
    /// While a tile is being dictated into, the active outline stands down.
    /// </summary>
    /// <remarks>
    /// The two markers say overlapping things — the border frames this tile, so it already answers
    /// "which one" — and the tile being dictated into is nearly always the active one, so both would
    /// light the same edge of the same tile. The outline comes back when the transcript lands, which is
    /// also when it starts meaning something again.
    /// </remarks>
    [Fact]
    public void The_active_outline_stands_down_while_this_tile_is_being_dictated_into()
        => OnUiThread(() =>
        {
            var (leaf, _, _) = Build();
            leaf.IsActive = true;
            Assert.True(leaf.ShowsActiveOutline);

            leaf.IsRecordingDictation = true;
            leaf.IsDictating = true;
            Assert.False(leaf.ShowsActiveOutline);

            // Transcribing is still "being dictated into" — the words are on their way here.
            leaf.IsRecordingDictation = false;
            leaf.IsTranscribingDictation = true;
            Assert.False(leaf.ShowsActiveOutline);

            leaf.IsTranscribingDictation = false;
            leaf.IsDictating = false;
            Assert.True(leaf.ShowsActiveOutline);
        });

    /// <summary>
    /// The <em>view</em> repaints the outline, and it comes back when the dictation ends.
    /// </summary>
    /// <remarks>
    /// <para>The rule above is about the view model; this is about the thing on screen, and only this
    /// one catches what actually went wrong. The view listened for the two flags the rule is computed
    /// from rather than for the rule itself, so it repainted the marker while the third one had not been
    /// updated yet: on the way in it was still lit and showed through the half-transparent
    /// border, and on the way out the last change was to a property nothing was listening for, so the
    /// tile stayed unmarked until something else happened to it.</para>
    /// <para><c>IsDictating</c> is moved <b>on its own</b> here, with no other flag changing in the same
    /// step. That is what makes this a test of the subscription rather than of the order the view model
    /// happens to write its properties in: a view listening to the inputs instead of the rule sees
    /// nothing at all, and the outline stays as it was.</para>
    /// </remarks>
    [Fact]
    public void The_outline_on_screen_goes_dark_for_the_dictation_and_comes_back_after_it()
        => OnUiThread(() =>
        {
            var (leaf, _, view) = Build();
            var lit = Token(view, "AccentOutline");
            var dark = Token(view, "BorderSubtle");
            Assert.NotNull(lit);
            Assert.NotEqual(lit, dark);          // the tokens differ, so the assertions below mean something

            leaf.IsActive = true;
            Assert.Equal(lit, OutlineBrush(view));

            leaf.IsDictating = true;
            Assert.Equal(dark, OutlineBrush(view));

            leaf.IsRecordingDictation = true;
            Assert.Equal(dark, OutlineBrush(view));

            leaf.IsRecordingDictation = false;
            leaf.IsTranscribingDictation = true;
            Assert.Equal(dark, OutlineBrush(view));

            // The transcript lands. The tile is still the active one, and has to look like it.
            leaf.IsTranscribingDictation = false;
            leaf.IsDictating = false;
            Assert.Equal(lit, OutlineBrush(view));
        });

    /// <summary>
    /// The border closes around the whole tile, top edge included.
    /// </summary>
    /// <remarks>
    /// Safe only because the active outline stands down while this is showing (see above). Two markers
    /// on the same edge is what this avoids; the earlier version of the same trap was a frame drawn
    /// below a two-pixel strip, which left a notch out of its top edge.
    /// </remarks>
    [Fact]
    public void The_dictation_border_frames_the_whole_tile()
        => OnUiThread(() =>
        {
            // Recording from the start, so the border is laid out along with everything else — a
            // control that was collapsed when the window measured itself has no bounds to compare.
            var leaf = new LeafTileNodeViewModel(TileContentType.Empty, null, "", new TileActivationScope())
            {
                IsRecordingDictation = true,
            };
            var view = new LeafTileView { DataContext = leaf };
            new Window { Content = view, Width = 400, Height = 300 }.Show();

            var indicator = view.FindControl<Border>("DictationBorder")!;
            var toolbar = view.FindControl<Border>("TileToolbar")!;

            Assert.True(toolbar.Bounds.Height > 0, "the toolbar has no height; the layout did not run");
            Assert.True(indicator.Bounds.Height > 0, "the border has no height; the layout did not run");

            // Bounds are relative to each control's parent, and the border sits in the card's grid,
            // which starts at the card's top edge — so a border that framed only the content below the
            // header would not start at zero.
            Assert.Equal(0, indicator.Bounds.Top);
            Assert.True(indicator.Bounds.Height > toolbar.Bounds.Height,
                "the border does not reach past the header");
        });

    // What is NOT covered here, stated rather than left to be assumed: the styles themselves. Neither
    // the brushes nor the animations could be observed in this harness — the application's styles are
    // applied from App.axaml, which does not run under the headless session (measured: with
    // Controls.axaml added to the window by hand, the base style's own BorderThickness setter still
    // read 0), and the animation clock does not advance under forced render-timer ticks (forty of them,
    // opacity constant at 1). So these tests pin the wiring — which class is set, and when the border
    // is on screen at all — and the pulse is left as something a person has to look at.
}
