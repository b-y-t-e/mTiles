using System.Runtime.InteropServices;

namespace mTiles.Services.Phone;

/// <summary>Whether the person using this application is sitting at the machine or connected to it.</summary>
internal enum SessionLocation
{
    /// <summary>We could not tell. Neither audience is favoured and the ranking falls back on the network alone.</summary>
    Unknown,

    /// <summary>A local session — the user is at this machine, so their phone is probably on the same Wi-Fi.</summary>
    Console,

    /// <summary>A remote session (RDP, or a shell over SSH). The user — and their phone — are somewhere else.</summary>
    Remote,
}

/// <summary>
/// Answers whether this session is being driven from the machine itself or from somewhere else.
/// </summary>
/// <remarks>
/// The one signal that makes the QR panel's guess good rather than a coin toss, and it is nearly free.
/// The phone is next to the *user*, so where the user is decides which address can possibly reach it: at
/// the console the LAN address is the answer and the tunnel is a detour; over RDP the LAN address cannot
/// work at all, because the phone is on a network with no route to this machine.
/// <para>An interface rather than a static call because it is the input to a pure ranking that has to be
/// testable at both values, and because the honest answer on a platform we have not thought about is
/// <see cref="SessionLocation.Unknown"/> rather than a guess.</para>
/// </remarks>
internal interface ISessionLocationProbe
{
    SessionLocation Current { get; }
}

/// <summary>Reads the session location from the operating system.</summary>
internal sealed class SessionLocationProbe : ISessionLocationProbe
{
    /// <summary><c>SM_REMOTESESSION</c>. Non-zero when the calling process is running in a Terminal
    /// Services (Remote Desktop) client session.</summary>
    private const int SmRemoteSession = 0x1000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public SessionLocation Current
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    return GetSystemMetrics(SmRemoteSession) != 0 ? SessionLocation.Remote : SessionLocation.Console;

                // On Linux the application is a desktop app, so a session with a display server is
                // somebody sitting in front of it — unless it arrived over SSH with X11 forwarding, which
                // SSH_CONNECTION reveals. Anything else is genuinely unknown, and says so.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")))
                    return SessionLocation.Remote;

                var display = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")
                              ?? Environment.GetEnvironmentVariable("DISPLAY");
                return string.IsNullOrEmpty(display) ? SessionLocation.Unknown : SessionLocation.Console;
            }
            catch
            {
                return SessionLocation.Unknown;
            }
        }
    }
}
