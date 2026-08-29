namespace mTiles.ViewModels;

/// <summary>
/// The order the workspaces panel reads in: pinned workspaces first, then by name.
/// </summary>
/// <remarks>
/// One comparison, used by every place that puts a row somewhere — building the list, adding a
/// workspace, and moving one that has just been pinned. Three orderings written out separately is three
/// chances for the list to disagree with itself, and it is the kind of rule that is argued about rather
/// than derived, so it is worth being able to state in a test without a panel.
/// </remarks>
public static class WorkspaceDisplayOrder
{
    /// <summary>Pinned first, then by the name the row shows.</summary>
    /// <remarks>
    /// <para><see cref="WorkspaceItemViewModel.Name"/> deliberately, not <c>Workspace.Name</c>: a
    /// workspace that is shown under an alias sorts where that alias reads. The home directory is
    /// called <c>Home</c> and belongs under H, not under the login the folder happens to carry — a list
    /// ordered by a name nobody can see is a list whose order looks like no order at all, and it costs
    /// the user the one thing an alphabetical list gives them, which is knowing where to look before
    /// they look. Any alias <see cref="WorkspaceDisplayName"/> grows is covered by the same line.</para>
    /// <para>The glyph beside the name is not part of it. What a row wears says which directory it is,
    /// which is a second way of saying what the name already says — sorting on it as well would bunch
    /// every aliased row together at one end and quietly override the alphabet the rest of the list is
    /// read by. Pinning is the only thing here that outranks the name, because the user asked for it
    /// row by row.</para>
    /// </remarks>
    public static int Compare(WorkspaceItemViewModel a, WorkspaceItemViewModel b) =>
        a.IsFavorite != b.IsFavorite
            ? (a.IsFavorite ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
}
