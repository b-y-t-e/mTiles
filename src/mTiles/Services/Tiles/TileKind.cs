using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using mTiles.ViewModels;

namespace mTiles.Services.Tiles;

/// <summary>
/// A kind that builds one particular type of tile, so its own two methods are typed.
/// </summary>
/// <remarks>
/// The cast that <see cref="ITileKind"/> implies lives in exactly one line, here, and is always sound:
/// this class built the instance being handed back to it. Without the base every kind would repeat it,
/// and one of them would eventually repeat it wrongly.
/// </remarks>
public abstract class TileKind<T> : ITileKind where T : ITile
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string IconId { get; }
    public abstract string AccentKey { get; }

    /// <summary>False: every kind is something put into a layout, except the window's own two.</summary>
    public virtual bool IsPermanent => false;

    /// <summary>Defaults to the display name, which is right for five of the six.</summary>
    public virtual string NamePrefix => DisplayName;

    /// <summary>Numbered after the prefix, as in <c>Git#1</c> — one more than the highest number
    /// already used, so a saved layout's own names are picked up rather than clashed with.</summary>
    public virtual string NameFor(IReadOnlySet<string> used) =>
        $"{NamePrefix}#{used.Select(HighestNumber).DefaultIfEmpty(0).Max() + 1}";

    /// <summary>Nothing to ask, unless a kind says otherwise.</summary>
    public virtual IReadOnlyList<TileSetupOption> SetupOptions(TileContext context) => [];

    private static int HighestNumber(string name) =>
        NumberSuffix.Match(name) is { Success: true } match ? int.Parse(match.Groups[1].Value) : 0;

    private static readonly Regex NumberSuffix = new(@"#(\d+)$", RegexOptions.Compiled);

    protected abstract T Create(TileContext context, JsonObject? state);

    /// <summary>What to write down. Nothing, unless a kind says otherwise.</summary>
    protected virtual JsonObject? Save(T tile) => null;

    /// <summary>Every key the option carries, answered the same way by what this tile would be saved
    /// as.</summary>
    /// <remarks>A subset rather than a whole-state comparison, because an option describes one decision
    /// and the state carries everything the kind writes down — the agent tile's shell and its captured
    /// session id are not part of what the card offered. An option that carries no state at all is
    /// "however this kind starts by default", which nothing here can compare against, so a kind that
    /// offers one answers for itself.</remarks>
    protected virtual bool IsCurrentSetup(TileContext context, T tile, TileSetupOption option) =>
        option.State is { } wanted && Save(tile) is { } saved
        && wanted.All(pair => JsonNode.DeepEquals(pair.Value, saved[pair.Key]));

    ITile ITileKind.Create(TileContext context, JsonObject? state) => Create(context, state);

    bool ITileKind.IsCurrentSetup(TileContext context, ITile tile, TileSetupOption option) =>
        IsCurrentSetup(context, (T)tile, option);

    JsonObject? ITileKind.Save(ITile tile) => Save((T)tile);
}
