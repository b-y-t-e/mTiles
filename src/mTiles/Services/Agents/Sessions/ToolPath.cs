namespace mTiles.Services.Agents.Sessions;

/// <summary>
/// The file name at the end of a path an agent reported, whichever platform wrote it.
/// </summary>
/// <remarks>
/// Not <see cref="System.IO.Path.GetFileName(string)"/>: on Linux that splits on <c>/</c> alone, so a
/// Windows path an agent reports — through WSL, or read back out of a conversation stored on the other
/// platform — came out as the whole of <c>C:\w\probe.txt</c> in a tool row's title.
/// </remarks>
public static class ToolPath
{
    public static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
}
