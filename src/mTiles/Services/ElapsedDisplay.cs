namespace mTiles.Services;

/// <summary>
/// How long something has been running, written for a place in the UI that is a few characters wide.
/// </summary>
/// <remarks>
/// <para>Pure and separate from the tile because the interesting part is not the arithmetic but the
/// three shapes it switches between, and those are an opinion that is easier to argue in a table test
/// than to read off a running screen.</para>
/// <para>The unit never appears twice: a run is either seconds (<c>42s</c>), or minutes and seconds
/// (<c>4:07</c>), or hours as well (<c>1:02:33</c>). Colons say what the letters would, and the shortest
/// form is the one on screen for the first minute of every run — the minute in which the reader is
/// asking "did it start?" rather than "how long has this taken?".</para>
/// <para>Seconds are always shown, at every scale. A tool's output arrives in bursts and the reader is
/// watching for movement as much as for a number; a display that only changed once a minute would look
/// like the one thing this label exists to disprove — a stalled tile.</para>
/// </remarks>
internal static class ElapsedDisplay
{
    /// <summary>The elapsed time as the UI writes it.</summary>
    /// <remarks>
    /// A negative span answers <c>0s</c> rather than throwing. Nothing here should produce one — the
    /// caller measures with a <see cref="System.Diagnostics.Stopwatch"/> — but a label is not worth an
    /// exception on the UI thread, and "0s" is what a run that has just started says anyway.
    /// </remarks>
    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) return "0s";

        var total = (long)elapsed.TotalSeconds;
        var seconds = total % 60;
        var minutes = total / 60 % 60;
        var hours = total / 3600;

        if (hours > 0) return $"{hours}:{minutes:00}:{seconds:00}";
        if (minutes > 0) return $"{minutes}:{seconds:00}";
        return $"{seconds}s";
    }
}
