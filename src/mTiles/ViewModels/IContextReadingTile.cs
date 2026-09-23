namespace mTiles.ViewModels;

/// <summary>
/// A tile that says how full its agent's context is in the header, where it draws no bar for it.
/// </summary>
/// <remarks>
/// <para>The terminal agent tile, with <c>AppSettings.ShowContextBar</c> off: it has no composer to put the
/// reading beside, and the header is the one strip of it that is always on screen. One word — <c>42%</c> —
/// set apart from the name and the note, and the whole sentence in its tooltip.</para>
/// <para>Changes are announced through <see cref="ITile"/>'s own change notification, as
/// <see cref="IDescribedTile.HeaderNote"/>'s are.</para>
/// </remarks>
public interface IContextReadingTile : ITile
{
    /// <summary><c>42%</c>, <c>128k</c>, or empty when the header has nothing to show.</summary>
    string ContextReading { get; }

    /// <summary>The reading in full — <c>"106.8k / 1M tokens · $1.15"</c>.</summary>
    string ContextReadingTip { get; }

    /// <summary>Whether the window is full enough to draw the reading as a warning.</summary>
    bool ContextReadingIsTight { get; }
}
