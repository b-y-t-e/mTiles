using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace mTiles.Services.Phone;

/// <summary>A TLS certificate for the bridge, and how much the browser will complain about it.</summary>
/// <param name="Certificate">Ready for Kestrel: carries its private key.</param>
/// <param name="Trusted">
/// True when a phone will open the page with no warning at all. False means the user must read and
/// dismiss a full-page security warning first — which works, but is a bad first thirty seconds.
/// </param>
/// <param name="Hosts">The names and addresses this certificate is valid for.</param>
internal sealed record PhoneCertificate(X509Certificate2 Certificate, bool Trusted, IReadOnlyList<string> Hosts);

/// <summary>
/// Somewhere a TLS certificate for the bridge can come from.
/// </summary>
/// <remarks>
/// An interface because the two ways of getting one are not variations of the same idea — one asks
/// another program, the other generates — and because the *next* one (a certificate the user points at,
/// a corporate CA, mkcert) should be an addition rather than another branch in a growing if.
/// <para>Sources are not alternatives: every one that answers is kept, because each certifies a different
/// subset of the addresses the single listening socket answers for, and TLS picks between them per
/// connection by the name the client asked for.</para>
/// </remarks>
internal interface IPhoneCertificateSource
{
    string Name { get; }

    /// <summary>
    /// A certificate covering as many of <paramref name="hosts"/> as this source can, or null.
    /// Must not throw: a source that cannot answer is normal, not exceptional.
    /// </summary>
    PhoneCertificate? TryGet(IReadOnlyList<string> hosts);
}

