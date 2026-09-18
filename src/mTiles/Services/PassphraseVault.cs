using System.Security.Cryptography;
using System.Text;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Secrets across a passphrase, for the two exports that carry them.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> Everything encrypted at rest here is DPAPI, which is bound to one
/// user on one machine: copied anywhere else it decrypts to nothing. So an export either blanks the
/// secrets — which is what both exports did, and means the person on the other end retypes a dozen
/// passwords — or re-encrypts them under something that travels. A passphrase travels, by a route the
/// two people choose (and the file does not carry it, which is the whole point: the file can go through
/// the channel everything else goes through, and the passphrase through a different one).</para>
/// <para><b>PBKDF2-SHA256 into AES-256-GCM.</b> GCM because a wrong key must fail rather than produce
/// plausible rubbish that is then saved as somebody's password, and PBKDF2 because it is in the box —
/// <c>System.Security.Cryptography</c> ships it on every platform this application runs on, with no
/// package to add and nothing native to verify in <c>bin/</c>. The cost is written into the file
/// (<see cref="ExportProtection.Iterations"/>) rather than assumed, so raising it later does not orphan
/// the files already written.</para>
/// <para>Pure and self-contained: it holds no state, reads no settings, and every failure is an answer
/// rather than an exception, because the caller is a dialog and a wrong passphrase is the ordinary case
/// rather than a fault.</para>
/// </remarks>
public static class PassphraseVault
{
    /// <summary>What a new export is written with. Raising it does not break older files.</summary>
    public const int DefaultIterations = 310_000;

    /// <summary>The most a file may ask for; above it the file is refused, not waited on.</summary>
    /// <remarks>The count is read out of the file, and the file is somebody else's — hand-edited or
    /// hostile, the trust model of this whole feature. A count near <see cref="int.MaxValue"/> is hours
    /// of synchronous derivation on the thread that asked, which is the UI thread for both imports, and
    /// a legitimate count never comes near it: new exports sit three orders of magnitude below.</remarks>
    public const int MaxIterations = 10_000_000;

    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int KeyBytes = 32;

    /// <summary>The plaintext behind <see cref="ExportProtection.Check"/>.</summary>
    private const string CheckPhrase = "mtiles-export-v1";

    /// <summary>The header for a file whose secrets travel, so the user is told before it is written.</summary>
    public const string PassphraseWarning =
        "The passwords and API keys in this file are encrypted with the passphrase you type here. "
        + "Send the passphrase by a different route than the file itself, and remember that anyone who "
        + "has both has every credential in it.";

    /// <summary>Makes a protection block for a new file, and the key to encrypt it with.</summary>
    public static (ExportProtection Protection, byte[] Key) Create(string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(passphrase, salt, DefaultIterations);
        var protection = new ExportProtection
        {
            Algorithm = ExportProtection.Pbkdf2AesGcm,
            Iterations = DefaultIterations,
            Salt = Convert.ToBase64String(salt),
        };
        protection.Check = Encrypt(CheckPhrase, key);
        return (protection, key);
    }

    /// <summary>
    /// The key for reading <paramref name="protection"/>, or null with a sentence saying why not.
    /// </summary>
    /// <remarks>The wrong passphrase and a file this build cannot read are different answers, and both
    /// are shown to the user as they are: one is retyped, the other is not.</remarks>
    public static byte[]? Open(ExportProtection protection, string passphrase, out string problem)
    {
        problem = "";

        if (!string.Equals(protection.Algorithm, ExportProtection.Pbkdf2AesGcm, StringComparison.Ordinal))
        {
            problem = $"This file is protected with \"{protection.Algorithm}\", which this version "
                      + "cannot read.";
            return null;
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(protection.Salt);
        }
        catch
        {
            problem = "This file's protection header is damaged.";
            return null;
        }

        if (salt.Length == 0 || protection.Iterations <= 0)
        {
            problem = "This file's protection header is damaged.";
            return null;
        }

        if (protection.Iterations > MaxIterations)
        {
            problem = $"This file asks for {protection.Iterations} hashing rounds — more than this "
                      + "version will spend on opening it.";
            return null;
        }

        var key = DeriveKey(passphrase, salt, protection.Iterations);
        if (Decrypt(protection.Check, key) != CheckPhrase)
        {
            problem = "That passphrase does not open this file.";
            return null;
        }

        return key;
    }

    /// <summary>Base64 of nonce ‖ ciphertext ‖ tag, or an empty string for an empty secret.</summary>
    /// <remarks>An empty secret stays empty rather than becoming a ciphertext of nothing: a provider
    /// instance with no key configured has to arrive on the other side with no key configured, not with
    /// a blank that had to be decrypted to be recognised as one.</remarks>
    public static string Encrypt(string plainText, byte[] key)
    {
        if (plainText.Length == 0) return "";

        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plain = Encoding.UTF8.GetBytes(plainText);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, cipher, tag);

        var packed = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, packed, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, packed, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, packed, nonce.Length + cipher.Length, tag.Length);
        return Convert.ToBase64String(packed);
    }

    /// <summary>What <see cref="Encrypt"/> wrote, or null when the key or the bytes are wrong.</summary>
    public static string? Decrypt(string encoded, byte[] key)
    {
        if (encoded.Length == 0) return "";

        try
        {
            var packed = Convert.FromBase64String(encoded);
            if (packed.Length < NonceBytes + TagBytes) return null;

            var nonce = packed.AsSpan(0, NonceBytes);
            var cipher = packed.AsSpan(NonceBytes, packed.Length - NonceBytes - TagBytes);
            var tag = packed.AsSpan(packed.Length - TagBytes, TagBytes);
            var plain = new byte[cipher.Length];

            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations,
            HashAlgorithmName.SHA256, KeyBytes);
}
