using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using MTerminal.Models;

namespace MTerminal.Views;

public static class DirectLauncher
{
    private const int FallbackTimeoutMs = 5000;
    private const int MinLifetimeForRelaunchMs = 10_000;

    public static IReadOnlyList<string> BuildCommands(string startupScript, string fallbackScript, string tileId)
    {
        var commands = new List<string>();
        if (!string.IsNullOrWhiteSpace(startupScript))
            commands.Add(startupScript.Trim().Replace("${tileId}", tileId));
        if (!string.IsNullOrWhiteSpace(fallbackScript))
            commands.Add(fallbackScript.Trim().Replace("${tileId}", tileId));
        return commands;
    }

    public static async Task LaunchWithFallback(TerminalControl terminal, string workingDir,
        IReadOnlyList<string> commands, ShellProfile shell, bool autoRelaunch = true)
    {
        var chainStart = Environment.TickCount64;
        var success = false;

        for (var i = 0; i < commands.Count; i++)
        {
            var (exe, args) = WrapCommand(commands[i], shell);
            await terminal.LaunchProcess(workingDir, exe, args);

            var exited = await WaitForProcessExit(terminal, FallbackTimeoutMs);
            if (!exited)
            {
                success = true;
                break;
            }

            try { terminal.Kill(); } catch { }
            await Task.Delay(200);
        }

        if (!success)
        {
            await terminal.LaunchProcess(workingDir, shell.ExecutablePath, shell.Args);
            return;
        }

        if (autoRelaunch)
            AttachRelaunchOnExit(terminal, workingDir, commands, shell, chainStart);
    }

    private static void AttachRelaunchOnExit(TerminalControl terminal, string workingDir,
        IReadOnlyList<string> commands, ShellProfile shell, long chainStart)
    {
        var tv = terminal.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
        if (tv == null) return;

        var pty = PtyWriter.PtyField?.GetValue(tv);
        if (pty == null) return;

        var exitEvent = pty.GetType().GetEvent("ProcessExited");
        if (exitEvent == null) return;

        EventHandler<Porta.Pty.PtyExitedEventArgs>? handler = null;
        handler = (_, _) =>
        {
            exitEvent.RemoveEventHandler(pty, handler);

            var lifetime = Environment.TickCount64 - chainStart;
            if (lifetime < MinLifetimeForRelaunchMs)
                return;

            Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(500);
                try { terminal.Kill(); } catch { }
                await Task.Delay(300);
                await LaunchWithFallback(terminal, workingDir, commands, shell, autoRelaunch: true);
            });
        };
        exitEvent.AddEventHandler(pty, handler);
    }

    private static (string exe, string[] args) WrapCommand(string command, ShellProfile shell)
    {
        var flag = shell.Type switch
        {
            ShellType.Cmd => "/c",
            ShellType.PowerShell => "-Command",
            _ => "-c"
        };
        return (shell.ExecutablePath, [flag, command]);
    }

    private static async Task<bool> WaitForProcessExit(TerminalControl terminal, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>();

        var tv = terminal.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
        if (tv == null) return true;

        var pty = PtyWriter.PtyField?.GetValue(tv);
        if (pty == null) return true;

        var exitEvent = pty.GetType().GetEvent("ProcessExited");
        if (exitEvent == null) return true;

        EventHandler<Porta.Pty.PtyExitedEventArgs> handler = (_, _) => tcs.TrySetResult(true);
        exitEvent.AddEventHandler(pty, handler);

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => tcs.TrySetResult(false));
            return await tcs.Task;
        }
        finally
        {
            exitEvent.RemoveEventHandler(pty, handler);
        }
    }
}
