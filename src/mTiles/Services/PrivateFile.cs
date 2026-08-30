using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace mTiles.Services;

/// <summary>
/// Writes a file only the owner can read.
/// </summary>
/// <remarks>
/// <para>On Windows there is nothing to do: everything written this way lives under <c>%APPDATA%</c>,
/// whose ACL already grants the owning user and administrators only, and a file inherits it. Writing an
/// explicit ACL there would replace a correct inherited one with a hand-made one, for no gain.</para>
/// <para>On Unix the default is <c>umask</c>-dependent and routinely group- or world-readable, and that
/// matters more here than it looks: <see cref="ProtectedStringConverter"/> has no DPAPI to call outside
/// Windows, so the API keys and database passwords in <c>settings.json</c> are in it as plain text. The
/// mode is set <em>as the file is created</em> rather than afterwards, because narrowing after the write
/// leaves a window in which the secrets exist at whatever the umask said.</para>
/// </remarks>
public static class PrivateFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, contents);
            return;
        }

        using (var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerOnly,
        }))
        {
            file.Write(contents);
        }

        // The create mode only applies to a file being created, so this covers the one that was already
        // there with wider permissions.
        Protect(path);
    }

    /// <summary>Takes an existing file out of reach of other users on this machine.</summary>
    /// <remarks>Best effort: a mode that cannot be set is worth a line in the log and nothing more —
    /// the alternative is losing the write that carries the user's settings.</remarks>
    public static void Protect(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, OwnerOnly);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not restrict permissions on '{0}': {1}", path, ex.Message);
        }
    }
}
