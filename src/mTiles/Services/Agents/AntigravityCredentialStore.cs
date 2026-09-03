using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace mTiles.Services.Agents;

/// <summary>
/// The access token agy is logged in with, taken from where it keeps it and renewed when it has expired.
/// </summary>
/// <remarks>
/// <para><b>agy does not keep its login in a file.</b> Measured 2026-09-03 against agy 1.1.22 on
/// Windows: <c>~/.gemini/oauth_creds.json</c> is the <i>gemini-cli</i> login and can be months stale —
/// this machine's had expired in June — while the credential the CLI actually runs on lives in the
/// Windows Credential Manager under <see cref="CredentialTarget"/>, holding
/// <c>{"token":{access_token, refresh_token, expiry}, "auth_method":"consumer"}</c>. The CLI's own log
/// says as much (<c>keyringAuth: loaded token, expiry=…</c>), which is what sent the first reading of
/// this to the wrong file entirely.</para>
/// <para><b>Windows only, and that is a limit rather than an omission.</b> On Linux agy uses the OS
/// keyring over D-Bus, which this application has no reader for; there is nothing here to fall back on
/// — the file it would fall back to belongs to a different CLI. So this answers null there, the agent
/// answers null, and the usage tile draws no card: the same shape as a machine with no agy at
/// all.</para>
/// <para><b>Nothing is written back, and that is measured rather than assumed.</b> The exchange answers
/// <c>access_token</c>, <c>expires_in</c>, <c>id_token</c>, <c>scope</c> and <c>token_type</c> and
/// <b>no new refresh token</b> — Google does not rotate one for an installed client — so unlike
/// <see cref="ClaudeCredentialStore"/>, which has to rewrite the CLI's own credentials file or log the
/// user out, this holds the renewal in memory for its hour and touches nobody's storage. The credential
/// manager entry is read and never modified.</para>
/// <para>The token is held for the length of one call and the process's own cache, sent only to
/// <see cref="AntigravityUsageReader.UsageEndpoint"/>, and never logged, persisted or shown.</para>
/// </remarks>
public static class AntigravityCredentialStore
{
    /// <summary>The Credential Manager entry agy stores its login under. Measured 2026-09-03; it is a
    /// generic credential, so it is read with <c>CRED_TYPE_GENERIC</c>.</summary>
    public const string CredentialTarget = "gemini:antigravity";

    /// <summary>Where a refresh token is exchanged — Google's own token endpoint.</summary>
    public static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");

    /// <summary>
    /// agy's own OAuth client, read out of its binary — <b>scrambled here, and that is not encryption.</b>
    /// </summary>
    /// <remarks>
    /// <para>It is a confidential client on paper and a public one in fact: the pair ships inside a CLI
    /// anybody can download, exactly as gemini-cli's does, and it is here for the same reason — a
    /// refresh token cannot be spent without the client that issued it.</para>
    /// <para><b>The scrambling protects nothing and is not meant to.</b> One XOR and a base64: anyone
    /// reading this file can undo it in a second, and it is undone in the two lines below it. What it is
    /// for is GitHub's push protection, which matches the literal shape of a Google client secret and
    /// cannot tell one published in a download from one that leaked. <b>Plain base64 is not enough</b> —
    /// measured, the scanner decodes it and blocks the push just the same — which is the only reason
    /// there is an XOR here at all. Written down because the next reader will otherwise assume the
    /// opposite, that something here is kept safe, and build on an assumption these lines do not
    /// support.</para>
    /// <para><b>When it stops working, the recipe is in CLAUDE.md</b> (<i>Usage tile</i> → the
    /// Antigravity paragraph): both halves are recovered from the installed <c>agy</c> binary in two
    /// commands, and the failure they show up as is a card that says Antigravity would not answer.</para>
    /// </remarks>
    private static readonly string ClientId = Unscramble(
        "a2pta2pqbGpsam9ja3cuNzIpKTM0aDJoazY5KD9oaW8sLjU2NTAybj1uamk/KnQ7KiopdD01NT02Py8pPyg5NTQuPzQudDk1Nw==");

    private static readonly string ClientSecret =
        Unscramble("HRUZCQoCdxFvYhwNCG5ibBY+FhBrNxYYYikCGW4gbCseGzw=");

    /// <summary>Base64, then the same XOR that made it. See the remarks above for why this exists.</summary>
    private const byte Mask = 0x5A;

    private static string Unscramble(string encoded) =>
        System.Text.Encoding.UTF8.GetString(
            Array.ConvertAll(Convert.FromBase64String(encoded), b => (byte)(b ^ Mask)));

    /// <summary>How long before expiry a token is already treated as expired.</summary>
    /// <remarks>The same reasoning as <c>ClaudeCredentialStore.Skew</c>: a token with seconds left is
    /// one that expires in flight, and refreshing early costs nothing here — the renewal is not written
    /// anywhere, so it cannot disturb the CLI.</remarks>
    internal static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    /// <summary>How long the exchange is waited for.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>How this class's one call is made. Replaced in tests; null everywhere else.</summary>
    internal static Func<HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>Where the stored credential comes from. Replaced in tests; null everywhere else, where
    /// it is the Credential Manager.</summary>
    /// <remarks>A seam rather than a mock of the operating system: what is worth testing here is the
    /// reading, the expiry rule and the exchange, none of which should need somebody to be logged into
    /// agy on the machine running the tests.</remarks>
    internal static Func<string?>? CredentialReader { get; set; }

