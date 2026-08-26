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

    /// <summary>
    /// What the user has typed against this question so far.
    /// </summary>
    /// <remarks>
    /// Persisted with the question, and that is the point of it: the pending set is saved so a tile can
    /// be closed mid-question and come back still asking, and coming back with the boxes emptied is
    /// answering a promise with half of it. Somebody who wrote three answers and closed the tile lost
    /// them without a word — the one thing the rest of this tile refuses to do with typed text.
    /// </remarks>
    public string Answer
    {
        get => _answer;
        set => _answer = value ?? "";
    }
    private string _answer = "";

    /// <summary>
    /// Answers the tool considers likely, if it offered any.
    /// </summary>
    /// <remarks>
    /// <para>They become the chips under the question in the panel: clicking one puts it in that
    /// question's box, so answering is choosing rather than typing — and it is still a box, so they
    /// stay an offer rather than a list to pick from.</para>
    /// <para>Guarded against a null <em>in</em> the list as well as a null list.
    /// </summary>
    /// <remarks>
    /// The same level, and the same lesson, as <c>GoalTileState.ClarificationHistory</c>: a guard only
    /// ever covers the one somebody remembered. Two ways in, both real — the tool can answer
    /// <c>"options":["a",null]</c>, and a goal file on disk carries these now that the pending questions
    /// are persisted. Either ends the same way: a <c>NullReferenceException</c> in
    /// <c>GoalQuestionAnswer</c>'s constructor or in <c>GoalTranscript.Questions</c>, inside the view
    /// model's catch of last resort, which stops the tile saving for the rest of its life.
    /// </remarks>
    public List<string> Options
    {
        get => _options;
        set => _options = Without.Nulls(value);
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

    /// <summary>Guarded against a null in the list as well — see <see cref="GoalQuestion.Options"/>.
    /// </summary>
    public List<GoalQuestion> Questions
    {
        get => _questions;
        set => _questions = Without.Nulls(value);
    }
    private List<GoalQuestion> _questions = [];

    /// <summary>See <see cref="GoalReviewResult.WasStructured"/>: prose is a legitimate answer and
    /// falls back to the behaviour this tile had before.</summary>
    public bool WasStructured { get; set; }

    /// <summary>
    /// The command this project checks itself with, worked out by the tool from the repository — and
    /// from the goal, where the goal names a check of its own.
    /// </summary>
    /// <remarks>
    /// <para>This is where a verify command comes from now. It used to be a text box on the criteria
    /// panel, which asked the user to translate their own goal into a shell command and then to know
    /// which one this project uses — a fair question in a C# repository and a worse one in every
    /// other, since the panel had no idea whether it was looking at <c>dotnet test</c>, <c>npm test</c>
    /// or <c>cargo test</c>. The tool is standing in the repository and can see.</para>
    /// <para>Asked for on every goal rather than only one that mentions running something. Most goals
    /// are business goals — "add cart discounts" — and they still have to compile; a command
    /// conditional on the user saying so would almost never arm the gate, in front of the failure it
    /// exists for. Empty in three cases, all of them ordinary: a repository with nothing to run, a
    /// goal whose work is not code that runs (a document, a README — no exit code says whether that
    /// was written well), and a project whose command the tool cannot identify, which it asks about
    /// instead of guessing.</para>
    /// <para>It is a <em>proposal</em>, not a setting: nothing here reaches a shell without the same
    /// approval a command out of a saved file needs. See
    /// <c>GoalTileViewModel.ConsentToVerifyCommandAsync</c>.</para>
    /// </remarks>
    public string Verify
    {
        get => _verify;
        set => _verify = value ?? "";
    }
    private string _verify = "";

    public string RawText
    {
        get => _rawText;
        set => _rawText = value ?? "";
    }
    private string _rawText = "";
}
