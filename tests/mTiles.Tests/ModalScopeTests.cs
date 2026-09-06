using System.ComponentModel;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Speech;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>That a dialog is modal to the keyboard, not only to the mouse.</summary>
/// <remarks>
/// A scrim stops the pointer and nothing else, which is how Alt+Space over a confirmation came to
/// dictate a sentence into the terminal tile behind it — and the phone's Enter would then have run
/// it. Reported from a real session on Omarchy.
/// </remarks>
public class ModalScopeTests
{
    [Fact]
    public void Nothing_is_open_to_begin_with()
        => Assert.False(ModalScope.IsAnyOpen);

    [Fact]
    public void A_dialog_is_open_until_its_handle_goes()
    {
        var open = ModalScope.Enter();
        Assert.True(ModalScope.IsAnyOpen);

        open.Dispose();
        Assert.False(ModalScope.IsAnyOpen);
    }

    /// <summary>Dialogs stack — Settings is one of them and asks questions of its own.</summary>
    [Fact]
    public void The_outer_dialog_is_still_open_when_the_inner_one_closes()
    {
        var outer = ModalScope.Enter();
        var inner = ModalScope.Enter();

        inner.Dispose();
        Assert.True(ModalScope.IsAnyOpen);

        outer.Dispose();
        Assert.False(ModalScope.IsAnyOpen);
    }

    /// <summary>A handle disposed twice must not take the count below what is open.</summary>
    /// <remarks>The first symptom of that would be a dialog modal to nothing, which is the bug this
    /// whole class exists to prevent — arriving by the back door.</remarks>
    [Fact]
    public void Closing_the_same_dialog_twice_costs_nothing()
    {
        var outer = ModalScope.Enter();
        var inner = ModalScope.Enter();

        inner.Dispose();
        inner.Dispose();
        Assert.True(ModalScope.IsAnyOpen);

        outer.Dispose();
        Assert.False(ModalScope.IsAnyOpen);
    }

    /// <summary>The tile is not a destination while something is being asked.</summary>
    /// <remarks>Asserted on <see cref="DictationTextSink.TileInput"/> rather than on dictation,
    /// because that is the one place the sentence and the key that submits it both choose their
    /// destination — the phone's keys route through the same call.</remarks>
    [Fact]
    public void A_dialog_takes_the_tile_out_of_reach_of_dictation_and_the_phone()
    {
        var tile = new LeafTileNodeViewModel(TileKindIds.Note, new TypeableTile(), "",
            new TileActivationScope());

        Assert.NotNull(DictationTextSink.TileInput(tile));

        using (ModalScope.Enter())
            Assert.Null(DictationTextSink.TileInput(tile));

        // And it comes back, rather than the gate latching shut for the session.
        Assert.NotNull(DictationTextSink.TileInput(tile));
    }

    private sealed class TypeableTile : ITextInputTile
    {
        public string KindId => TileKindIds.Note;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool TrySendText(string text, bool submit) => true;

        public bool TryPressKey(TileKey key) => true;

        public void Dispose() => PropertyChanged = null;
    }
}
