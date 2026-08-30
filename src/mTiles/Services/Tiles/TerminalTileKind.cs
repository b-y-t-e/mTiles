using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.Services.Shells;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>
/// A shell in a tile.
/// </summary>
/// <remarks>
/// <para>A shell and nothing else. What used to make this kind complicated — a startup script, a
/// fallback, a required AI binary — was the shell profile, and a profile that started an AI CLI is now
/// an agent tile (<see cref="AgentTileKind"/>) with the CLI's own commands rather than a script the user
/// had to write and keep working.</para>
/// <para>One <see cref="Create"/> for both ways in: a fresh terminal chosen from the shell chooser
/// arrives with <c>shellName</c> in its state, and one restored from disk arrives with the same key. A
/// shell that is no longer installed falls through to the default rather than leaving the tile
/// without one.</para>
/// </remarks>
public sealed class TerminalTileKind : TileKind<TerminalTileViewModel>
{
    /// <summary>What a layout written before agents existed called the profile a tile was created
    /// from.</summary>
    /// <remarks>Nothing here reads it any more — <c>AgentTileMigration</c> does, once, to work out which
    /// of those tiles were an AI CLI in a shell. Kept as a name rather than a literal because that
    /// migration and this kind have to agree about the spelling.</remarks>
    public const string UserProfileIdKey = "userProfileId";

    /// <summary>The shell this tile runs.</summary>
    /// <remarks>The shell's <em>display</em> name rather than its id, and that is about rollback: a
    /// build Velopack has rolled back matches this against the names it detected, where <c>Git Bash</c>
    /// is a match and <c>gitbash</c> is not. Reading is tolerant of both
    /// (<c>ShellTerminalCatalog.Find</c>).</remarks>
    public const string ShellNameKey = "shellName";

    /// <summary>
    /// A command typed into the tile the moment it opens.
    /// </summary>
    /// <remarks><b>Set when a tile is created, never saved, and run once</b>, which is the whole of its
    /// lifetime: what puts it there is the install command an agent's <c>InstallPlan</c> names, and
    /// running that again is not what anybody agreed to in the dialog that showed it. A tile restored
    /// from disk is a plain shell, and so is this one the moment it has launched — the script is handed
    /// to the tile as its one-time startup (<c>TerminalTileViewModel</c> consumes it at the first
    /// launch), so Restart shell and Ctrl+Shift+R start a shell rather than the installer again.</remarks>
    public const string StartupScriptKey = "startupScript";

    public override string Id => TileKindIds.Terminal;
    public override string DisplayName => "Terminal";
    public override string IconId => "console";
    public override string AccentKey => "TileAccentTerminal";

    /// <summary>An adjective and an animal, not a number: there are usually several terminals open at
    /// once and <c>Terminal#3</c> says nothing about which one is which.</summary>
    public override string NameFor(IReadOnlySet<string> used) => TileNameGenerator.Generate(used);

    /// <summary>
    /// The shells this machine has, with the default named as such.
    /// </summary>
    /// <remarks>
    /// <para>The step is skipped when there is only one shell to offer: a chooser with a single card is
    /// a click the user has to make and cannot get wrong, which is a click for nothing.</para>
    /// <para>The default comes first and is a separate card rather than a mark on one of the others,
    /// because it is a different answer: "whatever Settings says" follows a change made there, where
    /// picking PowerShell by name does not.</para>
    /// </remarks>
    public override IReadOnlyList<TileSetupOption> SetupOptions(TileContext context)
    {
        var shells = context.Shells;
        if (shells.Count <= 1) return [];

        return
        [
            new TileSetupOption("Default shell", IconId, "TextMuted", State: null),
            .. shells.Select(shell => new TileSetupOption(
                shell.DisplayName, shell.Shell.IconId, "TextMuted",
                new JsonObject { [ShellNameKey] = shell.DisplayName })),
        ];
    }

    protected override TerminalTileViewModel Create(TileContext context, JsonObject? state)
    {
        // A shell that is no longer installed falls through to the default rather than leaving the tile
        // without one — which is also the answer for a tile created without the chooser.
        var shellName = state.String(ShellNameKey);
        var shell = shellName is null
            ? null
            : ShellTerminalCatalog.Resolve(shellName, context.Shells, context.Settings.Settings);

        var startup = state.String(StartupScriptKey);

        return new TerminalTileViewModel(context.WorkingDirectory, shell, context.Settings,
            tileId: context.TileId, oneTimeStartup: startup);
    }

    protected override JsonObject? Save(TerminalTileViewModel tile) =>
        new() { [ShellNameKey] = tile.Shell.DisplayName };
}
