using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The test classes that replace this process's <c>PATH</c>.
/// <para>The variable is process-wide, so a test in another class starting git or a shell while it is
/// swapped would not find either and fail at random. <c>DisableParallelization</c> runs this collection
/// alone, with nothing else in flight.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessPathCollection
{
    public const string Name = "process-path";
}
