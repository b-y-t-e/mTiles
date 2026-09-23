using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The two file-watcher suites, named as one collection because both drive a real
/// <see cref="System.IO.FileSystemWatcher"/> and assert within a debounce or two.
/// </summary>
/// <remarks>The assembly runs serially today (<c>AssemblyInfo.cs</c>), so this changes nothing now; it
/// records that these two must stay off each other's back if parallelisation is ever turned on, since
/// running them alongside one another is the load that turns a debounce into a flake.</remarks>
[CollectionDefinition(CollectionName)]
public sealed class AgentFileSyncTests
{
    public const string CollectionName = "agent file sync";
}
