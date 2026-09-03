using System.Diagnostics;
using mTiles.Models;

namespace mTiles.Services.Agents;

/// <summary>
/// Google's Antigravity CLI.
/// </summary>
/// <remarks>
/// <para>Measured, and it needed a class of its own rather than the generic fallback: a bare positional
/// argument does not start a print run, it opens the interactive session and <b>hangs</b> — which, on a
/// path with no wall-clock timeout, is a Goal tile that never comes back.
/// <c>agy --print &lt;prompt&gt;</c> answers on stdout and exits 0.</para>
/// <para><b>An unknown conversation id is not an error here</b>, which is the trap this agent exists to
/// keep out of the launch chain: <c>agy --conversation &lt;unknown&gt;</c> warns, silently starts a new
/// conversation and exits 0. So a chain judging on the exit code cannot tell a resumed tile from a lost
/// one, and there is no local session store to consult — conversations live server-side. The way in is
/// to <em>pre-create</em>: <see cref="SessionCapture"/> runs one cheap <c>--print</c> with
/// <c>--output-format json</c>, takes <c>conversation_id</c>, and the tile launches with an id that is
/// known to exist.</para>
/// <para><b>It does have an effort flag</b> — <c>--effort low|medium|high</c> — which this application
/// previously said it did not, so every agy run went out at whatever the model name implied. Three
/// levels, so the canonical top two round down.</para>
/// <para><b>Measured limitation:</b> <c>agy --print-timeout</c> defaults to five minutes, and nothing
/// here raises it. The Goal tile's "a run has no wall-clock timeout" guarantee therefore does not hold
/// on agy: a long implementation is cut off by the agent itself. Passing a larger value would be a flag
/// an older agy could refuse, which fails <em>every</em> run rather than the long ones — so this is
/// written down rather than worked around.</para>
/// </remarks>
public sealed class AntigravityAgent : AiAgent
{
    /// <summary>The flag whose value is the prompt, named once because two places depend on the pair
    /// staying together.</summary>
    private const string PrintFlag = "--print";

    public override string Id => "agy";
    public override string DisplayName => "Antigravity";
    public override string BinaryName => "agy";

    /// <summary>
    /// No, and it is the one agent here where that is an answer rather than a gap.
    /// </summary>
    /// <remarks><para>Measured 2026-08-30 against agy 1.1.22: it keeps its state in <c>~/.gemini</c>,
    /// and the binary carries no <c>*_HOME</c>, <c>*_DIR</c> or <c>*_CONFIG</c> variable that moves it —
    /// so there is nothing this application could set. Giving it a sign-in row anyway would be
    /// configuration that does nothing: a second account named, logged into, and then running as the
    /// first, which is the failure the whole feature exists to prevent.</para>
    /// <para><b>It already has its own answer.</b> <c>~/.gemini/google_accounts.json</c> is
    /// <c>{"active": …, "old": […]}</c> — a switch between Google accounts, kept by the tool and
    /// changed inside it. A second mechanism over the top of that would be two places claiming to
    /// decide the same thing, and only one of them would be listened to.</para></remarks>
    public override bool SupportsSignIns => false;
    public override string? InstallUrl => "https://antigravity.google/product/antigravity-cli";
    public override SessionStrategy SessionStrategy => SessionStrategy.CapturedAfterStart;

    /// <summary>Nothing to offer. Antigravity is installed by Google's own installer rather than from a
    /// package registry, so a command here would be a guess about somebody's machine.</summary>
    public override InstallPlan? InstallPlan => null;

    /// <summary>Google's models through Google's own service. Not one of the three open flavors: agy
    /// takes no base URL, so pairing it with a provider instance would be offered and would not
    /// work.</summary>
    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [];

