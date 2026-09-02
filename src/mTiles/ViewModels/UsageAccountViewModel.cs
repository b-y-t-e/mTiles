using mTiles.Models;
using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>One limit window as a card draws it: a bar, a percentage, a reset and a verdict.</summary>
/// <remarks>Built once from a report rather than binding to one, because everything on it is derived
/// from the same instant: a card whose bar and whose countdown were worked out from two different
/// <c>DateTimeOffset.Now</c> would disagree with itself by however long the refresh took.</remarks>
public sealed class UsageWindowViewModel
{
    public UsageWindowViewModel(AiUsageWindow window, DateTimeOffset now)
    {
        var pace = UsagePace.For(window, now);

        Label = window.Label;
        UsedPercent = window.UsedPercent ?? 0;
        HasPercent = window.UsedPercent is not null;
        PercentLabel = window.UsedPercent is { } used ? $"{used:0}%" : "";
        ExpectedPercent = pace.ExpectedPercent;
        IsOverspending = pace.State == UsagePaceState.Ahead;
        ResetLabel = UsageDisplay.Reset(window.ResetsAt, now);
        CountdownLabel = UsageDisplay.Countdown(window.ResetsAt, now);
        PaceLabel = UsageDisplay.Pace(pace, now);
        AmountLabel = "";
    }

    /// <summary>What the window is called — <c>5h</c>, <c>7d</c>.</summary>
    public string Label { get; }

    /// <summary>The same, punctuated for a row with no bar between the name and the figure.</summary>
    /// <remarks>A window with a bar needs no colon: the bar is what separates the name from the figure,
    /// and it is wide. A window answered in money has nothing in between, so the two were left at
    /// opposite ends of an empty cell — <c>today</c> against the left margin and <c>$0.18</c> against
    /// the right, reading as two facts that had nothing to do with each other.</remarks>
    public string LabelWithColon => Label.Length > 0 ? $"{Label}:" : "";

    /// <summary>How much of it is spent, 0..100, and zero where nothing was said — which is why
    /// <see cref="HasPercent"/> is asked before a bar is drawn.</summary>
    public double UsedPercent { get; }

    public bool HasPercent { get; }
    public string PercentLabel { get; }
    public double? ExpectedPercent { get; }
    public bool IsOverspending { get; }
    /// <summary>The reset spelled out in full, which lives in the tooltip.</summary>
    public string ResetLabel { get; }

    /// <summary>How long until it comes back — the half of the reset a glance is for.</summary>
    public string CountdownLabel { get; }

    public string PaceLabel { get; }

    /// <summary>
    /// The one piece of text on the right of the row: what is spent, and when it comes back.
    /// </summary>
    /// <remarks><b>One string and not three TextBlocks.</b> A percentage, a clock time and a countdown
    /// each in their own column made three columns to align and three things to read where the row
    /// carries two facts. The clock time went to the tooltip; what is left is the figure and the wait.
    /// </remarks>
    public string Figure =>
        string.Join(" · ", new[]
        {
            HasAmount ? AmountLabel : PercentLabel,
            CountdownLabel,
        }.Where(part => part.Length > 0));

    /// <summary>
    /// Everything the row knows, for whoever hovers it.
    /// </summary>
    /// <remarks><b>This is where the pace sentence went, and it is not a demotion.</b> "on pace" and
    /// "13 points spare" under every bar was a line of prose per window per account — a number on every
    /// point, which is the thing a reader stops seeing. The state worth acting on is already on the row
    /// without words: fill past the tick in the danger colour, and the figure beside it in the same.
    /// </remarks>
    public string Tooltip =>
        string.Join(" · ", new[] { Label, ResetLabel, PaceLabel }.Where(part => part.Length > 0));

    /// <summary>What the window cost, where the account answers in money. Set by the money card.</summary>
    public string AmountLabel { get; init; }

    /// <summary>Whether there is an amount to draw, which is what stands in for the bar on a money
    /// account: it has no percentage, so the row would otherwise be a label and nothing else.</summary>
    public bool HasAmount => AmountLabel.Length > 0;
}

/// <summary>
/// One account's card.
/// </summary>
/// <remarks>
/// <para><b>A subscription answers in percent and a key in money, and the card shows whichever it was
/// given.</b> There is no rate that converts one into the other, so nothing here tries: an account with
/// windows gets bars, an account with amounts gets its amounts, and one that answers both gets both.
/// </para>
/// <para><b>There is no card for an account that could not be asked.</b> The tile drops those before it
/// gets here — see <c>UsageTileViewModel.Rebuild</c> — so nothing on this type carries a problem.</para>
/// </remarks>
public sealed class UsageAccountViewModel
{
    public UsageAccountViewModel(AiUsageReport report, DateTimeOffset now)
    {
        SourceId = report.SourceId;
        Title = report.SourceName;
        Plan = report.Plan ?? "";
        AgeLabel = UsageDisplay.Age(report, now);
        Windows = [.. report.Windows.Select(window => new UsageWindowViewModel(window, now)
        {
            AmountLabel = UsageDisplay.Money(window.UsedAmount, report.Currency),
        })];
        RemainingLabel = UsageDisplay.Money(report.RemainingCredit, report.Currency);
    }

    public string SourceId { get; }
    public string Title { get; }
    public string Plan { get; }

    /// <summary>Whether the plan is worth saying beside the name.</summary>
    /// <remarks><b>Not when the name already says it.</b> A sign-in is called whatever the user typed,
    /// and what they type for the subscription they are logging into is usually its plan — so the card
    /// read "Claude Code · Max Max", the account's own name followed by the service agreeing with it.
    /// The plan is the service's answer and the name is the user's, so the user's wins and the echo
    /// goes.</remarks>
    public bool HasPlan =>
        Plan.Length > 0 && !Title.Contains(Plan, StringComparison.OrdinalIgnoreCase);
    /// <summary>How old the reading is, where it is older than the window it describes.</summary>
    public string AgeLabel { get; }

    public bool IsStale => AgeLabel.Length > 0;
    public IReadOnlyList<UsageWindowViewModel> Windows { get; }
    public bool HasWindows => Windows.Count > 0;
    public string RemainingLabel { get; }
    public bool HasRemaining => RemainingLabel.Length > 0;

    /// <summary>What is left on the key.</summary>
    /// <remarks><b>The balance and nothing beside it.</b> What each window cost is already on that
    /// window's own row; a row of daily bars and a note saying how many of those days this application
    /// had been watching for was a second line answering a question nobody was asking. What a metered
    /// account is looked at for is how much money is left.</remarks>
    public string MoneyLabel => HasRemaining ? $"{RemainingLabel} left" : "";

    public bool HasMoney => MoneyLabel.Length > 0;
}
