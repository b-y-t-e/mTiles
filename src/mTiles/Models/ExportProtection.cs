namespace mTiles.Models;

/// <summary>
/// How the secrets in an exported file are protected, written into the file beside them.
/// </summary>
/// <remarks>
/// <para>Shared by both exports — the settings file and the manual-connections file — because they are
/// one question asked twice: a secret leaving this machine has to stop being DPAPI (bound to one user on
/// one machine, so it decrypts to nothing anywhere else) and start being something the person on the
/// other end can open with a passphrase agreed by another route.</para>
/// <para>The cost parameters are <em>in the file</em> rather than assumed by the reader. They are the one
/// thing that has to be raised over time, and a file written today has to go on opening after they are.
/// </para>
/// </remarks>
public sealed class ExportProtection
{
    /// <summary>The only algorithm this version knows: PBKDF2-SHA256 into AES-256-GCM.</summary>
    public const string Pbkdf2AesGcm = "pbkdf2-sha256/aes-256-gcm";

    public string Algorithm { get; set; } = Pbkdf2AesGcm;
    public int Iterations { get; set; }

    /// <summary>Base64. One per file — a single derivation answers every secret in it.</summary>
    public string Salt { get; set; } = "";

    /// <summary>
    /// Base64 of a known plaintext encrypted under the same key, so a wrong passphrase can be named as a
    /// wrong passphrase.
    /// </summary>
    /// <remarks>Without it the only symptom is every secret failing to decrypt, which reads as a corrupt
    /// file — and the user's next move is then to ask for a new export rather than to retype the
    /// passphrase.</remarks>
    public string Check { get; set; } = "";
}
