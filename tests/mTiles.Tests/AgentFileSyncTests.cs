using Xunit;

namespace mTiles.Tests;

/// <summary>
/// Keeps the two file-watcher suites off each other's back: both drive a real
/// <see cref="System.IO.FileSystemWatcher"/> and assert within a few seconds, so running them
/// alongside one another is the load that turns a debounce into a flake.
/// </summary>
[CollectionDefinition(CollectionName)]
public sealed class AgentFileSyncTests
{
    public const string CollectionName = "agent file sync";
}
