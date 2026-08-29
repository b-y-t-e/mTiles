using System.Text.Json.Nodes;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>
/// A shell in a tile.
/// </summary>
/// <remarks>
/// The kind where one <see cref="Create"/> replaced three: a fresh terminal takes the user's default
/// shell, one chosen from the profile chooser arrives with <c>userProfileId</c> in its state, and one
/// restored from disk arrives with the same key plus the shell it was last running. All three read the
/// same two values out of the same object, so the chooser and the loader can no longer disagree about
/// what a profile means.
/// </remarks>
public sealed class TerminalTileKind : TileKind<TerminalTileViewModel>
{
    /// <summary>What the layout file calls the profile a tile was created from.</summary>
    public const string UserProfileIdKey = "userProfileId";

    /// <summary>And the shell it was running, as a fallback for when that profile has been deleted.</summary>
    public const string ShellNameKey = "shellName";

    public override string Id => TileKindIds.Terminal;
    public override string DisplayName => "Terminal";
    public override string IconId => "console";
    public override string AccentKey => "TileAccentTerminal";

    /// <summary>An adjective and an animal, not a number: there are usually several terminals open at
    /// once and <c>Terminal#3</c> says nothing about which one is which.</summary>
    public override string NameFor(IReadOnlySet<string> used) => TileNameGenerator.Generate(used);

    /// <summary>
    /// The shell profiles this workspace offers, and the default shell beside them.
    /// </summary>
    /// <remarks>
    /// The one kind with a step before it, and the reason there is a general one: an empty tile no
    /// longer knows that a terminal has profiles, it asks whichever kind was clicked what it needs
    /// first. Nothing to ask when there are no profiles — the tile is built on the click, as it always
    /// was.
    /// </remarks>
    public override IReadOnlyList<TileSetupOption> SetupOptions(TileContext context)
    {
        var profiles = context.AvailableProfiles();
        if (profiles.Count == 0) return [];

        var options = new List<TileSetupOption>
        {
            new("Default shell", IconId, "TextMuted", State: null)
        };
        options.AddRange(profiles.Select(profile => new TileSetupOption(
            profile.Name, "script-outline", "TextMuted",
            new JsonObject { [UserProfileIdKey] = profile.Id })));
        return options;
    }

    protected override TerminalTileViewModel Create(TileContext context, JsonObject? state)
    {
        var settings = context.Settings.Settings;
        var profileId = state.String(UserProfileIdKey);

        if (profileId is not null
            && settings.ShellProfiles.FirstOrDefault(p => p.Id == profileId) is { } profile)
        {
            return new TerminalTileViewModel(context.WorkingDirectory,
                ShellDetector.ResolveFromUserProfile(profile, settings, context.Shells), context.Settings,
                LaunchScripts.FromProfile(profile.StartupScript, profile.FallbackScript),
                profile.Id, context.TileId);
        }

        // The profile is gone, or there never was one. The shell it was last running is the closest
        // thing to what the user had, and a shell that is no longer installed falls through to the
        // default rather than leaving the tile without one.
        var shellName = state.String(ShellNameKey);
        var shell = shellName is null
            ? null
            : context.Shells.FirstOrDefault(s =>
                s.Name.Equals(shellName, StringComparison.OrdinalIgnoreCase));

        return new TerminalTileViewModel(context.WorkingDirectory, shell, context.Settings,
            tileId: context.TileId);
    }

    protected override JsonObject? Save(TerminalTileViewModel tile)
    {
        var state = new JsonObject { [ShellNameKey] = tile.Shell.Name };
        if (tile.UserProfileId is { Length: > 0 } profileId)
            state[UserProfileIdKey] = profileId;
        return state;
    }
}
