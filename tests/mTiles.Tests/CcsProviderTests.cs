using System.Net;
using System.Net.Http;
using System.Text;
using mTiles.Models;
using mTiles.Services.Agents;
using mTiles.Services.Providers;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The CCS provider: what it is compatible with, what its state detection answers, and what a launch
/// does when the proxy behind it is down.
/// </summary>
/// <remarks>No key, no network and no installed CCS: the HTTP layer goes through
/// <c>AiProvider.HandlerFactory</c>, the start command through <c>CcsProvider.StartOverride</c>, and the
/// machine's state through the two override seams — the same style as the other provider tests. The two
/// <see cref="CcsProvider.RunAsync"/> tests are the exception, and deliberately: every seam-driven test
/// bypasses the real process invocation, which is exactly where the one bug this file has caught so far
/// lived.</remarks>
[Collection(ProviderSeamCollection.Name)]
public class CcsProviderTests : IDisposable
{
    private static readonly CcsProvider Ccs = new();
    private static readonly IAiAgent Claude = AiAgentCatalog.Find("claude")!;

    public void Dispose()
    {
        AiProvider.HandlerFactory = null;
        CcsProvider.StartOverride = null;
        CcsProvider.InstalledOverride = null;
        CcsProvider.AuthDirectoryOverride = null;
        CcsProvider.ProxyStartTimeout = TimeSpan.FromSeconds(20);
        CcsProvider.DrainTimeout = TimeSpan.FromSeconds(2);
    }

    // ── Compatibility and address ─────────────────────────────────────────────────────────────────

    /// <summary>CCS serves an Anthropic-shaped endpoint, which is Claude Code's — and nobody else's:
    /// it is a bridge <em>to</em> Claude Code, so the flavor list must not admit codex or opencode.</summary>
    [Fact]
    public void Ccs_is_compatible_with_claude_alone()
    {
        var compatible = AiProviderCatalog.CompatibleWith(Agent("claude")).Select(p => p.Id).ToList();

        Assert.Contains("ccs", compatible);

        foreach (var agentId in new[] { "codex", "opencode", "pi", "agy" })
            Assert.False(AiProviderCatalog.IsCompatible(Agent(agentId), Ccs), agentId);
    }

    /// <summary>The published address is the whole of the setup: an instance that names none runs on
    /// 127.0.0.1:8317, and the endpoint handed Claude Code is that same root.</summary>
    [Fact]
    public void An_empty_address_means_the_published_proxy()
    {
        var instance = new AiProviderInstance { ProviderId = "ccs" };

        Assert.Equal("http://127.0.0.1:8317/", Ccs.BaseUrlFor(instance)?.ToString());
        Assert.Equal("http://127.0.0.1:8317/", Ccs.EndpointFor(ApiFlavor.Anthropic, instance)?.ToString());
        Assert.Null(Ccs.EndpointFor(ApiFlavor.OpenAiChatCompletions, instance));
    }

    // ── The model list ────────────────────────────────────────────────────────────────────────────

    /// <summary>The proxy answers the OpenAI-shaped listing, and a window it names is read rather than
    /// assumed — the model behind it is an id Claude Code knows nothing about.</summary>
    [Fact]
    public async Task The_model_list_is_read_and_its_window_carried()
    {
        var windows = new List<HttpRequestMessage>();
        AiProvider.HandlerFactory = () => new RecordingHandler(
            """{"data":[{"id":"gpt-5.4","context_length":400000}]}""", windows);

        var models = await Ccs.ModelsAsync(new AiProviderInstance { ProviderId = "ccs" });
        var window = await Ccs.ContextWindowAsync(new AiProviderInstance { ProviderId = "ccs" }, "gpt-5.4");

        Assert.Single(models);
        Assert.Equal("gpt-5.4", models[0].Id);
        Assert.Equal(400000, window);
        Assert.Contains(windows, w => w.RequestUri!.PathAndQuery.Contains("v1/models"));
    }

