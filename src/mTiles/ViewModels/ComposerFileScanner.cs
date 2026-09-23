using mTiles.Services;

namespace mTiles.ViewModels;

/// <summary>
/// Reads a composer's text for the <see cref="ComposerFile"/>s it names — one per tile, because it
/// remembers what the disk said.
/// </summary>
/// <remarks>
/// <para><b>Asked on every keystroke, on the UI thread</b>, so the disk is asked once per path rather than
/// once per keystroke: in a workspace on a network share or <c>\\wsl$</c> every existence check is a round
/// trip, and a draft naming a few files would otherwise stutter under every character typed anywhere in
/// it. Only the paths the latest text still names are remembered, so a mention deleted and typed again is
/// looked up afresh — which is how a file created meanwhile gets its chip.</para>
/// </remarks>
public sealed class ComposerFileScanner(string workspaceDirectory)
{
    private enum Found { Nothing, File, Directory }

    private Dictionary<string, Found> _known = new(StringComparer.Ordinal);

    /// <summary>The files the text names, in the order it names them.</summary>
    public IReadOnlyList<ComposerFile> In(string? text)
    {
        var files = new List<ComposerFile>();
        var stillNamed = new Dictionary<string, Found>(StringComparer.Ordinal);
        foreach (var (start, length, path) in GoalScopeFilter.MentionSpans(text))
        {
            if (FullPathOf(path) is not { } full) continue;
            var found = stillNamed[full] = _known.TryGetValue(full, out var known) ? known : Look(full);
            if (found != Found.Nothing)
                files.Add(new ComposerFile(start, length, text!.Substring(start, length), full, found == Found.Directory));
        }

        _known = stillNamed;
        return files;
    }

    private string? FullPathOf(string mentionPath)
    {
        try
        {
            return Path.IsPathRooted(mentionPath) || string.IsNullOrEmpty(workspaceDirectory)
                ? Path.GetFullPath(mentionPath)
                : Path.GetFullPath(Path.Combine(workspaceDirectory, mentionPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static Found Look(string fullPath) =>
        Directory.Exists(fullPath) ? Found.Directory : File.Exists(fullPath) ? Found.File : Found.Nothing;
}
