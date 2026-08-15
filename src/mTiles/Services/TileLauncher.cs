using System.Diagnostics;
using mTiles.Models;
using mTiles.ViewModels;
using Terminal.Avalonia;

namespace mTiles.Services;

/// <summary>
/// Launching (and relaunching) the shell of one terminal tile: picks the profile's current scripts and
/// either runs the direct-launch chain or starts the shell interactively.
/// <para>One place for it because first launch and "restart shell" must do exactly the same thing —
/// including tearing down the previous launch. They drifted apart while they were two copies, and the
/// difference was a tile that ended up with two competing chains after a restart.</para>
/// </summary>
internal static class TileLauncher
{
    /// <summary>Starts the tile's shell, replacing whatever was running in it. The tile's identity is
    /// the caller's to set: this reads <see cref="TerminalTileViewModel.TileId"/>, it does not assign
    /// it — a launcher that renamed the thing it launches is a launcher nobody can reason about.</summary>
    public static void Launch(TerminalControl terminal, TerminalTileViewModel vm)
    {
        // Whatever was running this tile stops owning it now, before anything new is started: the old
        // chain must not see the restart's kill as "its" session ending and relaunch into ours.
        vm.ReplaceLaunchSession(null);

        var scripts = Runnable(vm);

        // Before anything runs, and in front of both launch paths: a profile may name the import
        // document in either script, and the command that reads it is the tile's own — by the time it
        // runs there is nobody left to write the file for it.
        OpenCodeSession.PrepareIfReferenced(scripts, vm.TileId, vm.WorkingDirectory);

        if (scripts.RunsCommandChain)
        {
            try
            {
                vm.ReplaceLaunchSession(DirectLaunchSession.Start(terminal, vm.WorkingDirectory, vm.Shell,
                    scripts, vm.TileId));
                return;
            }
            catch (Exception ex)
            {
                // This runs on the dispatcher from an `async void` attach handler, so an escaping
                // exception is not a failed launch — it is the application going down. The tile still
                // gets a shell; what it does not get is the profile's commands.
                Trace.TraceError(
                    "The launch chain for tile {0} could not be started; falling back to a plain shell: {1}",
                    vm.TileId, ex);
            }

            _ = LaunchShellAsync(terminal, vm, startupScript: null);
            return;
        }

        _ = LaunchShellAsync(terminal, vm, scripts.Startup);
    }

    /// <summary>
    /// The tile's scripts, or none when they cannot be resolved.
    /// <para>Only one thing can make them unresolvable: a blank <see cref="TerminalTileViewModel.TileId"/>
    /// under a script that uses <c>${tileId}</c>. Expanding it to nothing would turn
    /// <c>claude -r ${tileId}</c> into <c>claude -r</c> — a different command, which may well run, so
    /// the scripts are dropped rather than mangled.</para>
    /// <para>In front of <em>both</em> launch paths, which is the point. The chain throws where a caller
    /// can catch it; the interactive path builds its script inside a task nobody awaits, so there the
    /// same fault used to log and leave the tile with nothing at all. The asymmetry was the defect.</para>
    /// </summary>
    private static LaunchScripts Runnable(TerminalTileViewModel vm)
    {
        var scripts = vm.ResolveCurrentScripts();

        // The rule itself, not a second copy of it: resolving is the only thing that knows what an
        // acceptable id is, and asking it here is what keeps this check and the real one from drifting.
        // `TryResolve` rather than catching what `Resolve` throws — this is a question, and a question
        // answered by an exception reads as a failure at every call site that is really a decision.
        if (TileScript.TryResolve(scripts.Startup ?? "", vm.TileId, out _, out var why)
            && TileScript.TryResolve(scripts.Fallback ?? "", vm.TileId, out _, out why))
            return scripts;

        Trace.TraceError("Tile {0} cannot resolve its profile scripts, so they were dropped rather than "
            + "run: {1}", vm.TileId, why);
        return LaunchScripts.None;
    }

    /// <summary>Starting a shell fails for ordinary reasons — a profile pointing at a binary that was
    /// uninstalled, a working directory that no longer exists — and there is nobody to await this, so
    /// without the trace the tile just goes black with no explanation anywhere.</summary>
    private static async Task LaunchShellAsync(TerminalControl terminal, TerminalTileViewModel vm,
        string? startupScript)
    {
        try
        {
            await ShellStarter.StartAsync(terminal, vm.WorkingDirectory,
                vm.Shell.ExecutablePath, vm.Shell.Args, startupScript, vm.TileId);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Launching {0} in tile {1} failed: {2}",
                vm.Shell.ExecutablePath, vm.TileId, ex);
        }
    }
}
