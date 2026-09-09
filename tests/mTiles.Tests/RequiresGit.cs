using System.Diagnostics;
using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Whether this machine has git, for the handful of tests that spawn it.
/// </summary>
/// <remarks>
/// <para><b>It fails loudly rather than passing quietly.</b> An earlier version of the baseline tests
/// returned early when git was missing, which xunit 2 reports as a pass — a test that says nothing and
/// says it in green, over the part of the Goal tile that stands between a destroyed afternoon and one
/// command. Skipping properly is not available either: dynamic skip arrived in xunit v3, and this
/// project is on v2, so the choice is between a loud failure and a green lie. The premise behind
/// skipping was wrong anyway — this repository cannot be checked out without git, so a machine running
/// these without it does not exist.</para>
/// <para>Here rather than in one test class because there are two of them now, and a second copy of the
/// probe is a second place for the rule to be decided differently.</para>
/// </remarks>
internal static class RequiresGit
{
    /// <param name="what">What the caller cannot say anything about without git, named in the failure.
    /// </param>
    public static void OrFail(string what) =>
        Assert.True(IsInstalled(), $"git is not on PATH, so this cannot say anything about {what}.");

    private static bool IsInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
