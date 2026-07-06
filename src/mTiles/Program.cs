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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ => CrashHandler.AttachAvaloniaExceptionHandler());
}
