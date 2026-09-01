namespace mTiles.Services;

/// <summary>
/// Whether what a tool printed is Claude Code refusing to start on the model it was told to run.
/// </summary>
/// <remarks>
/// <para>Before a headless (<c>-p</c>) run Claude Code verifies the model id against the endpoint's own
/// catalogue, and a provider that does not serve that verification stops <b>every</b> goal on the
/// pairing before the model is asked anything. Measured 2026-09-01 against 2.1.250, 2.1.251 and
/// 2.1.252 with OpenRouter, which answers <b>404</b> on the per-model route the CLI asks — even a
/// genuine Anthropic id is refused there.</para>
/// <para><b>Matched on the CLI's own tag alone.</b> The refusal also puts a sentence on stdout
/// ("There's an issue with the selected model"), and matching it read two shapes for the price of one
/// — until a failed run whose <em>answer</em> quoted the sentence was considered: this tile's goals are
/// goals about this application, whose sources contain the sentence, so a run that failed for an
/// unrelated reason would have carried its diff into the failure text and been advised about a model.
/// The tag is printed on stderr whatever the output format — the stream reader drops the sentence but
/// keeps the tag, and <c>AiProcessRunner</c> appends the stderr dump to every non-zero exit — so the
/// tag covers both shapes and nothing else spells this refusal.</para>
/// <para>Only the failed-run path asks this. The interactive session — the agent tile — runs no such
/// check, which is why the same instance works there and fails here; that asymmetry is the advice.</para>
/// </remarks>
internal static class UnrecognizedModel
{
    /// <summary>The CLI's own tag, emitted whatever the output format. The one spelling that survives
    /// into a streamed run's stderr dump — and the one a quoted sentence cannot fake.</summary>
    private const string Tag = "[claude-code:unrecognized_model]";

    public static bool Named(string? toolOutput) =>
        (toolOutput ?? "").Contains(Tag, StringComparison.Ordinal);

    /// <summary>What to tell the user when it was: what the check is, that this tile already asks the
    /// gateway for its catalogue, and the route that still works.</summary>
    public const string Advice =
        "This looks like Claude Code refusing to start a headless run on this model: it verifies the " +
        "model id against the provider's own catalogue before every -p run. This tile already asks the " +
        "provider for that catalogue on the run (the gateway discovery switch), so a refusal getting " +
        "past it means the provider does not serve a model list this CLI can read. The interactive " +
        "agent tile on this instance runs the same model without the check; a goal run starts once the " +
        "provider serves a readable catalogue or the CLI relaxes the check.";
}
