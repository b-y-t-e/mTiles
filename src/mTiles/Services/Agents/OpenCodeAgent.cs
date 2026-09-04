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

    /// <summary>Measured 2026-09-03: <c>.opencode/skills</c> under the project — configurable through
    /// its own <c>paths</c>, and this is the default.</summary>
    public override string? SkillsDirectory(string workspaceDir) =>
        Path.Combine(workspaceDir, ".opencode", "skills");
    public override string BinaryName => "opencode";

    /// <summary>
    /// <c>XDG_DATA_HOME</c>, measured 2026-08-30 against opencode 1.18.18 and narrowed 2026-08-31.
    /// </summary>
    /// <remarks><para>opencode has no variable of its own: it keeps its credentials in
    /// <c>~/.local/share/opencode/auth.json</c> and its configuration in <c>~/.config/opencode</c>, and
    /// the credentials move with <c>XDG_DATA_HOME</c> — <c>opencode auth list</c> against a
    /// relocated pair answers <c>0 credentials</c> while the default one lists the account, so the
    /// isolation is real.</para>
    /// <para><b>Only <c>XDG_DATA_HOME</c>, and that is the whole point.</b> It began as both XDG
    /// variables, which did isolate the login and also took the user's own
    /// <c>~/.config/opencode/opencode.json</c> away from the tile — no default model, no MCP servers,
    /// no agents, no instructions. Exactly the loss <c>OpenCodeProviderConfig</c> spends a paragraph
    /// defending against on the provider path, arriving silently by the other one. Measured 2026-08-31:
    /// <c>XDG_DATA_HOME</c> alone answers <c>0 credentials</c>, so the config variable was never needed
    /// — a sign-in is a login, and a login is data.</para>
    /// <para><b>They reach the tile's shell, not just opencode</b>, and that is worth stating plainly
    /// because it is the one place a sign-in leaks past the agent it belongs to: the environment goes
    /// into <c>PtyOptions.Environment</c>, so every program the user then runs in that tile inherits
    /// them, and on Unix these two are where <em>any</em> XDG-aware tool keeps its configuration and
    /// data. It is not fixable from here — the agent is started by that shell — and it is bounded: the
    /// tile is an agent tile the user opened for this instance, and the redirection is to a directory
    /// this application made. Anything narrower would mean not launching the agent through a shell at
    /// all.</para>
    /// </remarks>
    public override bool SupportsSignIns => true;

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string?> SignInEnv(string configDirectory) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["XDG_DATA_HOME"] = Path.Combine(configDirectory, "data"),
        };

    /// <summary>Whether <c>auth.json</c> is under this sign-in's data directory.</summary>
    /// <remarks>The path mirrors <see cref="SignInEnv"/> exactly, because opencode appends its own name
    /// to whatever <c>XDG_DATA_HOME</c> says; a status read from anywhere else would be answering about
    /// a directory the launch does not use.</remarks>
    public override SignInStatus ReadSignIn(string? configDirectory)
    {
        // A sign-in's directory holds the data root under `data`, because that is what SignInEnv puts
        // in XDG_DATA_HOME. The default account's root is XDG_DATA_HOME itself where the machine
        // exports one, and ~/.local/share otherwise — *not* that path with a directory taken off it,
        // which is what an earlier version of this line did and which resolved ~/.local/share to
        // ~/.local.
        var dataHome = configDirectory is { Length: > 0 } directory
            ? Path.Combine(directory, "data")
            : Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local", "share");

        var authFile = Path.Combine(dataHome, "opencode", "auth.json");

        return File.Exists(authFile) ? SignInStatus.SignedInAnonymously : SignInStatus.NotSignedIn;
    }
    public override string? InstallUrl => "https://opencode.ai";
    public override SessionStrategy SessionStrategy => SessionStrategy.ImportedFixed;

    public override InstallPlan? InstallPlan => new("npm",
        ["install", "-g", "opencode-ai"],
        "Installs OpenCode globally through npm, which has to be on PATH already.");

    public override IReadOnlyList<ApiFlavor> ConsumesApiFlavors => [ApiFlavor.OpenAiChatCompletions];

    /// <summary>
    /// The chosen service's own key, under the name opencode reads it from.
    /// </summary>
    /// <remarks><b>This used to set <c>OPENAI_BASE_URL</c> and <c>OPENAI_API_KEY</c>, and that could
    /// not work.</b> opencode does not take a base URL from the environment: it keeps a registry of
    /// providers, decides which one is available from which key variable is set, and validates the
    /// model against a catalogue before opening a socket. Measured 2026-08-31 — <c>opencode auth
    /// list</c> reported our <c>OPENAI_API_KEY</c> as the <em>OpenAI</em> provider, so an instance
    /// configured for OpenRouter authenticated against api.openai.com, and <c>--model</c> with a bare
    /// id was refused outright.
    /// <para>A local server has no key and no registry entry, so the key half contributes nothing and
    /// the address is declared in a generated file instead — see <c>OpenCodeProviderConfig</c>.</para>
    /// </remarks>
    protected override void Configure(IDictionary<string, string?> environment, AgentRuntime runtime)
    {
        ApplyProviderKey(environment, runtime);

        // A server opencode's registry cannot name is declared to it in a file, which is the only route
        // in - see OpenCodeProviderConfig. Only *named* here: the path is a pure function of the
        // instance's id, and writing it belongs to PrepareToLaunch, which is a moment rather than a
        // property read.
        // Only once it is really there. Set unconditionally, a write that failed left opencode pointed
        // at a missing file with `<provider>/<model>` on its command line - ProviderModelNotFoundError,
        // which is the error this document exists to prevent, and nothing said so. Written by
        // PrepareToLaunch a moment earlier; absent, the launch falls back to opencode's own
        // configuration, which is wrong but visible in its own output.
        //
        // The file being there is a good enough question because Write owns the other half: a write it
        // could not complete takes the previous launch's document with it, so what is on disk is either
        // this launch's or nothing. Asking the filesystem would otherwise have found a stale document
        // naming the address and model of the launch before.
        var path = OpenCodeProviderConfig.PathFor(runtime.Instance.Id);
        if (OpenCodeProviderConfig.IsNeededFor(runtime) && File.Exists(path))
            environment["OPENCODE_CONFIG"] = path;
    }

    /// <inheritdoc />
    /// <remarks>The provider document, rewritten on every launch so an address edited in Settings takes effect
    /// without anything having to notice it changed.</remarks>
    protected override void Prepare(AgentRuntime runtime) => OpenCodeProviderConfig.Write(runtime);

    /// <inheritdoc />
    /// <remarks><c>opencode run --help</c>: "model to use in the format of provider/model".</remarks>
    public override string QualifiedModel(AgentRuntime runtime) => WithProviderPrefix(runtime);

    /// <summary>
    /// opencode has its own slot for the small, frequent calls: <c>small_model</c> in its config.
    /// </summary>
    /// <remarks>Measured 2026-08-31 in the 1.18.18 binary (<c>Provider.getSmallModel</c>) and its
    /// documented schema: the slot is spelled <c>provider/model</c>, and empty it falls back to a
    /// cheap model <em>picked from the same provider's catalogue</em> — so on a hosted provider the
    /// field can stay empty without Claude Code's failure mode, whose fallback is a hardcoded
    /// Anthropic id. It reaches the CLI through the generated provider document
    /// (<see cref="OpenCodeProviderConfig"/>), which is written only where an endpoint is declared:
    /// on the user's own configuration this application does not write, so there a named fast model
    /// has nowhere to go and the CLI's own pick answers.</remarks>
    public override bool UsesFastModel => true;

    /// <inheritdoc />
    /// <remarks>The slot lives in the generated provider document, and that document exists only where
    /// an endpoint is declared — so on a hosted provider at its published address the field would save
    /// a value nothing reads, and the form hides it instead.</remarks>
    public override bool FastModelNeedsDeclaredEndpoint => true;

    /// <inheritdoc />
    /// <remarks>The prefix is the only place this CLI hears which service to use.</remarks>
    public override bool NamesProviderInModel => true;

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

    /// <summary>
    /// Measured 2026-09-01 against 1.18.18 and 1.18.25: with no message argument,
    /// <c>opencode run</c> reads the prompt from standard input and answers it — exit 0, answer on
    /// stdout.
    /// </summary>
    /// <remarks><para>Everything the command line did to the prompt here was real on this machine.
    /// npm resolves <c>opencode</c> to a <c>.cmd</c> shim, so the line this application built was
    /// re-parsed by cmd.exe before the CLI ever saw it, and past roughly eight thousand characters
    /// cmd.exe refuses the line outright — <c>The command line is too long.</c>, reproduced at 8.4k
    /// where the same prompt down stdin ran and answered. Between those two stood every
    /// quote-and-newline shape in a goal prompt, and one failure that reached the CLI printed its own
    /// usage page as the run's error text.</para>
    /// <para><see cref="AiProcessRunner.PromptBudget"/> answers null for this agent from here on, which
    /// also stops the prompt builder trimming the working tree to fit a limit that no longer
    /// exists.</para></remarks>
    public override bool AcceptsPromptOnStdin => true;

    /// <inheritdoc />
    /// <remarks>The prompt is unused here for the same reason <see cref="ClaudeAgent"/>'s is: it goes
    /// down standard input, which <c>AiProcessRunner</c> writes and closes.</remarks>
    public override void ConfigureProcess(ProcessStartInfo psi, string prompt, bool streaming,
        AiUsage usage, AiBehaviour behaviour = AiBehaviour.Auto,
        AiEffort effort = AiEffort.High, string model = "")
    {
        psi.ArgumentList.Add("run");

        foreach (var argument in BehaviourArgs(behaviour, usage).Concat(ModelArgs(model, usage)))
            psi.ArgumentList.Add(argument);
    }
}
