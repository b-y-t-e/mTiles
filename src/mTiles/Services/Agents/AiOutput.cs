
namespace mTiles.Services.Agents;

/// <summary>What one piece of a tool's output is.</summary>
public enum AiChunkKind
{
    /// <summary>Words the tool wrote. Kept, and joined in order, as the fallback answer.</summary>
    Text,

    /// <summary>The tool's own final text — its answer, in preference to this side's reassembly.
    /// </summary>
    Result,

    /// <summary>What it is doing this second: a file, a command, a skill. Shown and thrown away.
    /// </summary>
    Activity,

    /// <summary>The tool saying it failed. Never the answer while there is one, and never silently
    /// dropped either.</summary>
    Error,

    /// <summary>A tool call the tool was refused permission for. Counted rather than shown: one of
    /// these is normal — an agent asking for something it does not need — and a run made entirely of
    /// them is the tile's own permission mode being wrong, which is what the count is for.</summary>
    Denied,
}

public sealed class AiOutputChunk
{
    public AiChunkKind Kind { get; init; } = AiChunkKind.Text;
    public string Content { get; init; } = "";
}

/// <summary>
/// What a run produced, and whether the tool said it failed.
/// </summary>
/// <remarks>
/// The flag is separate from the text because they answer different questions and the loop needs both.
/// It used to be text alone, so a run that ended in <c>error_max_turns</c> or a refused API key came
/// back as a non-empty string and was judged <c>Answered</c> — the failure adopted as the plan, or as
/// the review, and acted on. Throwing the text away instead would have been the other half of the same
/// mistake: a failed implementation has usually already written files, and what it managed to say about
/// them is the only account of what is now in the worktree.
/// </remarks>
public readonly record struct AiOutput(string Text, bool Failed, int PermissionDenials = 0)
{
    /// <summary>A run that said something and did not fail. Named rather than implicit: a conversion
    /// from string would set <c>Failed</c> to false silently, and that bit is the whole of what this
    /// type was introduced to stop being decided by accident.</summary>
    public static AiOutput Answered(string text) => new(text, Failed: false);

    /// <summary>A run the tool said had failed, keeping whatever it managed to say.</summary>
    public static AiOutput Failure(string text) => new(text, Failed: true);

    /// <summary>
    /// A bare string is an answer that did not fail.
    /// </summary>
    /// <remarks>
    /// Kept for the tests, which stand in for a tool a few dozen times over and mean "it answered this"
    /// every time. Nothing in the application converts a string any more — every producer here names
    /// <see cref="Answered"/> or <see cref="Failure"/>, so the bit this type exists to carry is chosen
    /// rather than defaulted wherever it actually matters.
    /// </remarks>
    public static implicit operator AiOutput(string text) => Answered(text);
}
