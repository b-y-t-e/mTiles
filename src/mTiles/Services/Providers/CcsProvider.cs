using System.Diagnostics;
using System.Text.Json;
using mTiles.Models;

namespace mTiles.Services.Providers;

/// <summary>
/// CCS (Claude Codex Switch) — a local OAuth proxy that lets Claude Code run on a Codex subscription.
/// </summary>
/// <remarks>
/// <para>CCS wraps <b>CLIProxyAPI</b>, a daemon on <c>127.0.0.1:8317</c> that serves an
/// <b>Anthropic-shaped</b> endpoint (<c>/v1/messages</c>) backed by an OAuth subscription — ChatGPT/Codex
/// today. The token is the proxy's own business: it is obtained once by <c>ccs codex --auth</c> and
/// refreshed by the proxy itself, so no key of any kind is configured here.</para>
/// <para><b>Why a provider and not a new agent.</b> <c>ccs codex</c> as a command is only a wrapper that
/// sets <c>ANTHROPIC_BASE_URL</c> and launches Claude Code — and a provider that could inject CLI
/// fragments would break the agents' pinned argv tables and session strategies for everybody. So the CLI
/// stays <c>claude</c>, and this class contributes only what a provider contributes anywhere: the
/// address, the flavor, and the model list. <c>ClaudeAgent.Configure</c> does the rest with the pair it
/// already sets — the placeholder token included, since an <em>empty</em> token makes Claude Code refuse
/// to run at all while any non-empty one passes a keyless server.</para>
/// <para><b>Not an <see cref="ILocalAiProvider"/>, deliberately.</b> Its address is fixed and published,
/// so there is nothing to discover; and "whatever the server has loaded" is a question about a model
/// server, which a subscription proxy cannot answer. <see cref="Models"/> therefore names no sentinel
/// and the Discover button never shows.</para>
/// <para>Only the Codex subscription is wired today. More of CLIProxy's providers (Gemini, Kimi, …)
/// would arrive as a choice on the instance, not as more provider classes — the proxy is the same
/// server either way.</para>
/// </remarks>
public sealed class CcsProvider : AiProvider, IManagedAiProvider
{
    public override string Id => "ccs";
    public override string DisplayName => "CCS";
    public override IReadOnlyList<ApiFlavor> ApiFlavors => [ApiFlavor.Anthropic];

    /// <summary>The port CLIProxyAPI documents. It is also what the proxy's own status names.</summary>
    public override int DefaultPort => 8317;
    public override Uri? DefaultBaseUrl => new($"http://127.0.0.1:{DefaultPort}/");

    /// <summary>No key on a local proxy — its address is the whole of how it is reached.</summary>
    public override bool NeedsApiKey => false;
    public override bool IsLocal => true;

