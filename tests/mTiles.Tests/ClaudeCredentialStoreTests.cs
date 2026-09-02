using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Renewing the token Claude Code's own credentials file holds.
/// </summary>
/// <remarks>
/// What is pinned here is not Anthropic's behaviour — nothing in this repository controls that — but
/// the two rules that follow from one measured fact. The refresh token <b>rotates</b>: the answer
/// carries a new one and the old one is dead the moment it is spent. So a renewal that is not written
/// back logs the user out of Claude Code, and a renewal spent twice on one file does the same. Both are
/// silent failures a day later, in somebody else's application, which is why they are tests rather than
/// a paragraph.
/// </remarks>
public class ClaudeCredentialStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "mtiles-credentials-" + Guid.NewGuid().ToString("N"));

    public ClaudeCredentialStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        ClaudeCredentialStore.HandlerFactory = null;
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* A temporary directory that will not go is not a failing test. */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>A token with hours left is used as it is, and nothing is sent anywhere.</summary>
    [Fact]
    public async Task A_live_token_is_used_unchanged()
    {
        var file = WriteCredentials("live-token", "refresh-1", DateTimeOffset.Now.AddHours(4));
        var before = File.ReadAllText(file);
        using var exchange = new StubExchange(Reply("new-token", "refresh-2"));

        var token = await ClaudeCredentialStore.AccessTokenAsync(file);

        Assert.Equal("live-token", token);
        Assert.Equal(0, exchange.Calls);
        Assert.Equal(before, File.ReadAllText(file));
    }

    /// <summary>An expired one is exchanged, and the answer goes back into the CLI's own file.</summary>
    /// <remarks>The rotation is the whole point of the write: leaving <c>refresh-1</c> in the file
    /// would leave the CLI holding a token Anthropic has already invalidated.</remarks>
    [Fact]
    public async Task An_expired_token_is_renewed_and_the_new_pair_is_stored()
    {
        var file = WriteCredentials("stale-token", "refresh-1", DateTimeOffset.Now.AddMinutes(-5));
        using var exchange = new StubExchange(Reply("new-token", "refresh-2"));

        var token = await ClaudeCredentialStore.AccessTokenAsync(file);

        Assert.Equal("new-token", token);
        Assert.Equal(1, exchange.Calls);

        var stored = ClaudeCredentialStore.Read(file);
        Assert.NotNull(stored);
        Assert.Equal("new-token", stored!.AccessToken);
        Assert.Equal("refresh-2", stored.RefreshToken);
        Assert.True(stored.ExpiresAt > DateTimeOffset.Now.AddHours(7));
    }

    /// <summary>A token whose expiry is minutes away is renewed before it dies mid-request.</summary>
    [Fact]
    public async Task A_token_inside_the_skew_is_renewed_early()
    {
        var file = WriteCredentials("almost-stale", "refresh-1", DateTimeOffset.Now.AddSeconds(30));
        using var exchange = new StubExchange(Reply("new-token", "refresh-2"));

        Assert.Equal("new-token", await ClaudeCredentialStore.AccessTokenAsync(file));
        Assert.Equal(1, exchange.Calls);
    }

    /// <summary>Everything the file carries that this build knows nothing about survives the write.</summary>
    [Fact]
    public async Task Fields_this_build_does_not_know_survive()
    {
        var file = WriteCredentials("stale-token", "refresh-1", DateTimeOffset.Now.AddMinutes(-5));
        using var exchange = new StubExchange(Reply("new-token", "refresh-2"));

        await ClaudeCredentialStore.AccessTokenAsync(file);

        using var document = JsonDocument.Parse(File.ReadAllText(file));
        var oauth = document.RootElement.GetProperty("claudeAiOauth");
        Assert.Equal("max", oauth.GetProperty("subscriptionType").GetString());
        Assert.Equal("default_claude_max_5x", oauth.GetProperty("rateLimitTier").GetString());
        Assert.Equal("kept", document.RootElement.GetProperty("somethingElse").GetString());
    }

    /// <summary>A refused exchange hands back the token there is, and leaves the file alone.</summary>
    /// <remarks>The call that follows then fails as it did before — a card carrying a sentence — rather
    /// than the account disappearing. Overwriting anything here would be spending a refresh token that
    /// may still be good on an outage that is not the user's fault.</remarks>
    [Fact]
    public async Task A_refused_exchange_keeps_the_stale_token_and_the_file()
    {
        var file = WriteCredentials("stale-token", "refresh-1", DateTimeOffset.Now.AddMinutes(-5));
        var before = File.ReadAllText(file);
        using var exchange = new StubExchange("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);

        Assert.Equal("stale-token", await ClaudeCredentialStore.AccessTokenAsync(file));
        Assert.Equal(before, File.ReadAllText(file));
    }

    /// <summary>An answer with no access token in it is a format that has moved, not a renewal.</summary>
    [Fact]
    public async Task An_answer_without_a_token_changes_nothing()
    {
        var file = WriteCredentials("stale-token", "refresh-1", DateTimeOffset.Now.AddMinutes(-5));
        var before = File.ReadAllText(file);
        using var exchange = new StubExchange("""{"token_type":"Bearer","expires_in":28800}""");

        Assert.Equal("stale-token", await ClaudeCredentialStore.AccessTokenAsync(file));
        Assert.Equal(before, File.ReadAllText(file));
    }

    /// <summary>
    /// Two sources naming one file spend one refresh token between them.
    /// </summary>
    /// <remarks>This is the routine case, not a corner: a machine whose default account lives inside a
    /// sign-in's directory is asked twice in the same round. A second exchange would answer
    /// <c>invalid_grant</c> — and would have overwritten the good token on its way there.</remarks>
    [Fact]
    public Task One_file_asked_twice_is_exchanged_once() => OneExchangeBetween(path => path);

    /// <summary>
    /// One file spelled two ways is still one file.
    /// </summary>
    /// <remarks>The two sources that name one credentials file reach it by different routes — the
    /// default account through <c>CLAUDE_CONFIG_DIR</c>, the sign-in through
    /// <c>AiSignInStore.DirectoryFor</c> — so the strings need not match. A gate keyed by the string
    /// rather than by the path would hand each of them its own, and the rotating token would be spent
    /// twice: the second exchange answering <c>invalid_grant</c> having already overwritten the good
    /// answer.</remarks>
    [Fact]
    public Task One_file_reached_by_two_spellings_is_exchanged_once() =>
        OneExchangeBetween(path => Path.Combine(_directory, ".", "..",
            Path.GetFileName(_directory), Path.GetFileName(path)));

    /// <summary>
    /// Two overlapping callers of one file, however each of them spells it, spend one refresh token.
    /// </summary>
    /// <remarks>The first is held inside the exchange until the second has arrived, so the second
    /// genuinely meets the gate. Without that the first call finishes before the second starts — the
    /// stub answers without ever yielding — and the assertion holds with no gate at all.</remarks>
    private async Task OneExchangeBetween(Func<string, string> spell)
    {
        var file = WriteCredentials("stale-token", "refresh-1", DateTimeOffset.Now.AddMinutes(-5));
        using var exchange = new StubExchange(Reply("new-token", "refresh-2"), hold: true);

        var first = ClaudeCredentialStore.AccessTokenAsync(file);
        await exchange.InFlight;
        var second = ClaudeCredentialStore.AccessTokenAsync(spell(file));
        exchange.Release();

        Assert.Equal("new-token", await first);
        Assert.Equal("new-token", await second);
        Assert.Equal(1, exchange.Calls);
    }

    /// <summary>A file with no login in it is no account, which is not the same as a failed one.</summary>
    [Fact]
    public async Task A_file_with_nobody_in_it_answers_null()
    {
        Assert.Null(await ClaudeCredentialStore.AccessTokenAsync(
            Path.Combine(_directory, "absent.json")));

        var empty = Path.Combine(_directory, "empty.json");
        File.WriteAllText(empty, "{}");
        Assert.Null(await ClaudeCredentialStore.AccessTokenAsync(empty));
    }

    /// <summary>A file that does not say when its token dies is left alone rather than guessed at.</summary>
    /// <remarks>A missing field is not evidence of expiry, and the cost of guessing wrong is a rotating
    /// refresh token spent for nothing.</remarks>
    [Fact]
    public async Task A_file_with_no_expiry_is_not_refreshed()
    {
        var file = Path.Combine(_directory, ".credentials.json");
        File.WriteAllText(file,
            """{ "claudeAiOauth": { "accessToken": "no-expiry", "refreshToken": "refresh-1" } }""");
        using var exchange = new StubExchange(Reply("new-token", "refresh-2"));

        Assert.Equal("no-expiry", await ClaudeCredentialStore.AccessTokenAsync(file));
        Assert.Equal(0, exchange.Calls);
    }

    private string WriteCredentials(string access, string refresh, DateTimeOffset expires)
    {
        var file = Path.Combine(_directory, ".credentials.json");
        File.WriteAllText(file, $$"""
            {
              "claudeAiOauth": {
                "accessToken": "{{access}}",
                "refreshToken": "{{refresh}}",
                "expiresAt": {{expires.ToUnixTimeMilliseconds()}},
                "refreshTokenExpiresAt": {{expires.AddDays(14).ToUnixTimeMilliseconds()}},
                "scopes": ["user:inference", "user:profile"],
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_5x"
              },
              "somethingElse": "kept"
            }
            """);

        return file;
    }

    /// <summary>The shape Anthropic's token endpoint answered in, measured 2026-09-02.</summary>
    private static string Reply(string access, string refresh) => $$"""
        {
          "token_type": "Bearer",
          "access_token": "{{access}}",
          "refresh_token": "{{refresh}}",
          "expires_in": 28800,
          "refresh_token_expires_in": 1278923,
          "scope": "user:inference user:profile"
        }
        """;

    /// <summary>
    /// One canned answer, a count of how many times it was asked for, and — when asked to hold — a way
    /// to keep the first caller inside the exchange while a second one arrives.
    /// </summary>
    /// <remarks><b>The holding is what makes the two "exchanged once" tests mean anything.</b> A stub
    /// answering from <c>Task.FromResult</c> never yields, so the first call runs to completion inside
    /// <c>Task.WhenAll</c>'s first synchronous step and the second one finds the fresh token already on
    /// disk: the gate is never contended, and the test passes just as happily with the gate removed —
    /// which is what it did until this was measured.</remarks>
    private sealed class StubExchange : IDisposable
    {
        private readonly TaskCompletionSource _inFlight =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        public StubExchange(string body, HttpStatusCode status = HttpStatusCode.OK, bool hold = false)
        {
            if (!hold) _released.SetResult();

            ClaudeCredentialStore.HandlerFactory =
                () => new CannedHandler(body, status, Entered, _released.Task);
        }

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Answers once a caller is inside the exchange and cannot yet leave it.</summary>
        public Task InFlight => _inFlight.Task;

        /// <summary>Lets whoever is inside the exchange finish.</summary>
        public void Release() => _released.TrySetResult();

        public void Dispose()
        {
            Release();
            ClaudeCredentialStore.HandlerFactory = null;
        }

        private void Entered()
        {
            Interlocked.Increment(ref _calls);
            _inFlight.TrySetResult();
        }

        private sealed class CannedHandler(string body, HttpStatusCode status, Action entered,
            Task released) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                entered();
                await released.WaitAsync(cancellationToken);

                return new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
