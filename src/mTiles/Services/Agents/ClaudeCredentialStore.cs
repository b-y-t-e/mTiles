using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace mTiles.Services.Agents;

/// <summary>
/// The access token Claude Code's own <c>.credentials.json</c> holds, exchanged for a fresh one when it
/// has expired.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> The CLI refreshes its token lazily, when it is run; nothing
/// refreshes it while it sits there. So an account the user is genuinely logged into — a second
/// subscription they work in on Tuesdays — has an expired <c>accessToken</c> in its file for most of
/// the week, the usage endpoint answers 401, and the usage tile drew no card for a login that is
/// perfectly good. Measured 2026-09-02 on a machine with three logins: two of the three were expired
/// and the one card on screen was the account that happened to have been used that hour.</para>
/// <para><b>The refresh token rotates, and the old one dies immediately.</b> Measured against
/// <see cref="TokenEndpoint"/> on 2026-09-02: the answer carries a new <c>refresh_token</c>, and the one
/// that bought it answers <c>invalid_grant</c> on the very next call. That single fact decides the whole
/// shape of this class — <b>a refresh that is not written back logs the user out of Claude Code</b>,
/// because the CLI would be left holding a refresh token that no longer exists. Keeping the new token
/// in memory only was the original plan here and is not available: the choice is to write the file or
/// not to refresh.</para>
/// <para><b>So this writes into somebody else's CLI's file</b>, which nothing else in this application
/// does, and it is written the way a credential file has to be: the document is rewritten whole so that
/// every field this build does not know about survives, it goes through a temporary file and a move so
/// a crash mid-write cannot leave a truncated credential, and it is created owner-only through
/// <see cref="PrivateFile"/> — the same rule the sign-in directories are made under. Nothing here is
/// logged: not the token, not the account, not the body of a failed exchange.</para>
/// <para><b>A refresh that fails is not an emergency.</b> The stale token is handed back and the call
/// that follows fails as it did before — one card carrying a sentence — rather than the account
/// vanishing or an exception reaching a dashboard refresh.</para>
/// <para><b>The residual risk, stated rather than hidden: this rotates a token a running
/// <c>claude</c> may be holding.</b> The usage tile asks every few minutes, so the exchange can happen
/// while a session is open in a tile next door, and if that process kept its refresh token in memory
/// rather than reading the file each time it renews, this would invalidate the token it is about to
/// spend — the opposite of what this class is for. <b>Reasoned, not measured:</b> several
/// <c>claude</c> sessions at once on one machine is the ordinary case here, and the old refresh token
/// dies immediately, so processes caching it would already be logging each other out without any help
/// from mTiles. They do not, which says the CLI reads the file when it renews. What is left is a race
/// of a few hundred milliseconds between its read and this write, and narrowing <see cref="Skew"/>
/// does not close it — a round every three minutes lands after expiry regardless — so it is written
/// down instead of being defended against with machinery that would not work.</para>
/// </remarks>
public static class ClaudeCredentialStore
{
    /// <summary>Where a refresh token is exchanged. Measured 2026-09-02.</summary>
    /// <remarks>The path is <c>/v1/oauth/token</c> on <c>api.anthropic.com</c> and nowhere else: the
    /// four other spellings that look right (<c>console.anthropic.com/v1/oauth/token</c>,
    /// <c>api.anthropic.com/api/oauth/token</c> beside the usage endpoint's own prefix,
    /// <c>console.anthropic.com/api/oauth/token</c>, <c>claude.ai/api/oauth/token</c>) all answer 404.
    /// </remarks>
    public static readonly Uri TokenEndpoint = new("https://api.anthropic.com/v1/oauth/token");

    /// <summary>Claude Code's own public OAuth client. There is no secret — it is a PKCE client, and
    /// this is the id its own login flow uses.</summary>
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary><b>Load-bearing.</b> Without a user agent the exchange is refused at the edge by
    /// Cloudflare with <c>403</c> and a body of <c>error code: 1010</c> — which is not an OAuth answer
    /// at all and would have read here as a dead refresh token.</summary>
    private const string UserAgent = "mTiles (Claude Code credentials)";

