namespace mTiles.Services;

/// <summary>
/// What actually happens to a key or a password typed into Settings, in the words the screen uses.
/// </summary>
/// <remarks>
/// <para>One place, because the field that takes the secret and the sentence explaining it must not be
/// able to disagree: the API key box promised "stored encrypted on this machine" on every platform,
/// while <see cref="ProtectedStringConverter"/> has DPAPI only on Windows and writes the key verbatim
/// everywhere else — with nothing but a line in a log file to say so. A field that claims an encryption
/// it does not perform is worse than one that admits it, because it is what the user weighs the risk
/// against.</para>
/// <para>The other half of the answer is <see cref="PrivateFile"/>: where there is no encryption, the
/// file is at least owner-only. <see cref="Warning"/> says both, so the user knows the exposure is
/// "anyone who can read my account's files" and not "anyone on this machine".</para>
/// </remarks>
public static class SecretStorage
{
    /// <summary>True where a stored secret is encrypted at rest (DPAPI, Windows only).</summary>
    public static bool IsEncrypted => OperatingSystem.IsWindows();

    /// <summary>The hint in an empty key field.</summary>
    public static string KeyFieldHint => IsEncrypted
        ? "stored encrypted on this machine"
        : "stored as plain text — see the note below";

    /// <summary>What to say beside the field, or null where there is nothing to warn about.</summary>
    public static string? Warning => IsEncrypted
        ? null
        : "This platform has no key store mTiles can use, so keys and passwords are written to "
          + "settings.json as plain text. The file is readable only by your account.";

    /// <summary>Whether <see cref="Warning"/> has anything to show.</summary>
    public static bool HasWarning => Warning is not null;
}
