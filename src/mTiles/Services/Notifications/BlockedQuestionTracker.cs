using mTiles.Models;

namespace mTiles.Services.Notifications;

/// <summary>
/// One tile's history of questions: fed every state the tile reports, answers whether this one is a
/// question to announce. The rules are <see cref="BlockedNotificationPolicy"/>'s; this only remembers
/// what they need — the previous state, whether the pending question was announced, and when the last
/// one ended.
/// </summary>
public sealed class BlockedQuestionTracker
{
    private TileActivity _activity;
    private bool _questionAnnounced;
    private DateTime? _questionEndedAt;

    /// <returns>Whether a notification should be shown now.</returns>
    public bool Observe(TileActivity current, bool onScreen, bool enabled, DateTime now)
    {
        var previous = _activity;
        _activity = current;
        if (previous == current) return false;

        if (BlockedNotificationPolicy.EndsQuestion(current))
        {
            _questionEndedAt ??= now;
            return false;
        }
        if (current is not TileActivity.Blocked) return false;

        BeginQuestionIfNew(now);
        var announce = BlockedNotificationPolicy.ShouldNotify(enabled, onScreen, _questionAnnounced);
        // A question the user was looking at when it arrived has been seen, and needs no toast later.
        if (announce || onScreen) _questionAnnounced = true;
        return announce;
    }

    private void BeginQuestionIfNew(DateTime now)
    {
        if (BlockedNotificationPolicy.IsNewQuestion(_questionEndedAt, now)) _questionAnnounced = false;
        _questionEndedAt = null;
    }
}