    /// <summary>
    /// The models the proxy serves, and the reason this is asked of it at all.
    /// </summary>
    /// <remarks>The OpenAI-shaped listing CLIProxyAPI keeps in sync with the accounts behind it. A model
    /// here is a GPT-5.x id — one Claude Code does not know — so the window it names, if any, is what
    /// <see cref="ModelContextWindow"/> hands the CLI in place of its own 200 000 assumption.</remarks>
    public override async Task<IReadOnlyList<AiModelInfo>> ModelsAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        using var document = await GetJsonAsync(instance, "v1/models", ct);
        return document is null ? [] : ReadOpenAiModels(document);
    }

    /// <summary>
    /// Asks whether the proxy is up — starting it when it can be — and then counts the models.
    /// </summary>
    /// <remarks>The Test button is deliberately the same work a launch does, because it is the one press
    /// a user makes while setting this up: answered with a running proxy and a model count, it has
    /// already done everything the first tile launch would have needed.</remarks>
    public override async Task<ProviderCheck> TestAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        if (AddressProblem(instance) is { } problem) return problem;

        var ensured = await EnsureRunningAsync(instance, ct);
        if (!ensured.Ok) return ensured;

        var models = await ModelsAsync(instance, ct);
        return models.Count > 0
            ? ProviderCheck.Reached($"{models.Count} models")
            : ProviderCheck.Failed($"The proxy answered at {Address(instance)}, but served no models — "
                + "is the account behind it signed in? Run \"ccs codex --auth\".");
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><b>Probe, start, probe.</b> The probe first, because the ordinary case is a proxy already
    /// running its own life as a daemon: one local HTTP call, and the launch pays nothing else. Only a
    /// silent address starts anything, and what it starts is <c>ccs cliproxy start</c> — the tool's own
    /// lifecycle command, idempotent by its own contract.</para>
    /// <para>Installing CCS is <em>not</em> part of this: an installation is a decision and a visible
    /// tile, which is what the Settings row's Install button is for. A launch answers with the sentence
    /// pointing there instead.</para>
    /// </remarks>
    public async Task<ProviderCheck> EnsureRunningAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        if (BaseUrlFor(instance) is not { } baseUrl)
            return ProviderCheck.Failed($"\"{instance.BaseUrl.Trim()}\" is not an address this can read.");

        if (await IsServingAsync(baseUrl, ct))
            return ProviderCheck.Reached($"Proxy running at {baseUrl}");

        if (!IsInstalled)
            return ProviderCheck.Failed(
                "CCS is not installed on this machine, so there is no proxy to run Claude Code "
                + "through. Install it from CCS's row in Settings → AI.");

        var started = await StartProxyAsync(ct);
        if (started is { ExitCode: not 0 and not null })
        {
            var tail = Tail(started.Output);
            return ProviderCheck.Failed($"\"ccs cliproxy start\" failed with exit code "
                + $"{started.ExitCode}" + (tail.Length > 0 ? $": {tail}" : "."));
        }

        // The start command returns on its own; the daemon comes up a moment later. Poll rather than
        // sleep once: a proxy that answers in half a second need not cost the launch a full second,
        // and one that never comes up is reported rather than waited on for ever.
        var deadline = DateTimeOffset.UtcNow + ProxyStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            if (await IsServingAsync(baseUrl, ct))
                return ProviderCheck.Reached($"Proxy started at {baseUrl}");
        }

        return ProviderCheck.Failed(
            "The CCS proxy was started but did not answer within "
            + $"{(int)ProxyStartTimeout.TotalSeconds} s — it may still be coming up. Try again, or ask "
            + "it directly with \"ccs cliproxy status\".");
    }

    /// <summary>How long a freshly started proxy is waited for before the launch gives up on it.</summary>
    /// <remarks>A first start downloads nothing — the binary ships with CCS — so anything past this is
    /// a proxy that is not going to answer, and a launch that waits longer is a tile that hangs.
    /// Writable for the tests, which cannot afford twenty seconds to prove a timeout.</remarks>
    internal static TimeSpan ProxyStartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Whether this address is really the CLIProxy daemon — asked by protocol, never by port.
    /// </summary>
    private async Task<bool> IsServingAsync(Uri baseUrl, CancellationToken ct)
    {
        var probe = new AiProviderInstance { ProviderId = Id, BaseUrl = baseUrl.ToString() };
        using var document = await GetJsonAsync(probe, "v1/models", ct);
        return document is not null;
    }

    // ── What the Settings row does with the machine's state ──────────────────────────────────────

    /// <summary>Whether the <c>ccs</c> command exists on this machine.</summary>
    /// <remarks>Read on demand rather than cached: it is asked when a form opens or changes kind, and
    /// the answer can genuinely change between two opens — the user has just installed it in a tile.</remarks>
    public static bool IsInstalled =>
        InstalledOverride?.Invoke() ?? ExecutableFinder.Anywhere(CommandName) is not null;

    /// <summary>Whether CCS is installed, in tests.</summary>
    internal static Func<bool>? InstalledOverride { get; set; }

    /// <summary>Whether a Codex account is signed in to the proxy.</summary>
    /// <remarks>
    /// <para>The proxy keeps its OAuth tokens under <c>~/.ccs/cliproxy/auth/</c>, named after the
    /// provider and the account. Presence is the question this application is entitled to ask — whether
    /// a token still <em>works</em> is the proxy's own refresh cycle, and a launch that outruns it
    /// fails with the proxy's error, not a configuration fault of ours.</para>
    /// <para><b>The <c>codex-</c> prefix is inferred, not measured.</b> CCS's own documentation shows
    /// the naming for its neighbours (<c>gemini-&lt;account&gt;.json</c>, <c>kiro-…</c>,
    /// <c>xai-…</c>), and this reads the same convention for codex — but nobody has logged in here and
    /// looked. The failure mode if the guess is wrong is benign and points at its own fix: the Auth
    /// button shows for ever after a login that worked, and the next user of this file should replace
    /// the prefix with what a real login wrote.</para>
    /// </remarks>
    public static bool HasCodexAuth => Directory.Exists(AuthDirectory)
        && Directory.EnumerateFiles(AuthDirectory, "codex-*.json").Any();

    /// <summary>The command line everything here is spelled around.</summary>
    public const string CommandName = "ccs";

    /// <summary>What installing CCS actually types. Shown before it runs, and run in a tile.</summary>
    public static readonly InstallPlan Install = new("npm",
        ["install", "-g", "@kaitranntt/ccs"],
        "Installs CCS globally through npm, which has to be on PATH already. "
        + "CCS carries the CLIProxy daemon this provider runs Claude Code through.");

    /// <summary>The one-time login the proxy's Codex account needs. Auth only — no session starts.</summary>
    public static IReadOnlyList<string> AuthArguments { get; } = ["codex", "--auth"];

    /// <summary>Where the proxy keeps its signed-in accounts.</summary>
    private static string AuthDirectory
    {
        get
        {
            if (AuthDirectoryOverride is { } directory) return directory();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".ccs", "cliproxy", "auth");
        }
    }

    // ── The seams the tests drive ─────────────────────────────────────────────────────────────────

    /// <summary>Where the auth tokens are looked for, in tests.</summary>
    internal static Func<string>? AuthDirectoryOverride { get; set; }

    /// <summary>How <c>ccs cliproxy start</c> is run. Replaced in tests; the real one runs a process.</summary>
    internal static Func<string, IReadOnlyList<string>, CancellationToken, Task<CcsStartResult>>? StartOverride
    { get; set; }

    /// <summary>What a started command answered: its exit code, and what it printed along the way.</summary>
    /// <remarks>A null exit code is a process that was still running when the wait ended — which for a
    /// lifecycle command means the daemon it manages, not a hang of ours.</remarks>
    internal sealed record CcsStartResult(int? ExitCode, string Output);

    private static async Task<CcsStartResult> StartProxyAsync(CancellationToken ct)
    {
        if (StartOverride is { } start)
            return await start(CommandName, ["cliproxy", "start"], ct);

        var executable = ExecutableFinder.Anywhere(CommandName);
        if (executable is null)
            return new CcsStartResult(-1, "ccs is not on this machine.");

        return await RunAsync(executable, ["cliproxy", "start"], ct);
    }

    /// <summary>Runs one lifecycle command and collects what it printed, bounded by
    /// <see cref="ProxyStartTimeout"/>.</summary>
    /// <remarks>
    /// <para><b>A <c>.cmd</c> shim is not an executable.</b> npm installs <c>ccs</c> on Windows as a
    /// <c>.cmd</c>, which <c>CreateProcess</c> refuses to run directly — it goes through
    /// <c>cmd /c</c>. <b>The arguments go in separately.</b> Composing <c>/c</c> a single pre-quoted
    /// string escapes the embedded quotes, and cmd receives <c>\"…\"</c> as the command name —
    /// measured: <c>'…' is not recognized</c>, exit 1, every time. Separate arguments let .NET quote
    /// the path when it needs to and cmd's two-quote rule handles the rest, with spaces in the path
    /// and without.</para>
    /// <para><b>Everything here is bounded, including the reads.</b> <c>ReadToEndAsync</c> is awaited
    /// only through a drain deadline of its own: a daemonized child that inherited the redirected
    /// handles would hold the pipes open for its whole life, and awaiting it unconditionally is the
    /// hang past the timeout this method exists to prevent. Whatever has not drained by then is
    /// discarded — the exit code and the probe matter more than the tail of the log.</para>
    /// </remarks>
    internal static async Task<CcsStartResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        // Parentheses carry the meaning: a cmd shim is a *Windows* thing, so the OS check covers both
        // extensions — without them a .bat found on Linux would route to cmd.exe, which is not there.
        var viaCmd = OperatingSystem.IsWindows() && (
            executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (viaCmd)
        {
            info.FileName = "cmd.exe";
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(executable);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }
        else
        {
            info.FileName = executable;
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
                return new CcsStartResult(-1, "The process could not be started.");

            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);

            var wait = process.WaitForExitAsync(ct);
            var finished = await Task.WhenAny(wait, Task.Delay(ProxyStartTimeout, ct));
            var exitedOnItsOwn = finished == wait;
            if (!exitedOnItsOwn)
            {
                // Still running when the deadline passed. Killed this process only, not its children:
                // a lifecycle command that lingers may already have daemonized the proxy, and the
                // grandchild is the thing the probe after this is waiting to see.
                process.Kill(entireProcessTree: false);
            }

            var reads = Task.WhenAll(output, error);
            var drained = await Task.WhenAny(reads, Task.Delay(DrainTimeout, ct));
            var printed = drained == reads && reads.IsCompletedSuccessfully
                ? (output.Result + error.Result).Trim()
                : "";

            // The tasks abandoned above fault when the token later fires — observed, or the unhandled
            // one reaches TaskScheduler.UnobservedTaskException for something working as designed.
            ObserveAbandoned(wait);
            ObserveAbandoned(output);
            ObserveAbandoned(error);

            // A code only counts when the command ended by itself: the kill above is ours, and
            // reporting its termination code would read as the tool having failed.
            var code = exitedOnItsOwn && process.HasExited ? (int?)process.ExitCode : null;
            return new CcsStartResult(code, printed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Running {0} {1} failed: {2}", executable, string.Join(' ', arguments),
                ex.Message);
            return new CcsStartResult(-1, ex.Message);
        }
    }

    /// <summary>How long the pipes of an exited command are given to drain before what they hold is
    /// discarded. Short on purpose: this covers a child that died and left a daemon holding the
    /// handles, not a log worth reading.</summary>
    internal static TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Watches a task this method stopped awaiting, so its eventual fault is seen and
    /// swallowed rather than reported by the unobserved-task handler.</summary>
    private static void ObserveAbandoned(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>The last lines of what a command printed — the part that says why it failed.</summary>
    private static string Tail(string output)
    {
        if (output.Length == 0) return "";
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" · ", lines.TakeLast(3));
    }
}