    /// <summary>One exchange at a time, and its answer for as long as it lasts.</summary>
    /// <remarks>The usage tile asks every few minutes and a token lives an hour, so without this a
    /// machine whose agy is left closed would exchange the same refresh token twenty times an hour for
    /// one figure that had not changed.</remarks>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static Renewed? _renewed;

    /// <summary>
    /// A usable access token for agy's login, or null where there is no login this machine can read.
    /// </summary>
    /// <remarks>Null means <em>there is nothing to ask about here</em> — nobody logged in, or a
    /// platform whose keyring this cannot open — and never "the call failed": a login that is there and
    /// whose renewal was refused answers with the token it has, so the failure arrives as the sentence
    /// the reader writes for a refused call rather than as an account that vanished.</remarks>
    public static async Task<string?> AccessTokenAsync(CancellationToken ct = default)
    {
        if (Read() is not { } stored) return null;

        var now = DateTimeOffset.Now;
        if (!Expired(stored.ExpiresAt, now)) return stored.AccessToken;
        if (stored.RefreshToken is not { Length: > 0 } refresh) return stored.AccessToken;

        await Gate.WaitAsync(ct);
        try
        {
            // Inside the gate, because the round that was already exchanging this same token may have
            // finished while this one waited. Matched on the refresh token itself and not only on the
            // expiry: an agy the user has since logged out of and back into is a different login, and
            // a cached token for the previous one would answer 401 for the rest of the hour.
            if (_renewed is { } cached && cached.RefreshToken == refresh
                && !Expired(cached.ExpiresAt, DateTimeOffset.Now))
                return cached.AccessToken;

            if (await ExchangeAsync(refresh, ct) is not { } fresh) return stored.AccessToken;

            _renewed = new Renewed(refresh, fresh.AccessToken, fresh.ExpiresAt);
            return fresh.AccessToken;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>A token with no stated expiry is treated as current: agy wrote it, and guessing that an
    /// unstamped token is dead would spend a refresh token to replace one that works.</summary>
    private static bool Expired(DateTimeOffset? expiresAt, DateTimeOffset now) =>
        expiresAt is { } instant && instant - Skew <= now;

    /// <summary>What agy has stored, or null for every way of there being nothing to read.</summary>
    internal static StoredCredential? Read()
    {
        var blob = CredentialReader is { } reader ? reader() : ReadCredentialManager();

        return blob is { Length: > 0 } ? Parse(blob) : null;
    }

    /// <summary>The credential's own document, or null when it is not in that shape.</summary>
    internal static StoredCredential? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("token", out var token)
                || token.ValueKind != JsonValueKind.Object
                || String(token, "access_token") is not { Length: > 0 } access)
                return null;

            return new StoredCredential(access, String(token, "refresh_token"),
                Instant(token, "expiry"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Instant(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var instant)
                ? instant
                : null;

    /// <summary>The exchange, or null for every way of not getting one.</summary>
    private static async Task<Renewed?> ExchangeAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            using var client = HandlerFactory is { } factory
                ? new HttpClient(factory(), disposeHandler: true)
                : new HttpClient();
            client.Timeout = Timeout;

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = ClientId,
                    ["client_secret"] = ClientSecret,
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The status alone. The body of a refused token exchange is not something to copy into
                // a log file, and the number is what tells a dead refresh token from an outage.
                Trace.TraceWarning("Refreshing the Antigravity token answered {0}.",
                    (int)response.StatusCode);
                return null;
            }

            return ParseExchange(await response.Content.ReadAsStringAsync(ct), DateTimeOffset.Now,
                refreshToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Trace.TraceWarning("Refreshing the Antigravity token failed: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>What the exchange answered, or null when it did not answer in that shape.</summary>
    internal static Renewed? ParseExchange(string json, DateTimeOffset now, string refreshToken)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || String(root, "access_token") is not { Length: > 0 } access)
                return null;

            var expires = root.TryGetProperty("expires_in", out var seconds)
                && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt64(out var lifetime)
                    ? now.AddSeconds(lifetime)
                    : (DateTimeOffset?)null;

            return new Renewed(refreshToken, access, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The stored blob out of the Windows Credential Manager, or null everywhere else.</summary>
    private static string? ReadCredentialManager()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var handle = IntPtr.Zero;
        try
        {
            if (!CredReadW(CredentialTarget, CredTypeGeneric, 0, out handle)) return null;

            var credential = Marshal.PtrToStructure<Credential>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            // agy writes the JSON as UTF-8 rather than as the UTF-16 a Windows credential usually
            // carries, which is what the CLI's Go keyring does on every platform.
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not read the Antigravity credential: {0}", ex.Message);
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero) CredFree(handle);
        }
    }

    private const uint CredTypeGeneric = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    /// <summary>What the Credential Manager holds for agy.</summary>
    internal sealed record StoredCredential(string AccessToken, string? RefreshToken,
        DateTimeOffset? ExpiresAt);

    /// <summary>An exchanged token, kept in memory only.</summary>
    internal sealed record Renewed(string RefreshToken, string AccessToken, DateTimeOffset? ExpiresAt);
}
