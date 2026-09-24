using mTiles.Services.Phone;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// One self-signed bridge certificate, generated once per run and copied into every test that needs one.
/// </summary>
/// <remarks>
/// Minting an RSA key takes the better part of a second, and the phone tests used to mint one per test.
/// <see cref="SelfSignedCertificateSource"/> reuses a <c>bridge.pfx</c> it finds on disk when it names
/// every address asked for, so a test that copies this one in gets the real load path and no key
/// generation — and a test that asks for an address outside <see cref="Hosts"/> still gets a fresh one.
/// </remarks>
internal static class PhoneTestCertificate
{
    /// <summary>What the shared certificate names: loopback under both spellings, the address the
    /// bridge-manager tests move to, and the three a laptop's certificate tests ask about.</summary>
    public static readonly string[] Hosts =
        ["localhost", "127.0.0.1", "10.1.2.3", "192.168.1.20", "10.0.0.5", "pc.local"];

    private static readonly Lazy<(string Directory, PhoneCertificate Certificate)> Template = new(() =>
    {
        var directory = Path.Combine(TestTempRoot.Root, "phone-certificate-template");
        var certificate = new SelfSignedCertificateSource(directory).TryGet(Hosts);
        Assert.NotNull(certificate);
        return (directory, certificate);
    });

    /// <summary>The certificate as it came out of generation. Read it; do not dispose it.</summary>
    public static PhoneCertificate Generated => Template.Value.Certificate;

    /// <summary>Puts the shared certificate into <paramref name="directory"/>, where a
    /// <see cref="SelfSignedCertificateSource"/> pointed at it will find it.</summary>
    public static string CopyTo(string directory)
    {
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(Template.Value.Directory, "bridge.pfx"), Path.Combine(directory, "bridge.pfx"), overwrite: true);
        return directory;
    }
}

/// <summary>
/// The shared certificate as TLS material, for a class whose server only ever reads it.
/// </summary>
/// <remarks>What is deliberately <em>not</em> shared is the server, the pairing and the sink: tests revoke
/// pairings, fill the connection table and count cancellations, so isolation there is load-bearing.</remarks>
public sealed class PhoneCertificateFixture : IDisposable
{
    private readonly TempDirectory _directory = new("mtiles-phone-tests");

    internal PhoneTlsMaterial Tls { get; }

    public PhoneCertificateFixture()
    {
        PhoneTestCertificate.CopyTo(_directory.Path);
        var certificate = new SelfSignedCertificateSource(_directory.Path).TryGet(["localhost", "127.0.0.1"]);
        Assert.NotNull(certificate);
        Tls = new PhoneTlsMaterial([certificate]);
    }

    public void Dispose()
    {
        Tls.Dispose();                                       // and with it the certificate's key handle
        _directory.Dispose();
    }
}
