using System.Diagnostics;

namespace mTiles.Services;

/// <summary>
/// The <c>PATH</c> the user's own login shell builds, for a process started without one.
/// </summary>
/// <remarks>
/// <para><b>Unix only, and for the reason <see cref="BackgroundInstaller"/> needs it.</b> An application
/// launched from a desktop launcher inherits the session's <c>PATH</c>, not the one <c>.bashrc</c> or
/// <c>.zshrc</c> builds — and that is where nvm, volta, fnm and asdf put <c>npm</c> and <c>node</c>. An
/// install run in a tile had that for free, because the tile's shell read those files; run as a process
/// of our own it found no <c>npm</c>, and even an <c>npm</c> found elsewhere would fail on its
/// <c>#!/usr/bin/env node</c> shebang. So the shell is asked once, as an interactive login shell (nvm is
/// loaded from the rc file, which a non-interactive shell skips), and the answer is both searched and
/// handed to the child.</para>
/// <para>Asked once per session and never on Windows, where installers do not live behind a shell rc
/// file. Everything fails soft: no answer is <c>null</c>, and the caller keeps its own <c>PATH</c>.</para>
/// </remarks>
internal static class LoginShellPath
{
    private const string Marker = "__MTILES_PATH__";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly Lazy<string?> Cached = new(Read);

    /// <summary>The login shell's <c>PATH</c>, or null where there is none to ask.</summary>
    public static string? Value => Cached.Value;

    /// <summary>The login shell's <c>PATH</c> if it has already been read, without waiting for it; the
    /// first ask starts the read in the background. For the UI thread, where the read's ten seconds
    /// would freeze the window.</summary>
    public static string? ValueIfRead
    {
        get
        {
            if (Cached.IsValueCreated) return Cached.Value;
            StartReading();
            return null;
        }
    }

    /// <summary>Starts the read in the background without waiting for it — called at startup on Unix,
    /// so a <see cref="ValueIfRead"/> asked when Settings opens or a tile launches already has the answer
    /// rather than reporting a program on the login shell's <c>PATH</c> as off it.</summary>
    public static void StartReading()
    {
        if (!OperatingSystem.IsWindows() && !Cached.IsValueCreated)
            _ = ReadAsync();
    }

    /// <summary>Whether <see cref="ValueIfRead"/> already answers with the real value — always on
    /// Windows, where there is nothing to read.</summary>
    public static bool IsRead => OperatingSystem.IsWindows() || Cached.IsValueCreated;

    /// <summary>The read, off the calling thread; for a caller that can wait without freezing the
    /// window.</summary>
    public static Task<string?> ReadAsync() =>
        OperatingSystem.IsWindows() ? Task.FromResult<string?>(null) : Task.Run(() => Cached.Value);

    /// <summary>Where <paramref name="name"/> is on <paramref name="path"/>, or null.</summary>
    internal static string? Find(string name, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The <c>PATH</c> between the two markers in a shell's output, which is what survives an rc
    /// file that prints a greeting.</summary>
    internal static string? Parse(string output)
    {
        var start = output.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += Marker.Length;
        var end = output.IndexOf(Marker, start, StringComparison.Ordinal);
        return end < 0 ? null : output[start..end].Trim() is { Length: > 0 } path ? path : null;
    }

    private static string? Read()
    {
        if (OperatingSystem.IsWindows()) return null;
        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell)) return null;

        try
        {
            var info = new ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[] { "-l", "-i", "-c", $"printf '{Marker}%s{Marker}' \"$PATH\"" })
                info.ArgumentList.Add(argument);

            using var process = Process.Start(info);
            if (process is null) return null;
            process.StandardInput.Close();
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Deadline) || !output.Wait(Deadline))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return null;
            }
            return Parse(output.Result);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Could not read the login shell's PATH: {ex.Message}");
            return null;
        }
    }
}
