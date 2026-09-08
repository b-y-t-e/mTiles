namespace mTiles.Models;

/// <summary>
/// What a tile is doing, as far as anything watching it can tell.
/// </summary>
/// <remarks>
/// <para><b>Four states, because three lie.</b> <see cref="Unknown"/> is not <see cref="Idle"/> — it is
/// the absence of an answer, which falls through to a lower-ranked source instead of asserting
/// anything. Read as "idle" it becomes the failure every screen-reading tool has already paid for: a
/// prompt shape nobody has a rule for is reported as a finished agent, and whoever is watching the list
/// never goes back to it.</para>
/// <para><b><see cref="Blocked"/> earns its place by being the state worth acting on.</b> An agent
/// working is a reason to leave the tile alone; an agent waiting for permission is the one moment the
/// user has to come back. Folded into <see cref="Working"/> — which is where it was — the list says
/// "something is happening in there" for both, and the only one that needed saying is the one it
/// cannot.</para>
/// </remarks>
public enum TileActivity
{
    /// <summary>Nothing that can be believed. Falls through to a lower authority, and where there is
    /// none, shows as no light at all.</summary>
    Unknown,

    /// <summary>At rest: a shell at its prompt, an agent waiting for a message.</summary>
    Idle,

    /// <summary>Doing something the user is waiting on.</summary>
    Working,

    /// <summary>Stopped on the user: a permission prompt, a question, a form.</summary>
    Blocked,
}

/// <summary>
/// How much a reading is worth, as a rank. Higher wins, and <b>silences</b> everything below it.
/// </summary>
/// <remarks>
/// <para>The whole of the arbitration is this ordering. It is an enum rather than an integer so the
/// ranking is the type's business and cannot be spelled differently in two places, and it is ordered
/// deliberately — <c>CompareTo</c> is what <c>ActivityPolicy</c> asks.</para>
/// <para><b>Silencing, not averaging.</b> A lower source is not evidence to be weighed against a higher
/// one; it is a worse instrument measuring the same thing. An agent that reports its own state through
/// a hook and a screen rule reading the same pane disagree constantly — the screen still shows the last
/// frame the agent painted — and a scheme that lets both speak flickers between them.</para>
/// </remarks>
public enum ActivityAuthority
{
    /// <summary>The child is writing bytes. True of a build, a shell and an agent alike, and says
    /// nothing about which — the honest floor.</summary>
    Output,

    /// <summary>Text the child painted recently, matched against what that CLI's own UI says. Somebody
    /// else's strings, so it moves; below OSC for that reason.</summary>
    Screen,

    /// <summary>A terminal sequence the child emitted on purpose — its title, or a progress report.
    /// Still a symptom rather than a statement, but one the CLI chose to send.</summary>
    Osc,

    /// <summary>The agent said so itself, through a hook or its own event stream. Authoritative; when
    /// one of these is fresh, nothing else is read.</summary>
    Lifecycle,
}

/// <summary>One source's answer, at a moment.</summary>
/// <remarks><paramref name="At"/> is stamped by the source rather than read when the reading is
/// examined, for the reason <c>AiUsageReport.MeasuredAt</c> is: a reading is as fresh as the evidence
/// behind it, not as fresh as the look at it.</remarks>
/// <param name="Detail">What to say about it in a tooltip — "waiting for permission to run a command",
/// "compacting". Never required: a state with nothing to add carries null.</param>
public sealed record ActivityReading(
    TileActivity State,
    ActivityAuthority Authority,
    DateTime At,
    string? Detail = null);

/// <summary>The one place four states become the one question a light asks.</summary>
/// <remarks>An extension rather than a property repeated on the leaf, the workspace and the row: it was
/// the same expression written three times, which is a second definition that will drift the first time
/// somebody decides Blocked should not light a row.</remarks>
public static class TileActivityExtensions
{
    /// <summary>Whether this state is worth a mark in the workspace list.</summary>
    /// <remarks>Blocked counts. A tile stopped on a question is the strongest reason there is to go
    /// back to a workspace, and a row that goes dark the moment an agent starts waiting is the failure
    /// this whole layer exists to end.</remarks>
    public static bool ShowsAsBusy(this TileActivity activity) =>
        activity is TileActivity.Working or TileActivity.Blocked;
}
