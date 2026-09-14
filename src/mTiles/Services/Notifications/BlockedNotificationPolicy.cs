using mTiles.Models;

namespace mTiles.Services.Notifications;

/// <summary>
/// Whether a tile that has just changed state is worth a desktop notification.
/// </summary>
/// <remarks>
/// <para><b>Only <see cref="TileActivity.Blocked"/>.</b> It is the one state that needs somebody: a tile
/// working is a reason to leave it alone, and a finished one will still be finished when it is looked at.
/// A notification for every state would be a notification nobody reads by the second day.</para>
/// <para><b>Not while it is already in front of the user</b> — the window focused and the tile's own
/// workspace the one on screen. There the header's mark already says it, and a toast on top of a prompt
/// the user is reading is noise. Another workspace in the same focused window is <em>not</em> on screen:
/// the row's mark is a small thing at the edge of a window somebody is busy in.</para>
/// <para><b>Once per question.</b> The state is read off the screen, and a reading goes stale: a prompt
/// nobody answers in a quiet TUI falls from Blocked to Unknown when its reading expires and comes back at
/// the next repaint. <see cref="TileActivity.Unknown"/> is the absence of an answer, so it does not end
/// the question — only <see cref="EndsQuestion"/> does, when the tile is affirmatively working or idle
/// again.</para>
/// <para><b>A question has to have been over for a moment to be over.</b> A permission prompt redrawn
/// while it is answered can go Blocked → Working → Blocked within a second, and that is one question. The
/// window is measured from the end of the question and not from the last notification: measured from the
/// notification it swallowed a genuinely new question asked shortly after the first was answered, and
/// since nothing re-observes a tile that stays Blocked, that question was never announced at all.</para>
/// </remarks>
public static class BlockedNotificationPolicy
{
    /// <summary>How long a question must have been over for the next Blocked to be a new one.</summary>
    public static readonly TimeSpan SameQuestionWindow = TimeSpan.FromSeconds(3);

    /// <summary>Whether this state says the question the tile was waiting on is over.</summary>
    public static bool EndsQuestion(TileActivity current) =>
        current is TileActivity.Working or TileActivity.Idle;

    /// <param name="questionEndedAt">When the tile last stopped waiting, or null when it has not since the
    /// last question.</param>
    public static bool IsNewQuestion(DateTime? questionEndedAt, DateTime now) =>
        questionEndedAt is { } ended && now - ended >= SameQuestionWindow;

    /// <param name="onScreen">Whether the window is focused and showing this tile's workspace.</param>
    /// <param name="questionAlreadyAnnounced">Whether the pending question was already notified about or
    /// seen on screen.</param>
    public static bool ShouldNotify(bool enabled, bool onScreen, bool questionAlreadyAnnounced) =>
        enabled && !onScreen && !questionAlreadyAnnounced;
}
