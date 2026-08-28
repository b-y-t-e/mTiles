namespace mTiles.Services;

/// <summary>
/// Where an image pasted into a Goal tile is kept.
/// </summary>
/// <remarks>
/// <para><b>In the workspace, not in the temporary directory</b>, because the path outlives the paste
/// by a long way: it is written into the goal file, handed to every prompt of the run, and opened by a
/// tool that may not start until the user comes back and resumes. A temporary file swept while a run
/// was paused would leave a marker pointing at nothing, and the tool would be told to open it anyway.
/// </para>
/// <para>Beside the goal files under <c>.mtiles/</c>, which the Git tile keeps ignored, so pasting a
/// screenshot does not put an untracked binary in front of the user in their own repository.</para>
/// <para>Never pruned. What a sweep would reclaim is a few hundred kilobytes; what it can cost is a
/// goal that no longer resolves its own markers, which is not a trade to make on the user's behalf.
/// </para>
/// </remarks>
internal sealed class GoalImageStore(string workingDirectory)
{
    /// <summary>Stands in for the writing, so the tile's own failure path can be driven.</summary>
    /// <remarks>
    /// The seam this class was missing, and the same one <c>GoalBaseline.Factory</c> and
    /// <c>GoalTileViewModel.AiRunnerFactory</c> give the rest of the tile. Without it the rule the view
    /// model calls the trap — <b>a save that fails inserts no marker</b>, because a marker whose file
    /// was never written is one the tool is told to open — could only be reached by making a real
    /// directory unwritable, so it was the one load-bearing rule here with no test behind it.
    /// </remarks>
    internal static Func<byte[], string>? Factory { get; set; }


    private readonly string _directory = WorkspacePaths.Combine(workingDirectory, "goals", "images");

    /// <summary>Writes one image and returns the absolute path it went to.</summary>
    /// <remarks>
    /// <para>PNG, and not a guess: the clipboard's own format is gone by the time this is reached —
    /// Avalonia hands back a decoded bitmap, and the view encodes it once — so there is no platform
    /// format left to preserve and nothing to sniff. Lossless, which matters when the image is a
    /// screenshot of text, which is what these mostly are.</para>
    /// <para>The name carries the time it was pasted and a guid: the time so a user opening the
    /// directory can tell which run an image belongs to, the guid because two pastes within one second
    /// are one keystroke apart and the second must not overwrite the first.</para>
    /// </remarks>
    public string SavePng(byte[] image)
    {
        if (Factory is { } stub) return stub(image);

        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, image);
        return path;
    }
}
