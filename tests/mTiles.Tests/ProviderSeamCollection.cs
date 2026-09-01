using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The test classes that replace the provider layer's static seams — <c>AiProvider.HandlerFactory</c>,
/// and the <c>CcsProvider</c> state and process overrides beside it.
/// <para>xUnit runs collections in parallel, and those fields are process-wide: two classes both
/// setting <c>AiProvider.HandlerFactory</c> in a <c>Dispose</c> that nulls it will eventually null it
/// under another class's in-flight test, and the failure looks like a bug in the code under test rather
/// than in the fixture. Naming a shared collection puts them in one lane.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderSeamCollection
{
    public const string Name = "provider-static-seams";
}
