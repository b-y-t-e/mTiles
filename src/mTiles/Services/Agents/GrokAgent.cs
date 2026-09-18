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
        new Sessions.Grok.GrokAcpSession(launch, this, sink);

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>None, although the store is readable</b> (<see cref="SessionLogs.GrokSessionLog"/>,
    /// measured 2026-09-18 against 1.0.34). A store is read by the id of the conversation a tile is in,
    /// and a terminal Grok tile never has one: it resumes nothing
    /// (<see cref="ResumesTerminalSession"/>) and captures nothing, because its store marks nothing that
    /// tells this TUI from an Agent tile's ACP session or a Goal run in the same working directory — so
    /// "the newest conversation written since this launch" could be either, and the tile would draw
    /// somebody else's tokens and cost as its own.</para>
    /// <para>Wired in, the log would only keep a file watcher on <c>~/.grok/sessions</c> running for a
    /// reading that can never arrive. A terminal Grok tile therefore draws no bar, which says less and
    /// nothing wrong — the same answer agy gives, for a different reason.</para>
    /// </remarks>
    public override SessionLogs.IAgentSessionLog? SessionLog => null;

    /// <summary>Nothing survives a terminal restart — see the remarks.</summary>
    public override SessionStrategy SessionStrategy => SessionStrategy.CapturedAfterStart;

    /// <inheritdoc />
    public override bool ResumesTerminalSession => false;

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

    protected override LaunchScripts Resume(string program, string sessionId) =>
        LaunchScripts.FromProfile(program, null);

    /// <summary>The prompt as a plain argument, as <see cref="GenericAgent"/> does: no print flag has
    /// been read off this CLI.</summary>
    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "") =>
        psi.ArgumentList.Add(prompt);
}
