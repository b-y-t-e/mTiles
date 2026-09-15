using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// xAI's Grok Build CLI.
/// </summary>
/// <remarks>
/// <para><b>Added without a measurement, and it says so.</b> The CLI was not installed on the machine
/// this class was written on, so every table here comes from t3code, which runs it through ACP
/// (<c>GrokAcpSupport.ts</c>): the binary is <c>grok</c>, the login is <c>grok login</c> or
/// <c>XAI_API_KEY</c>, and the permission modes are <c>--permission-mode default|acceptEdits|auto</c> plus
/// <c>--always-approve</c>. Each of the other agents in this directory was measured against its binary
/// before it was trusted; this one is waiting for that run, and the conversation view
/// (<see cref="Sessions.Grok.GrokAcpSession"/>) is the part it was added for.</para>
/// <para><b>What is deliberately not claimed:</b> a way to resume a terminal session, a headless print
/// mode, a sign-in directory or a usage report. Each would be a flag or a path guessed rather than read,
/// and a wrong guess is a tile that fails in a way nobody can explain. So a terminal Grok tile starts a
/// fresh <c>grok</c> each time, a Goal run passes the prompt as a plain argument the way
/// <see cref="GenericAgent"/> does, and the conversation — which does resume, through ACP's
/// <c>session/load</c> — is the way to keep one.</para>
/// </remarks>
public sealed class GrokAgent : AiAgent, Sessions.IConversationalAgent
{
    public override string Id => "grok";
    public override string DisplayName => "Grok";
    public override string BinaryName => "grok";
    public override string? InstallUrl => "https://x.ai/cli";

    /// <summary>A conversation over ACP — see <see cref="Sessions.Grok.GrokAcpSession"/>.</summary>
    public AgentSessions.IAgentSession CreateSession(Sessions.AgentSessionLaunch launch,
        AgentSessions.IAgentEventSink sink) =>
        new Sessions.Grok.GrokAcpSession(launch, sink);

    /// <summary>Nothing survives a terminal restart — see the remarks.</summary>
    public override SessionStrategy SessionStrategy => SessionStrategy.CapturedAfterStart;

    /// <summary>Its own login or <c>XAI_API_KEY</c>; no provider in the catalogue speaks to it.</summary>
    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [];

    /// <summary>The four modes t3code maps, and the CLI's own default.</summary>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        usage.IsHeadless
            ? [AiBehaviour.ToolDefault]
            : [AiBehaviour.Ask, AiBehaviour.AcceptEdits, AiBehaviour.Auto, AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault];

    /// <summary>Effort reaches Grok only inside a conversation, as <c>_meta.reasoningEffort</c> on
    /// <c>session/set_model</c>; there is no flag for it.</summary>
    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        usage.IsHeadless ? [AiEffort.ToolDefault] : FullEffortScale;

    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage) => [];

    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage) =>
        usage.IsHeadless ? [] : PermissionArgs(behaviour);

    /// <summary>
    /// <c>grok --permission-mode X agent stdio</c>, or <c>grok agent --always-approve stdio</c> for
    /// bypass — the order t3code passes them in, the mode flag before the subcommand and the approval
    /// flag inside it.
    /// </summary>
    internal static IReadOnlyList<string> AcpArguments(AiBehaviour behaviour) => behaviour switch
    {
        AiBehaviour.BypassPermissions => ["agent", "--always-approve", "stdio"],
        _ => [.. PermissionArgs(behaviour), "agent", "stdio"],
    };

    private static IReadOnlyList<string> PermissionArgs(AiBehaviour behaviour) => behaviour switch
    {
        AiBehaviour.Ask or AiBehaviour.Plan => ["--permission-mode", "default"],
        AiBehaviour.AcceptEdits => ["--permission-mode", "acceptEdits"],
        AiBehaviour.Auto => ["--permission-mode", "auto"],
        _ => [],
    };

    protected override LaunchScripts Resume(string sessionId) => LaunchScripts.FromProfile("grok", null);

    /// <summary>The prompt as a plain argument, as <see cref="GenericAgent"/> does: no print flag has
    /// been read off this CLI.</summary>
    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "") =>
        psi.ArgumentList.Add(prompt);
}
