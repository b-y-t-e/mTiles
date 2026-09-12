using mTiles.Services;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The command offered for the two programs an AI CLI needs to reach the clipboard on Linux.
/// </summary>
/// <remarks>
/// A table, for the reason <c>ChainDecisionTests</c> is one: this is a line that runs on somebody's
/// machine with elevation, and the only other way to read all six of them is to install six
/// distributions. What the probing around it answers — which manager is here, whether there is a
/// <c>sudo</c> — is the filesystem's business and is deliberately not what these assert.
/// </remarks>
public class ClipboardHelpersTests
{
    [Fact]
    public void The_elevator_goes_in_front_of_the_manager_and_not_around_it()
    {
        (string Manager, string[] Arguments, string? Elevator, string Expected, string Why)[] cases =
        [
            ("pacman", ["-S", "--needed", "wl-clipboard", "xclip"], "sudo",
                "sudo pacman -S --needed wl-clipboard xclip", "the ordinary Arch case"),
            ("apt-get", ["install", "wl-clipboard", "xclip"], "sudo",
                "sudo apt-get install wl-clipboard xclip", "Debian and its descendants"),
            ("apk", ["add", "wl-clipboard", "xclip"], "doas",
                "doas apk add wl-clipboard xclip", "a machine with doas rather than sudo"),
            // Nothing to raise it with is not a reason to withhold the command: a root session runs it
            // as it stands, and anywhere else it fails in a tile saying it needs privileges — which
            // names the problem better than a sudo that is not installed either.
            ("dnf", ["install", "wl-clipboard", "xclip"], null,
                "dnf install wl-clipboard xclip", "no elevator on this machine"),
        ];

        foreach (var (manager, arguments, elevator, expected, why) in cases)
        {
            var plan = ClipboardHelpers.PlanFor(manager, arguments, elevator);
            Assert.Equal(expected, plan.CommandLine);
        }
    }

    /// <summary>
    /// The note says which of the two it is, because the tile is where the password is typed.
    /// </summary>
    [Fact]
    public void The_note_warns_about_the_password_only_when_there_is_one_to_type()
    {
        Assert.Contains("password",
            ClipboardHelpers.PlanFor("pacman", ["-S"], "sudo").Note);
        Assert.DoesNotContain("password",
            ClipboardHelpers.PlanFor("pacman", ["-S"], null).Note);
    }

    /// <summary>
    /// No manager is told to skip its own confirmation.
    /// </summary>
    /// <remarks>
    /// The one rule in the table that a later hand would undo without noticing, and the one that makes
    /// offering this honest at all: the command runs in a tile the user is looking at, and a prompt they
    /// answer there is worth more than one keystroke saved. An install already finished by the time the
    /// tile is drawn is exactly the shape of thing this whole route exists to avoid.
    /// </remarks>
    [Fact]
    public void No_manager_is_told_to_answer_its_own_question()
    {
        string[] skips = ["-y", "--yes", "--noconfirm", "--assumeyes", "--non-interactive"];

        foreach (var (manager, arguments) in ClipboardHelpers.Managers)
            foreach (var argument in arguments)
                Assert.False(skips.Contains(argument),
                    $"{manager} is told to skip its confirmation with {argument}");
    }

    /// <summary>
    /// Every manager installs both packages, which is the point of installing either.
    /// </summary>
    /// <remarks>Which of the two answers is the session's business — <c>wl-paste</c> talks to a
    /// compositor and <c>xclip</c> to an X server — and a machine on one today logs into the other
    /// tomorrow, so a table that grew an entry naming only one would leave that distribution's users
    /// with the failure this exists to prevent, on half their logins.</remarks>
    [Fact]
    public void Every_manager_installs_both_packages()
    {
        foreach (var (manager, arguments) in ClipboardHelpers.Managers)
            foreach (var package in ClipboardHelpers.Packages)
                Assert.True(arguments.Contains(package), $"{manager} does not install {package}");
    }

