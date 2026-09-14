using System.Runtime.CompilerServices;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Activity;
using mTiles.Services.Notifications;

namespace mTiles.ViewModels;

/// <summary>
/// Tells the desktop when a tile stops to wait for the user — see <see cref="BlockedNotificationPolicy"/>
/// for when.
/// </summary>
/// <remarks>
/// <para>Holds one <see cref="BlockedQuestionTracker"/> per tile, because a property change says only that
/// the state moved, and "a new question arrived" needs the states before it. Keyed weakly on the leaf, so a
/// closed tile is forgotten with it.</para>
/// <para>What counts as on screen is asked of the window rather than known here: only the view knows
/// whether it has the keyboard.</para>
/// </remarks>
public sealed class BlockedTileNotifications
{
    private readonly SettingsService _settings;
    private readonly IDesktopNotifier _notifier;
    private readonly Func<WorkspaceViewModel, bool> _isOnScreen;
    private readonly Func<DateTime> _now;
    private readonly ConditionalWeakTable<LeafTileNodeViewModel, BlockedQuestionTracker> _trackers = new();

    public BlockedTileNotifications(SettingsService settings, IDesktopNotifier notifier,
        Func<WorkspaceViewModel, bool> isOnScreen, Func<DateTime>? now = null)
    {
        _settings = settings;
        _notifier = notifier;
        _isOnScreen = isOnScreen;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Called whenever a tile's activity may have changed.</summary>
    public void Observe(WorkspaceViewModel workspace, LeafTileNodeViewModel leaf)
    {
        var tracker = _trackers.GetOrCreateValue(leaf);
        if (!tracker.Observe(leaf.Activity, _isOnScreen(workspace),
                _settings.Settings.NotifyWhenTileBlocked, _now()))
            return;

        var tile = string.IsNullOrWhiteSpace(leaf.TileName) ? "A tile" : leaf.TileName.Trim();
        _notifier.Show($"{tile} — {workspace.Name}", ActivityDisplay.Tip(TileActivity.Blocked));
    }
}
