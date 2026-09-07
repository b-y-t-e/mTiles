using System.Diagnostics;
using System.Globalization;

namespace mTiles.Services;

/// <summary>Reads the desktop's text-scaling factor, and keeps reading it.</summary>
/// <remarks>
/// <para>Two machines, two keys, and neither is exposed by Avalonia:</para>
/// <list type="bullet">
/// <item><b>Linux</b> — <c>org.gnome.desktop.interface</c> / <c>text-scaling-factor</c>, a double. It is
/// not a GNOME-only convention in practice: it is the one cross-toolkit answer there is, which is why
/// a GTK application on Hyprland follows it and why the settings panels that offer a text size on
/// those desktops write it.</item>
/// <item><b>Windows</b> — <c>HKCU\Software\Microsoft\Accessibility\TextScaleFactor</c>, a percentage
/// from 100 to 225. <b>Absent is the normal state</b> and means 100: Windows writes the value only
/// once the slider in Settings → Accessibility → Text size has been moved, so a missing key is not a
/// failure to report.</item>
/// </list>
/// <para><b>Why <c>gsettings</c> and not the settings portal.</b> The portal is the better-mannered
/// route — it works inside a sandbox and it has a change signal — but its GTK backend answers this key
/// by reading the very same GSettings schema. A machine without <c>gsettings-desktop-schemas</c>
/// therefore has nothing for either route to find, so the portal buys no answer this does not already
/// get, at the cost of hand-written D-Bus messages that cannot be exercised from a Windows dev box.
/// If that stops being true — a desktop that publishes a text scale through the portal alone — this is
/// the class that grows a second source, and <see cref="TextScale"/> above it does not change.</para>
/// <para><b>Everything here fails soft, and silence is 1.0.</b> No <c>gsettings</c> on <c>PATH</c>, no
/// schema, a key nobody wrote, output in a shape this does not recognise: all of them leave the text
/// the size the user asked for. A text-scaling reader must never be a reason an application does not
/// start.</para>
/// </remarks>
public sealed class DesktopTextScale : IDisposable
{
    private const string Schema = "org.gnome.desktop.interface";
    private const string Key = "text-scaling-factor";

    private readonly Action _onChanged;
    private Process? _monitor;
    private bool _disposed;

    /// <param name="onChanged">Raised when the desktop's factor moves. Called off the UI thread.</param>
    public DesktopTextScale(Action onChanged) => _onChanged = onChanged;

    /// <summary>Reads the factor now, and starts watching for it to change.</summary>
    /// <remarks>
    /// The first read is synchronous and deliberately so: it happens before the first window is built,
    /// and a factor that arrives afterwards is an interface that visibly resizes itself a moment after
    /// it appears. It is bounded, so a <c>gsettings</c> that hangs costs a second of startup rather
    /// than the launch.
    /// </remarks>
    public void Start()
    {
        TextScale.Set(Read());

        if (OperatingSystem.IsLinux())
            StartMonitor();
    }

    /// <summary>The factor this machine reports, or 1.0 if it reports none.</summary>
    public static double Read()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindows();
            if (OperatingSystem.IsLinux()) return ReadGSettings();
        }
        catch (Exception ex)
        {
            Trace.TraceInformation("Reading the desktop's text scale failed: {0}", ex.Message);
        }

        return 1.0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static double ReadWindows()
    {
        // Not watched. RegNotifyChangeKeyValue would do it, but this is the machine where the setting
        // is rarest and the one where the reader could not be exercised, so the honest arrangement is
        // one read at startup and a sentence saying a change needs a restart, rather than a watcher
        // nobody has seen fire.
        using var key = Microsoft.Win32.Registry.CurrentUser
            .OpenSubKey(@"Software\Microsoft\Accessibility");

        return key?.GetValue("TextScaleFactor") is int percent ? percent / 100.0 : 1.0;
    }

    private static double ReadGSettings()
    {
        var output = RunGSettings(["get", Schema, Key], TimeSpan.FromSeconds(2));
        return Parse(output);
    }

    /// <summary>The number in a line <c>gsettings</c> printed, or 1.0 for anything else.</summary>
    /// <remarks>
    /// <para>Two shapes, because the same value arrives two ways: <c>gsettings get</c> prints the
    /// value alone (<c>1.25</c>) and <c>gsettings monitor</c> prints it with its key
    /// (<c>text-scaling-factor: 1.25</c>). Taking what follows the last colon covers both without the
    /// caller having to say which command it ran.</para>
    /// <para><see cref="CultureInfo.InvariantCulture"/> and not the current one: GVariant prints a
    /// double with a full stop, and this machine's own culture uses a comma — parsed with the current
    /// culture, <c>1.25</c> is refused and read as no answer at all, on exactly the machines where a
    /// mistake here is hardest to notice.</para>
    /// </remarks>
    internal static double Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return 1.0;

        var text = output.Trim();
        var colon = text.LastIndexOf(':');
        if (colon >= 0) text = text[(colon + 1)..].Trim();

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? TextScale.Normalise(value)
            : 1.0;
    }

    /// <summary>Keeps one <c>gsettings monitor</c> alive, which prints a line per change.</summary>
    /// <remarks>
    /// One cheap child for the session, against polling a process on a timer for a value that changes
    /// perhaps twice in a machine's life. It is also the notification and the new value in one, so
    /// there is no second read to race the signal.
    /// </remarks>
    private void StartMonitor()
    {
        try
        {
            var start = GSettingsStart(["monitor", Schema, Key]);
            var process = Process.Start(start);
            if (process is null) return;

            _monitor = process;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (TextScale.Set(Parse(e.Data))) _onChanged();
            };
            process.BeginOutputReadLine();
        }
        catch (Exception ex)
        {
            // A machine with no gsettings simply never reports a change, which is the same thing as a
            // desktop that has no text scale to report.
            Trace.TraceInformation("Watching the desktop's text scale is not available: {0}", ex.Message);
        }
    }

    private static string? RunGSettings(string[] args, TimeSpan timeout)
    {
        using var process = Process.Start(GSettingsStart(args));
        if (process is null) return null;

        var output = process.StandardOutput.ReadToEnd();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return null;
        }

        return process.ExitCode == 0 ? output : null;
    }

    private static ProcessStartInfo GSettingsStart(string[] args)
    {
        var start = new ProcessStartInfo("gsettings")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_monitor is { HasExited: false }) _monitor.Kill(entireProcessTree: true);
            _monitor?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Stopping the text-scale watcher failed: {0}", ex.Message);
        }
    }
}
