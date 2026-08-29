using System.Text.Json.Nodes;

namespace mTiles.Services.Tiles;

/// <summary>
/// Reading a saved tile's state without a null check per value.
/// </summary>
/// <remarks>
/// Every one of these is asked of a <c>JsonObject?</c> that may be absent, may be missing the key, and
/// may hold something of the wrong type — the file is on the user's disk and older builds wrote it.
/// <c>TryGetValue</c> rather than <c>GetValue</c> for the last of those: a number where a string was
/// expected throws, and a layout that will not open is a far worse answer than a tile that comes back
/// with its default.
/// </remarks>
internal static class TileState
{
    /// <summary>The value under that key, or null when there is not one that reads as a non-empty
    /// string.</summary>
    public static string? String(this JsonObject? state, string key)
    {
        if (state?[key] is not JsonValue value) return null;
        return value.TryGetValue<string>(out var parsed) && parsed.Length > 0 ? parsed : null;
    }

    /// <summary>The value under that key, or <paramref name="fallback"/>.</summary>
    public static bool Bool(this JsonObject? state, string key, bool fallback)
    {
        if (state?[key] is not JsonValue value) return fallback;
        return value.TryGetValue<bool>(out var parsed) ? parsed : fallback;
    }
}
