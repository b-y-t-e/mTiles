using mTiles.Models;

namespace mTiles.Services;

/// <summary>How much larger the desktop says this user needs their text.</summary>
/// <remarks>
/// <para>A third size question, and not a restatement of either of the other two. The compositor's
/// scale — which Avalonia already honours exactly, fractional included — says how dense the display is.
/// <see cref="InterfaceScale"/> says how much bigger this user wants the whole window than that.
/// This one says something narrower and more personal: that <em>text</em> specifically has to be
/// larger, whatever the display is. It is the accessibility setting, and a desktop that has been told
/// it must not be silently ignored by one application on it.</para>
/// <para><b>Nothing in Avalonia reads it.</b> Measured against 12.1.2: <c>Avalonia.FreeDesktop</c> asks
/// the settings portal for exactly two things, the theme variant and the accent colour. There is no
/// text-scaling reader on any backend, X11 and Windows included.</para>
/// <para><b>It is ambient on purpose.</b> This is a fact about the machine, like
/// <see cref="AppPaths"/>, not a property of anything the application constructs — and every reader of
/// it is a view model built in a different place. The alternative was threading one more constructor
/// parameter through five tile view models to carry a number that is the same for all of them.</para>
/// </remarks>
public static class TextScale
{
    private static double _current = 1.0;

    /// <summary>The desktop's factor, or 1.0 where nothing said otherwise.</summary>
    /// <remarks>1.0 rather than "unknown": a machine that does not answer wants its text the size it
    /// asked for, and there is no third behaviour to distinguish.</remarks>
    public static double Current => Volatile.Read(ref _current);

    /// <summary>
    /// The range a factor is allowed to take.
    /// </summary>
    /// <remarks>
    /// The value comes off somebody else's desktop, so it is not ours to trust: GNOME's own slider
    /// stops at 2.0 and Windows' at 2.25, but the underlying key is writable by hand and by any
    /// program on the machine. The ends are wider than either offers, because this is a guard against
    /// a value that would empty the window rather than a second opinion about what is legible.
    /// </remarks>
    public const double Min = 0.5;

    /// <inheritdoc cref="Min"/>
    public const double Max = 4.0;

    /// <summary>A factor read off the machine, made safe to multiply by.</summary>
    /// <remarks>Anything that is not a finite positive number is 1.0 and not <see cref="Min"/>: a key
    /// nobody has written, a parse that failed and a desktop that answered nonsense should all leave
    /// the text the size the user asked for.</remarks>
    public static double Normalise(double read) =>
        double.IsFinite(read) && read > 0 ? Math.Clamp(read, Min, Max) : 1.0;

    /// <summary>The interface's font size once the desktop has had its say.</summary>
    public static double UiFontSize(AppSettings settings) => Of(settings.FontSize);

    /// <summary>The terminal's font size once the desktop has had its say.</summary>
    /// <remarks>
    /// The terminal follows too, and that is a decision rather than symmetry. It changes the cell grid,
    /// so the shell reflows and the child is told a new size — a real cost. Against it: the terminal is
    /// the one surface in this application made entirely of text, and a text-size setting that moved
    /// every label except the ten thousand characters of code would be answering the wrong question.
    /// </remarks>
    public static double TerminalFontSize(AppSettings settings) => Of(settings.TerminalFontSize);

    /// <summary>One stored size, scaled.</summary>
    /// <remarks>Rounded to a tenth for the reason <see cref="UiFontScale.For"/> rounds: this is the
    /// number a view model hands to a control, and a cell grid measured against 17.499999999999996 is
    /// a column of pixels nobody chose.</remarks>
    public static double Of(double storedSize) => Math.Round(storedSize * Current, 1);

    /// <summary>Records a factor read off the machine. Answers whether it moved.</summary>
    /// <remarks>Internal rather than public: only <see cref="DesktopTextScale"/> and the tests that
    /// pin this behaviour may write it, because a second writer is a size that changes for a reason
    /// nobody can find.</remarks>
    internal static bool Set(double read)
    {
        var value = Normalise(read);
        var previous = Interlocked.Exchange(ref _current, value);
        return Math.Abs(previous - value) > AppDefaults.FontSizeEpsilon;
    }
}
