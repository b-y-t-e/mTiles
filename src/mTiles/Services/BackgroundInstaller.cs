using System.Diagnostics;
using System.Text;
using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Runs an <see cref="InstallPlan"/> as a process of this application's own, with no shell and no
/// tile, and answers what became of it.
/// </summary>
/// <remarks>
/// <para><b>No shell, and that is what fixes winget.</b> The tile route composes a command *line* and
/// hands it to whichever shell the user's default is, which means the program has to be found by that
/// shell's own lookup — and <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c> is in no process' <c>PATH</c>
/// on a good many Windows machines, so `winget` answered "command not found" in Git Bash and in
/// PowerShell alike while the binary sat right there. Here the executable is resolved to a file by
/// <see cref="ExecutableFinder.Anywhere"/> and the arguments go into <c>ArgumentList</c>, so nothing
/// is quoted, nothing is parsed twice, and a profile directory with a space in it is not a special
/// case.</para>
/// <para><b>What running in the background costs, said plainly.</b> An installer that asks a question
/// — UAC, a licence to accept, a <c>y/n</c> — has nobody to answer it, so it hangs. That is what
/// <see cref="Timeout"/> is for and why it is not generous: a killed installer that says so is worth
/// more than one that waits for the life of the session. It is also why the output is kept: the exit
/// code alone names nothing, and the last few lines are the only account of what happened the user
/// will ever get.</para>
/// <para><b>Nothing here elevates.</b> A plan that needs administrator rights fails, and its failure
/// carries the installer's own words about that, which is the most this route can honestly do.</para>
/// </remarks>
public static class BackgroundInstaller
{
    /// <summary>How long an install may take before it is treated as waiting for somebody.</summary>
    /// <remarks>Five minutes: a package manager fetching over a slow connection is inside it, and a
    /// process sitting on a prompt is well outside. It is a guess rather than a measurement, and it is
    /// the one number here worth revisiting if a real install is ever killed by it.</remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>How many lines of the installer's own output a failure carries back.</summary>
    /// <remarks>Enough to name the cause and few enough to fit the sentence a settings row can show.
    /// The whole of it goes to the log regardless.</remarks>
    private const int LinesKept = 6;

    /// <summary>What became of an install.</summary>
    /// <param name="Succeeded">Whether the installer exited zero.</param>
    /// <param name="Problem">One sentence naming what went wrong, or null when nothing did.</param>
    public readonly record struct Outcome(bool Succeeded, string? Problem)
    {
        /// <summary>It worked.</summary>
        public static Outcome Ok => new(true, null);

        /// <summary>It did not, and this is why.</summary>
        public static Outcome Failed(string problem) => new(false, problem);
    }

    /// <summary>Runs the plan and answers what became of it. Never throws.</summary>
    /// <remarks>Off the caller's thread, because the first call asks the login shell for its
    /// <c>PATH</c>, which starts a process and reads the user's rc files.</remarks>
    public static Task<Outcome> RunAsync(InstallPlan plan, CancellationToken ct = default) =>
        Task.Run(() => RunAsync(plan,
            name => ExecutableFinder.Anywhere(name) ?? LoginShellPath.Find(name, LoginShellPath.Value),
            LoginShellPath.Value, ct), ct);

    /// <summary>As above, with where a binary is found and the child's <c>PATH</c> supplied by the
    /// caller — so a test states the machine instead of depending on it.</summary>
    /// <param name="childPath">The <c>PATH</c> the installer runs with, or null to inherit ours. On Unix
    /// it is the login shell's (<see cref="LoginShellPath"/>): an <c>npm</c> from nvm is a script whose
    /// <c>#!/usr/bin/env node</c> finds no <c>node</c> on the launcher's <c>PATH</c>.</param>
    internal static async Task<Outcome> RunAsync(InstallPlan plan, Func<string, string?> locate,
        string? childPath, CancellationToken ct = default)
    {
        if (plan.NeedsATerminal)
            return Outcome.Failed("This command has to run in a terminal tile.");

        // The resolved file, never the bare name: this starts a process directly, so there is no shell
        // lookup behind it to fall back on. A name nothing can find is named as such rather than
        // becoming an opaque Win32Exception.
        if (locate(plan.Executable) is not { } executable)
        {
            return Outcome.Failed(
                $"{plan.Executable} was not found on this machine, so the install could not start.");
        }

        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // winget and npm write UTF-8 when redirected; left unset, .NET decodes with the OEM code
            // page on a Polish Windows, and the tail the failure dialog shows comes out mangled.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in plan.Arguments) info.ArgumentList.Add(argument);
        if (childPath is not null) info.Environment["PATH"] = childPath;

        var output = new StringBuilder();
        try
        {
            using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            // Both streams into one buffer, because which of the two an installer writes its refusal to
            // is its own business and the user is owed the reason either way.
            process.OutputDataReceived += (_, e) => Append(output, e.Data);
            process.ErrorDataReceived += (_, e) => Append(output, e.Data);

            if (!process.Start())
                return Outcome.Failed($"{plan.Executable} could not be started.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                Trace.WriteLine($"Install of {plan.Executable} was stopped:{Environment.NewLine}{Whole(output)}");
                return Outcome.Failed(ct.IsCancellationRequested
                    ? "The install was cancelled."
                    : "The install did not finish and was stopped — it may have been waiting for an "
                      + "answer. Run the command in a terminal to see what it is asking.");
            }

            Trace.WriteLine($"Install of {plan.Executable} exited {process.ExitCode}:{Environment.NewLine}{Whole(output)}");

            return process.ExitCode == 0
                ? Outcome.Ok
                : Outcome.Failed($"The install failed (exit code {process.ExitCode}). {Tail(output)}".Trim());
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Install of {plan.Executable} threw: {ex.Message}");
            return Outcome.Failed($"The install could not be run: {ex.Message}");
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        var shown = LastRedrawOf(line);
        if (!string.IsNullOrWhiteSpace(shown)) lock (output) output.AppendLine(shown.Trim());
    }

    private const char CarriageReturn = (char)13;

    /// <summary>What a line redrawn with a carriage return finally showed.</summary>
    /// <remarks>winget draws its progress bar by returning the carriage without a newline, so one
    /// "line" is every frame of the bar; only the last frame is what a terminal would have left.</remarks>
    internal static string? LastRedrawOf(string? line)
    {
        if (line is null) return null;
        var frames = line.Split(CarriageReturn,StringSplitOptions.RemoveEmptyEntries);
        return frames.LastOrDefault(frame => !string.IsNullOrWhiteSpace(frame));
    }

    /// <summary>Everything the installer said, for the log.</summary>
    private static string Whole(StringBuilder output)
    {
        lock (output) return output.ToString();
    }

    /// <summary>The last few lines of what the installer said, for the dialog.</summary>
    private static string Tail(StringBuilder output)
    {
        var lines = Whole(output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" · ", lines.TakeLast(LinesKept));
    }

    /// <summary>Ends a process that is not going to finish, and says nothing when it already has.</summary>
    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception or AggregateException)
        {
            // It finished between the check and the kill, the platform refused, or part of the tree could
            // not be ended (an elevated child answers AggregateException). Either way there is
            // nothing left to do about it and the outcome is already decided.
        }
    }
}