/// <summary>
/// Asks Tailscale for a real, publicly-trusted certificate for this machine's MagicDNS name.
/// </summary>
/// <remarks>
/// The only source that produces a page a phone opens without complaint, which matters more here than
/// anywhere else in this application: a browser hands out no microphone at all outside a secure context,
/// and "secure" for a self-signed certificate means "after the user has clicked through a warning that
/// tells them not to". Requires HTTPS to be enabled for the tailnet; when it is not, <c>tailscale cert</c>
/// fails and the self-signed source takes over — which is a worse first run, not a broken one.
/// </remarks>
internal sealed class TailscaleCertificateSource(ITailscaleCli? cli = null, string? directory = null)
    : IPhoneCertificateSource
{
    private readonly ITailscaleCli _cli = cli ?? new TailscaleCli();
    private readonly string _directory = directory ?? AppPaths.GetPhoneDirectory();

    public string Name => "tailscale";

    public PhoneCertificate? TryGet(IReadOnlyList<string> hosts)
    {
        // Only the MagicDNS name can be certified; Tailscale will not issue for a bare 100.x address.
        var host = hosts.FirstOrDefault(h => h.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase));
        if (host is null)
            return null;

        try
        {
            Directory.CreateDirectory(_directory);
            var certPath = Path.Combine(_directory, "tailscale.crt");
            var keyPath = Path.Combine(_directory, "tailscale.key");

            // Cheap to re-ask: tailscale renews in place and returns the cached certificate otherwise.
            if (!_cli.TryIssueCertificate(host, certPath, keyPath))
                return null;

            // Narrowed after the fact, and that is the best available here: tailscale creates this file
            // itself, so the window between its creation and this call belongs to tailscale and cannot be
            // closed from here. The self-signed key, which this application does create, has no such
            // window — see WritePrivateFile.
            SelfSignedCertificateSource.ProtectPrivateFile(keyPath);

            var certificate = X509Certificate2.CreateFromPemFile(certPath, keyPath);
            return new PhoneCertificate(ForServerUse(certificate), Trusted: true, Hosts: [host]);
        }
        catch (Exception ex)
        {
            Trace.TraceInformation("Tailscale certificate unavailable: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Makes a PEM-loaded certificate usable as a TLS server certificate on Windows.
    /// </summary>
    /// <remarks>
    /// <c>CreateFromPemFile</c> produces a certificate whose private key SChannel refuses to use, and the
    /// failure surfaces during the handshake as an opaque error rather than at load time. Round-tripping
    /// through PKCS#12 rebuilds it as a key SChannel accepts. A platform quirk, not a design choice.
    /// </remarks>
    internal static X509Certificate2 ForServerUse(X509Certificate2 certificate)
    {
        if (!OperatingSystem.IsWindows())
            return certificate;

        // The original is disposed here, not by the caller: it is a stepping stone that exists only to be
        // re-imported, and it holds a key handle the operating system keeps until it is released. Leaving
        // it to the garbage collector leaked one per bridge start, and the bridge restarts whenever the
        // machine changes network. Only when a replacement was actually made — on Linux the same instance
        // comes back, and disposing that would hand the server a certificate with no key.
        using (certificate)
        {
            var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
            return X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet);
        }
    }
}

/// <summary>
/// Generates and keeps a certificate covering every address the bridge might be reached at.
/// </summary>
/// <remarks>
/// The fallback, and the only thing available on a LAN: no public authority will ever certify
/// <c>192.168.1.20</c>, so a private address and an unwarned browser are mutually exclusive. The phone
/// shows a warning once, the user accepts it, and the microphone works from then on.
/// <para>Regenerated when the set of addresses changes, because a certificate is only accepted for a host
/// listed in its SANs — and on a laptop that set changes with every network joined. Kept on disk so that
/// accepting the warning once survives a restart of the application: a new certificate every launch would
/// mean a new warning every launch, and a user trained to dismiss certificate warnings is a worse outcome
/// than the warning itself.</para>
/// </remarks>
internal sealed class SelfSignedCertificateSource(string? directory = null) : IPhoneCertificateSource
{
    /// <summary>
    /// How long a generated certificate is valid for.
    /// </summary>
    /// <remarks>
    /// Under Apple's 398-day ceiling, deliberately. Safari rejects a TLS certificate valid for longer than
    /// that outright rather than offering the usual "accept the risk" route, and Chrome follows the same
    /// rule — so 825 days, which is the older CA/Browser Forum figure, bought nothing and risked turning a
    /// warning the user can click through into a wall they cannot. The bridge regenerates on a change of
    /// address anyway, so a shorter life costs nothing.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(397);

    /// <summary>Regenerate before it expires rather than on the day, so the bridge never serves an expired one.</summary>
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly string _directory = directory ?? AppPaths.GetPhoneDirectory();

    public string Name => "self-signed";

    public PhoneCertificate? TryGet(IReadOnlyList<string> hosts)
    {
        // Anything a certificate cannot name is dropped here rather than allowed to throw halfway through
        // building one — a single bad entry otherwise fails the whole certificate, and with it the bridge.
        hosts = [.. hosts.Where(PhoneHostName.IsUsable)];

        if (hosts.Count == 0)
            return null;

        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, "bridge.pfx");

            if (TryLoadUsable(path, hosts) is { } existing)
                return new PhoneCertificate(existing, Trusted: false, hosts);

            // The names the old certificate carried are kept. A certificate is only accepted for a host in
            // its SANs, so a new network forces a new one — and every new one is a security warning the
            // user has to dismiss again. Carrying the previous names forward means the set converges: the
            // second visit to a network already covered reuses the certificate that was accepted there,
            // instead of minting a third that covers only today.
            hosts = [.. hosts.Concat(PreviousHosts(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxSubjectNames)];

            var created = Create(hosts);
            WritePrivateFile(path, created.Export(X509ContentType.Pkcs12));

            // Reloaded rather than used directly, for the same SChannel reason as above: a certificate
            // built in memory and one loaded from PKCS#12 do not have interchangeable key handles.
            return new PhoneCertificate(
                TailscaleCertificateSource.ForServerUse(created), Trusted: false, hosts);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("The phone bridge certificate could not be created: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// How many names one certificate may carry.
    /// </summary>
    /// <remarks>
    /// Carrying old names forward has to stop somewhere: a laptop that joins a new network every day would
    /// otherwise grow a certificate without bound. Thirty-two covers years of ordinary use.
    /// </remarks>
    private const int MaxSubjectNames = 32;

    /// <summary>The names the certificate on disk was issued for, or nothing.</summary>
    private static IEnumerable<string> PreviousHosts(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            using var loaded = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            return
            [
                .. loaded.Extensions
                    .OfType<X509SubjectAlternativeNameExtension>()
                    .SelectMany(extension => extension.EnumerateDnsNames()
                        .Concat(extension.EnumerateIPAddresses().Select(ip => ip.ToString())))
            ];
        }
        catch
        {
            return [];   // unreadable, or from another machine: start from what we have
        }
    }

    private static X509Certificate2? TryLoadUsable(string path, IReadOnlyList<string> hosts)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var loaded = X509CertificateLoader.LoadPkcs12FromFile(
                path, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

            // Both rejections dispose it. This runs on every bridge start, and the two paths that say
            // "no" — expiring soon, and not naming the addresses we now have — are exactly the ones that
            // repeat: a laptop moving between networks takes the second one every time it moves back.
            if (loaded.NotAfter - RenewBefore < DateTime.Now || !Covers(loaded, hosts))
            {
                loaded.Dispose();
                return null;
            }

            return loaded;
        }
        catch
        {
            return null;   // unreadable or from another machine — replaced rather than reported
        }
    }

    /// <summary>
    /// Whether the stored certificate names every host we are about to serve on.
    /// </summary>
    /// <remarks>
    /// Every, not any. A certificate covering three of four addresses passes a check for "any" and then
    /// fails on the fourth, in the browser, as a warning the user cannot act on.
    /// </remarks>
    internal static bool Covers(X509Certificate2 certificate, IReadOnlyList<string> hosts)
    {
        var names = certificate.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(extension => extension.EnumerateDnsNames()
                .Concat(extension.EnumerateIPAddresses().Select(ip => ip.ToString())))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return hosts.All(names.Contains);
    }

    private static X509Certificate2 Create(IReadOnlyList<string> hosts)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            new X500DistinguishedName("CN=mTiles phone bridge"), key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var alternativeNames = new SubjectAlternativeNameBuilder();
        foreach (var host in hosts)
        {
            if (IPAddress.TryParse(host, out var address))
                alternativeNames.AddIpAddress(address);
            else
                alternativeNames.AddDnsName(host);
        }
        request.CertificateExtensions.Add(alternativeNames.Build());

        // Backdated an hour so a phone whose clock runs slightly behind does not reject a certificate
        // minted seconds ago as "not yet valid" — which reads to the user as a broken feature.
        var from = DateTimeOffset.UtcNow.AddHours(-1);
        return request.CreateSelfSigned(from, from + Lifetime);
    }

    /// <summary>
    /// Writes a private key file that is never, even briefly, readable by anyone else.
    /// </summary>
    /// <remarks>
    /// Writing and then narrowing leaves a window: between the two calls the file exists, holds the key,
    /// and carries whatever the user's umask says — which on a good many systems is group-readable. The
    /// window is short, but it is a private key, and closing it costs one <see cref="FileStreamOptions"/>.
    /// <para>The mode only applies to a file being <em>created</em>, so the narrowing afterwards is kept
    /// for the case where one was already there with wider permissions.</para>
    /// </remarks>
    private static void WritePrivateFile(string path, byte[] contents)
    {
        if (OperatingSystem.IsWindows())
        {
            // Nothing to set: the file inherits the ACL of %APPDATA%, which grants the owner and
            // administrators and nobody else.
            File.WriteAllBytes(path, contents);
            return;
        }

        using (var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        }))
        {
            file.Write(contents);
        }

        ProtectPrivateFile(path);
    }

    /// <summary>
    /// Takes the private key file out of reach of other users on this machine.
    /// </summary>
    /// <remarks>
    /// A no-op on Windows, and deliberately so rather than by omission: the file lives under
    /// <c>%APPDATA%</c>, whose ACL already grants the owning user and administrators only, and inherits
    /// it. Writing an explicit ACL there would replace a correct inherited one with a hand-made one, for
    /// no gain. On Unix the default is <c>umask</c>-dependent and routinely group- or world-readable, so
    /// there the narrowing is real.
    /// </remarks>
    internal static void ProtectPrivateFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not restrict permissions on '{0}': {1}", path, ex.Message);
        }
    }
}

