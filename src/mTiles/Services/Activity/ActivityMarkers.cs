using mTiles.Models;

namespace mTiles.Services.Activity;

/// <summary>One phrase a CLI paints, and what it means when it is the most recent one.</summary>
/// <param name="Phrase">Matched case-insensitively, as a substring. Not a regular expression: these
/// are somebody else's UI strings and the ones worth matching are the fixed words in them.</param>
/// <param name="Detail">What to put in the tooltip when this marker decides the answer.</param>
public sealed record ActivityMarker(string Phrase, TileActivity State, string? Detail = null);

/// <summary>
/// Reads a window of recently painted text by asking which of a CLI's own markers was painted last.
/// </summary>
/// <remarks>
/// <para><b>Last one wins, and that is the rule rather than a tie-break.</b> A window of recent output
/// holds the permission prompt <em>and</em> the working indicator that came after the user answered it,
/// because both were painted inside it. Precedence by kind — "blocked beats working" — gets that
/// backwards exactly when it matters, leaving a tile marked as waiting for an answer it was given. The
/// order the child painted them in is the only evidence of which is current.</para>
/// <para>Every marker table is somebody else's UI, so each one is pinned by a test for the reason
/// <c>AiAgentTests</c> pins the flags: when the CLI rewords its status bar, that has to arrive as a
/// failing build rather than as a tile that quietly stops reporting.</para>
/// </remarks>
public static class ActivityMarkers
{
    /// <summary>The state of whichever marker appears last in <paramref name="recent"/>, or Unknown
    /// when none of them do.</summary>
    public static TileActivity LastWins(
        string recent, IReadOnlyList<ActivityMarker> markers, out string? detail)
    {
        detail = null;
        if (recent.Length == 0) return TileActivity.Unknown;

        var bestAt = -1;
        var best = TileActivity.Unknown;

        foreach (var marker in markers)
        {
            var at = recent.LastIndexOf(marker.Phrase, StringComparison.OrdinalIgnoreCase);
            if (at <= bestAt) continue;
            bestAt = at;
            best = marker.State;
            detail = marker.Detail;
        }

        if (best == TileActivity.Unknown) detail = null;
        return best;
    }
}
