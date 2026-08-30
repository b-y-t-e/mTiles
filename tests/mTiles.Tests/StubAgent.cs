using System.Diagnostics;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;

namespace mTiles.Tests;

/// <summary>
/// An agent that answers every question with nothing, so a test can override the one thing it is about.
/// </summary>
/// <remarks>
/// <see cref="AiAgent"/> makes a real agent say what it is called, how it resumes a conversation, what
/// API it speaks and what flags it takes — which is the point of the type and a page of noise in a test
/// that only wants to see what reached <c>ConfigureProcess</c>. The answers here are the ones a stub can
/// give honestly: no session, no provider, no flags.
/// </remarks>
internal abstract class StubAgent : AiAgent
{
    public override string Id => "stub";
    public override string DisplayName => "Stub";
    public override string BinaryName => "stub";
    public override string? InstallUrl => null;
    public override SessionStrategy SessionStrategy => SessionStrategy.Fixed;
    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [];

    /// <summary>Everything, so that a test about what reaches <c>ConfigureProcess</c> sees what it
    /// passed rather than what the rounding made of it.</summary>
    /// <remarks>A stub that supported nothing would have <c>AiProcessRunner.Fit</c> flatten every value
    /// to "tool default" before the call being observed — which is the rounding working, and the wrong
    /// thing for these tests to be about. A test that <em>is</em> about the rounding says so with a real
    /// agent, in <c>AiAgentTests</c>.</remarks>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        AiBehaviours.All;

    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        AiEfforts.All;

    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage) => [];

    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage) => [];

    protected override LaunchScripts Resume(string sessionId) => LaunchScripts.None;

    public override IReadOnlyList<AiOutputChunk> ParseLine(string line) => [];

    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
    }
}
