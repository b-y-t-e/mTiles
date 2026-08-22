using System.Security.Cryptography.X509Certificates;
using mTiles.Services.Phone;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The TLS material: what it covers, when it is regenerated, and which one a connection gets.
/// </summary>
/// <remarks>
/// Untested until a review pointed at it, and it is the part with the least forgiving failure mode: a
/// certificate that does not name the address the phone used produces a security warning the user cannot
/// act on, on the one page in this feature that must not look broken.
/// </remarks>
public sealed class PhoneCertificateTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mtiles-cert-tests-" + Guid.NewGuid().ToString("N"));

    private SelfSignedCertificateSource Source => new(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch { }
    }

    [Fact]
    public void A_generated_certificate_names_every_address_it_will_be_served_for()
    {
        var certificate = Source.TryGet(["192.168.1.20", "10.0.0.5", "pc.local"]);

        Assert.NotNull(certificate);
        Assert.False(certificate.Trusted);
        Assert.True(SelfSignedCertificateSource.Covers(
            certificate.Certificate, ["192.168.1.20", "10.0.0.5", "pc.local"]));
    }

    /// <summary>
    /// Every, not any. One covering three addresses out of four passes an "any" check and then fails on
    /// the fourth — in the browser, as a warning with no way forward.
    /// </summary>
    [Fact]
    public void Coverage_is_all_or_nothing()
    {
        var certificate = Source.TryGet(["192.168.1.20"])!;

        Assert.False(SelfSignedCertificateSource.Covers(
            certificate.Certificate, ["192.168.1.20", "10.0.0.5"]));
    }

    /// <summary>
    /// Kept on disk, because a new certificate every launch means a new browser warning every launch —
    /// and a user trained to dismiss those is a worse outcome than the warning.
    /// </summary>
    [Fact]
    public void The_same_addresses_get_the_same_certificate_back()
    {
        var first = Source.TryGet(["192.168.1.20"])!;
        var again = Source.TryGet(["192.168.1.20"])!;

        Assert.Equal(first.Certificate.Thumbprint, again.Certificate.Thumbprint);
    }

    /// <summary>A laptop changes networks; a certificate is only accepted for a host in its SANs.</summary>
    [Fact]
    public void A_new_address_forces_a_new_certificate()
    {
        var first = Source.TryGet(["192.168.1.20"])!;
        var afterMoving = Source.TryGet(["192.168.1.20", "10.0.0.5"])!;

        Assert.NotEqual(first.Certificate.Thumbprint, afterMoving.Certificate.Thumbprint);
        Assert.True(SelfSignedCertificateSource.Covers(
            afterMoving.Certificate, ["192.168.1.20", "10.0.0.5"]));
    }

    /// <summary>
    /// Backdated an hour, so a phone whose clock runs slightly behind does not reject a certificate minted
    /// seconds ago as "not yet valid" — which reads to the user as a broken feature.
    /// </summary>
    [Fact]
    public void A_fresh_certificate_is_already_valid()
    {
        var certificate = Source.TryGet(["192.168.1.20"])!;

        Assert.True(certificate.Certificate.NotBefore < DateTime.Now);
        Assert.True(certificate.Certificate.NotAfter > DateTime.Now.AddDays(365));
    }

    // ── choosing between several ────────────────────────────────────────────────────────────────────

    private static PhoneCertificate Fake(bool trusted, params string[] hosts) =>
        new(X509Certificate2.CreateFromPem(Pem.Certificate, Pem.Key), trusted, hosts);

    /// <summary>
    /// The bug this replaced: taking the first source that answered served the Tailscale certificate to a
    /// phone connecting over the LAN — a name mismatch, which is a *worse* warning than the self-signed
    /// one, while the panel promised there would be none.
    /// </summary>
    [Fact]
    public void A_connection_gets_the_certificate_that_names_it()
    {
        var trusted = Fake(true, "pc.tail1234.ts.net");
        var everything = Fake(false, "pc.tail1234.ts.net", "192.168.1.20");
        using var material = new PhoneTlsMaterial([trusted, everything]);

        Assert.True(material.IsTrustedFor("pc.tail1234.ts.net"));
        Assert.False(material.IsTrustedFor("192.168.1.20"));
    }

    [Fact]
    public void An_unknown_or_missing_name_gets_the_broadest_certificate()
    {
        var narrow = Fake(true, "pc.tail1234.ts.net");
        var broad = Fake(false, "192.168.1.20", "10.0.0.5", "pc.local");
        using var material = new PhoneTlsMaterial([narrow, broad]);

        // Asserted through the thumbprint rather than the count, so reordering the list — which is what
        // the old "last entry wins" rule silently depended on — cannot make this pass by accident.
        Assert.Equal(broad.Certificate.Thumbprint, material.Select(null).Thumbprint);
        Assert.Equal(broad.Certificate.Thumbprint, material.Select("something.else").Thumbprint);
    }

    [Fact]
    public void Nothing_is_trusted_when_no_certificate_names_the_host()
    {
        using var material = new PhoneTlsMaterial([Fake(false, "192.168.1.20")]);

        Assert.False(material.IsTrustedFor("10.0.0.5"));
    }

    /// <summary>A throwaway certificate, so the selection tests need no key generation.</summary>
    private static class Pem
    {
        public static string Certificate { get; }
        public static string Key { get; }

        static Pem()
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new CertificateRequest("CN=test", rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);

            using var built = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddYears(1));

            Certificate = built.ExportCertificatePem();
            Key = rsa.ExportPkcs8PrivateKeyPem();
        }
    }
}

