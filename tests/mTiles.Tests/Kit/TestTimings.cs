using System.Runtime.CompilerServices;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Agents;
using mTiles.ViewModels;
using mTiles.ViewModels.AgentConversation;

namespace mTiles.Tests;

/// <summary>
/// Every clock the application waits on, shortened once for the whole run.
/// </summary>
/// <remarks>
/// <para>The rule this enforces: <b>no test waits on a real clock</b>. A debounce, a quiet window, a
/// poll or a countdown is the application's own interval, not the thing under test, and sat out at its
/// production length it was most of the suite's wall time — four Goal tests alone spent 150 s watching
/// the review gate count down from fifteen.</para>
/// <para>A test that is <em>about</em> an interval states it relative to the value here
/// (<c>SkillChangePolicy.QuietWindow * 3</c>), never as a literal, so it scales with this file. A new
/// interval in <c>src/</c> gets a settable property and a line here, or its tests will be the next slow
/// ones.</para>
/// <para>Short, not zero: a coalescing window of zero stops coalescing, and the tests that pin
/// coalescing would then be testing something else.</para>
/// </remarks>
internal static class TestTimings
{
    [ModuleInitializer]
    internal static void Shorten()
    {
        AppDefaults.SaveDebounceMs = 50;
        AppDefaults.WatcherDebounceMs = 50;
        GoalTileViewModel.GateTick = TimeSpan.FromMilliseconds(10);
        SkillChangePolicy.QuietWindow = TimeSpan.FromMilliseconds(100);
        WorkspaceGitWatcher.RepositoryPollInterval = TimeSpan.FromMilliseconds(100);
        AgentSessionWatcher.QuietWindow = TimeSpan.FromMilliseconds(50);
        AgentSessionWatcher.AttachRetryInterval = TimeSpan.FromMilliseconds(100);
        AgentConversationTileViewModel.StopButtonHold = TimeSpan.FromMilliseconds(100);
    }
}
