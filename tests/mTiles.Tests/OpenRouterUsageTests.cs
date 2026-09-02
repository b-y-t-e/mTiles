using System.Diagnostics;
using System.Net;
using System.Text;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Providers;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// What a metered key answers, and what it answers when it does not.
/// </summary>
/// <remarks>
/// The one provider of six that publishes anything about spending, driven through
/// <c>AiProvider.HandlerFactory</c> so this runs without a key. What is pinned is the tri-state the
/// whole tile rests on: an unmetered key and an exhausted one must not read alike, so <c>null</c> is
/// <i>did not say</i> everywhere and never a zero.
/// </remarks>
[Collection(ProviderSeamCollection.Name)]
public class OpenRouterUsageTests
{
    private static AiProviderInstance Instance() =>
        new() { Id = "abc", ProviderId = "openrouter", Name = "OpenRouter", ApiKey = "sk-test" };

    [Fact]
    public async Task A_limited_key_reports_its_windows_and_what_is_left()
    {
        using var _ = new StubHttp("""
            { "data": { "limit": 20, "limit_remaining": 14.6, "usage_daily": 1.18,
                        "usage_weekly": 5.4, "usage_monthly": 12.0,
                        "limit_reset": "2026-09-04T00:00:00Z" } }
            """);

        var report = await new OpenRouterProvider().UsageAsync(Instance());

        Assert.NotNull(report);
        Assert.True(report!.Answered);
        Assert.Equal(14.6m, report.RemainingCredit);
        Assert.Equal("$", report.Currency);
        Assert.Equal(["today", "7d", "30d"], report.Windows.Select(window => window.Label));
        Assert.Equal(1.18m, report.Windows[0].UsedAmount);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero), report.Windows[1].ResetsAt);
    }

    /// <summary>The subtraction is the only figure here that is ours, and it is a difference of two the
    /// service did state.</summary>
    [Fact]
    public async Task An_unlimited_key_falls_back_to_the_credits_endpoint()
    {
        using var _ = new RoutedHttp(request => request.RequestUri!.AbsolutePath.EndsWith("credits")
            ? """{ "data": { "total_credits": 25.0, "total_usage": 4.4 } }"""
            : """{ "data": { "usage_daily": 0.5 } }""");

        var report = await new OpenRouterProvider().UsageAsync(Instance());

        Assert.Equal(20.6m, report!.RemainingCredit);
    }

    /// <summary>A field the service did not state is null, because drawn as 0 it reads as a week that
    /// cost nothing.</summary>
    [Fact]
    public async Task A_missing_amount_is_null_rather_than_zero()
    {
        using var _ = new StubHttp("""{ "data": { "limit_remaining": 3.0 } }""");

        var report = await new OpenRouterProvider().UsageAsync(Instance());

        Assert.All(report!.Windows, window => Assert.Null(window.UsedAmount));
    }

    /// <summary>A key that exists and could not be asked is a card with a sentence on it, never a
    /// zero.</summary>
    [Fact]
    public async Task A_refused_key_is_a_problem_rather_than_an_empty_card()
    {
        using var _ = new StubHttp("""{"error":"nope"}""", HttpStatusCode.Unauthorized);

        var report = await new OpenRouterProvider().UsageAsync(Instance());

        Assert.NotNull(report);
        Assert.False(report!.Answered);
        Assert.NotEmpty(report.Problem!);
    }

    /// <summary>An address that cannot be read is named, and nothing is sent.</summary>
    [Fact]
    public async Task An_unreadable_address_is_named()
    {
        var instance = Instance();
        instance.BaseUrl = "192.168.1.10:abc";

        var report = await new OpenRouterProvider().UsageAsync(instance);

        Assert.False(report!.Answered);
        Assert.Contains("192.168.1.10:abc", report.Problem!);
    }

    /// <summary>The instance's id and not its name, because two keys for one service are two
    /// identically spelled rows and a renamed one must keep its own history.</summary>
    [Fact]
    public void The_history_key_is_the_instances_id()
    {
        var instance = Instance();
        instance.Name = "renamed";

        Assert.Equal("openrouter:abc", OpenRouterProvider.UsageSourceId(instance));
    }

    /// <summary>
    /// A 200 that is not this service's shape is a card with a sentence, never a card that vanishes.
    /// </summary>
    /// <remarks><c>JsonElement.TryGetProperty</c> is a lookup and not a test: it throws on anything that
    /// is not an object, so a proxy or a captive portal answering with an array took
    /// <c>UsageAsync</c> — documented never to throw — out through the service's own catch, and the
    /// account disappeared from the tile instead of saying it could not be asked.</remarks>
    [Theory]
    [InlineData("[]")]
    [InlineData("\"nope\"")]
    [InlineData("null")]
    [InlineData("""{ "data": [] }""")]
    public async Task AnAnswerThatIsNotThisServicesShapeIsReportedRatherThanThrown(string body)
    {
        using var _ = new StubHttp(body);

        var report = await new OpenRouterProvider().UsageAsync(Instance());

        Assert.NotNull(report);

        // Not thrown *and* not a card of blanks: built as an ordinary report it came out Answered, with
        // three window labels and not one figure under them - the "says nothing" this type exists to
        // keep off the screen, reached by the quieter half of the same fault.
        Assert.False(report!.Answered);
        Assert.NotNull(report.Problem);
        Assert.Empty(report.Windows);
        Assert.Null(report.RemainingCredit);
    }

    /// <summary>The reason an account could not be asked reaches the log, since it reaches no card.</summary>
    [Fact]
    public async Task TheReasonAKeyCouldNotBeAskedIsTraced()
    {
        using var _ = new StubHttp("[]");
        using var listener = new CapturedTrace();

        var service = new AiUsageService(new SettingsService(), null,
            _ => [new ProviderUsageSource(new OpenRouterProvider(), Instance())]);
        await service.RefreshAsync(force: true);
        service.Dispose();

        Assert.Contains(listener.Lines, line => line.Contains("could not be read"));
    }

    /// <summary>Everything Trace was told while this was alive.</summary>
    private sealed class CapturedTrace : TraceListener
    {
        public CapturedTrace() => Trace.Listeners.Add(this);

        public List<string> Lines { get; } = [];

        public override void Write(string? message) => WriteLine(message);

        public override void WriteLine(string? message)
        {
            if (message is not null) Lines.Add(message);
        }

        protected override void Dispose(bool disposing) => Trace.Listeners.Remove(this);
    }

    /// <summary>The same shape through the older reader, which had it first.</summary>
    [Fact]
    public async Task TheKeyTestSurvivesAnAnswerThatIsNotAnObject()
    {
        using var _ = new StubHttp("[]");

        var check = await new OpenRouterProvider().TestAsync(Instance());

        Assert.Null(check.Balance);
    }

    private sealed class StubHttp(string body, HttpStatusCode status = HttpStatusCode.OK)
        : RoutedHttp(_ => body, status);

    /// <summary>One canned reply per request, chosen by the address it was sent to.</summary>
    /// <remarks>The two-call path is the point: the credits endpoint is asked only when the key
    /// endpoint left the balance unknown, and one body for both would not show which was which.</remarks>
    private class RoutedHttp : IDisposable
    {
        public RoutedHttp(Func<HttpRequestMessage, string> body,
            HttpStatusCode status = HttpStatusCode.OK) =>
            AiProvider.HandlerFactory = () => new RoutingHandler(body, status);

        public void Dispose() => AiProvider.HandlerFactory = null;

        private sealed class RoutingHandler(Func<HttpRequestMessage, string> body, HttpStatusCode status)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(body(request), Encoding.UTF8, "application/json"),
                });
        }
    }
}
