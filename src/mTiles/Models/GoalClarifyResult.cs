namespace mTiles.Models;

/// <summary>One question the tool wants answered before it plans anything.</summary>
public sealed class GoalQuestion
{
    public string Question
    {
        get => _question;
        set => _question = value ?? "";
    }
    private string _question = "";

    /// <summary>Why it is being asked. Shown quieter than the question itself, and left out when the
    /// tool did not say — a reason invented to fill the field would be worse than none.</summary>
    public string Why
    {
        get => _why;
        set => _why = value ?? "";
    }
    private string _why = "";

    /// <summary>Answers the tool considers likely, if it offered any. They become the suggested text in
    /// the numbered skeleton the composer is filled with, so answering is editing rather than typing.
    /// </summary>
    public List<string> Options
    {
        get => _options;
        set => _options = value ?? [];
    }
    private List<string> _options = [];
}

/// <summary>
/// A clarification round, read rather than shown as-is.
/// <para>The questions used to be a paragraph of prose and the answer a single free-text message, so
/// nothing connected an answer to the question it answered — including on the next round, where the
/// whole blob went back as "previous conversation". Numbering them is what lets an answer be filed
/// against a question.</para>
/// </summary>
public sealed class GoalClarifyResult
{
    /// <summary>
    /// False when the tool says the goal is already clear enough to plan.
    /// <para>The old prompt asked for this too — "if the goal is already fully clear, confirm you have
    /// no questions" — but nothing read the answer, so the tile waited for the user regardless. Every
    /// goal, however precise, cost one round trip and one message before anything could be planned.
    /// </para>
    /// </summary>
    public bool NeedsClarification { get; set; } = true;

    public List<GoalQuestion> Questions
    {
        get => _questions;
        set => _questions = value ?? [];
    }
    private List<GoalQuestion> _questions = [];

    /// <summary>See <see cref="GoalReviewResult.WasStructured"/>: prose is a legitimate answer and
    /// falls back to the behaviour this tile had before.</summary>
    public bool WasStructured { get; set; }

    public string RawText
    {
        get => _rawText;
        set => _rawText = value ?? "";
    }
    private string _rawText = "";
}