    /// <summary>How long before expiry a token is already treated as expired.</summary>
    /// <remarks>A token with thirty seconds left is one that expires while the request is in flight,
    /// and the cost of refreshing early is nothing — the exchange is cheap and the CLI reads whatever
    /// this leaves behind.</remarks>
    internal static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    /// <summary>How long the exchange is waited for.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>How this class's one call is made. Replaced in tests; null everywhere else.</summary>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>One refresh at a time per file.</summary>
    /// <remarks><para><b>Two sources routinely name one file.</b> A machine whose default account lives
    /// in a sign-in's directory is asked twice in the same round, and a rotating refresh token cannot be
    /// spent twice: the second exchange would answer <c>invalid_grant</c> and, worse, would do so
    /// having already overwritten the file. Inside the gate the file is read again, so the second
    /// caller simply finds the fresh token the first one wrote.</para>
    /// <para><b>Keyed by the canonical path and not by the string handed in</b>, which is the whole
    /// point rather than tidiness: those two sources reach the same file by different routes — one
    /// through <c>CLAUDE_CONFIG_DIR</c>, the other through <c>AiSignInStore.DirectoryFor</c> — so a
    /// relative segment or a different separator spells one file two ways, hands out two gates and
    /// spends the rotating token twice, which is the exact failure this exists to prevent.</para>
    /// </remarks>
    private static readonly Dictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A usable access token for this credentials file, or null when nobody is signed in there.
    /// </summary>
    /// <remarks>Null means <em>there is no login here</em> and nothing else — the distinction
    /// <c>IAiAgent.UsageAsync</c> rests on. A login whose token could not be renewed answers with the
    /// token it has, so the failure arrives as the sentence the caller already writes for a refused
    /// call rather than as an account that disappeared.</remarks>
    public static async Task<string?> AccessTokenAsync(string credentialsFile,
        CancellationToken ct = default)
    {
        if (Read(credentialsFile) is not { } credentials) return null;
        if (!credentials.NeedsRefresh(DateTimeOffset.Now)) return credentials.AccessToken;
        if (credentials.RefreshToken is not { Length: > 0 }) return credentials.AccessToken;

        var gate = GateFor(credentialsFile);
        await gate.WaitAsync(ct);
        try
        {
            // Read again inside the gate: whoever held it may have been the other source naming this
            // same file, in which case the answer is already on disk and there is nothing to spend.
            if (Read(credentialsFile) is not { } current) return null;
            if (!current.NeedsRefresh(DateTimeOffset.Now)) return current.AccessToken;
            if (current.RefreshToken is not { Length: > 0 }) return current.AccessToken;

            var refreshed = await ExchangeAsync(current.RefreshToken, ct);
            if (refreshed is null) return current.AccessToken;

            Write(credentialsFile, refreshed);
            return refreshed.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim GateFor(string path)
    {
        var key = CanonicalPath(path);

        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out var gate))
                Gates[key] = gate = new SemaphoreSlim(1, 1);

            return gate;
        }
    }

    /// <summary>The one spelling of a path, so two routes to one file take one gate.</summary>
    /// <remarks>A path this cannot canonicalise is used as it stands: the worst that costs is a gate of
    /// its own, and refusing to refresh over a path that the file system accepted would be the larger
    /// failure.</remarks>
    private static string CanonicalPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>What the file says, or null for every way of it not saying it.</summary>
    /// <remarks>Parsed rather than walked, unlike <c>AiAgent.ReadJsonString</c>: this file is a few
    /// hundred bytes and four of its fields are wanted at once, where <c>.claude.json</c> is megabytes
    /// and one field is.</remarks>
    internal static ClaudeCredentials? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || oauth.ValueKind != JsonValueKind.Object
                || String(oauth, "accessToken") is not { Length: > 0 } access)
                return null;

            return new ClaudeCredentials(access, String(oauth, "refreshToken"),
                Milliseconds(oauth, "expiresAt"));
        }
        catch (Exception ex)
        {
            // The name of the file and the reason, never anything out of it.
            Trace.TraceWarning("Could not read {0}: {1}", path, ex.Message);
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Milliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var epoch)
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
                : null;

    /// <summary>The exchange, or null for every way of not getting one.</summary>
    private static async Task<RefreshedTokens?> ExchangeAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            using var client = HandlerFactory is { } factory
                ? new HttpClient(factory(), disposeHandler: true)
                : new HttpClient();
            client.Timeout = Timeout;

            var body = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken,
                client_id = ClientId,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The status alone. The body of a refused token exchange is not something to copy into
                // a log file, and the number is what tells a dead refresh token (400) from an outage.
                Trace.TraceWarning("Refreshing a Claude Code token answered {0}.", (int)response.StatusCode);
                return null;
            }

            return Parse(await response.Content.ReadAsStringAsync(ct), DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Refreshing a Claude Code token failed: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>What the exchange answered, or null when it did not answer in that shape.</summary>
    /// <remarks>A reply carrying no access token is a format that has moved, and the honest answer to
    /// that is to leave the file alone: writing half of it would take the working refresh token with
    /// it.</remarks>
    internal static RefreshedTokens? Parse(string json, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || String(root, "access_token") is not { Length: > 0 } access)
                return null;

            return new RefreshedTokens(access, String(root, "refresh_token"),
                Expiry(root, "expires_in", now), Expiry(root, "refresh_token_expires_in", now),
                String(root, "scope"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Expiry(JsonElement root, string name, DateTimeOffset now) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var seconds)
                ? now.AddSeconds(seconds)
                : null;

    /// <summary>
    /// Puts the new tokens into the CLI's own file, leaving everything else in it exactly as it was.
    /// </summary>
    /// <remarks><para>The document is loaded as a tree and three or four values are replaced, rather
    /// than a document of ours being written in its place: that file carries fields this build has
    /// never heard of — the rate limit tier, whatever the next CLI version adds — and rewriting it from
    /// what is understood here would quietly delete them.</para>
    /// <para>Through a temporary file and a move, because the half-written state of a credentials file
    /// is a logged-out account; and owner-only, because it holds a refresh token that is now the only
    /// one there is.</para></remarks>
    private static void Write(string path, RefreshedTokens tokens)
    {
        var temporary = path + ".mtiles-new";
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject root
                || root["claudeAiOauth"] is not JsonObject oauth)
            {
                // The one path here that used to be silent, and the one that least could afford to be:
                // the file changed shape between the read and the write - the CLI writing it at that
                // moment, or a format that has moved - so the token has been spent and there is nowhere
                // to put it. Everything on screen goes on working off the token in memory, which is
                // exactly why nothing but this line would ever say that the login is now broken.
                Lost(path, "the file is no longer in the shape a credentials file has");
                return;
            }

            oauth["accessToken"] = tokens.AccessToken;

            // No refresh token in the answer means the service did not rotate this one - standard OAuth,
            // and the file keeps the token it has, which is still good. Nothing to report: it is the
            // rotating case that is dangerous, and it is the one being written.
            if (tokens.RefreshToken is { Length: > 0 } refresh) oauth["refreshToken"] = refresh;
            if (tokens.ExpiresAt is { } expires)
                oauth["expiresAt"] = expires.ToUnixTimeMilliseconds();
            if (tokens.RefreshExpiresAt is { } refreshExpires)
                oauth["refreshTokenExpiresAt"] = refreshExpires.ToUnixTimeMilliseconds();
            if (tokens.Scope is { Length: > 0 } scope && oauth["scopes"] is JsonArray)
                oauth["scopes"] = new JsonArray(scope.Split(' ',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(one => (JsonNode)JsonValue.Create(one)!)
                    .ToArray());

            PrivateFile.WriteAllText(temporary,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            PrivateFile.Protect(path);
        }
        catch (Exception ex)
        {
            Lost(path, ex.Message);

            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception cleanup) { Trace.TraceWarning("Could not remove {0}: {1}", temporary, cleanup.Message); }
        }
    }

    /// <summary>
    /// Says that a spent refresh token could not be stored, and what that costs.
    /// </summary>
    /// <remarks><b>The sentence names the consequence rather than the operation.</b> "Could not write
    /// the file" reads as something to try again later; what has actually happened is that the token
    /// the CLI holds has been invalidated and the replacement is only in this process's memory, so the
    /// user is signed out of that account in Claude Code the moment their own token expires. Every card
    /// on screen goes on working off the token in memory, which is precisely why this line is the only
    /// account of it there will be. The path and the reason, never the token.</remarks>
    private static void Lost(string path, string reason) =>
        Trace.TraceWarning(
            "A refreshed Claude Code token could not be stored in {0} ({1}). The login there will have "
            + "to be made again in the CLI: the refresh token it holds has already been spent.",
            path, reason);
}

/// <summary>What one Claude Code login's credentials file says.</summary>
/// <param name="AccessToken">The bearer token, valid until <paramref name="ExpiresAt" />.</param>
/// <param name="RefreshToken">What buys the next one, or null in a file that carries none.</param>
/// <param name="ExpiresAt">When the access token dies, or null when the file does not say — which is
/// treated as <em>still good</em>, because a missing field is not evidence of expiry and spending a
/// rotating refresh token on a guess is how a working login is lost.</param>
internal sealed record ClaudeCredentials(string AccessToken, string? RefreshToken,
    DateTimeOffset? ExpiresAt)
{
    public bool NeedsRefresh(DateTimeOffset now) =>
        ExpiresAt is { } expiry && expiry - now <= ClaudeCredentialStore.Skew;
}

/// <summary>What the token endpoint answered.</summary>
internal sealed record RefreshedTokens(string AccessToken, string? RefreshToken,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RefreshExpiresAt, string? Scope);
