namespace mTiles.Services;

/// <summary>How much larger or smaller the whole interface is drawn than the display asks for.</summary>
/// <remarks>
/// <para>Not the same question as the font size beside it. The font size moves text and leaves the
/// padding, the icons, the splitter gutters and the terminal's own cell grid where they were, so past a
/// point the interface stops being bigger and starts being crowded. This multiplies everything at once,
/// which is what a display too dense for the size the compositor reports actually needs.</para>
/// <para>It exists because on Wayland there is nowhere else to say it. The X11 backend reads
/// <c>AVALONIA_GLOBAL_SCALE_FACTOR</c> and the Qt variables beside it; the Wayland backend reads none of
/// them — measured against Avalonia 12.1.2, whose Wayland assembly carries no scale environment variable
/// at all. What it does carry is <c>wp_fractional_scale_v1</c>, so the compositor's own scale is already
/// honoured exactly, including a fractional one: this is the adjustment *on top of* that, for a laptop
/// where the correct scale still comes out too small to read.</para>
/// <para>Deliberately not Linux-only. A scale that exists on one platform is a setting that is missing
/// on the others, and the mechanism — a layout transform over the window's content — is the same
/// everywhere and costs nothing at 1.0.</para>
/// </remarks>
public static class InterfaceScale
{
    /// <summary>Half size: the smallest that leaves the settings dialog usable enough to undo this.</summary>
    public const double Min = 0.5;

    /// <summary>Triple size, which is roughly one line of a tile header filling a laptop's width.</summary>
    public const double Max = 3.0;

    /// <summary>No scaling — the display's own scale and nothing else.</summary>
    public const double Default = 1.0;

    /// <summary>
    /// A stored scale, made safe to draw with.
    /// </summary>
    /// <remarks>
    /// <c>settings.json</c> is hand-editable and is also read by an older build after a rollback, so 0,
    /// a negative number and <c>NaN</c> are all reachable — and each of them is a window with nothing
    /// visible in it, including the dialog holding the mistake. Anything that is not a finite number
    /// falls back to <see cref="Default"/> rather than to <see cref="Min"/>: an unreadable answer should
    /// leave the interface as though the setting had never been touched.
    /// </remarks>
    public static double Normalise(double stored) =>
        double.IsFinite(stored) && stored > 0 ? Math.Clamp(stored, Min, Max) : Default;
}