/// <summary>Takes the best certificate any source can produce.</summary>
internal sealed class PhoneCertificateProvider(IReadOnlyList<IPhoneCertificateSource> sources)
{
    /// <summary>Tailscale first: it is the only source whose certificate a browser already trusts.</summary>
    public static PhoneCertificateProvider CreateDefault() =>
        new([new TailscaleCertificateSource(), new SelfSignedCertificateSource()]);

    /// <summary>
    /// Everything the sources can produce, for TLS to choose between per connection. Null only when every
    /// source failed, which means the bridge cannot start — reported rather than worked around, because
    /// plain HTTP would start and then be refused a microphone by the phone, which is far harder to
    /// diagnose than a server that says why it did not run.
    /// </summary>
    /// <remarks>
    /// All of them, not the first that answered. The sources are not alternatives: each certifies a
    /// different subset of the addresses this one server is listening on at the same moment.
    /// </remarks>
    public PhoneTlsMaterial? Resolve(IReadOnlyList<string> hosts)
    {
        var found = new List<PhoneCertificate>();

        foreach (var source in sources)
        {
            try
            {
                if (source.TryGet(hosts) is { } certificate)
                    found.Add(certificate);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning("Certificate source '{0}' failed: {1}", source.Name, ex.Message);
            }
        }

        return found.Count > 0 ? new PhoneTlsMaterial(found) : null;
    }
}

