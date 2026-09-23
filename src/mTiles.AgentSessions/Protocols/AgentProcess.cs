using System.Diagnostics;
using System.Text;

namespace mTiles.AgentSessions.Protocols;

/// <summary>
/// An agent's process as three lines of text: what it writes, what it complains about, and when it ends.
/// </summary>
/// <remarks>
/// <para>Every stdio protocol here — Claude Code's stream-json, codex's JSON-RPC, ACP, pi's RPC, agy's
/// step updates — is one JSON value per line, so this is the one place a process is started and read.
/// </para>
/// <para><b>UTF-8 without a byte order mark, in both directions, set explicitly.</b> Measured
/// (agy 1.1.26): a BOM in front of the first line is refused with <c>invalid character '﻿'</c>, and
/// the encoding a redirected stdin gets by default on Windows is the console's, which is not UTF-8.</para>
/// <para>Standard error is kept as a bounded tail rather than thrown away, because when a process dies
/// before it says anything in JSON, those last lines are the only account of why.</para>
/// </remarks>
public sealed class AgentProcess : IAsyncDisposable
{
    private const int StderrTail = 8 * 1024;

    private readonly Process _process;
    private readonly int _processId;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;
    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private AgentProcess(Process process, Action<string> onLine)
    {
        _process = process;
        _processId = process.Id;
        _stdoutPump = Task.Run(() => PumpAsync(process.StandardOutput, onLine));
        _stderrPump = Task.Run(() => PumpAsync(process.StandardError, AppendStderr));
        _ = Task.Run(WatchExitAsync);
    }

    /// <summary>Completes when the process has ended and everything it wrote has been read, with its exit
    /// code, or null when there was none to read.</summary>
    public Task<int?> Exited => _exited.Task;

    /// <summary>The last few kilobytes the process wrote to standard error.</summary>
    public string StderrText
    {
        get
        {
            lock (_stderr) return _stderr.ToString();
        }
    }

    /// <summary>The child's process id while it is running, and null once it has ended.</summary>
    /// <remarks>Read from the handle once, at the start: <see cref="Process.Dispose"/> clears the id it
    /// cached, so asking afterwards throws rather than answering. Null after the exit because the number
    /// belongs to whoever the operating system gives it to next.</remarks>
    public int? ProcessId => _exited.Task.IsCompleted ? null : _processId;

    /// <summary>Whether the process ended because it was told to — its exit code then says nothing about
    /// the agent, since a process killed on the way out reports -1.</summary>
    public bool StoppedByUs => Volatile.Read(ref _disposed) == 1;

    /// <summary>The last few lines of a text, for putting a process's complaint in front of a person.</summary>
    public static string Tail(string text, int lines = 12) =>
        string.Join('\n', text.Trim().Split('\n').TakeLast(lines));

    /// <summary>Starts the process. Throws when it cannot be started at all — a missing binary is the
    /// caller's to put into words.</summary>
    /// <param name="onLine">Called once per non-empty line of standard output, in order, on a background
    /// thread.</param>
    public static AgentProcess Start(ProcessStartInfo psi, Action<string> onLine)
    {
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        psi.StandardInputEncoding = utf8;
        psi.StandardOutputEncoding = utf8;
        psi.StandardErrorEncoding = utf8;

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"{psi.FileName} could not be started.");
        return new AgentProcess(process, onLine);
    }

    /// <summary>Writes one line. Lines from concurrent callers never interleave.</summary>
    /// <remarks>A line written while or after the process is disposed is dropped rather than thrown: the
    /// callers answering an abandoned approval are released by the very disposal they race.</remarks>
    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (StoppedByUs) return;
        await _writeGate.WaitAsync(ct);
        try
        {
            if (StoppedByUs || _process.HasExited) return;
            await _process.StandardInput.WriteAsync(line.AsMemory(), ct);
            await _process.StandardInput.WriteAsync("\n".AsMemory(), ct);
            await _process.StandardInput.FlushAsync(ct);
        }
        catch (IOException)
        {
            // The pipe closed under us: the process is ending, and Exited says so.
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Disposed between the check and the write: the handle is gone, and so is the process.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Closes standard input — for a CLI that treats the end of its input as the end of the
    /// session.</summary>
    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Ends the process: its input is closed, it is given a moment, and then it is killed with
    /// everything it started.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        CloseInput();
        try
        {
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        try
        {
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
        }

        // The write gate is deliberately not disposed: a writer may still be waiting on it, and a
        // SemaphoreSlim whose wait handle was never asked for holds nothing to release.
        _process.Dispose();
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    onLine(line);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"[AgentSessions] A line handler failed: {ex}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void AppendStderr(string line)
    {
        lock (_stderr)
        {
            _stderr.AppendLine(line);
            if (_stderr.Length > StderrTail) _stderr.Remove(0, _stderr.Length - StderrTail);
        }
    }

    private async Task WatchExitAsync()
    {
        int? code = null;
        try
        {
            await _process.WaitForExitAsync();
            await Task.WhenAll(_stdoutPump, _stderrPump).WaitAsync(TimeSpan.FromSeconds(5));
            code = _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
        }

        _exited.TrySetResult(code);
    }
}
