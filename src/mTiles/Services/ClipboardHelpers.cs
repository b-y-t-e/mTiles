using mTiles.Models;

namespace mTiles.Services;

/// <summary>
/// Whether an AI CLI running in a tile can take an image off this machine's clipboard — and, when it
/// cannot, what would make it able to.
/// </summary>
/// <remarks>
/// <para><b>Not something this application does for them.</b> mTiles hands the keystroke over and stops
/// there (<c>ForwardCtrlVWhenClipboardHasNoText</c>): the agent reads the clipboard itself, and on Linux
/// none of them has a clipboard of its own — they shell out. Measured 2026-09-12 against Claude Code
/// 2.1.269 and opencode 1.18.18, both run <c>xclip -selection clipboard -t image/png -o</c> and
/// <c>wl-paste --type image/png</c>, one as the other's fallback, after a check of the same shape.</para>
/// <para><b>The failure is silent at every layer, which is the whole reason this class exists.</b> With
/// neither program installed the check exits non-zero, Claude Code returns null and opencode gets an
/// empty buffer; neither says anything, and the terminal shows a keystroke that did nothing. Our own log
/// says <c>clipboard has no text (forwarding key: True)</c>, which is us reporting success. So the one
/// place the cause can be named is here, before the user has spent an afternoon on it.</para>
/// <para><b>Asked of <c>PATH</c> and nothing else</b> — deliberately not <see
/// cref="ExecutableFinder.Anywhere"/>, which is right for the agents themselves because it also looks
/// where a global npm or cargo install puts a binary that this process' <c>PATH</c> never mentions. Here
/// the question is what the <em>child</em> will find, and a child inherits exactly our <c>PATH</c>: a
/// <c>wl-paste</c> in a directory nobody exports is one the agent cannot run either, so finding it would
/// be an answer of the wrong question.</para>
/// </remarks>
internal static class ClipboardHelpers
{
    /// <summary>The programs the CLIs reach for.</summary>
    /// <remarks>Which of the two can answer is the <em>session's</em> business — <c>wl-paste</c> talks to
    /// a Wayland compositor and <c>xclip</c> to an X server — which is why nothing here asks about them
    /// as a pair. Both are installed together all the same, because a machine that is on one today logs
    /// into the other tomorrow.</remarks>
    public static readonly string[] Programs = ["wl-paste", "xclip"];

    /// <summary>What kind of display server this session talks to.</summary>
    /// <remarks><see cref="Unknown"/> is a third answer and not a default: a session that says neither
    /// — a plain tty, a stripped environment — is one where nothing here can name the program that would
    /// work, and asserting one would be inventing the measurement.</remarks>
    internal enum SessionKind
    {
        Unknown,
        Wayland,
        X11,
    }

    /// <summary>Which display server this session is on, from what it says about itself.</summary>
    /// <remarks>Pure, and asked of three variables in that order because they disagree: freedesktop's own
    /// <c>XDG_SESSION_TYPE</c> is the stated answer where a login manager set one, and the two display
    /// variables are the evidence where nothing did. <c>WAYLAND_DISPLAY</c> outranks <c>DISPLAY</c>
    /// because XWayland sets both, and a Wayland session with XWayland reaches its clipboard through the
    /// compositor.</remarks>
    internal static SessionKind KindOf(string? sessionType, string? waylandDisplay, string? display)
    {
        if (string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase))
            return SessionKind.Wayland;
        if (string.Equals(sessionType, "x11", StringComparison.OrdinalIgnoreCase))
            return SessionKind.X11;

        if (!string.IsNullOrEmpty(waylandDisplay)) return SessionKind.Wayland;
        if (!string.IsNullOrEmpty(display)) return SessionKind.X11;

