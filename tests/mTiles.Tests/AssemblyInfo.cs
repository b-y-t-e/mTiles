using System.Runtime.CompilerServices;
using Avalonia.Headless;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(mTiles.Tests.TestApp))]

// The headless Avalonia session is single-threaded and shared by the whole assembly; running these
// alongside anything else in parallel drops tests without reporting it as a failure.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace mTiles.Tests;

/// <summary>Makes the suite independent of what is installed on the machine running it.</summary>
/// <remarks>
/// <para>Whether an AI CLI is on <c>PATH</c> is a fact about the developer's laptop, and several
/// tests were quietly asserting it: the sign-in form's tool list and a tile's instance chooser are
/// both narrowed by <c>AiAgentCatalog.IsAvailable</c>, so on a machine with Claude Code and codex
/// installed they had something to assert on, and on a bare CI agent they were empty and the
/// assertions said nothing. Three of them failed the first time the suite ran on Linux.</para>
/// <para>A module initializer rather than a fixture: it has to be in place before the first test
/// touches the catalogue, and there is nothing to tear down — the cache is process-wide and this
/// process is the test run. Parallelization is off for the whole assembly (above), so nothing races
/// it.</para>
/// <para>The consequence to know: no test here can observe an agent as <em>not</em> installed.
/// <c>AiAgentCatalog.ForgetWhatIsInstalled</c> is there for one that needs to, and it would have to
/// put the pretence back.</para>
/// </remarks>
internal static class TestMachine
{
    [ModuleInitializer]
    internal static void EveryAgentIsInstalled() =>
        mTiles.Services.Agents.AiAgentCatalog.PretendEveryAgentIsInstalled();
}
