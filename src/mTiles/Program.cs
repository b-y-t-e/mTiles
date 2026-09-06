using System.Diagnostics;
using Avalonia;
using mTiles.Services;
using Velopack;

namespace mTiles;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var logWriter = new FileLogWriter();
        CrashHandler.Initialize(logWriter);
        Trace.Listeners.Add(new LogTraceListener(logWriter));

        // After the listener, and that is the point of it being here rather than where it happens.
        // Resolving the application data directory is what may *move* it, and the first thing to ask
        // for that directory is the log writer above — so a line written at the moment of the move
        // reaches no log file at all, which is the one line somebody will search for on the day their
        // settings appear to have vanished.
        if (AppPaths.MigrationNote is { } moved)
            Trace.TraceInformation(moved);

        // Claude Code ≥2.1.89 defaults to "fullscreen rendering": it draws on the
        // alternate screen buffer and captures the mouse, which kills the terminal's
        // native scrollback, drag-selection and select-while-scrolling in tiles.
        // Opt back into the classic renderer for all PTYs spawned by mTiles.
        // A user-defined value (set before launching mTiles) always wins.
        SetDefaultEnv("CLAUDE_CODE_DISABLE_ALTERNATE_SCREEN", "1");
        SetDefaultEnv("CLAUDE_CODE_DISABLE_MOUSE", "1");

        try
        {
            VelopackApp.Build().Run();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            logWriter.Write("FATAL", ex.Message, ex.ToString());
            throw;
        }
    }

    private static void SetDefaultEnv(string name, string value)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>
    /// Builds the application, and on Linux asks the compositor directly rather than going through
    /// XWayland.
    /// </summary>
    /// <remarks>
    /// <para><c>UsePlatformDetect</c> picks the X11 backend on Linux, which on a Wayland session means
    /// XWayland — and XWayland has no per-monitor scaling to report. The application is handed a scale
    /// of 1 whatever the display is, so on a HiDPI laptop the whole interface comes out at a fraction
    /// of its intended size: measured on Omarchy (Hyprland), where every label was too small to read.
    /// The Wayland backend is told the output's scale by the compositor and applies it.</para>
    /// <para><c>WithFallback</c> rather than <c>UseWayland</c>: the backend dlopens libwayland,
    /// libxkbcommon and libgbm, and there is no compositor to talk to in an X11 session or over a
    /// plain SSH forward. The fallback is X11, which is exactly what this did before — so the worst
    /// case of adding it is the behaviour we already had.</para>
    /// <para>Linux only, and explicitly: the method exists on every platform and asking for Wayland on
    /// Windows would be a fallback path taken every launch for no reason.</para>
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(OperatingSystem.IsLinux());

    /// <param name="useWayland">Whether to ask for the Wayland backend. A parameter so a test can
    /// take the Linux path on any machine — the ordering fault below is invisible to a Windows dev
    /// box and to CI, which builds Linux but runs no tests against it, and it shipped once.</param>
    internal static AppBuilder BuildAvaloniaApp(bool useWayland)
    {
        // UsePlatformDetect first, always, and that ordering is the whole of it: it is what
        // installs the fallback, and UseWaylandWithFallback throws outright without one —
        // "A fallback windowing backend must be configured before calling UseWaylandWithFallback".
        // Calling the two the other way round is an application that does not start at all, which
        // is how this was shipped in 0.4.43.
        AppBuilder builder = AppBuilder.Configure<App>().UsePlatformDetect();

        if (useWayland)
            builder = builder.UseWaylandWithFallback();

        return builder
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ => CrashHandler.AttachAvaloniaExceptionHandler());
    }
}