    /// <summary>
    /// Which program is wanted is the session's answer, not "whichever of the two happens to be here".
    /// </summary>
    /// <remarks>
    /// The failure this closes is the silent one a step along from the one the class was written for: a
    /// machine in an X11 session carrying only <c>wl-clipboard</c> — and the Wayland machine carrying
    /// only <c>xclip</c> — has a program on <c>PATH</c> that cannot reach its clipboard, so accepting
    /// either left it with no sentence, no button and a screenshot the agent never receives.
    /// </remarks>
    [Fact]
    public void A_session_that_says_what_it_is_wants_one_program_and_not_either()
    {
        (string? SessionType, string? Wayland, string? Display,
            ClipboardHelpers.SessionKind Kind, string[] Wanted, string Why)[] cases =
        [
            ("wayland", "wayland-0", ":0", ClipboardHelpers.SessionKind.Wayland, ["wl-paste"],
                "a Wayland session with XWayland beside it — the compositor holds the clipboard"),
            ("x11", null, ":0", ClipboardHelpers.SessionKind.X11, ["xclip"],
                "the ordinary X11 session"),
            // No login manager set XDG_SESSION_TYPE, so the display variables are the evidence.
            (null, "wayland-1", null, ClipboardHelpers.SessionKind.Wayland, ["wl-paste"], "bare Wayland"),
            (null, null, ":1", ClipboardHelpers.SessionKind.X11, ["xclip"], "bare X11"),
            ("", "", "", ClipboardHelpers.SessionKind.Unknown, ["wl-paste", "xclip"],
                "an empty variable says nothing, exactly as an absent one does"),
            // A tty, or an environment somebody stripped: nothing here can name the one that works, and
            // withholding the clipboard from a machine whose agent can reach it is the same mistake back
            // to front.
            ("tty", null, null, ClipboardHelpers.SessionKind.Unknown, ["wl-paste", "xclip"],
                "no display server named at all"),
        ];

        foreach (var (sessionType, wayland, display, kind, wanted, why) in cases)
        {
            Assert.Equal(kind, ClipboardHelpers.KindOf(sessionType, wayland, display));
            Assert.Equal(wanted, ClipboardHelpers.ProgramsFor(kind));
        }
    }

    /// <summary>
    /// The sentence names the program the session actually needs.
    /// </summary>
    /// <remarks>"Neither is on this machine" is untrue on a Wayland box that has <c>xclip</c> and no
    /// <c>wl-clipboard</c>, and a sentence a user can disprove with one <c>which</c> is one they stop
    /// believing about the rest of it.</remarks>
    [Fact]
    public void The_sentence_names_the_program_this_session_needs()
    {
        var wayland = ClipboardHelpers.ExplanationFor(ClipboardHelpers.SessionKind.Wayland);
        Assert.Contains("wl-paste", wayland);
        Assert.DoesNotContain("xclip", wayland);
        Assert.DoesNotContain("neither", wayland);

        var x11 = ClipboardHelpers.ExplanationFor(ClipboardHelpers.SessionKind.X11);
        Assert.Contains("xclip", x11);
        Assert.DoesNotContain("wl-paste", x11);

        var unknown = ClipboardHelpers.ExplanationFor(ClipboardHelpers.SessionKind.Unknown);
        Assert.Contains("wl-paste", unknown);
        Assert.Contains("xclip", unknown);
        Assert.Contains("neither", unknown);
    }

    /// <summary>
    /// Only Linux has the question, and everywhere else the answer is "nothing is wrong here".
    /// </summary>
    /// <remarks>Claude Code reads the clipboard through PowerShell on Windows and <c>osascript</c> on
    /// macOS, both part of the operating system. <c>ArePresent</c> answering true there rather than
    /// false is what lets a caller ask without testing the platform first — and a notice that appeared
    /// on Windows would be advice nobody could act on.</remarks>
    [Fact]
    public void The_question_is_asked_on_Linux_alone()
    {
        Assert.Equal(OperatingSystem.IsLinux(), ClipboardHelpers.AppliesHere);

        if (!ClipboardHelpers.AppliesHere)
        {
            Assert.True(ClipboardHelpers.ArePresent);
            Assert.Null(ClipboardHelpers.Install);
        }
    }
}
