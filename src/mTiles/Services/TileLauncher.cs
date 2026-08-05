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

        var (startupScript, fallbackScript, isDirectLaunch) = vm.ResolveCurrentScripts();

        // Either script is enough to have something to run: a profile with only a fallback is unusual
        // but legal, and testing the startup script alone silently dropped its command on the floor.
        if (isDirectLaunch && (startupScript != null || fallbackScript != null))
        {
            vm.ReplaceLaunchSession(DirectLaunchSession.Start(terminal, vm.WorkingDirectory, vm.Shell,
                startupScript, fallbackScript, vm.TileId));
            return;
        }

        _ = LaunchShellAsync(terminal, vm, startupScript);
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
            System.Diagnostics.Trace.TraceWarning("Launching {0} in tile {1} failed: {2}",
                vm.Shell.ExecutablePath, vm.TileId, ex);
        }
    }
}
