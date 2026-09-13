using mTiles.Models;
using mTiles.Services.Tiles;

namespace mTiles.ViewModels;

/// <summary>
/// Hands out tile names for one tree, remembering every name it has given so none is given twice.
/// </summary>
/// <remarks>
/// <para>One per tree — a workspace, or the window's own layout — because names are unique only within
/// what the user sees at once: a note in the window and a note in a workspace may both be
/// <c>Note#1</c>.</para>
/// <para>A dictionary rather than a field per kind: five fields meant five parameters on the allocator
/// and a five-armed <c>else if</c> reading them back out of a saved layout, and a seventh kind meant
/// finding all three places again. Names rather than counters, because what a kind makes of them is the
/// kind's own business — a number for most, an adjective and an animal for a terminal. Names are never
/// taken back out, so a number is not handed out twice in one session because a tile was closed.</para>
/// </remarks>
public sealed class TileNameAllocator(TileCatalog catalog)
{
    private readonly Dictionary<string, HashSet<string>> _namesPerKind =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What to call a tile of that kind, given what this tree already holds.</summary>
    /// <remarks>The kind decides; this only keeps the list of names it has already handed out and
    /// remembers the answer. A tile of no kind has no name yet, and an id nothing is registered under
    /// gets the same answer — there is nothing to ask.</remarks>
    public string Allocate(string kindId)
    {
        if (catalog.Kind(kindId) is not { } kind) return "";

        var used = UsedNames(kindId);
        var name = kind.NameFor(used);
        used.Add(name);
        return name;
    }

    /// <summary>Picks up the names a saved layout already uses, so a new tile is not called
    /// <c>Git#1</c> beside one already called that.</summary>
    public void RememberSaved(TileNode? node)
    {
        if (node == null) return;
        if (!node.IsLeaf)
        {
            RememberSaved(node.First);
            RememberSaved(node.Second);
            return;
        }

        if (node.TileName is { Length: > 0 } tileName && node.Kind is { Length: > 0 } kindId)
            UsedNames(kindId).Add(tileName);
    }

    private HashSet<string> UsedNames(string kindId) =>
        _namesPerKind.TryGetValue(kindId, out var names)
            ? names
            : _namesPerKind[kindId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
