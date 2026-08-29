using Avalonia.Controls;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>One kind and the view that draws it, registered together.</summary>
/// <remarks>
/// <b>One registration per kind is the load-bearing part.</b> Two parallel lists — kinds here, views
/// there — is the arrangement that has already cost this codebase a bug: the note on
/// <see cref="LeafTileNodeViewModel.ConfigureNewLeaf"/> records how a list of callbacks copied by hand
/// left every tile after the first without dictation.
/// </remarks>
public sealed record TileCatalogEntry(ITileKind Kind, Func<ITile, Control> CreateView);

/// <summary>
/// Every kind of tile this application can build, by id.
/// </summary>
/// <remarks>
/// <para><b>The one layering boundary.</b> A tile's view is a <see cref="Control"/>, so a kind that
/// built its own view would drag <c>Views/</c> into what a view model can see, and this project keeps
/// <c>ViewModels/</c> from ever referencing <c>Views/</c>. So the entry holds both halves while the two
/// lookups are separate: <see cref="Kind"/> is what the view-model side asks for and knows nothing about
/// controls, <see cref="Entry"/> is what the view side asks for. Registration happens in
/// <c>App.axaml.cs</c>, which is allowed to see both.</para>
/// <para>An instance rather than a static, and handed down from the application through the workspace to
/// each tile: there is no DI container here, and a mutable global registry is the kind of thing two
/// tests fight over.</para>
/// </remarks>
public sealed class TileCatalog
{
    private readonly Dictionary<string, TileCatalogEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TileCatalogEntry> _order = [];

    /// <summary>The kinds in the order they were registered — which is the order the chooser offers
    /// them in, so registration is also where that decision is made.</summary>
    public IReadOnlyList<TileCatalogEntry> Entries => _order;

    /// <summary>Adds a kind and the view that draws it.</summary>
    /// <exception cref="ArgumentException">Two kinds registered under one id. A duplicate is not a
    /// preference to resolve — one of them would never be reachable, and which one would depend on the
    /// order of two lines in a startup method.</exception>
    public TileCatalog Register(TileCatalogEntry entry)
    {
        if (!_entries.TryAdd(entry.Kind.Id, entry))
            throw new ArgumentException($"A tile kind is already registered as '{entry.Kind.Id}'.", nameof(entry));

        _order.Add(entry);
        return this;
    }

    /// <summary>Adds a kind and the view that draws it.</summary>
    public TileCatalog Register(ITileKind kind, Func<ITile, Control> createView) =>
        Register(new TileCatalogEntry(kind, createView));

    /// <summary>The kind with that id, or null when nothing is registered under it.</summary>
    /// <remarks>Null rather than a throw: an id read from a layout file may have been written by a
    /// newer build, and what that costs is one tile shown as empty rather than a workspace that will
    /// not open.</remarks>
    public ITileKind? Kind(string? id) => Entry(id)?.Kind;

    /// <summary>The kind and its view.</summary>
    public TileCatalogEntry? Entry(string? id) =>
        id is { Length: > 0 } && _entries.TryGetValue(id, out var entry) ? entry : null;
}
