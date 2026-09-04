using System.Runtime.CompilerServices;
using mTiles.Services;

namespace mTiles.Tests;

/// <summary>
/// Points the whole test assembly at a throwaway <c>%APPDATA%/mTiles</c> before the first test runs.
/// </summary>
/// <remarks>
/// <para><b>The floor under <see cref="TempAppData"/>, not a replacement for it.</b> That one is opt-in
/// and per test, which works for the tests that know they touch the application's own directory. This
/// is for the ones that do not know: a class built in a test can reach <c>AppPaths</c> through
/// something several layers down that nobody was thinking about — <c>GoalLog</c> is what made it
/// concrete, since every Goal tile in every test opens one and a full run's worth of prompts and
/// answers then lands in the developer's live installation, unasked and unnoticed. A default that has
/// to be remembered is one that will be forgotten by the next class that needs it.</para>
/// <para>Restoring it is deliberately not attempted. There is nowhere to hang the end of an assembly's
/// life that runs on every path — a crashed or killed run reaches no teardown — and the override is
/// process-wide state in a process that exists only to run these tests. <see cref="TempAppData"/> still
/// saves and restores around itself, so it nests inside this rather than fighting it.</para>
/// <para>The directory is left behind, under the system temp directory and named after the run, for the
/// reason the log copies are: what a failing test wrote is the evidence, and deleting it at the moment
/// the run ends is deleting it exactly when somebody wants to look.</para>
/// </remarks>
internal static class TestAppDataRoot
{
    [ModuleInitializer]
    internal static void Redirect()
    {
        var root = Path.Combine(Path.GetTempPath(), "mTiles-tests",
            $"appdata-{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}");

        try
        {
            Directory.CreateDirectory(root);
            AppPaths.RootOverride = root;
        }
        catch
        {
            // A temp directory that cannot be made is not something a test run can do anything about,
            // and failing here would fail every test rather than the one with the problem.
        }
    }
}
