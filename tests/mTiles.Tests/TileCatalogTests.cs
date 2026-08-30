using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The invariants the tile registry has that a closed enum used to have for free.
/// </summary>
/// <remarks>
/// Going from a value in an enum to an object in a registry buys one class per kind and costs the two
/// guarantees the compiler was giving: that no two kinds are the same, and that every kind a saved
/// layout can name is one this build knows about. Both are pinned here, because the second one fails as
/// a workspace full of empty tiles rather than as anything a reader would call an error.
/// </remarks>
public sealed class TileCatalogTests
{
    /// <summary>Two kinds under one id would leave one of them unreachable, and which one would depend
    /// on the order of two lines in a startup method.</summary>
    [Fact]
    public void No_two_kinds_share_an_id()
    {
        using var settings = new TempSettings();
        var ids = TestTiles.Catalog(settings.Service).Entries.Select(e => e.Kind.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(TileKindIds.None, ids);
    }

    /// <summary>
    /// Every kind a layout written before this change could name is still buildable.
    /// </summary>
    /// <remarks>
    /// The test that catches a user's layout opening as a row of empty tiles. It is written against the
    /// historical enum on purpose: that is the exhaustive list of what is on people's disks, and it is
    /// the one thing about the old design still worth keeping — an enum nobody may add to is a perfect
    /// record of what was once written down.
    /// </remarks>
    [Theory]
    [InlineData(TileContentType.Terminal)]
    [InlineData(TileContentType.Note)]
    [InlineData(TileContentType.Todo)]
    [InlineData(TileContentType.Git)]
    [InlineData(TileContentType.Database)]
    [InlineData(TileContentType.Goal)]
    public void Every_kind_an_old_layout_could_name_is_registered(TileContentType historical)
    {
        using var settings = new TempSettings();
        var kind = TestTiles.Catalog(settings.Service).Kind(TileKindIds.FromLegacy(historical));

        Assert.NotNull(kind);
        Assert.Equal(TileKindIds.FromLegacy(historical), kind.Id);
    }

    /// <summary>And the absence of a kind stays the absence of a kind rather than becoming one.</summary>
    [Fact]
    public void Empty_is_not_a_kind()
    {
        using var settings = new TempSettings();
        var catalog = TestTiles.Catalog(settings.Service);

        Assert.Null(catalog.Kind(TileKindIds.None));
        Assert.Null(catalog.Kind(null));
        Assert.Null(catalog.Entry("something-nobody-registered"));
    }

    [Fact]
    public void A_duplicate_registration_is_refused()
    {
        var catalog = new TileCatalog().Register(new NoteTileKind(), _ => new Avalonia.Controls.UserControl());

        Assert.Throws<ArgumentException>(() =>
            catalog.Register(new NoteTileKind(), _ => new Avalonia.Controls.UserControl()));
    }

    /// <summary>
    /// What a kind writes down is enough to build the same tile again.
    /// </summary>
    /// <remarks>
    /// Save → Create → Save, compared as JSON. It is the whole promise the persistence side makes, and
    /// it is one a kind can break silently: a field read in <c>Create</c> under one key and written in
    /// <c>Save</c> under another comes back as a default, which for a note is a blank page in a new file
    /// and for a terminal is somebody else's shell.
    /// <para>Every registered kind is listed, including the one that writes nothing down today: what
    /// this asks is a promise about the kind rather than about its current state, so the first field
    /// Database ever remembers is covered by a line that is already here.</para>
    /// </remarks>
    [Theory]
    [InlineData(TileKindIds.Terminal)]
    [InlineData(TileKindIds.Note)]
    [InlineData(TileKindIds.Todo)]
    [InlineData(TileKindIds.Git)]
    [InlineData(TileKindIds.Database)]
    [InlineData(TileKindIds.Goal)]
    public void What_a_kind_saves_rebuilds_the_same_tile(string kindId)
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var kind = TestTiles.Catalog(settings.Service).Kind(kindId)!;
        var context = new TileContext(directory.Path, settings.Service);

        var first = kind.Create(context, null);
        JsonObject? saved;
        try { saved = kind.Save(first); }
        finally { first.Dispose(); }

        var second = kind.Create(context, saved);
        JsonObject? again;
        try { again = kind.Save(second); }
        finally { second.Dispose(); }

        Assert.Equal(saved?.ToJsonString(), again?.ToJsonString());
    }

