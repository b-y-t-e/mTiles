using mTiles.Services.Agents;
using Xunit;

namespace mTiles.Tests;

/// <summary>That the suite really is independent of what is installed on this machine.</summary>
/// <remarks>
/// The guard that was missing. <c>TestMachine</c> puts the pretence in place, and three tests depend
/// on it — but on a developer's machine those three pass anyway, because the CLIs are genuinely on
/// <c>PATH</c>. So the seam went unexercised and its first version was broken: it seeded the
/// location cache, which expires after thirty seconds, and the tests that needed it run well past
/// the first minute. It failed only on CI, and only after being shipped.
/// <para>This asserts the pretence directly, so a seam that stops working says so on every machine
/// and within a second of the run starting.</para>
/// </remarks>
public class TestMachineTests
{
    [Fact]
    public void Every_agent_reads_as_installed_whatever_this_machine_has()
    {
        Assert.NotEmpty(AiAgentCatalog.All);

        foreach (var agent in AiAgentCatalog.All)
        {
            var located = AiAgentCatalog.Locate(agent);
            Assert.NotNull(located);
            Assert.StartsWith(AiAgentCatalog.PretendedPathPrefix, located);
        }
    }
}