/// <summary>
/// The firewall script, checked as text.
/// </summary>
/// <remarks>
/// It runs elevated and only on a machine where the feature is already failing, which makes it the least
/// likely thing here to be exercised by hand and the worst place for a quiet mistake. Reading it is the
/// only test available: actually running it would need administrator rights and would edit the firewall
/// of whoever is running the suite.
/// </remarks>
public class PhoneFirewallScriptTests
{
    private static string Script(string program = @"C:\Program Files\mTiles\mTiles.exe") =>
        WindowsFirewallGuide.BuildScript(program);

    /// <summary>
    /// The block rule is the real target: dismissing Windows' own prompt writes one, and it wins over
    /// anything added afterwards. Removing first is what makes the repair a repair.
    /// </summary>
    [Fact]
    public void It_removes_existing_inbound_rules_before_adding_one()
    {
        var script = Script();

        var removes = script.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal);
        var adds = script.IndexOf("New-NetFirewallRule", StringComparison.Ordinal);

        Assert.True(removes >= 0 && adds > removes);
        Assert.Contains("Get-NetFirewallApplicationFilter", script);
    }

    /// <summary>Scoped to this program, so it can neither read nor disturb anything else's rules.</summary>
    [Fact]
    public void It_is_scoped_to_this_executable()
    {
        Assert.Contains(@"C:\Program Files\mTiles\mTiles.exe", Script());
    }

    /// <summary>
    /// No port in the rule. The bridge falls back to a free port when the configured one is unavailable,
    /// and a rule naming one port would stop matching the moment that happened — costing a UAC prompt per
    /// launch to re-approve.
    /// </summary>
    [Fact]
    public void It_does_not_pin_the_rule_to_a_port()
    {
        Assert.DoesNotContain("-LocalPort", Script());
    }

    /// <summary>A bridge for the user's own Wi-Fi has no business listening on café networks.</summary>
    [Fact]
    public void It_opens_private_networks_only()
    {
        var script = Script();

        Assert.Contains("-Profile Private", script);
        Assert.DoesNotContain("Public", script);
        Assert.DoesNotContain("-Profile Any", script);
    }

    /// <summary>
    /// A path is user data as far as this is concerned — it can contain a quote — and the script is
    /// handed to PowerShell, where doubling is the only escape a single-quoted string has.
    /// </summary>
    [Fact]
    public void A_quote_in_the_path_cannot_break_out_of_the_string()
    {
        var script = WindowsFirewallGuide.BuildScript(@"C:\od'd\mTiles.exe");

        Assert.Contains(@"'C:\od''d\mTiles.exe'", script);
    }
}
