
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

    /// <summary>
    /// Whether this is a fragment of something still being written rather than a whole thing.
    /// </summary>
    /// <remarks>
    /// Only the agent knows. A whole assistant message ends where it ends and the next one starts a
    /// paragraph of its own; a <c>content_block_delta</c> is often half a word, so a break between two
    /// of those puts one inside the word. Joined without the distinction, the end of one message and
    /// the start of the next become one line — which is enough to stop a closing code fence being a
    /// closing code fence, and the block inside it being read.
    /// </remarks>
    public bool Partial { get; init; }
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
    /// <summary>
    /// Everything the tool said during the run, in order, or empty when there was no way to tell.
    /// </summary>
    /// <remarks>
    /// <para>Not the same question as <see cref="Text"/>, which is the tool's own <em>final</em> word
    /// and is what the user is shown. Measured live, 2026-09-09: a review that had already written its
    /// json block was interrupted by a background task finishing, wrote one more paragraph about it,
    /// and that paragraph was the whole of what this side ever saw — the findings, the severities and
    /// the verdict all went to a message nobody read, and the goal was marked unmet with nothing on
    /// screen saying why. The block is the deliverable and the last message is not always where it is.
    /// </para>
    /// <para>Empty for a tool that cannot stream, where there is only ever one blob of output —
    /// <see cref="Transcript"/> is what callers ask, and it falls back to the answer itself.</para>
    /// </remarks>
    public string WholeTurn { get; init; } = "";

    /// <summary>Where to look for a block that <see cref="Text"/> did not carry: the whole turn when
    /// there is one, and otherwise the answer, which is then all there was.</summary>
    public string Transcript => string.IsNullOrEmpty(WholeTurn) ? Text : WholeTurn;

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