    /// <summary>A kind builds the tile its own <c>KindId</c> names, or the view resolved from that id
    /// draws something else.</summary>
    [Fact]
    public void A_kind_builds_a_tile_that_agrees_about_what_it_is()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);

        foreach (var entry in TestTiles.Catalog(settings.Service).Entries)
        {
            var tile = entry.Kind.Create(context, null);
            try { Assert.Equal(entry.Kind.Id, tile.KindId); }
            finally { tile.Dispose(); }
        }
    }

    /// <summary>The one value a kind reads that is not its own: the tile's identity, which moves under
    /// the content while it is alive.</summary>
    [Fact]
    public void A_terminal_follows_the_tile_id_it_was_given_rather_than_a_copy_of_it()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();

        var id = "first";
        var context = new TileContext(directory.Path, settings.Service) { TileId = () => id };

        var terminal = Assert.IsType<TerminalTileViewModel>(((ITileKind)new TerminalTileKind()).Create(context, null));
        try
        {
            Assert.Equal("first", terminal.TileId);

            id = "second";
            Assert.Equal("second", terminal.TileId);
        }
        finally { terminal.Dispose(); }
    }

    /// <summary>Naming belongs to the kind: numbered by default, and one past the highest number a
    /// saved layout already used.</summary>
    [Fact]
    public void A_kind_numbers_its_tiles_after_the_names_already_in_use()
    {
        using var settings = new TempSettings();
        var git = TestTiles.Catalog(settings.Service).Kind(TileKindIds.Git)!;

        Assert.Equal("Git#1", git.NameFor(Names()));
        Assert.Equal("Git#4", git.NameFor(Names("Git#1", "Git#3")));
    }

    /// <summary>The one kind that does not number, and the reason the rule is the kind's to state:
    /// several terminals are open at once and a number says nothing about which is which.</summary>
    [Fact]
    public void A_terminal_names_itself_and_never_twice_the_same()
    {
        using var settings = new TempSettings();
        var terminal = TestTiles.Catalog(settings.Service).Kind(TileKindIds.Terminal)!;

        var first = terminal.NameFor(Names());

        // An adjective and an animal, not a count: the same name is never handed out twice, and the
        // numbered default is not what comes back.
        Assert.NotEqual("Terminal#1", first);
        Assert.NotEqual(first, terminal.NameFor(Names(first)));
    }

    /// <summary>
    /// A kind says for itself whether anything must be asked before it is built.
    /// </summary>
    /// <remarks>The branch the empty tile used to carry: it knew that a terminal has to be asked which
    /// shell. Every other kind answers with nothing, so it is built on the click.</remarks>
    [Fact]
    public void Only_a_kind_that_asks_for_one_gets_a_setup_step()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);

        foreach (var entry in TestTiles.Catalog(settings.Service).Entries)
        {
            var options = entry.Kind.SetupOptions(context);
            if (entry.Kind.Id == TileKindIds.Terminal)
            {
                // The default shell first, carrying no state, then one card per detected shell — and
                // nothing at all when this machine has only one, because a chooser with a single card
                // is a click that cannot be got wrong. Computed rather than written out for the same
                // reason as the agents below: how many shells are here is a fact about the machine.
                if (context.Shells.Count <= 1)
                {
                    Assert.Empty(options);
                }
                else
                {
                    Assert.Equal(context.Shells.Count + 1, options.Count);
                    Assert.Null(options[0].State);
                    Assert.Equal(context.Shells.Select(shell => shell.DisplayName),
                        options.Skip(1).Select(o => o.Label));
                    Assert.Equal(context.Shells.Select(shell => shell.DisplayName),
                        options.Skip(1).Select(
                            o => o.State?[TerminalTileKind.ShellNameKey]?.GetValue<string>()));
                }
            }
            else if (entry.Kind.Id == TileKindIds.Agent)
            {
                // One card per agent this machine has, and nothing to ask when it has at most one —
                // which is why the expectation is computed rather than written out: how many of the
                // five are installed is a fact about the machine the tests are running on.
                var available = settings.Service.Settings.AiAgentInstances
                    .Where(instance => AiAgentCatalog.IsAvailable(instance, settings.Service.Settings))
                    .ToList();

                if (available.Count <= 1)
                {
                    Assert.Empty(options);
                }
                else
                {
                    Assert.Equal(available.Select(i => i.Name), options.Select(o => o.Label));
                    Assert.Equal(available.Select(i => i.Id),
                        options.Select(o => o.State?[AgentTileKind.InstanceIdKey]?.GetValue<string>()));
                }
            }
            else
            {
                Assert.Empty(options);
            }
        }
    }

    private static IReadOnlySet<string> Names(params string[] names) =>
        new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Shell detection happens once per context, and a copy of a context does not repeat it.
    /// </summary>
    /// <remarks>
    /// Detection walks every directory on <c>PATH</c>, on the UI thread, while a workspace is being
    /// restored — so a workspace with eight saved terminals paying for eight scans is the failure this
    /// pins. Same list means one scan, and it has to survive the <c>with</c> a terminal makes when it
    /// binds its own tile id, because that copy is what every terminal actually builds from.
    /// </remarks>
    [Fact]
    public void A_context_detects_the_shells_once_and_its_copies_share_them()
    {
        using var settings = new TempSettings();
        using var directory = new TempDirectory();
        var context = new TileContext(directory.Path, settings.Service);

        var copy = context with { TileId = () => "tile" };

        Assert.Same(context.Shells, context.Shells);
        Assert.Same(context.Shells, copy.Shells);
    }
}

/// <summary>A directory that goes away with the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("mtiles-tiles").FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* a temp directory nobody will read */ }
    }
}
