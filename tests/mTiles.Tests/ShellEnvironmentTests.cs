using System.Text;
using mTiles.Services.Activity;
using Terminal.Pty;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The one half of the environment story a dictionary of overrides could not do until now: removing a
/// variable the child would otherwise inherit.
/// </summary>
/// <remarks>
/// <para>Deliberately against a <b>real</b> pseudo-terminal and a real shell. Every other launch test
/// here runs on <see cref="FakePty"/>, which is right when what is under test is this application's own
/// decisions — but "the variable is not in the child" is a claim about somebody else's process
/// creation, and a fake that reports what it was handed would assert only that we handed it over. The
/// misconfiguration this exists to prevent — an agent instance authenticating through one credential on
/// a machine that exports another — is invisible to any test that does not look at a child.</para>
/// <para>Both directions are asserted, because a probe that reads empty proves nothing on its own: it
/// reads empty just as well when the shell never ran, when the marker was swallowed by the terminal, or
/// when the variable was never set in the parent to begin with.</para>
/// </remarks>
public class ShellEnvironmentTests
{
    /// <summary>Long enough not to collide with anything on a developer's machine, short enough to
    /// survive an 80-column terminal without being wrapped in the middle.</summary>
    private const string ProbeName = "MTILES_ENV_PROBE";

    private const string ParentValue = "from-parent";

    /// <summary>The console host asks for the terminal's device attributes as it starts and holds the
    /// child's output back until it is answered or three seconds pass; a terminal answers, so this does too.
    /// </summary>
    private const string AttributesQuery = "\u001b[c";

    private static readonly byte[] AttributesReply = "\u001b[?1;0c"u8.ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("from-mtiles")]
    public async Task An_override_reaches_the_child_and_a_null_one_removes_the_inherited_variable(string? overrideValue)
    {
        var probe = await ReadProbeInChild(overrideValue);

        // cmd leaves an unset variable's reference as it was written; sh expands it to nothing.
        var unset = OperatingSystem.IsWindows() ? $"%{ProbeName}%" : string.Empty;
        Assert.Equal(overrideValue ?? unset, probe);
    }

    /// <summary>
    /// What a child shell sees in <see cref="ProbeName"/> when the parent exports
    /// <see cref="ParentValue"/> and the launch overrides it with <paramref name="overrideValue"/>.
    /// </summary>
    private static async Task<string> ReadProbeInChild(string? overrideValue)
    {
        var previous = Environment.GetEnvironmentVariable(ProbeName);
        Environment.SetEnvironmentVariable(ProbeName, ParentValue);
        try
        {
            var (command, arguments) = ProbeCommand();
            using var pty = PtyConnection.Start(new PtyOptions
            {
                Command = command,
                Arguments = arguments,
                Environment = new Dictionary<string, string?> { [ProbeName] = overrideValue },
            });

            return ExtractProbe(await ReadUntilProbe(pty));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProbeName, previous);
        }
    }

    /// <summary>
    /// A shell run non-interactively, printing the probe between two markers.
    /// </summary>
    /// <remarks>Not routed through <c>ShellTerminalCatalog</c>: printing a variable is not something
    /// <c>IShellTerminal</c> knows how to do. <c>cmd.exe</c> rather than PowerShell on Windows because it
    /// starts in milliseconds rather than seconds, and the claim is about the process environment the
    /// pseudo-terminal hands any child, not about PowerShell.</remarks>
    private static (string Command, string[] Arguments) ProbeCommand() =>
        OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/c", $"echo [{{%{ProbeName}%}}]"])
            : ("/bin/sh", ["-c", $"printf '[{{%s}}]\\n' \"${ProbeName}\""]);

    /// <summary>Everything the child wrote, read until the closing marker has arrived and the child has
    /// exited — waited on, never slept through.</summary>
    /// <remarks>
    /// <para>A pseudo-terminal is owned by the parent, not by the child: ConPTY keeps the pipe open after
    /// the child is gone, so a read that stops at end-of-stream never returns. The marker is what says
    /// the line has arrived; the connection is then closed here so the pump sees the end and stops.</para>
    /// <para>Disposing here and again in the caller's <c>using</c> is deliberate — the second one is
    /// what covers the paths that throw before this is reached.</para>
    /// </remarks>
    private static async Task<string> ReadUntilProbe(IPtyConnection pty)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var text = new StringBuilder();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pump = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = pty.Output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    lock (text)
                    {
                        var chunk = Encoding.UTF8.GetString(buffer, 0, read);
                        text.Append(chunk);
                        if (chunk.Contains(AttributesQuery, StringComparison.Ordinal)) pty.Write(AttributesReply);
                        if (Visible(text.ToString()).Contains("}]", StringComparison.Ordinal)) closed.TrySetResult();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Closing the connection is how this read is ended.
            }
            catch (IOException)
            {
                // The same ending on POSIX: reading a master pty whose child has gone is EIO.
            }
            closed.TrySetResult();
        }, CancellationToken.None);

        await closed.Task.WaitAsync(timeout.Token);
        await pty.WaitForExitAsync(timeout.Token);
        pty.Dispose();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        lock (text) return text.ToString();
    }

    /// <summary>The output with its escape sequences taken out — a window title can carry the command
    /// line, markers and all, before the command has run.</summary>
    private static string Visible(string output) => AnsiText.Strip(output);

    /// <summary>
    /// What stood between the markers, with whatever the terminal wrapped or coloured around it taken
    /// out.
    /// </summary>
    /// <remarks>A pseudo-terminal is not a pipe: the output carries escape sequences and may break a
    /// line at the window's width, so the probe is read between two markers.</remarks>
    private static string ExtractProbe(string output)
    {
        var visible = Visible(output);
        int start = visible.IndexOf("[{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The child printed no probe marker. It wrote: {output}");

        int end = visible.IndexOf("}]", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The child's probe marker was not closed. It wrote: {output}");

        return new string([.. visible[(start + 2)..end].Where(c => !char.IsControl(c))]);
    }
}
