using Xunit;

namespace mTiles.Tests;

/// <summary>
/// A theory that runs a real AI CLI, and so is reported as <em>skipped</em> — not passed — unless
/// <c>MTILES_LIVE_AGENTS</c> names at least one agent.
/// </summary>
/// <remarks>These returned early and were counted as passes, so every run claimed live coverage it did
/// not have. xUnit 2 has no skip from inside a test, which is why the decision is made here, per theory;
/// the test itself still returns early for the agents the variable does not name.</remarks>
public sealed class LiveAgentTheoryAttribute : TheoryAttribute
{
    public LiveAgentTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MTILES_LIVE_AGENTS")))
            Skip = "Live agents: set MTILES_LIVE_AGENTS to a comma-separated list of agent ids.";
    }
}
