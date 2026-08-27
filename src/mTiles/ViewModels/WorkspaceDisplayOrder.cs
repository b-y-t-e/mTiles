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
    public static int Compare(WorkspaceItemViewModel a, WorkspaceItemViewModel b) =>
        a.IsFavorite != b.IsFavorite
            ? (a.IsFavorite ? -1 : 1)
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
}