        return SessionKind.Unknown;
    }

    /// <summary>The programs that could answer in a session of this kind.</summary>
    /// <remarks><b>One program, not a pair, wherever the session is known.</b> Accepting either was the
    /// silent failure this class exists to name, one step further along: an X11 machine carrying only
    /// <c>wl-clipboard</c> — or a Wayland one carrying only <c>xclip</c> — passed the check, got neither
    /// the sentence nor the button, and went on pasting screenshots into an agent that received
    /// nothing. Where the session says nothing, either is still enough: a guess that withheld the
    /// clipboard from a machine whose agent can reach it would be the same mistake facing the other
    /// way.</remarks>
    internal static IReadOnlyList<string> ProgramsFor(SessionKind kind) => kind switch
    {
        SessionKind.Wayland => ["wl-paste"],
        SessionKind.X11 => ["xclip"],
        _ => Programs,
    };

    /// <summary>What this session is, read from this process' own environment.</summary>
    private static SessionKind ThisSession => KindOf(
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
        Environment.GetEnvironmentVariable("DISPLAY"));

    /// <summary>The packages those programs come in.</summary>
    public static readonly string[] Packages = ["wl-clipboard", "xclip"];

    /// <summary>
    /// The package managers this knows how to ask, and how each one is asked.
    /// </summary>
    /// <remarks>
    /// <b>None of them is told to skip its confirmation.</b> The command runs in a tile the user is
    /// looking at, which is the only reason offering it is honest at all, and a prompt answered there is
    /// worth more than one fewer keystroke — an install that has already happened by the time the tile
    /// is drawn is the shape of thing this whole route exists to avoid.
    /// </remarks>
    internal static readonly (string Manager, string[] Arguments)[] Managers =
    [
        ("pacman", ["-S", "--needed", .. Packages]),
        ("apt-get", ["install", .. Packages]),
        ("dnf", ["install", .. Packages]),
        ("zypper", ["install", .. Packages]),
        ("apk", ["add", .. Packages]),
        ("xbps-install", ["-S", .. Packages]),
    ];

    /// <summary>How a command is raised to root here, best first.</summary>
    private static readonly string[] Elevators = ["sudo", "doas"];

    /// <summary>Whether this machine is one where the question arises at all.</summary>
    /// <remarks>Linux only, and not for want of measuring the others: Claude Code reads the clipboard
    /// through PowerShell on Windows and <c>osascript</c> on macOS, both of which are part of the
    /// operating system. There is nothing to install and nothing that can be missing.</remarks>
    public static bool AppliesHere => OperatingSystem.IsLinux();

    /// <summary>Whether an agent started here would find a way to the clipboard.</summary>
    /// <remarks>True everywhere the question does not arise, so a caller may ask without testing the
    /// platform first — the answer "nothing is wrong here" is the same answer in both cases.</remarks>
    public static bool ArePresent =>
        !AppliesHere || ProgramsFor(ThisSession).Any(program => ExecutableFinder.OnPath(program) is not null);

    /// <summary>What installing them would run, or null when nothing here can say.</summary>
    /// <remarks>Null is <em>this machine's package manager is not one of the six</em>, which is a real
    /// answer and not a failure: the sentence naming the two programs still stands, and a user on a
    /// distribution nobody here has met can act on it. Offering a command built for the wrong manager
    /// would be worse than offering none.</remarks>
    public static InstallPlan? Install
    {
        get
        {
            if (!AppliesHere || ArePresent) return null;

            var manager = Managers.FirstOrDefault(m => ExecutableFinder.OnPath(m.Manager) is not null);
            if (manager.Manager is null) return null;

            var elevator = Elevators.FirstOrDefault(name => ExecutableFinder.OnPath(name) is not null);
            return PlanFor(manager.Manager, manager.Arguments, elevator);
        }
    }

    /// <summary>
    /// One sentence about what is missing, for the page that has to say it.
    /// </summary>
    /// <remarks>It names the programs rather than the feature, because the programs are what a user can
    /// check for themselves and what every search about this will be about.</remarks>
    public static string Explanation => ExplanationFor(ThisSession);

    /// <summary>The same sentence, for a session of a given kind.</summary>
    /// <remarks>Pure, and it names <em>the</em> program wherever the session names one: "neither is on
    /// this machine" is untrue on a Wayland box that has <c>xclip</c> and no <c>wl-clipboard</c>, and a
    /// sentence a user can disprove in one <c>which</c> is one they stop believing about the rest of
    /// it.</remarks>
    internal static string ExplanationFor(SessionKind kind)
    {
        var wanted = ProgramsFor(kind);
        var names = string.Join(" or ", wanted);
        var missing = wanted.Count == 1 ? "it is not on this machine" : "neither is on this machine";
        var until = wanted.Count == 1 ? "Until it is" : "Until one is";

        return $"AI tools read an image off the clipboard by running {names}, and {missing}. " +
            $"{until}, pasting a screenshot into an agent tile does nothing and says nothing.";
    }

    /// <summary>
    /// The command, given a manager and whatever raises it to root.
    /// </summary>
    /// <remarks>Pure, and separated from the probing above for the reason <c>ChainPolicy</c> is
    /// separated from the chain: what gets run on somebody's machine with elevation should be readable
    /// in a table rather than only by installing six distributions. A null <paramref name="elevator"/>
    /// yields the bare command, which is right for a session that is already root and honest everywhere
    /// else — it fails saying it needs privileges, in a tile, which names the problem better than a
    /// <c>sudo</c> that is not installed either.</remarks>
    internal static InstallPlan PlanFor(string manager, IReadOnlyList<string> arguments, string? elevator)
    {
        var note = elevator is null
            ? $"Installs {string.Join(" and ", Packages)} with {manager}. Run it as root."
            : $"Installs {string.Join(" and ", Packages)} with {manager}. It asks for your password in the tile.";

        return elevator is null
            ? new InstallPlan(manager, [.. arguments], note)
            : new InstallPlan(elevator, [manager, .. arguments], note);
    }
}