    // ── EnsureRunning ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A proxy already up costs one probe and starts nothing — the ordinary case, since the
    /// daemon lives its own life between launches.</summary>
    [Fact]
    public async Task A_running_proxy_is_probed_and_not_started()
    {
        var starts = 0;
        CcsProvider.StartOverride = (_, _, _) =>
        {
            Interlocked.Increment(ref starts);
            return Task.FromResult(new CcsProvider.CcsStartResult(0, ""));
        };
        AiProvider.HandlerFactory = () => new CannedHandler("""{"data":[]}""");

        var check = await Ccs.EnsureRunningAsync(new AiProviderInstance { ProviderId = "ccs" });

        Assert.True(check.Ok);
        Assert.Equal(0, starts);
    }

    /// <summary>A silent address starts the proxy, and the launch waits for it to answer.</summary>
    [Fact]
    public async Task A_down_proxy_is_started_and_then_answers()
    {
        var answer = false;
        CcsProvider.ProxyStartTimeout = TimeSpan.FromSeconds(5);
        CcsProvider.InstalledOverride = () => true;
        CcsProvider.StartOverride = (_, _, _) =>
        {
            answer = true;
            return Task.FromResult(new CcsProvider.CcsStartResult(0, ""));
        };
        AiProvider.HandlerFactory = () => answer
            ? new CannedHandler("""{"data":[]}""")
            : new CannedHandler("", HttpStatusCode.ServiceUnavailable);

        var check = await Ccs.EnsureRunningAsync(new AiProviderInstance { ProviderId = "ccs" });

        Assert.True(check.Ok);
        Assert.Contains("started", check.Message);
    }

    /// <summary>A start command that fails is named, with what it printed — never swallowed into a
    /// launch that fails later with a network error mid-session.</summary>
    [Fact]
    public async Task A_failed_start_is_reported()
    {
        CcsProvider.ProxyStartTimeout = TimeSpan.FromSeconds(5);
        CcsProvider.InstalledOverride = () => true;
        CcsProvider.StartOverride = (_, _, _) =>
            Task.FromResult(new CcsProvider.CcsStartResult(3, "port 8317 in use"));

        var check = await Ccs.EnsureRunningAsync(new AiProviderInstance { ProviderId = "ccs" });

        Assert.False(check.Ok);
        Assert.Contains("3", check.Message);
        Assert.Contains("port 8317 in use", check.Message);
    }

    /// <summary>A start that never comes up is given up on inside the launch, not waited on for ever.
    /// </summary>
    [Fact]
    public async Task A_proxy_that_never_answers_gives_up()
    {
        CcsProvider.ProxyStartTimeout = TimeSpan.FromMilliseconds(400);
        CcsProvider.InstalledOverride = () => true;
        CcsProvider.StartOverride = (_, _, _) =>
            Task.FromResult(new CcsProvider.CcsStartResult(0, ""));
        AiProvider.HandlerFactory = () => new CannedHandler("", HttpStatusCode.ServiceUnavailable);

        var check = await Ccs.EnsureRunningAsync(new AiProviderInstance { ProviderId = "ccs" });

        Assert.False(check.Ok);
        Assert.Contains("did not answer", check.Message);
    }

