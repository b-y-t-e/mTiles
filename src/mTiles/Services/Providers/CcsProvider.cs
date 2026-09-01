using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
/// already sets — asking here for the token to present, which is the key the daemon's own config hands
/// out.</para>
/// <para><b>The daemon takes a key after all</b> — measured 2026-09-01, when a CCS update began writing
/// <c>api-keys:</c> into its config, and every request without one, this application's probe included,
/// was refused with <c>401 {"error":"Missing API key"}</c>. The key is not a secret the user keeps: it
/// is <see cref="ManagedApiKey"/>, read from the same file, so nothing is typed, nothing is asked, and
/// an update that rotates it is picked up at the next read.</para>
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

    /// <summary>No key for the <em>user</em> to type — the daemon's own config is where the credential
    /// comes from, and <see cref="ClientToken"/> reads it.</summary>
    public override bool NeedsApiKey => false;
    public override bool IsLocal => true;

    /// <summary>The key the daemon's own config hands out, or the word that passes a server old enough
    /// to take none.</summary>
    /// <remarks>A key typed on the instance still wins — tolerance rather than a route, since the form
    /// offers no key field here and nothing writes one. The one statement of that rule: everything
    /// that presents a credential to this proxy asks here.</remarks>
    public override string ClientToken(AiProviderInstance instance) =>
        instance.ApiKey.Length > 0 ? instance.ApiKey : ManagedApiKey ?? NoKeyWord;

    /// <inheritdoc />
    /// <remarks>The form note that fits the row beside it: there is nothing to type, and the sentence
    /// says where the credential actually comes from.</remarks>
    public override string NoKeyNote => "no key to type — mTiles authenticates with the proxy's own key";

    /// <summary>The key read out of the daemon's config, or null when it names none.</summary>
    /// <remarks>
    /// <para>CCS writes <c>api-keys:</c> — first entry <c>ccs-internal-managed</c> — into
    /// <c>~/.ccs/cliproxy/config.yaml</c>, and since the update that did, it refuses every anonymous
    /// request with 401. Read at every call rather than cached: the calls are a handful a launch, the
    /// file is a few kilobytes, and an update that rotates the key is then picked up without anything
    /// having to notice.</para>
    /// <para><b>A file that cannot be read is a keyless daemon as far as this answers.</b> Null sends
    /// the request unauthenticated, which is what every version before the key requirement answered —
    /// so an unreadable config costs nothing on an old CCS and reports an honest failure on a new one,
    /// rather than inventing a key.</para>
    /// </remarks>
    public static string? ManagedApiKey
    {
        get
        {
            if (ConfigReader is { } read) return read() is { } yaml ? ApiKeysIn(yaml).FirstOrDefault() : null;

            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ccs", "cliproxy", "config.yaml");
            try
            {
                return File.Exists(path) ? ApiKeysIn(File.ReadAllText(path)).FirstOrDefault() : null;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Reading {0} failed: {1}", path, ex.Message);
                return null;
            }
        }
    }

    /// <summary>Where the daemon's config is read from, in tests.</summary>
    /// <remarks>Answers the file's <em>text</em>, so a test drives the parse as well as the read.</remarks>
    internal static Func<string?>? ConfigReader { get; set; }

    /// <summary>
    /// The keys under <c>api-keys:</c> in the daemon's YAML config, in the order written.
    /// </summary>
    /// <remarks>
    /// <para>Read what CCS writes, not the whole of YAML: the file is the daemon's own, regenerated on
    /// its updates, and its shape is a block list of quoted or bare entries. A comment or a blank line
    /// inside the block is skipped; the next key at column zero ends it. An <c>api-keys:</c> line
    /// carrying its entries inline is not recognised — the block form is what the daemon writes, and a
    /// parser confident about shapes it has not seen is a parser that guesses about somebody else's
    /// file.</para>
    /// <para>Pure, and argued in a table test for the same reason <c>PhoneEndpointRanker</c> is: it is
    /// an opinion about a file format.</para>
    /// </remarks>
    internal static IReadOnlyList<string> ApiKeysIn(string yaml)
    {
        var keys = new List<string>();
        var underApiKeys = false;
        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!underApiKeys)
            {
                underApiKeys = line.TrimEnd() == "api-keys:";
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            // Anything that is not a list entry ends the block — the next top-level key, chiefly.
            if (!trimmed.StartsWith("- ")) break;

            var value = trimmed[2..].Trim().Trim('"').Trim('\'');
            if (value.Length > 0) keys.Add(value);
        }
        return keys;
    }

    /// <summary>Presents <see cref="ClientToken"/>'s answer as the bearer credential — the one rule
    /// for what authenticates a call to this proxy, so the probe, the model list and the agent's own
    /// CLI cannot disagree about which key is in play. The standing word is not sent: it is a token
    /// shape for the CLI's environment, not a credential, and the wire carries either a key or
    /// nothing.</summary>
    protected override void Authenticate(HttpRequestMessage request, AiProviderInstance instance)
    {
        if (ClientToken(instance) is { Length: > 0 } token && token != NoKeyWord)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

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
    /// <para><b>A refusal is read as what it is</b> — a daemon that is up and declining the credential —
    /// and is answered immediately: neither starting it again nor waiting out the poll can change its
    /// mind, and reading the 401 as silence produced the one dishonest sentence this class could still
    /// print, "it may still be coming up", at a proxy that had been up the whole time.</para>
    /// <para>Installing CCS is <em>not</em> part of this: an installation is a decision and a visible
    /// tile, which is what the Settings row's Install button is for. A launch answers with the sentence
    /// pointing there instead.</para>
    /// </remarks>
    public async Task<ProviderCheck> EnsureRunningAsync(AiProviderInstance instance,
        CancellationToken ct = default)
    {
        if (BaseUrlFor(instance) is not { } baseUrl)
            return ProviderCheck.Failed($"\"{instance.BaseUrl.Trim()}\" is not an address this can read.");

        switch (await ProbeAsync(baseUrl, instance, ct))
        {
            case ProxyAnswer.Serving:
                return ProviderCheck.Reached($"Proxy running at {baseUrl}");
            case ProxyAnswer.Refused:
                return ProviderCheck.Failed(RefusalMessage(baseUrl, instance));
        }

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
        // and one that never comes up is reported rather than waited on for ever. A refusal in the
        // loop ends it the same way — up is up, however freshly it became so.
        var deadline = DateTimeOffset.UtcNow + ProxyStartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            switch (await ProbeAsync(baseUrl, instance, ct))
            {
                case ProxyAnswer.Serving:
                    return ProviderCheck.Reached($"Proxy started at {baseUrl}");
                case ProxyAnswer.Refused:
                    return ProviderCheck.Failed(RefusalMessage(baseUrl, instance));
            }
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

    /// <summary>What the daemon said when asked by protocol, never by port.</summary>
    private enum ProxyAnswer { Serving, Refused, Silent }

    /// <summary>Asks the address whether the CLIProxy daemon is there — and, when it answers, whether
    /// it takes the credential this would present.</summary>
    /// <remarks><b>Silent and refusing are different answers, and only silence is "not coming up".</b>
    /// The 401 is the measured refusal (2026-09-01, the key requirement) and proves the daemon is up;
    /// every other non-success, and every failure to connect, is silence. Narrow on 401 deliberately,
    /// the way a denial is matched by its words elsewhere: a guess at other codes would spend a
    /// refusal message on a server that is merely broken.</remarks>
    private async Task<ProxyAnswer> ProbeAsync(Uri baseUrl, AiProviderInstance instance,
        CancellationToken ct)
    {
        try
        {
            using var client = ClientFor(instance);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, "v1/models"));
            Authenticate(request, instance);

            using var response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return ProxyAnswer.Serving;
            if (response.StatusCode == HttpStatusCode.Unauthorized) return ProxyAnswer.Refused;

            Trace.TraceWarning("{0} answered {1} for the probe.", Id, (int)response.StatusCode);
            return ProxyAnswer.Silent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Probing {0} failed: {1}", baseUrl, ex.Message);
            return ProxyAnswer.Silent;
        }
    }

    /// <summary>What a refusing daemon says to its user: it is up, the credential is the problem, and
    /// where the key it wants lives.</summary>
    /// <remarks>The advice stops at the config file on purpose: it is the one route the UI offers
    /// anything about. The instance's key <em>can</em> be set — a row converted from another provider
    /// keeps the key it was typed on, and a hand-edited settings.json is honoured — but the form has
    /// no field for it here, and pointing at a control that is not there is a sentence that sends the
    /// user looking for their own mistake.</remarks>
    private static string RefusalMessage(Uri baseUrl, AiProviderInstance instance)
    {
        var presented = instance.ApiKey.Length > 0
            ? "the key typed on this instance is not one it accepts"
            : ManagedApiKey is { }
                ? "the key from its own config is not one it accepts"
                : "it requires a key and none could be read from its config";

        return "The proxy at " + baseUrl + " refused the request with 401 — " + presented
            + ". Its keys are the \"api-keys:\" entries in ~/.ccs/cliproxy/config.yaml.";
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
