using System.Text;
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

    [Fact]
    public async Task NullOverrideRemovesAnInheritedVariable()
    {
        var probe = await ReadProbeInChild(overrideValue: null);

        Assert.Equal(string.Empty, probe);
    }

    [Fact]
    public async Task ValueOverrideReplacesAnInheritedVariable()
    {
        var probe = await ReadProbeInChild(overrideValue: "from-mtiles");

        Assert.Equal("from-mtiles", probe);
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

            var output = await ReadToExit(pty);
            return ExtractProbe(output);
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
    /// <c>IShellTerminal</c> knows how to do, and giving it a member for the sake of one test would put
    /// a shell feature in the product to serve the test suite. The two shells named here are the ones
    /// every machine this runs on has.</remarks>
    private static (string Command, string[] Arguments) ProbeCommand() =>
        OperatingSystem.IsWindows()
            ? ("powershell.exe",
               ["-NoProfile", "-NonInteractive", "-Command", $"Write-Output \"<{{$env:{ProbeName}}}>\""])
            : ("/bin/sh", ["-c", $"printf '<{{%s}}>\\n' \"${ProbeName}\""]);

    /// <summary>Everything the child wrote, read until the pseudo-terminal is closed.</summary>
    /// <remarks>
    /// <para>The exit is not the end of the output. A pseudo-terminal is owned by the parent, not by the
    /// child: ConPTY keeps the pipe open after the child is gone and goes on writing the console's own
    /// repaint into it, so a read that stops at end-of-stream never returns and one that stops at the
    /// exit event can miss the line written just before it. Hence exit, then a drain window, then
    /// closing the connection ourselves so the pump sees end-of-stream and stops.</para>
    /// <para>Disposing here and again in the caller's <c>using</c> is deliberate — the second one is
    /// what covers the paths that throw before this is reached.</para>
    /// </remarks>
    private static async Task<string> ReadToExit(IPtyConnection pty)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var text = new StringBuilder();

        var pump = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = pty.Output.Read(buffer, 0, buffer.Length)) > 0)
                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (ObjectDisposedException)
            {
                // Closing the connection is how this read is ended; a blocked one is torn out of the
                // pipe rather than told about it, which arrives here and not as end-of-stream.
            }
        }, CancellationToken.None);

        await pty.WaitForExitAsync(timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        pty.Dispose();
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        return text.ToString();
    }

    /// <summary>
    /// What stood between the markers, with whatever the terminal wrapped or coloured around it taken
    /// out.
    /// </summary>
    /// <remarks>A pseudo-terminal is not a pipe: the output carries escape sequences and may break a
    /// line at the window's width, so the probe is read between two markers and stripped of everything
    /// a terminal is entitled to add rather than compared to the whole of what came back.</remarks>
    private static string ExtractProbe(string output)
    {
        int start = output.IndexOf("<{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"The child printed no probe marker. It wrote: {output}");

        int end = output.IndexOf("}>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The child's probe marker was not closed. It wrote: {output}");

        return new string([.. output[(start + 2)..end].Where(c => !char.IsControl(c))]);
    }
}
