using System.Diagnostics;
using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The one part of <see cref="SessionCapture"/> that cannot be a pure function: running a CLI and
/// reading what it printed.
/// </summary>
/// <remarks>Windows-only, and deliberately so — what is being pinned is a command leaving a child
/// holding the redirected handles, and <c>start /b</c> is how that is arranged here. Elsewhere these
/// return rather than approximate it. The command itself is a script file rather than an argument,
/// because the JSON being echoed is full of quotes and cmd does not read the <c>\"</c> escaping .NET
/// quotes an argument list with.</remarks>
public class SessionCaptureRunTests : IDisposable
{
    private static bool OnWindows => OperatingSystem.IsWindows();

    private static string Cmd => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private readonly List<string> _scripts = [];

    private string Script(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mtiles-capture-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        _scripts.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var script in _scripts)
        {
            try { File.Delete(script); } catch { /* a temp file the next sweep can have */ }
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Reads_what_the_command_printed()
    {
        if (!OnWindows) return;

        var script = Script("echo {\"conversation_id\":\"abc-123\"}");

        var output = await SessionCapture.RunForOutputAsync(Cmd, Path.GetTempPath(),
            ["/c", script], CancellationToken.None);

        Assert.Equal("abc-123", SessionCapture.ConversationIdIn(output));
    }

    /// <summary>
    /// A command that exits and leaves a child holding the pipes answers, rather than hanging for ever.
    /// </summary>
    /// <remarks>This is the failure agy 1.1.24 lists as fixed, and the one that made Restart shell on an
    /// agy tile do nothing at all: the capture is awaited by <c>PrepareForLaunchAsync</c>, so a read
    /// that never completes is a launch that never starts — no timeout reaches it, and nothing is
    /// logged. The id is what a capture is allowed to cost; the tile is not.</remarks>
    [Fact]
    public async Task Answers_when_a_surviving_child_holds_the_pipes_open()
    {
        if (!OnWindows) return;

        var script = Script("echo {\"conversation_id\":\"abc-123\"}\r\n"
            + "start /b \"\" ping -n 30 127.0.0.1");

        var previous = SessionCapture.DrainTimeout;
        SessionCapture.DrainTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            var started = Stopwatch.StartNew();

            var output = await SessionCapture.RunForOutputAsync(Cmd, Path.GetTempPath(),
                ["/c", script], CancellationToken.None);

            started.Stop();

            // The point is that it came back at all, and quickly. Whether the pipes happened to drain
            // before the deadline is the child's business, not this test's — so both answers are
            // acceptable and neither is a hang.
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(5),
                $"The capture took {started.Elapsed}, which is a hang rather than a drain deadline.");
            Assert.True(output is null || SessionCapture.ConversationIdIn(output) == "abc-123",
                $"Unexpected output: {output}");
        }
        finally
        {
            SessionCapture.DrainTimeout = previous;
        }
    }
}
