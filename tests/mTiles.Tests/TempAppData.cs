using mTiles.Services;
using mTiles.Services.Agents;

namespace mTiles.Tests;

/// <summary>
/// A throwaway <c>%APPDATA%/mTiles</c> for the duration of one test.
/// </summary>
/// <remarks>
/// <para><b>Because two tests were writing into the real one.</b> A sign-in's directory and a generated
/// opencode config are both derived from <c>AppPaths.GetAppDataDirectory()</c>, and
/// <c>TempSettings</c> only redirects the files it is handed a path for. Both tests cleaned up in a
/// <c>finally</c>, so a passing run left nothing behind — and a failing or interrupted one left
/// directories inside a live installation, which is the run you least want touching it.</para>
/// <para>The seam is <c>AppPaths.RootOverride</c>, in the same style as
/// <c>AiProvider.HandlerFactory</c>: checked before the real root and restored on disposal, so one test
/// cannot leave the next one pointed at a directory that has been deleted.</para>
/// </remarks>
public sealed class TempAppData : IDisposable
{
    private readonly string? _previous;
    private readonly string? _previousHome;

    public TempAppData()
    {
        _previous = AppPaths.RootOverride;
        _previousHome = OpenCodeProviderConfig.HomeOverride;
        Root = Path.Combine(Path.GetTempPath(), "mTiles-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(Root);
        AppPaths.RootOverride = Root;

        // The other directory a launch reads: OpenCodeProviderConfig merges the user's own
        // opencode.json out of their home directory, so without this a test that prepares a launch
        // copies the developer's configuration - key and all - into the file it generates.
        OpenCodeProviderConfig.HomeOverride = Root;
    }

    /// <summary>The directory standing in for the application's own.</summary>
    public string Root { get; }

    public void Dispose()
    {
        AppPaths.RootOverride = _previous;
        OpenCodeProviderConfig.HomeOverride = _previousHome;

        // Best effort: a file the test left open is not a reason to fail a test that has already
        // finished, and this lives under the system temp directory either way.
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
