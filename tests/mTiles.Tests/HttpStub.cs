using System.Net;
using System.Text;
using mTiles.Services.Providers;

namespace mTiles.Tests;

/// <summary>An HTTP handler that answers whatever the test tells it to, and keeps what it was asked.
/// </summary>
/// <remarks>Every reader in the provider and usage layers takes a <c>HandlerFactory</c> seam of this
/// shape; this is the one handler behind all of them.</remarks>
internal sealed class FakeHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer,
    List<HttpRequestMessage>? seen = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (seen is not null)
            lock (seen) seen.Add(request);
        return answer(request, cancellationToken);
    }

    /// <summary>A reply carrying <paramref name="body"/> as JSON.</summary>
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A handler that answers every request with one body.</summary>
    public static FakeHttpHandler Canned(string body, HttpStatusCode status = HttpStatusCode.OK,
        List<HttpRequestMessage>? seen = null) =>
        new((_, _) => Task.FromResult(Json(body, status)), seen);
}

/// <summary>
/// Stands in for every request <see cref="AiProvider"/> makes, for as long as it is not disposed.
/// </summary>
/// <remarks>A handler rather than a client, because the base address and the timeout are exactly the two
/// per-instance things worth seeing applied. The provider layer makes a fresh handler per call, so what
/// is shared between calls — the requests seen — lives here. The seam is restored on disposal, so one test
/// cannot leave the next talking to a stub.</remarks>
internal sealed class HttpStub : IDisposable
{
    private readonly List<HttpRequestMessage> _requests = [];

    /// <summary>One body for every request.</summary>
    public HttpStub(string body, HttpStatusCode status = HttpStatusCode.OK)
        : this(_ => body, status)
    {
    }

    /// <summary>A body chosen by the request, for the paths that make more than one call.</summary>
    public HttpStub(Func<HttpRequestMessage, string> body, HttpStatusCode status = HttpStatusCode.OK)
        : this((request, _) => Task.FromResult(FakeHttpHandler.Json(body(request), status)))
    {
    }

    /// <summary>Whatever <paramref name="answer"/> does, the request included.</summary>
    public HttpStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) =>
        AiProvider.HandlerFactory = () => new FakeHttpHandler(answer, _requests);

    /// <summary>Every request fails the way an unreachable address does.</summary>
    public static HttpStub Throwing(Exception failure) =>
        new((_, _) => Task.FromException<HttpResponseMessage>(failure));

    /// <summary>What was asked, in order.</summary>
    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_requests) return [.. _requests];
        }
    }

    public void Dispose() => AiProvider.HandlerFactory = null;
}