    // ── Machine state ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The Codex login is read from the proxy's own token directory, and only a Codex token
    /// counts — the directory holds one file per provider and account.</summary>
    [Fact]
    public void The_codex_login_is_read_from_the_proxy_token_directory()
    {
        using var temp = new TempDirectory();
        CcsProvider.AuthDirectoryOverride = () => temp.Path;

        Assert.False(CcsProvider.HasCodexAuth);

        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "gemini-someone@gmail.com.json"), "{}");
        Assert.False(CcsProvider.HasCodexAuth);

        File.WriteAllText(Path.Combine(temp.Path, "codex-someone@gmail.com.json"), "{}");
        Assert.True(CcsProvider.HasCodexAuth);
    }

    /// <summary>A resolver question about an instance on CCS is answered after the proxy is settled:
    /// a launch is refused with a sentence while the proxy cannot be brought up.</summary>
    /// <remarks>This is the tile's <c>LaunchProblem</c> and the Goal run's refusal in one — both ask
    /// the resolver first, which is why the ensure lives there.</remarks>
    [Fact]
    public async Task A_launch_on_a_dead_proxy_is_refused_by_the_resolver()
    {
        CcsProvider.ProxyStartTimeout = TimeSpan.FromMilliseconds(400);
        CcsProvider.InstalledOverride = () => true;
        CcsProvider.StartOverride = (_, _, _) =>
            Task.FromResult(new CcsProvider.CcsStartResult(0, ""));
        AiProvider.HandlerFactory = () => new CannedHandler("", HttpStatusCode.ServiceUnavailable);

        var settings = new AppSettings();
        var provider = new AiProviderInstance { ProviderId = "ccs" };
        settings.AiProviderInstances.Add(provider);

        var (model, problem) = await AgentModelResolver.ResolveAsync(settings, Claude,
            new AiAgentInstance { AgentId = Claude.Id, ApiAccountId = provider.Id, Model = "gpt-5.4" });

        Assert.Null(model);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    // ── The real process invocation ───────────────────────────────────────────────────────────────

    /// <summary>A <c>.cmd</c> shim runs through <c>cmd /c</c> with <b>separate</b> arguments, and the
    /// shim sees its arguments whether its path has spaces in it or not.</summary>
    /// <remarks>Real <c>cmd.exe</c>, real shim — the one place a seam cannot lie. It caught a real bug:
    /// composing <c>/c</c> a single pre-quoted string made .NET escape the embedded quotes, cmd
    /// received <c>\"…\"</c> as the command name, and every start answered <c>'…' is not
    /// recognized</c>, exit 1. Skipped off Windows, where no shim is ever composed.</remarks>
    [Fact]
    public async Task A_cmd_shim_runs_through_cmd_with_separate_arguments()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new TempDirectory();
        var spaced = Path.Combine(temp.Path, "dir with space");
        Directory.CreateDirectory(spaced);
        foreach (var directory in new[] { temp.Path, spaced })
        {
            File.WriteAllText(Path.Combine(directory, "ccs.cmd"),
                "@echo off\r\necho shim ran %*\r\nexit /b 0\r\n");
        }

        var plain = await CcsProvider.RunAsync(Path.Combine(temp.Path, "ccs.cmd"),
            ["cliproxy", "start"], CancellationToken.None);
        var withSpaces = await CcsProvider.RunAsync(Path.Combine(spaced, "ccs.cmd"),
            ["cliproxy", "start"], CancellationToken.None);

        Assert.Equal(0, plain.ExitCode);
        Assert.Contains("shim ran cliproxy start", plain.Output);
        Assert.Equal(0, withSpaces.ExitCode);
        Assert.Contains("shim ran cliproxy start", withSpaces.Output);
    }

    /// <summary>A shim that leaves a child holding the redirected pipes does not hang the read past
    /// its drain deadline — the exit code comes back and the unread tail is discarded.</summary>
    /// <remarks>The daemonizing-child case, which is what <c>ccs cliproxy start</c> plausibly is:
    /// the started daemon inherits the handles, the pipe stays open for its lifetime, and awaiting the
    /// reads unconditionally hung launch preparation past the very timeout this method exists to
    /// enforce. Skipped off Windows like its sibling.</remarks>
    [Fact]
    public async Task A_child_holding_the_pipes_does_not_hang_the_read()
    {
        if (!OperatingSystem.IsWindows()) return;

        CcsProvider.DrainTimeout = TimeSpan.FromSeconds(2);
        using var temp = new TempDirectory();
        var shim = Path.Combine(temp.Path, "ccs.cmd");
        File.WriteAllText(shim,
            "@echo off\r\n" +
            "echo started\r\n" +
            "start /b cmd /c \"timeout /t 30 > nul\"\r\n" +
            "exit /b 0\r\n");

        var started = DateTimeOffset.UtcNow;
        var result = await CcsProvider.RunAsync(shim, ["cliproxy", "start"], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        // The bound is the drain deadline, not the child's lifetime — an unbounded read would still
        // be waiting on a process that lives thirty seconds.
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(15),
            "RunAsync returned after too long — the reads were not bounded.");
    }

    private static IAiAgent Agent(string id) => AiAgentCatalog.Find(id)
        ?? throw new InvalidOperationException($"No agent {id} in the catalog.");

    /// <summary>One answer for every call, with nothing recorded.</summary>
    private sealed class CannedHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>The canned answer, plus the requests that were asked for.</summary>
    private sealed class RecordingHandler(string body, List<HttpRequestMessage> seen)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            seen.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }
}