    /// <summary>
    /// Plan, its one working mode, and its bypass.
    /// </summary>
    /// <remarks><see cref="AiBehaviour.Auto"/> and <see cref="AiBehaviour.AcceptEdits"/> both land on
    /// <c>--mode accept-edits</c>, because that is the only thing agy has between plan and bypass. That
    /// is a round-<em>down</em> hidden inside the mapping — asking for auto gets something weaker — and
    /// it is the safe direction; the alternative would be reading agy's one mode as a licence for the
    /// stronger of the two.</remarks>
    public override IReadOnlyList<AiBehaviour> SupportedBehaviours(AiAgentInstance instance, AiUsage usage) =>
        !usage.IsHeadless
            ? [AiBehaviour.Plan, AiBehaviour.AcceptEdits, AiBehaviour.Auto,
               AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault]
            : !usage.MayOnlyRead
                ? [AiBehaviour.Auto, AiBehaviour.BypassPermissions, AiBehaviour.ToolDefault]
                : [AiBehaviour.Plan, AiBehaviour.ToolDefault];

    public override IReadOnlyList<AiEffort> SupportedEfforts(AiAgentInstance instance, AiUsage usage) =>
        Efforts;

    /// <summary>Measured: <c>--effort low|medium|high</c>. The canonical <c>xhigh</c> and <c>max</c>
    /// round down to <c>high</c>.</summary>
    private static readonly IReadOnlyList<AiEffort> Efforts =
        [AiEffort.Low, AiEffort.Medium, AiEffort.High, AiEffort.ToolDefault];

    public override IReadOnlyList<string> EffortArgs(AiEffort effort, AiUsage usage)
    {
        var level = AiEfforts.RoundToNearest(effort, Efforts);
        return AiEfforts.Name(level) is { } name ? ["--effort", name] : [];
    }

    /// <summary>Measured (2026-08-30, agy 1.1.22): <c>--model</c> — "Model for the current CLI
    /// session".</summary>
    /// <remarks>agy takes no base URL, so <see cref="ConsumesApiFlavors"/> is empty and no provider can
    /// be paired with it — but the model is still the instance's to choose, and it is Google's own
    /// names that go here.</remarks>
    public override IReadOnlyList<string> ModelArgs(string model, AiUsage usage) =>
        model.Length > 0 ? ["--model", model] : [];

    public override IReadOnlyList<string> BehaviourArgs(AiBehaviour behaviour, AiUsage usage)
    {
        if (usage.MayOnlyRead)
            return ["--mode", "plan"];

        return behaviour switch
        {
            AiBehaviour.Plan => ["--mode", "plan"],
            AiBehaviour.AcceptEdits or AiBehaviour.Auto => ["--mode", "accept-edits"],
            AiBehaviour.BypassPermissions => ["--dangerously-skip-permissions"],
            _ => [],
        };
    }

    /// <summary>
    /// Resumes a conversation this application has watched agy create, and starts a fresh one when
    /// there is none.
    /// </summary>
    /// <remarks>The empty case is what keeps the pre-create honest: handing <c>--conversation</c> an id
    /// we invented would not fail, it would silently open a different conversation and report success.
    /// </remarks>
    protected override LaunchScripts Resume(string sessionId) =>
        LaunchScripts.FromProfile(
            sessionId is { Length: > 0 } ? $"agy --conversation {sessionId}" : "agy", "agy");

    /// <summary>
    /// Opens a conversation by asking agy something trivial, and keeps the id it answers with.
    /// </summary>
    /// <remarks>
    /// <para><b>A real API call, deliberately</b> (decision 5). The alternative — scraping the id out
    /// of the TUI's output — reads ANSI, an alternate screen buffer and a layout that changes with the
    /// next release, and is the fallback of last resort rather than the design.</para>
    /// <para>The prompt is as small as it can be while still being a prompt: this costs one model call
    /// per agy tile ever created, and asking for nothing at all is not something the CLI offers.</para>
    /// </remarks>
    public override async Task<string?> CaptureSessionAsync(AiAgentInstance instance,
        SessionCaptureRequest request, CancellationToken ct)
    {
        // The tile's own environment, because this call *makes* the conversation the tile resumes: an
        // instance that only works through its ExtraEnv has to work here too, or the pre-create runs
        // under a different account and the id it answers with is one the tile cannot use.
        var output = await SessionCapture.RunForOutputAsync(request.ExecutablePath,
            request.WorkingDirectory, [PrintFlag, "Reply with OK.", "--output-format", "json"], ct,
            request.Environment);

        return SessionCapture.ConversationIdIn(output);
    }

    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        foreach (var argument in BehaviourArgs(behaviour, usage)
                     .Concat(EffortArgs(effort, usage))
                     .Concat(ModelArgs(model, usage)))
            psi.ArgumentList.Add(argument);

