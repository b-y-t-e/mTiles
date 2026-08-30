using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// OpenCode.
/// </summary>
/// <remarks>
/// <para><b>The awkward session.</b> <c>opencode --session &lt;id&gt;</c> only ever <em>continues</em>
/// one — an id we invent is refused (<c>Session not found</c>, exit 1 after ~1.4 s) — and the TUI
/// creates no session at all until the first message, so there is nothing to observe at startup and
/// pick up either. The way in is <c>opencode import</c>, which takes a JSON document and keeps its
/// <c>id</c> verbatim: <see cref="SessionStrategy.ImportedFixed"/>. <see cref="OpenCodeSession"/> owns
/// the document and everything measured about it.</para>
/// <para><b>Its permission control is a boolean</b>, and its name is a trap: <c>--auto</c> is
/// documented as "auto-approve permissions that are not explicitly denied (dangerous!)", which is this
/// application's <see cref="AiBehaviour.BypassPermissions"/> and not its <see cref="AiBehaviour.Auto"/>.
/// Mapped by meaning, never by spelling.</para>
/// <para><b>Effort is the provider's, not opencode's.</b> <c>--variant</c> takes a provider-specific
/// string from an open list, and it exists on <c>opencode run</c> only — not on the TUI. That is the
/// measured fact behind <see cref="AiUsage"/> being a parameter rather than a property: the honest
/// answer to "what efforts does opencode support" is different in the two places.</para>
/// </remarks>
public sealed class OpenCodeAgent : AiAgent
{
    public override string Id => "opencode";
    public override string DisplayName => "OpenCode";
    public override string BinaryName => "opencode";
    public override string? InstallUrl => "https://opencode.ai";
    public override SessionStrategy SessionStrategy => SessionStrategy.ImportedFixed;

    public override InstallPlan? InstallPlan => new("npm",
        ["install", "-g", "opencode-ai"],
        "Installs OpenCode globally through npm, which has to be on PATH already.");

    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [ApiFlavor.OpenAiChatCompletions];

    /// <summary>
    /// The OpenAI-compatible pair, when the instance names a provider that serves that shape.
    /// </summary>
    /// <remarks>Nothing at all when it does not: an agent with no provider runs on its own
    /// configuration, which is what an unconfigured instance means and what a first run is in.</remarks>
    protected override void Configure(IDictionary<string, string?> environment, AgentRuntime runtime)
    {
        if (runtime.EndpointFor(ApiFlavor.OpenAiChatCompletions) is not { } endpoint)
            return;

        environment["OPENAI_BASE_URL"] = endpoint.ToString();
        environment["OPENAI_API_KEY"] = runtime.ApiKey;
    }

    /// <summary>Bypass or nothing — the two things a boolean can say. Asking for anything in between
    /// rounds down to <see cref="AiBehaviour.ToolDefault"/>, which is opencode's own asking
    /// behaviour.
    /// <para>A headless phase that writes nothing is offered <see cref="AiBehaviour.ToolDefault"/>
    /// alone: opencode has no read-only mode to be put into, so the most decision 9 can be here is to
    /// withhold the one flag that would let a reviewing agent write.</para></summary>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        !usage.MayOnlyRead
            ? [AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault]
            : [AiBehaviour.ToolDefault];

    /// <summary>
    /// Nothing this application can offer as a level.
    /// </summary>
    /// <remarks><c>--variant</c> is the only thing here that resembles effort, and it is a
    /// provider-specific string from an open list rather than a scale — so mapping the canonical five
    /// onto it would mean inventing five names that no provider is obliged to have. It is left to the
    /// instance's model configuration, and every level rounds to
    /// <see cref="AiEffort.ToolDefault"/>.</remarks>
    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        [AiEffort.ToolDefault];

    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage) => [];

    /// <summary>Measured (2026-08-30, opencode 1.18.18): <c>--model provider/model</c>, on the TUI and
    /// on <c>run</c> alike.</summary>
    /// <remarks>Without it an instance pointed at a provider through <c>OPENAI_BASE_URL</c> ran on
    /// opencode's own default model against an address that usually does not serve it — a launch that
    /// succeeds and a run that fails, which is the worse of the two orders.</remarks>
    public override IReadOnlyList<string> ModelArgs(string model, AiUsage usage) =>
        model.Length > 0 ? ["--model", model] : [];

    /// <summary>
    /// The one flag, and only for the one mode that means it. See the type's remarks on why
    /// <c>--auto</c> is not <see cref="AiBehaviour.Auto"/>.
    /// </summary>
    /// <remarks>A headless phase that writes nothing never gets it, whatever the strip says. opencode
    /// has no read-only mode, so this is the whole of what decision 9 can be here — and it is the part
    /// that matters, because <c>--auto</c> is exactly what would let a reviewing agent edit the
    /// worktree <c>GoalBaseline</c> photographed only once.</remarks>
    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage) =>
        behaviour == AiBehaviour.BypassPermissions && !usage.MayOnlyRead ? ["--auto"] : [];

    /// <summary>
    /// Resume the session, and create it first if resuming found nothing.
    /// </summary>
    /// <remarks>The import runs as one of the tile's own commands rather than being done for it,
    /// because the document's <c>directory</c> is ignored: the session lands in the project of the
    /// <em>import's</em> working directory, which is the tile's. Re-importing an id that exists is
    /// non-destructive, so this is create-if-missing and not a way to wipe the conversation being
    /// resumed.</remarks>
    protected override LaunchScripts Resume(string sessionId)
    {
        var resume = $"opencode --session {sessionId}";
        // The token rather than the path it expands to: writing the document is the launcher's job, and
        // what tells it there is one to write is exactly this token in the script (see
        // OpenCodeSession.PrepareIfReferenced). A path spelled out here reads the same to a shell and is
        // invisible to the launcher, so the import would point at a file nobody ever wrote.
        return LaunchScripts.FromProfile(resume,
            $"opencode import \"{TileScript.OpenCodeSessionFileToken}\" ; {resume}");
    }

    /// <inheritdoc />
    /// <remarks>The <c>ses_</c> prefix is the only thing opencode enforces on an imported id, and the
    /// rest of it is the tile's own — so no tile-to-session bookkeeping exists anywhere.</remarks>
    public override string SessionIdForTile(string tileId) => OpenCodeSession.IdFor(tileId);

    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        psi.ArgumentList.Add("run");

        foreach (var argument in BehaviourArgs(behaviour, usage).Concat(ModelArgs(model, usage)))
            psi.ArgumentList.Add(argument);

        psi.ArgumentList.Add(prompt);
    }
}
