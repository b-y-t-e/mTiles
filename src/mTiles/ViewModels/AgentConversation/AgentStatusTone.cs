namespace mTiles.ViewModels.AgentConversation;

/// <summary>
/// How the strip's status word is coloured: one tone per state worth telling apart at a glance.
/// </summary>
/// <remarks>The same four the workspace row's marks separate — working, waiting for you, failed — plus ready,
/// which is the one state a user returns to the tile for. Starting and not running stay quiet.</remarks>
public enum AgentStatusTone
{
    Quiet,
    Ready,
    Working,
    Waiting,
    Failed,
}
