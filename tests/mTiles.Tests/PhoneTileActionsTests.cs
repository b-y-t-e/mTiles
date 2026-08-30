using System.Text.Json;
using mTiles.Services.Phone;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a paired phone is allowed to see and press.
/// </summary>
/// <remarks>
/// The keys a phone can send are a closed enum decided at compile time; tile actions are not — a kind
/// registered later brings whatever it likes, and Git already has Discard changes and Undo last commit.
/// This is the single point at which that open set could quietly become the thing the design replaced,
/// so the rule is pure and it is pinned here rather than left to the page.
/// </remarks>
public sealed class PhoneTileActionsTests
{
    private static readonly TileAction Refresh = new("refresh", "Refresh", "refresh");
    private static readonly TileAction Disabled = new("commit", "Commit", "check", IsEnabled: false);
    private static readonly TileAction Discard = new("discard", "Discard changes", "delete",
        IsDestructive: true);

    /// <summary>
    /// Nothing destructive is offered at all.
    /// </summary>
    /// <remarks>
    /// Not "with a confirmation": confirming on a phone something you cannot see is theatre, and this
    /// codebase already holds that an unwired confirmation answers no.
    /// </remarks>
    [Fact]
    public void A_destructive_action_is_never_sent_to_a_phone()
    {
        var offered = PhoneTileActions.ForPhone([Refresh, Discard, Disabled]);

        Assert.Equal(["refresh", "commit"], offered.Select(a => a.Id));
    }

    /// <summary>And it cannot be reached by naming it either, which is the half that matters: the
    /// filter on the way out is a courtesy, the filter on the way in is the rule.</summary>
    [Fact]
    public void A_destructive_action_cannot_be_pressed_by_name()
    {
        Assert.False(PhoneTileActions.IsAllowed([Refresh, Discard], "discard"));
        Assert.True(PhoneTileActions.IsAllowed([Refresh, Discard], "refresh"));
    }

    /// <summary>
    /// An action the tile cannot do right now is refused as well.
    /// </summary>
    /// <remarks>
    /// Stricter than the keys are: Enter can always be pressed, whereas an action is gated on being
    /// enabled for this tile in this state. The phone's copy of the list is as old as the last state it
    /// was told about, and a tile moves through its own phases without anybody pressing anything.
    /// </remarks>
    [Fact]
    public void A_disabled_action_is_shown_and_refused()
    {
        Assert.Contains(PhoneTileActions.ForPhone([Disabled]), a => a.Id == "commit");
        Assert.False(PhoneTileActions.IsAllowed([Disabled], "commit"));
    }

    /// <summary>An id nothing offers gets the same answer malformed JSON gets: none.</summary>
    [Fact]
    public void An_unknown_id_is_refused()
    {
        Assert.False(PhoneTileActions.IsAllowed([Refresh], "format-the-disk"));
        Assert.False(PhoneTileActions.IsAllowed([], "refresh"));
    }

    /// <summary>The wire format, which the page is written against.</summary>
    [Fact]
    public void The_snapshot_carries_the_tile_and_what_it_can_do()
    {
        using var document = JsonDocument.Parse(
            PhoneTileActions.Describe("Git#1", [Refresh, Discard]));

        var root = document.RootElement;
        Assert.Equal("actions", root.GetProperty("type").GetString());
        Assert.Equal("Git#1", root.GetProperty("tile").GetString());

        var actions = root.GetProperty("actions");
        Assert.Equal(1, actions.GetArrayLength());
        Assert.Equal("refresh", actions[0].GetProperty("id").GetString());
        Assert.Equal("Refresh", actions[0].GetProperty("label").GetString());
        Assert.True(actions[0].GetProperty("enabled").GetBoolean());
    }

    /// <summary>A tile with nothing to offer still produces a message, so the page clears the row it
    /// was showing for the tile before it.</summary>
    [Fact]
    public void A_tile_with_no_actions_still_says_so()
    {
        using var document = JsonDocument.Parse(PhoneTileActions.Describe("", []));

        Assert.Equal(0, document.RootElement.GetProperty("actions").GetArrayLength());
    }

    /// <summary>
    /// Restarting a shell is the one thing a shipped tile keeps from a phone.
    /// </summary>
    /// <remarks>
    /// <para>The filter is the guarantee; this is the check that the guarantee is not doing all the work
    /// silently, and it is written as the exhaustive list rather than as "nothing is destructive" so
    /// that a kind adding a seventh action has to be thought about before the build goes green. Git's
    /// Discard changes and Undo last commit are deliberately not in its
    /// <see cref="ITileActions.Actions"/> at all — they are commands of its own view, where the user can
    /// see what they are about to lose.</para>
    /// <para>Restart shell is here because it kills whatever the shell is running, which is why the
    /// tile header asks first. A phone cannot be asked anything it could answer usefully, so the flag
    /// and not a confirmation is what stands between it and a build somebody had running.</para>
    /// </remarks>
    [Fact]
    public void Restarting_a_shell_is_the_only_thing_a_shipped_tile_withholds()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new mTiles.Services.Tiles.TileContext(directory.Path, settings.Service);

        List<string> withheld = [];
        foreach (var entry in TestTiles.Catalog(settings.Service).Entries)
        {
            var tile = entry.Kind.Create(context, null);
            try
            {
                if (tile is not ITileActions actions) continue;
                var offered = PhoneTileActions.ForPhone(actions.Actions);
                withheld.AddRange(actions.Actions.Except(offered).Select(a => a.Id));
            }
            finally { tile.Dispose(); }
        }

        // Distinct, because two shipped kinds now run a shell — a terminal and an agent — and both
        // withhold the same one action. What this pins is which actions a phone never sees, not how
        // many tiles offer them.
        Assert.Equal([TileActionIds.Restart], withheld.Distinct());
    }
}
