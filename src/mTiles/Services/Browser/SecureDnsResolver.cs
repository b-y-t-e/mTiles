using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace mTiles.Services.Browser;

/// <summary>
/// Turns "resolve names over an encrypted connection" into what WebView2 can actually be told.
/// </summary>
/// <remarks>
/// <para><b>Why not the DNS-over-HTTPS switches.</b> Measured 2026-09-16 against WebView2 153.0.4234.32:
/// <c>--dns-over-https-mode=secure</c> with a template is ignored — the embedded browser keeps using the
/// system resolver, so a network that answers <c>www.youtube.com</c> with Google's Restricted-Mode
/// address still wins. What does take effect is <c>--host-resolver-rules</c>: a static map of a
/// hostname to an address, applied below the point the network's DNS is asked at all. So this looks the
/// poisoned names up itself over DoH and hands the browser the answers as fixed rules.</para>
/// <para><b>Only the names a filter tampers with, never the video hosts.</b> The same measurement:
/// <c>www.youtube.com</c>, <c>youtube.com</c> and <c>www.google.com</c> were answered with the
/// restricted address, while <c>*.googlevideo.com</c> — where the video bytes come from — was answered
/// correctly. A single MAP for a googlevideo name would break playback (they are many, per session), so
/// they are deliberately left out: mapping the handful of front-door hosts is enough for the page to
/// come up unrestricted, and the media then loads normally.</para>
/// <para><b>Fixed for the life of the browser process</b>, like every other argument here, because
/// WebView2 runs one process and refuses a view whose arguments differ from it. The addresses are
/// re-resolved each time the first tile opens, so a session is always current; if DoH cannot be reached
/// the answer is null and the browser simply starts without the rule.</para>
/// </remarks>
public static class SecureDnsResolver
{
    /// <summary>The front-door hosts a network filter tends to answer with a substitute address.</summary>
    /// <remarks>The video and image CDNs are absent on purpose — see the class note.</remarks>
    internal static readonly string[] Hosts =
    [
        "www.youtube.com", "youtube.com", "m.youtube.com", "music.youtube.com",
        "www.google.com",
    ];

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    /// <summary>The <c>--host-resolver-rules</c> switch, or null when it is off or nothing resolved.</summary>
    /// <remarks>Called on the UI thread as the first tile opens, so the lookups run off it and are
    /// bounded: a slow or blocked resolver costs the browser its rule, never its launch.</remarks>
    public static string? HostRulesArgument(Models.BrowserSettings browser)
    {
        if (!browser.SecureDns)
            return null;

        var template = browser.SecureDnsTemplate?.Trim() ?? "";
        if (!Uri.TryCreate(template, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host.Length == 0 || template.Any(char.IsWhiteSpace))
            return null;

        try
        {
            // Off the UI thread (Task.Run), so waiting on it here cannot deadlock the sync context, and
            // bounded, so a blocked resolver costs the rule and not the launch.
            var work = Task.Run(() => ResolveAllAsync(template));
            return work.Wait(Budget + TimeSpan.FromSeconds(1))
                ? BuildRule(work.GetAwaiter().GetResult())
                : null;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Secure DNS: resolving over {0} failed: {1}", template, ex.Message);
            return null;
        }
    }

    /// <summary>Builds the quoted switch from a host→address map. Public for the test.</summary>
    /// <remarks>The value is quoted because each rule carries spaces (<c>MAP host addr</c>); unquoted,
    /// WebView2 cut the whole thing off at the first space (measured). An empty map is no switch.</remarks>
    internal static string? BuildRule(IReadOnlyList<(string Host, string Address)> map)
    {
        if (map.Count == 0)
            return null;

        var rules = string.Join(",", map.Select(m => $"MAP {m.Host} {m.Address}"));
        return $"--host-resolver-rules=\"{rules}\"";
    }

    private static async Task<IReadOnlyList<(string, string)>> ResolveAllAsync(string template)
    {
        using var cts = new CancellationTokenSource(Budget);
        using var http = new HttpClient { Timeout = Budget };
        http.DefaultRequestHeaders.Add("Accept", "application/dns-json");

        var results = await Task.WhenAll(Hosts.Select(host => ResolveOneAsync(http, template, host, cts.Token)));
        return results.Where(r => r is not null).Select(r => r!.Value).ToList();
    }

    private static async Task<(string, string)?> ResolveOneAsync(
        HttpClient http, string template, string host, CancellationToken ct)
    {
        try
        {
            var sep = template.Contains('?') ? '&' : '?';
            var doc = await http.GetFromJsonAsync<JsonElement>($"{template}{sep}name={host}&type=A", ct);
            if (!doc.TryGetProperty("Answer", out var answers) || answers.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var answer in answers.EnumerateArray())
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1
                    && answer.TryGetProperty("data", out var data)
                    && IPAddress.TryParse(data.GetString(), out var ip)
                    && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return (host, ip.ToString());

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
