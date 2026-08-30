namespace mTiles.ViewModels;

/// <summary>
/// One entry in the agent-instance form's provider chooser: which provider instance it is, and what it
/// is called on screen.
/// </summary>
/// <remarks>
/// <para><b>Identified by <see cref="Id"/>, never by the label.</b> Nothing makes an instance's name
/// unique — a new one is seeded with the provider's own display name — so two keys for the same service
/// (a work OpenRouter and a private one, which is the case several instances exist for at all) are two
/// rows spelled identically. A chooser keyed by the name binds the agent to whichever of them comes
/// first, and reopening the form shows that one whatever was saved: the agent quietly authenticates as
/// the wrong account. Every other place that resolves a provider — <c>AiProviderCatalog.FindInstance</c>,
/// <c>AgentRuntime.For</c> — already goes by id.</para>
/// <para><see cref="ToString"/> is what the combo box draws, so the label needs no template.</para>
/// </remarks>
public sealed record ProviderChoice(string Id, string Label)
{
    /// <summary>What "no provider" is called on screen. A word rather than a blank row, which reads as
    /// an unfinished form.</summary>
    public const string OwnAccountLabel = "The agent's own account";

    /// <summary>The agent's own configuration — an empty id, which is what an instance stores for it.
    /// </summary>
    public static ProviderChoice OwnAccount { get; } = new("", OwnAccountLabel);

    public override string ToString() => Label;
}
