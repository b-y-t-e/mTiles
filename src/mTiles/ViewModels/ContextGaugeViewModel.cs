using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.AgentSessions.Events;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.ViewModels;

/// <summary>
/// How full the model's context is and what the conversation has cost, as the bar at the foot of a tile
/// draws them.
/// </summary>
/// <remarks>
/// <para><b>One of these for both agent tiles.</b> The Agent tile is told the figures by the protocol it
/// drives; the terminal agent tile reads them out of the CLI's own store
/// (<c>IAgentSessionLog</c>). Two sources, one answer and one drawing — so the arithmetic, the wording
/// and the rule about what is drawn when live here rather than twice, and a bar means the same thing in
/// whichever tile it appears.</para>
/// <para><b>Three states, not two.</b> Nothing said at all (no bar, no figures — the ordinary state of a
/// tile whose agent has not spoken yet, and the permanent state of one whose CLI keeps no readable
/// store); figures without a window (the five agents that count tokens and never name the limit, until
/// the caller supplies one); and both, which is the only case that draws a bar. That is
/// <see cref="ContextGauge"/>'s rule, kept: a window nobody named is neither a full bar nor an empty
/// one.</para>
/// </remarks>
public sealed partial class ContextGaugeViewModel : ObservableObject
{
    /// <summary>What the bar reads before the first figure has arrived.</summary>
    /// <remarks>A constant here because the Agent tile says it too, with its own cost figure after it:
    /// that tile is told what the conversation has spent and this one is told only what some CLIs
    /// write down, so the shared half is the sentence and the cost is not. Saying it here rather than
    /// in the markup keeps the two tiles from drifting into two spellings of one state.</remarks>
    public const string NothingKnownYet = "context not known yet";

    /// <summary>
    /// Whether the bar stands there saying nothing is known yet, instead of not being drawn.
    /// </summary>
    /// <remarks>
    /// <para>The Agent tile's rule, and it is about layout rather than about the figure: a row that
    /// appears with the first reading pushes everything above it up one line, which in a terminal is
    /// not a nudge — the cell grid is remeasured and the shell reflows, in the middle of the first
    /// thing the user asked the agent to do.</para>
    /// <para><b>Off unless the tile can ever get a reading.</b> An agent whose CLI keeps no store this
    /// application can read (agy, Grok) would otherwise wear "context not known yet" for the life of
    /// the session — a line that never resolves, which is a worse answer than the honest blank the
    /// absent bar already gives.</para>
    /// </remarks>
    public bool KeepsItsPlace { get; init; }

    /// <summary>Whether the bar is drawn at all.</summary>
    public bool IsDrawn => HasAnythingToSay || KeepsItsPlace;

    /// <summary>What the bar actually reads — the figures, or that there are none yet.</summary>
    public string BarText => HasAnythingToSay ? Text : NothingKnownYet;

    /// <summary>How much of the window is gone, 0 to 100, or null when it cannot be said.</summary>
    /// <remarks>Null hides the bar and leaves the figures, which is the whole reason it is a property of
    /// its own rather than something derived in the view from <see cref="Text"/>.</remarks>
    [ObservableProperty] private double? _usedPercent;

    /// <summary>"106.8k / 1M tokens · $1.15" — whatever of it was actually said.</summary>
    [NotifyPropertyChangedFor(nameof(HasAnythingToSay))]
    [NotifyPropertyChangedFor(nameof(IsDrawn))]
    [NotifyPropertyChangedFor(nameof(BarText))]
    [ObservableProperty] private string _text = "";

    /// <summary>Whether there is anything to draw at all.</summary>
    public bool HasAnythingToSay => Text.Length > 0;

    /// <summary>Takes the figures as an agent reported them.</summary>
    /// <param name="fallbackWindow">The context window to use when the agent did not name one — the
    /// figure the provider gives for the model in question. Only codex names its own, so for the other
    /// four this is what turns a count of tokens into a bar. Ignored when the agent did say.</param>
    public void Show(long? used, long? window, decimal? cost, long? fallbackWindow = null)
    {
        var usage = new TokenUsage(used, window ?? fallbackWindow, CostUsd: cost);
        UsedPercent = ContextGauge.PercentUsed(usage);
        Text = Describe(usage);
    }

    /// <summary>Clears it, so the bar goes away rather than standing at its last reading.</summary>
    /// <remarks>What a tile calls when its agent stops being the one it was: a new conversation, a
    /// change of instance. A figure left behind from the previous conversation is worse than none,
    /// because nothing on screen says it is stale.</remarks>
    public void Clear()
    {
        UsedPercent = null;
        Text = "";
    }

    /// <summary>"42.1k / 200k tokens · $0.31" — whatever of it the agent said.</summary>
    /// <remarks>Each part is dropped rather than defaulted: a cost of zero on an agent that does not
    /// report cost reads as a conversation that has been free, and a window nobody named must not be
    /// invented in the words any more than it is in the bar.</remarks>
    public static string Describe(TokenUsage? usage)
    {
        if (usage is null) return "";

        var parts = new List<string>();
        if (usage.UsedTokens is { } used)
            parts.Add(usage.ContextWindow is { } window
                ? $"{Tokens(used)} / {Tokens(window)} tokens"
                : $"{Tokens(used)} tokens");
        if (usage.CostUsd is { } cost and > 0)
            // A real amount under a cent rounds to $0.00, which reads as the one thing it is not: free.
            // Measured on a live pi session — a turn cost $0.0024, and the bar said the conversation had
            // cost nothing.
            parts.Add(cost < 0.005m
                ? "<$0.01"
                : string.Create(CultureInfo.InvariantCulture, $"${cost:0.00}"));

        return string.Join(" · ", parts);
    }

    /// <summary>Invariant, like every other figure the application draws in its English interface.</summary>
    private static string Tokens(long count) => count switch
    {
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000_000d:0.#}M"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{count / 1_000d:0.#}k"),
        _ => count.ToString(CultureInfo.InvariantCulture),
    };
}
