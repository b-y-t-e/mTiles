using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// What a binary this application knows nothing about gets: the prompt as a plain first argument, no
/// flags, and no claim about standard input.
/// </summary>
/// <remarks>
/// <para>The fallback used to be Claude Code's runner, which was survivable while that ran everything
/// on the command line and became a hang when it moved to standard input — an unknown tool was launched
/// with Claude's flags, no prompt anywhere on its command line, and a pipe it had never agreed to read.
/// Passing the prompt as an argument is the one thing every CLI here does.</para>
/// <para>It has no session, no provider and no install plan, and says so rather than guessing: an agent
/// nothing is known about is one nothing should be claimed about.</para>
/// </remarks>
public sealed class GenericAgent(string binaryName) : AiAgent
{
    public override string Id => binaryName;
    public override string DisplayName => binaryName;
    public override string BinaryName => binaryName;
    public override string? InstallUrl => null;

    /// <summary>Nothing survives a restart. <see cref="SessionStrategy.Fixed"/> would be a claim that a
    /// session id reaches this tool somehow, and there is no flag here to carry one.</summary>
    public override SessionStrategy SessionStrategy => SessionStrategy.CapturedAfterStart;

    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [];

    /// <summary>Only "pass no flag", because no flag is known to pass. Every mode therefore rounds to
    /// <see cref="AiBehaviour.ToolDefault"/>, which is the truth about what this run will be.</summary>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        [AiBehaviour.ToolDefault];

    /// <inheritdoc cref="SupportedBehaviours"/>
    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        [AiEffort.ToolDefault];

    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage) => [];

    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage) => [];

    /// <summary>The binary on its own. There is no resume to offer and nothing to fall back to.</summary>
    protected override LaunchScripts Resume(string sessionId) =>
        LaunchScripts.FromProfile(binaryName, null);

    /// <summary>No model flag, so <see cref="AiAgent.AcceptsModel"/> answers false: a flag for a
    /// binary nothing is known about would be a guess, and a wrong guess fails every run.</summary>
    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "") =>
        psi.ArgumentList.Add(prompt);
}
