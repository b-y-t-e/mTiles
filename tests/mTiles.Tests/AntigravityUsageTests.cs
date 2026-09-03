using System.Net;
using System.Net.Http;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The document agy's limits are read out of, and the login they are asked with.
/// </summary>
/// <remarks>
/// Both are somebody else's and both are undocumented — an internal Google endpoint, and a credential
/// the CLI keeps in the Windows Credential Manager. What is pinned here is this application's own
/// behaviour: a window the service stops naming disappears rather than reading as spent, a window the
/// service says is empty reads as empty rather than disappearing, and a login that cannot be read is
/// nothing to report rather than an account in trouble.
/// </remarks>
public class AntigravityUsageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        AntigravityCredentialStore.HandlerFactory = null;
        AntigravityCredentialStore.CredentialReader = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>The answer as it was measured on 2026-09-03, trimmed of the prose fields.</summary>
    private const string Payload = """
        {
          "groups": [
            {
              "displayName": "Gemini Models",
              "buckets": [
                { "bucketId": "gemini-weekly", "window": "weekly",
                  "resetTime": "2026-09-04T07:50:38Z", "remainingFraction": 0.75 },
                { "bucketId": "gemini-5h", "window": "5h",
                  "resetTime": "2026-09-03T17:07:03Z", "remainingFraction": 0.9 }
              ]
            },
            {
              "displayName": "Claude and GPT models",
              "buckets": [
                { "bucketId": "3p-weekly", "window": "weekly", "remainingFraction": 1 },
                { "bucketId": "3p-5h", "window": "5h", "remainingFraction": 1 }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void The_two_model_groups_become_four_windows()
    {
        var report = AntigravityUsageReader.Parse(Payload, "agy", "Antigravity", Now);

        Assert.True(report.Answered);
        Assert.Collection(report.Windows,
            week =>
            {
                Assert.Equal("Gemini 7d", week.Label);
                Assert.Equal(TimeSpan.FromDays(7), week.Length);
                Assert.Equal(25, week.UsedPercent!.Value, 6);
                Assert.Equal(new DateTimeOffset(2026, 9, 4, 7, 50, 38, TimeSpan.Zero), week.ResetsAt);
            },
            five =>
            {
                Assert.Equal("Gemini 5h", five.Label);
                Assert.Equal(TimeSpan.FromHours(5), five.Length);
                Assert.Equal(10, five.UsedPercent!.Value, 6);
            },
            thirdWeek =>
            {
                Assert.Equal("Claude and GPT 7d", thirdWeek.Label);
                Assert.Equal(0, thirdWeek.UsedPercent!.Value, 6);

                // The service named no instant for this one, and null is what says so — a reset shown
                // as "now" would be a countdown this application invented.
                Assert.Null(thirdWeek.ResetsAt);
            },
            thirdFive => Assert.Equal("Claude and GPT 5h", thirdFive.Label));
    }

    /// <summary>
    /// A bucket with no fraction is an exhausted one, because proto3 over JSON omits a default value.
    /// </summary>
    /// <remarks>This is the one place the reader guesses, and it guesses towards saying something: the
    /// alternative — skipping a bucket that names its window perfectly well — takes the card quiet at
    /// the exact moment somebody is looking at it to find out why agy stopped answering.</remarks>
    [Fact]
    public void A_bucket_with_no_fraction_reads_as_nothing_left()
    {
        var report = AntigravityUsageReader.Parse(
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"window":"5h"}]}]}""",
            "agy", "Antigravity", Now);

        Assert.Equal(100, Assert.Single(report.Windows).UsedPercent);
    }

    /// <summary>A bucket that names no window at all has nothing to be labelled or measured by.</summary>
    [Fact]
    public void A_bucket_with_no_window_is_left_out()
    {
        var report = AntigravityUsageReader.Parse(
            """
            {"groups":[{"displayName":"Gemini Models","buckets":[
              {"bucketId":"gemini-5h","remainingFraction":0.5},
              {"window":"weekly","remainingFraction":0.5}]}]}
            """,
            "agy", "Antigravity", Now);

        Assert.Equal("Gemini 7d", Assert.Single(report.Windows).Label);
    }

    /// <summary>A window nobody here has heard of keeps the service's own word and no length.</summary>
    /// <remarks>Zero is what <c>UsagePace</c> reads as "there is no pace to work out", so the bar is
    /// drawn without its tick rather than measured against a length nobody stated.</remarks>
    [Fact]
    public void An_unknown_window_keeps_its_own_name_and_states_no_length()
    {
        var report = AntigravityUsageReader.Parse(
            """
            {"groups":[{"displayName":"Gemini Models","buckets":[
              {"window":"fortnightly","remainingFraction":0.5}]}]}
            """,
            "agy", "Antigravity", Now);

        var window = Assert.Single(report.Windows);
        Assert.Equal("Gemini fortnightly", window.Label);
        Assert.Equal(TimeSpan.Zero, window.Length);
    }

    /// <summary>A document naming no window this build reads is a format that has moved.</summary>
    [Fact]
    public void An_answer_with_no_readable_window_is_a_problem_and_not_an_empty_card()
    {
        var report = AntigravityUsageReader.Parse("""{"groups":[]}""", "agy", "Antigravity", Now);

        Assert.False(report.Answered);
        Assert.Empty(report.Windows);
    }

    [Fact]
    public void An_answer_that_is_not_json_is_a_problem()
    {
        var report = AntigravityUsageReader.Parse("<html>no</html>", "agy", "Antigravity", Now);

        Assert.False(report.Answered);
    }

    /// <summary>The word the service gates on is in the request, whatever else the header says.</summary>
    /// <remarks>Without it the endpoint answers 403 about a licence the account has, which names no
    /// cause at all — so this is the one header worth a test of its own.</remarks>
    [Fact]
    public void The_user_agent_carries_the_word_the_service_gates_on() =>
        Assert.Contains("antigravity", AntigravityUsageReader.UserAgent, StringComparison.Ordinal);

    /// <summary>The credential agy stores is read as agy writes it.</summary>
    [Fact]
    public void The_stored_credential_is_read_out_of_the_shape_agy_writes()
    {
        var stored = AntigravityCredentialStore.Parse(
            """
            {"token":{"access_token":"ya29.live","token_type":"Bearer",
              "refresh_token":"1//refresh","expiry":"2026-09-03T15:06:48.0142188+02:00"},
             "auth_method":"consumer"}
            """);

        Assert.NotNull(stored);
        Assert.Equal("ya29.live", stored!.AccessToken);
        Assert.Equal("1//refresh", stored.RefreshToken);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 15, 6, 48, 14, TimeSpan.FromHours(2)),
            stored.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>Anything else in that entry is nothing to log in with.</summary>
    [Theory]
    [InlineData("""{"auth_method":"consumer"}""")]
    [InlineData("""{"token":{"refresh_token":"1//refresh"}}""")]
    [InlineData("not json at all")]
    public void A_credential_in_any_other_shape_is_no_credential(string blob) =>
        Assert.Null(AntigravityCredentialStore.Parse(blob));

    /// <summary>A token with hours left is used as it is, and nothing is sent anywhere.</summary>
    [Fact]
    public async Task A_live_token_is_used_unchanged()
    {
        using var exchange = new StubExchange("""{"access_token":"fresh","expires_in":3599}""");
        AntigravityCredentialStore.CredentialReader =
            () => Credential("live-token", "refresh-live", DateTimeOffset.Now.AddHours(4));

        Assert.Equal("live-token", await AntigravityCredentialStore.AccessTokenAsync());
        Assert.Equal(0, exchange.Calls);
    }

    /// <summary>
    /// An expired one is exchanged, and the answer is kept here rather than written into agy's storage.
    /// </summary>
    /// <remarks>Measured: Google returns no new refresh token for this client, so there is nothing to
    /// write back and no chance of logging the user out of agy — which is the whole difference between
    /// this store and <c>ClaudeCredentialStore</c>. The second call proves the renewal is remembered:
    /// the tile asks every few minutes and the token lives an hour.</remarks>
    [Fact]
    public async Task An_expired_token_is_renewed_once_and_remembered()
    {
        using var exchange = new StubExchange("""{"access_token":"renewed","expires_in":3599}""");
        AntigravityCredentialStore.CredentialReader =
            () => Credential("stale", "refresh-expired", DateTimeOffset.Now.AddMinutes(-5));

        Assert.Equal("renewed", await AntigravityCredentialStore.AccessTokenAsync());
        Assert.Equal("renewed", await AntigravityCredentialStore.AccessTokenAsync());
        Assert.Equal(1, exchange.Calls);
    }

    /// <summary>A refused exchange hands back the token there is, so the failure arrives as a
    /// sentence on the card rather than as an account that vanished.</summary>
    [Fact]
    public async Task A_refused_exchange_leaves_the_stale_token_in_place()
    {
        using var exchange = new StubExchange("""{"error":"invalid_grant"}""",
            HttpStatusCode.BadRequest);
        AntigravityCredentialStore.CredentialReader =
            () => Credential("stale", "refresh-refused", DateTimeOffset.Now.AddMinutes(-5));

        Assert.Equal("stale", await AntigravityCredentialStore.AccessTokenAsync());
        Assert.Equal(1, exchange.Calls);
    }

    /// <summary>No credential is no account, which is a machine with no card rather than an error.</summary>
    [Fact]
    public async Task A_machine_with_no_login_answers_nothing()
    {
        AntigravityCredentialStore.CredentialReader = () => null;

        Assert.Null(await AntigravityCredentialStore.AccessTokenAsync());
    }

    private static string Credential(string access, string refresh, DateTimeOffset expiry) =>
        $$"""
          {"token":{"access_token":"{{access}}","refresh_token":"{{refresh}}",
            "expiry":"{{expiry:O}}"},"auth_method":"consumer"}
          """;

    private sealed class StubExchange : IDisposable
    {
        private int _calls;

        public StubExchange(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            AntigravityCredentialStore.HandlerFactory =
                () => new CannedHandler(body, status, () => Interlocked.Increment(ref _calls));

        public int Calls => Volatile.Read(ref _calls);

        public void Dispose() => AntigravityCredentialStore.HandlerFactory = null;

        private sealed class CannedHandler(string body, HttpStatusCode status, Action entered)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                entered();

                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body),
                });
            }
        }
    }
}
