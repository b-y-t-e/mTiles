using System.Diagnostics;
using System.Security;
using System.Text;

namespace mTiles.Services.Notifications;

/// <summary>Shows one notification on the desktop, outside the window.</summary>
public interface IDesktopNotifier
{
    /// <summary>Fire and forget: a notification that cannot be shown is logged and dropped, never an
    /// exception at whoever asked.</summary>
    void Show(string title, string body);
}

/// <summary>The notifier for this platform.</summary>
/// <remarks>
/// <para><b>Windows: a toast, sent by a hidden Windows PowerShell.</b> The application targets plain
/// <c>net10.0</c>, which cannot reach the WinRT notification API without a Windows-only target framework
/// for the whole project; PowerShell 5.1 can, and is on every Windows machine. A toast is refused
/// silently unless its sender's app id is registered, so the id is written once under
/// <c>HKCU\Software\Classes\AppUserModelId</c> — the key Windows reads for an unpackaged application, and
/// one this application owns, not somebody's configuration.</para>
/// <para><b>Linux: <c>notify-send</c></b>, which every notification daemon — mako, dunst, GNOME's, KDE's —
/// answers through the freedesktop D-Bus interface. Absent, nothing is shown, and that is logged once.</para>
/// </remarks>
public static class DesktopNotifier
{
    public static IDesktopNotifier ForThisPlatform() =>
        OperatingSystem.IsWindows() ? new WindowsToastNotifier()
        : OperatingSystem.IsLinux() ? new NotifySendNotifier()
        : new NullNotifier();

    private sealed class NullNotifier : IDesktopNotifier
    {
        public void Show(string title, string body) { }
    }

    /// <summary>Runs a process with nothing attached and nothing awaited, and logs what went wrong.</summary>
    internal static void Launch(ProcessStartInfo info)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        _ = Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(info);
                if (process is null) return;
                if (!process.WaitForExit(10_000))
                    Trace.WriteLine($"[Notify] {info.FileName} did not finish within ten seconds.");
                else if (process.ExitCode != 0)
                    Trace.WriteLine($"[Notify] {info.FileName} exited {process.ExitCode}.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Notify] {info.FileName} could not be started: {ex.Message}");
            }
        });
    }
}

internal sealed class NotifySendNotifier : IDesktopNotifier
{
    private bool _reportedMissing;

    public void Show(string title, string body)
    {
        if (ExecutableFinder.OnPath("notify-send") is not { } path)
        {
            if (!_reportedMissing)
                Trace.WriteLine("[Notify] notify-send is not on PATH; desktop notifications are off (install libnotify).");
            _reportedMissing = true;
            return;
        }

        var info = new ProcessStartInfo(path);
        info.ArgumentList.Add("--app-name=" + ViewModels.WindowTitle.AppName);
        info.ArgumentList.Add(title);
        info.ArgumentList.Add(body);
        DesktopNotifier.Launch(info);
    }
}

internal sealed class WindowsToastNotifier : IDesktopNotifier
{
    /// <summary>The sender Windows shows the toast as. Registered, never changed: renaming it orphans the
    /// user's own notification settings for this application.</summary>
    internal const string AppId = "mTiles";

    private bool _registered;

    public void Show(string title, string body)
    {
        if (!OperatingSystem.IsWindows()) return;
        EnsureRegistered();

        var info = new ProcessStartInfo("powershell.exe");
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand" })
            info.ArgumentList.Add(arg);
        info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(title, body))));
        DesktopNotifier.Launch(info);
    }

    /// <summary>The script that shows one toast.</summary>
    /// <remarks>The text reaches it twice escaped — as XML inside the toast, then as a single-quoted
    /// PowerShell string, where the only special character is the quote itself. A tile's name is typed
    /// by the user and must not be able to become a command.</remarks>
    internal static string Script(string title, string body)
    {
        var xml = "<toast><visual><binding template=\"ToastGeneric\">"
                  + $"<text>{SecurityElement.Escape(title)}</text>"
                  + $"<text>{SecurityElement.Escape(body)}</text>"
                  + "</binding></visual></toast>";
        return string.Join('\n',
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null",
            "[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null",
            "$xml = New-Object Windows.Data.Xml.Dom.XmlDocument",
            $"$xml.LoadXml('{xml.Replace("'", "''")}')",
            $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{AppId}').Show([Windows.UI.Notifications.ToastNotification]::new($xml))");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Classes\AppUserModelId\" + AppId);
            key.SetValue("DisplayName", ViewModels.WindowTitle.AppName);
            var icon = Path.Combine(AppContext.BaseDirectory, "mtiles.ico");
            if (File.Exists(icon)) key.SetValue("IconUri", icon);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Notify] Could not register the notification sender: {ex.Message}");
        }
    }
}