        psi.ArgumentList.Add(PrintFlag);
        psi.ArgumentList.Add(prompt);
    }

    /// <summary>
    /// The four windows this subscription reports, or null where nobody is logged in here.
    /// </summary>
    /// <remarks>
    /// <para><b>One account and no sign-in rows</b>, which is the whole of what
    /// <see cref="SupportsSignIns"/> being false means for this: agy switches Google accounts itself,
    /// so there is one login on this machine and one card. A sign-in reaching here is a row that cannot
    /// exist, and it answers null rather than reporting the default account's figures under somebody
    /// else's name.</para>
    /// <para><b>Null where the login cannot be read, a sentence where it can and the service would not
    /// answer.</b> On Linux — and on a Windows machine nobody has run agy on — there is no credential
    /// this application can open, and that is an account this machine does not have as far as the tile
    /// is concerned. See <see cref="AntigravityCredentialStore"/> for why that is a limit rather than a
    /// gap.</para>
    /// <para>The token is read here and handed straight to <see cref="AntigravityUsageReader"/>, held
    /// for the length of one call: what it authenticates is somebody else's service, and nothing in
    /// this application stores, logs or shows it.</para>
    /// </remarks>
    public override async Task<AiUsageReport?> UsageAsync(AiSignIn? signIn,
        CancellationToken ct = default)
    {
        if (signIn is not null) return null;

        if (await AntigravityCredentialStore.AccessTokenAsync(ct) is not { Length: > 0 } token)
            return null;

        return await AntigravityUsageReader.ReadAsync(UsageSourceId(null), UsageSourceName(null),
            token, DateTimeOffset.Now, UsageAccountKeyFor(null), ct);
    }

    /// <inheritdoc />
    /// <remarks><b>The account agy says is active, out of the file agy keeps it in.</b>
    /// <c>~/.gemini/google_accounts.json</c> is <c>{"active": …, "old": […]}</c> — the switch this agent
    /// deliberately has no sign-in rows over the top of — so the address in it is what one card here is
    /// a card <em>of</em>. It is an identifier and not a credential, compared in memory and never
    /// stored, shown or logged, and prefixed so it cannot collide with another agent's answer. There is
    /// only one agy row, so nothing is deduplicated against it today; it is here so that a card of this
    /// account is recognisable as one if a second route to the same login is ever added.</remarks>
    public override string? UsageAccountKeyFor(AiSignIn? signIn)
    {
        if (signIn is not null) return null;

        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini", "google_accounts.json");

        return ReadJsonString(file, "active") is { Length: > 0 } account
            ? $"agy-account:{account}"
            : null;
    }

    /// <summary>In front of <c>--print</c>, because the prompt is that flag's own value.</summary>
    /// <remarks>The base rule puts the extras in front of a trailing prompt, which here would separate
    /// the flag from its value: agy would read the first extra argument as what to print and leave the
    /// prompt as a stray positional.</remarks>
    public override int ExtraArgsIndex(IReadOnlyList<string> arguments, string prompt)
    {
        var flag = arguments.ToList().LastIndexOf(PrintFlag);
        return flag >= 0 ? flag : base.ExtraArgsIndex(arguments, prompt);
    }
}
