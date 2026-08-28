namespace mTiles.Models;

/// <summary>
/// An image pasted into a Goal tile: the number its marker carries, and the file it was written to.
/// </summary>
/// <remarks>
/// <para><b>A path, not the bytes.</b> The tools this tile drives read an image off disk when they are
/// given somewhere to read it from, and a prompt is a command line — see
/// <c>GoalPromptBuilder.MaxBorrowedChars</c> — so a base64 payload would not survive being fitted into
/// one even once. It keeps the goal file small for the same reason: a session holding three
/// screenshots is three lines rather than several megabytes rewritten at every message.</para>
/// <para>The path is guarded in its own setter, the rule every reference property in
/// <see cref="GoalTileState"/> follows: this one is spelled into a prompt block the tool is told to
/// open, so a null out of the file would throw where nothing expects it.</para>
/// </remarks>
public sealed class GoalImageAttachment
{
    /// <summary>The number in this image's marker — <c>[Image #1]</c> and so on — counting from one in
    /// the order the images were pasted.</summary>
    public int Index { get; set; }

    /// <summary>Where the image was written. Absolute, because the tool is given it verbatim and does
    /// not necessarily run with the workspace as its current directory.</summary>
    public string Path
    {
        get => _path;
        set => _path = value ?? "";
    }
    private string _path = "";
}