/// <summary>
/// Every certificate the bridge holds, and the rule for picking one per connection.
/// </summary>
/// <remarks>
/// <b>One certificate is not enough, and taking the first source that answered was a bug.</b> Tailscale
/// can only certify the MagicDNS name, but the server listens on every address at once — so a machine
/// with Tailscale running served its <c>.ts.net</c> certificate to a phone connecting over the LAN, which
/// is a name mismatch: a *worse* warning than the self-signed one it replaced, and the panel cheerfully
/// said there would be no warning at all. Both certificates are kept and TLS picks between them by the
/// name the client asked for.
/// <para>Every browser and phone this feature targets sends SNI. A client that does not gets the
/// self-signed certificate, which covers every address and is the honest default.</para>
/// </remarks>
internal sealed class PhoneTlsMaterial(IReadOnlyList<PhoneCertificate> certificates) : IDisposable
{
    /// <summary>Whether there is anything to serve. An empty set is a bridge that must not start.</summary>
    public bool Any => certificates.Count > 0;

    /// <summary>The certificate to serve for <paramref name="host"/>, as asked for by SNI.</summary>
    public X509Certificate2 Select(string? host)
    {
        if (!string.IsNullOrEmpty(host) && Covering(host) is { } match)
            return match.Certificate;

        // The broadest one, worked out rather than assumed. Taking the last entry encoded the source
        // order as a silent invariant: reordering the list in PhoneCertificateProvider, or adding a
        // narrow third source at the end, would have quietly started serving a certificate valid for one
        // name to every client that did not send SNI.
        return certificates.MaxBy(certificate => certificate.Hosts.Count)!.Certificate;
    }

    /// <summary>Whether a browser reaching this machine at <paramref name="host"/> sees no warning.</summary>
    /// <remarks>
    /// Asked per host rather than once for the whole bridge, because with two certificates in play the
    /// answer genuinely differs between the two QR codes on screen — and that sentence under the code is
    /// the user's only warning that a full-page security interstitial is about to appear.
    /// </remarks>
    public bool IsTrustedFor(string host) => Covering(host) is { Trusted: true };

    private PhoneCertificate? Covering(string host) =>
        certificates.FirstOrDefault(certificate =>
            certificate.Trusted && certificate.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        ?? certificates.FirstOrDefault(certificate =>
            certificate.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase));

    public void Dispose()
    {
        foreach (var certificate in certificates)
        {
            try { certificate.Certificate.Dispose(); }
            catch (Exception ex) { Trace.TraceWarning("Releasing a certificate failed: {0}", ex.Message); }
        }
    }
}
