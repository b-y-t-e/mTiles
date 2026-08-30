using System.Diagnostics;
using mTiles.Models;
using mTiles.Services.Providers;

namespace mTiles.Services.Agents;

/// <summary>
/// The pi coding agent.
/// </summary>
/// <remarks>
/// <para><b>pi has no permission gate at all</b>, and that is measured rather than assumed:
/// <c>--approve</c> is about trusting project-local files, not about tool calls. A pi run is always
/// unrestricted, so this agent declares <see cref="AiBehaviour.BypassPermissions"/> and nothing else —
/// offering "auto" here would be a lie about what is going to happen to somebody's repository, and it
/// is a lie the rounding rules would have no way to catch.</para>
/// <para><b>Sessions are the easy case</b>: <c>--session-id &lt;id&gt;</c> creates the session if it
/// is missing, so the tile's own id is the whole of the bookkeeping.</para>
/// </remarks>
public sealed class PiAgent : AiAgent
{
    public override string Id => "pi";
    public override string DisplayName => "Pi Agent";
    public override string BinaryName => "pi";
    public override string? InstallUrl => "https://github.com/mariozechner/pi-coding-agent";
    public override SessionStrategy SessionStrategy => SessionStrategy.Fixed;

    public override InstallPlan? InstallPlan => new("npm",
        ["install", "-g", "@mariozechner/pi"],
        "Installs pi globally through npm, which has to be on PATH already.");

    /// <summary>Bring-your-own-key against an OpenAI-shaped chat endpoint.</summary>
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

    /// <summary>
    /// Bypass, and the option of passing nothing — which come to the same thing here, and are both
    /// listed so that the difference between "we chose this" and "we said nothing" stays visible.
    /// </summary>
    /// <remarks>Anything else asked for rounds <em>down</em> to <see cref="AiBehaviour.ToolDefault"/>
    /// rather than up to bypass, which is the correct direction and also has no effect: pi will do as
    /// it likes either way. What it buys is that a Goal tile showing "auto" on a pi run is impossible,
    /// so nobody reads a restriction into a run that has none.</remarks>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        [AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault];

    /// <summary>Measured against <c>pi --help</c>: <c>--thinking off|minimal|low|medium|high|xhigh|max</c>.
    /// Every level the canonical scale names exists there under the same word.
    /// <para><c>off</c> and <c>minimal</c> are pi's alone and are deliberately not offered: the scale
    /// has no level below <c>low</c>, and adding two for one agent would make every other agent's table
    /// answer a question it cannot.</para></summary>
    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        FullEffortScale;

    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage) =>
        AiEfforts.Name(effort) is { } level ? ["--thinking", level] : [];

    /// <summary>Nothing, whatever is asked. There is no flag to pass — see the type's remarks.</summary>
    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage) => [];

    /// <summary>Measured (2026-08-30, pi 0.84.3): <c>--model &lt;pattern&gt;</c>, which also accepts a
    /// <c>provider/id</c> form — so the instance's model name is passed through as it was written
    /// rather than being split into a provider and an id here.</summary>
    public override IReadOnlyList<string> ModelArgs(string model, AiUsage usage) =>
        model.Length > 0 ? ["--model", model] : [];

    protected override LaunchScripts Resume(string sessionId) =>
        LaunchScripts.FromProfile($"pi --session-id {sessionId}", "pi");

    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--mode");
        psi.ArgumentList.Add("text");

        foreach (var argument in EffortArgs(effort, usage).Concat(ModelArgs(model, usage)))
            psi.ArgumentList.Add(argument);
    }
}
