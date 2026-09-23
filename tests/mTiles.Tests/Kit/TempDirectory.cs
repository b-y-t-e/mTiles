namespace mTiles.Tests;

/// <summary>A directory that goes away with the test.</summary>
/// <remarks>Use this rather than a <c>_dir</c> field and a <c>Dispose</c> of your own. A directory the
/// test leaves open is not a failure: it lives under the run's temporary directory, which
/// <see cref="TestTempRoot"/> clears two runs later.</remarks>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "mtiles-tiles") =>
        Path = Directory.CreateTempSubdirectory(prefix).FullName;

    public string Path { get; }

    /// <summary>A path inside the directory.</summary>
    public string this[string relative] => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* a temp directory nobody will read */ }
    }
}
