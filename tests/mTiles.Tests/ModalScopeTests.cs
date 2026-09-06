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

/// <summary>The dialog that handles the dictation shortcut itself, and the counting behind it.</summary>
/// <remarks>
/// The speech wizard's last step teaches the shortcut by having the user press it. While it was a
/// window of its own the main window never saw those keys; as a control inside that window, the
/// window-level tunnelling handler took them first, started a recording owned by the terminal tile
/// behind the wizard, and left the wizard waiting for a gesture that never arrived. These two say so
/// out loud, because nothing else in the suite would notice the interface being dropped.
/// </remarks>
public class DictationShortcutOwnershipTests
{
    [Fact]
    public void The_speech_wizard_claims_the_shortcut_and_nothing_else_does()
    {
        Assert.True(typeof(mTiles.Views.SpeechSetupWizard)
            .IsAssignableTo(typeof(mTiles.Views.OverlayHost.IOwnsDictationShortcut)));

        // Settings deliberately does not: dictating into one of its text boxes is a feature, and
        // there the window-level handler is the one that should act.
        Assert.False(typeof(mTiles.Views.MessageDialog)
            .IsAssignableTo(typeof(mTiles.Views.OverlayHost.IOwnsDictationShortcut)));
    }

    [Fact]
    public void Only_a_claim_that_asked_for_the_shortcut_speaks_for_it()
    {
        Assert.False(ModalScope.ShortcutIsSpokenFor);

        using (ModalScope.Enter())
        {
            // Open, but this one leaves the shortcut alone.
            Assert.True(ModalScope.IsAnyOpen);
            Assert.False(ModalScope.ShortcutIsSpokenFor);

            var owning = ModalScope.Enter(ownsDictationShortcut: true);
            Assert.True(ModalScope.ShortcutIsSpokenFor);

            // Released twice on purpose: a second release must not take the count below what is open,
            // and the first symptom of that would be the shortcut silently going back to the tile.
            owning.Dispose();
            owning.Dispose();
            Assert.False(ModalScope.ShortcutIsSpokenFor);
            Assert.True(ModalScope.IsAnyOpen);
        }

        Assert.False(ModalScope.IsAnyOpen);
    }
}
