using System.Globalization;

namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// When a conversation was last used, written the way a row in a list of them says it.
/// </summary>
/// <remarks>
/// <para>Pure, and argued in a table test, for the reason <c>UsagePace</c> and <c>ActivityPolicy</c> are: the
/// thresholds are an opinion, and an opinion in a converter is one nothing can disagree with in writing.</para>
/// <para><b>Not <c>UsageDisplay.Age</c>.</b> That answers "is this reading too old to trust", which is a
/// duration and is said only when it is worth saying. This answers "which of these is the one I was in
/// yesterday", which every row has to carry and which is read by recognising a day, not by comparing spans:
/// <c>2d 3h</c> is exactly the shape that has to be converted back into Tuesday before it means anything.</para>
/// </remarks>
public static class ConversationWhen
{
    /// <summary>The last week is named by its day, because that is the week somebody remembers by name.</summary>
    private const int DaysNamedByWeekday = 7;

    public static string For(DateTimeOffset instant, DateTimeOffset now)
    {
        var local = instant.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var days = (today - local.Date).Days;

        if (days <= 0) return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (days == 1) return $"yesterday {local:HH\\:mm}";
        if (days < DaysNamedByWeekday) return local.ToString("ddd HH:mm", CultureInfo.CurrentCulture);
        // The year only where it is not this one: on a list that is nearly all recent, it is noise on every row.
        return local.Year == today.Year
            ? local.ToString("d MMM", CultureInfo.CurrentCulture)
            : local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
