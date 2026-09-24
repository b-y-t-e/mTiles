using Xunit;

namespace mTiles.Tests;

/// <summary>
/// The test classes that replace the provider layer's static seams — <c>AiProvider.HandlerFactory</c>
/// (through <see cref="HttpStub"/>), and the <c>CcsProvider</c> state and process overrides beside it.
/// <para>Those fields are process-wide. The assembly runs serially today (<c>AssemblyInfo.cs</c>), so
/// this changes nothing now; it records which classes would have to share one lane if parallelisation
/// were turned back on, since a class nulling a seam in <c>Dispose</c> under another's in-flight test
/// fails as though the code under test were wrong.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProviderSeamCollection
{
    public const string Name = "provider-static-seams";
}
