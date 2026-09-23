using mTiles.Models;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.ViewModels;

/// <summary>
/// The Goal tile's status word and its tone — the Agent tile's strip, in this tile's terms.
/// </summary>
/// <remarks>
/// <para><b>One vocabulary of colour for both conversations.</b> The strip used to carry a dot coloured
/// by <i>phase</i> and a phase label that was empty in every idle state, beside an Agent tile whose strip
/// says Ready in green and Working in the accent. Two conversation tiles side by side then answered "can
/// I type here" in two languages. The tones are <see cref="AgentStatusTone"/>'s, so the same state wears
/// the same colour in both; the words say what only this tile knows — which stage a run is at, and
/// whether a finished goal was met.</para>
/// <para><b>Paused outranks running</b>, because a pause is the one state nothing else on the tile
/// accounts for (<c>GoalWorkflowEngine.GetPhaseLabel</c> says the same, and its sentence is the
/// tooltip). <b>A goal that stopped unmet is Failed's colour</b> without being an error: it is the state
/// somebody has to act on — Continue, or rewrite the goal — which is what that colour marks on the Agent
/// tile too.</para>
/// </remarks>
public static class GoalStatus
{
    public static (string Text, AgentStatusTone Tone) Of(
        bool isRunning, bool isPaused, bool isWaitingForUser, GoalPhase phase, GoalStopReason? stop,
        string phaseLabel) =>
        (isPaused, isRunning, isWaitingForUser) switch
        {
            (true, _, _) => ("Paused", AgentStatusTone.Waiting),
            (_, true, _) => (phaseLabel.Length > 0 ? phaseLabel : "Working", AgentStatusTone.Working),
            (_, _, true) => ("Waiting for you", AgentStatusTone.Waiting),
            _ when phase == GoalPhase.Summary && stop is GoalStopReason.Met or GoalStopReason.Reviewed
                => ("Done", AgentStatusTone.Ready),
            _ when phase == GoalPhase.Summary && stop is not null => ("Not met", AgentStatusTone.Failed),
            _ => ("Ready", AgentStatusTone.Ready),
        };
}
