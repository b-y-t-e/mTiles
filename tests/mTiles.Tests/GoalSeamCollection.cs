using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The test classes that replace the Goal tile's static seams — <c>WorktreeReader.Factory</c>,
/// <c>GoalTileViewModel.AiRunnerFactory</c>.
/// <para>xUnit runs collections in parallel, and those three fields are process-wide: two classes both
/// setting <c>WorktreeReader.Factory</c> in their constructors and clearing it in <c>Dispose</c> will
/// eventually clear each other's, and the failure looks like a bug in the code under test rather than
/// in the fixture. Naming a shared collection puts them in one lane.</para>
/// <para>The seams are static on purpose — a constructor argument every call site has to pass null for
/// is one that gets the wrong thing eventually — so this is the price of that decision, paid where it
/// is visible rather than by making the seams instance state nothing in the application would set.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GoalSeamCollection
{
    public const string Name = "goal-static-seams";
}
