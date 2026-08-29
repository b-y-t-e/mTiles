using System.Diagnostics;
using mTiles.Models;
using mTiles.ViewModels;

namespace mTiles.Services;

/// <summary>
/// What a first run opens on: one workspace at the user's own directory, holding one terminal.
/// </summary>
/// <remarks>
/// <para>Without it the application starts on an empty panel and an empty canvas, which says nothing
/// about what any of it is for. The home directory is the one place every machine has and the user can
/// certainly write to, and a terminal in it is the thing this application exists to run.</para>
/// <para>Once, on a machine that has never had a workspace list, and never again. The workspace is an
/// ordinary one afterwards — removable, renameable, pinnable — it is only the first one, not a
/// permanent fixture.</para>
/// </remarks>
public static class DefaultWorkspace
{
    /// <summary>Adds the first workspace and its layout, on a machine that has never had one.</summary>
    /// <remarks>
    /// <para>The question is <see cref="WorkspaceService.HasStoredList"/> and not an empty list, because
    /// an empty list is also what an unreadable <c>workspaces.json</c> looks like — one another instance
    /// or a virus scanner has locked, or a write a power cut truncated. Seeding on that would call
    /// <see cref="WorkspaceService.AddWorkspace"/>, which saves, and the user's whole list would be
    /// replaced by this one workspace with their layouts left orphaned behind it. The file is also what
    /// remembers the answer: a user who removes their last workspace has an empty list *on disk*, so the
    /// one they threw away is not put back on the next launch.</para>
    /// <para>Fails soft. This runs before the main window is built, and a home directory that cannot be
    /// written to is a reason to start on an empty panel — not a reason not to start.</para>
    /// </remarks>
    public static void SeedFirstRun(WorkspaceService workspaces, PersistenceService layouts)
    {
        if (workspaces.HasStoredList || workspaces.Workspaces.Count > 0) return;

        var home = SpecialDirectories.Home;
        if (string.IsNullOrEmpty(home) || !Directory.Exists(home)) return;

        try
        {
            var workspace = workspaces.AddWorkspace(home, WorkspaceDisplayName.Home);
            layouts.SaveLayout(workspace.Id, SingleTerminal());
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Could not create the default workspace at {0}: {1}", home, ex.Message);
        }
    }

    /// <summary>The layout the first workspace opens with.</summary>
    /// <remarks>No shell and no profile named: the tile takes the user's default shell, and the tile's
    /// own name is allocated when the layout is read, exactly as it is for a tile the user adds.</remarks>
    private static TileNode SingleTerminal() => new()
    {
        IsLeaf = true,
        ContentType = TileContentType.Terminal,
        TileId = Guid.NewGuid().ToString(),
        IsActive = true
    };
}
