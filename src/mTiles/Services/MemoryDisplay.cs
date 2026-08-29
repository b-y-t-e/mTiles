using System.Globalization;

namespace mTiles.Services;

/// <summary>
/// How many bytes reads in a list row.
/// </summary>
/// <remarks>
/// Pure, and its own class rather than a converter, because it is the row's wording and not its markup:
/// the same sentence is wanted in a tooltip and in a test, neither of which has a binding.
/// </remarks>
public static class MemoryDisplay
{
    private const long Megabyte = 1024L * 1024L;
    private const long Gigabyte = Megabyte * 1024L;

    /// <summary>What to put on the row, or nothing at all.</summary>
    /// <remarks>
    /// <para>Empty for nothing, because a workspace holding no processes has nothing to report and
    /// "0 MB" is a claim that something was measured. The meta line reserves its height either way, so
    /// an empty string costs the layout nothing.</para>
    /// <para>Whole megabytes below a gigabyte and one decimal above it: three significant figures is as
    /// much as a reading that moves every few seconds can honestly carry, and a row 240px wide has no
    /// space for more. Formatted in the current culture, so the separator is the one the rest of the
    /// user's machine uses.</para>
    /// </remarks>
    public static string Format(long bytes)
    {
        if (bytes <= 0) return "";

        if (bytes >= Gigabyte)
            return (bytes / (double)Gigabyte).ToString("0.#", CultureInfo.CurrentCulture) + " GB";

        // Rounded up rather than to nearest at the bottom of the scale: a process that is holding
        // something must not be reported as holding nothing.
        var megabytes = Math.Max(1, (long)Math.Round(bytes / (double)Megabyte));
        return megabytes.ToString(CultureInfo.CurrentCulture) + " MB";
    }
}
