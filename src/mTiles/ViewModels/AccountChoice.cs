using mTiles.Models;
using mTiles.Services.Agents;

namespace mTiles.ViewModels;

/// <summary>Which kind of answer an <see cref="AccountChoice"/> is.</summary>
/// <remarks>Two kinds and not a boolean, because a third — the CLI's own account — is neither and is
/// the one every seeded instance is in. A boolean would have made "default" indistinguishable from one
/// of the two real ones.</remarks>
public enum AccountKind
{
    /// <summary>Whatever the CLI is already signed into. Stores nothing.</summary>
    Default,

    /// <summary>One of the CLI's own logins, kept in its own directory.</summary>
    SignIn,

    /// <summary>An address and a key we hand to the process.</summary>
    Provider,
}

/// <summary>
/// One entry in the agent-instance form's account chooser: as whom this instance runs.
/// </summary>
/// <remarks>
/// <para><b>One chooser, because it is one question.</b> A provider and a sign-in are two answers to
/// "as whom does this agent run", and two fields side by side would let an instance carry both — a run
/// pointed at a second subscription's directory <em>and</em> handed somebody else's key, billed to the
/// provider while every row on screen named the subscription. <c>AgentRuntime.For</c> is where that is
/// finally enforced, but the way not to have to enforce it is to never offer it.</para>
/// <para><b>Identified by <see cref="Id"/>, never by the label.</b> Nothing makes an instance's name
/// unique — a new one is seeded with the provider's own display name — so two keys for the same service
/// (a work OpenRouter and a private one, which is the case several instances exist for at all) are two
/// rows spelled identically. A chooser keyed by the name binds the agent to whichever of them comes
/// first, and reopening the form shows that one whatever was saved: the agent quietly authenticates as
/// the wrong account. Every other place that resolves one — <c>AiProviderCatalog.FindInstance</c>,
/// <c>AiSignInStore.Find</c>, <c>AgentRuntime.For</c> — already goes by id.</para>
/// <para><see cref="ToString"/> is what the combo box draws, so the label needs no template.</para>
/// </remarks>
public sealed record AccountChoice(AccountKind Kind, string Id, string Label)
{
    /// <summary>What "whatever the CLI is signed into" is called on screen. A word rather than a blank
    /// row, which reads as an unfinished form.</summary>
    public const string DefaultLabel = "The agent's own account";

    /// <summary>The agent's own configuration — an empty id, which is what an instance stores for it.
    /// </summary>
    public static AccountChoice Default { get; } = new(AccountKind.Default, "", DefaultLabel);

    /// <summary>
    /// The account this instance stores, where the chooser has nothing to offer for it.
    /// </summary>
    /// <remarks><b>So that opening a form does not throw away what the form cannot offer.</b> The
    /// chooser lists what could be chosen now — a provider this agent can speak to, a sign-in belonging
    /// to it — and an instance the row explains as unavailable is precisely one whose account is not on
    /// that list. Restored to <see cref="Default"/> instead, the next Save wrote "the agent's own
    /// account" over it, and a rename was enough to destroy the evidence the row was explaining.
    /// <para>It carries the stored id verbatim, so saving puts back what was there; the label says it
    /// cannot be used, because a chooser must not show a broken answer as though it were a working one.
    /// The user is free to pick something else, which is the point of being able to open the form at
    /// all.</para></remarks>
    public static AccountChoice Unusable(AccountKind kind, string id, string? name) =>
        new(kind, id, name is { Length: > 0 }
            ? $"{name} (cannot be used here)"
            : "The account this instance stores (no longer available)");

    /// <summary>
    /// A sign-in, prefixed so the two kinds are told apart in a flat list.
    /// </summary>
    /// <remarks>A prefix rather than grouped headings: a combo box's groups cannot be selected, which
    /// means writing a template and a selection rule for a list that has at most a handful of entries
    /// in it. The word does the same work and is readable in the closed combo too, which a heading is
    /// not.</remarks>
    public static AccountChoice For(AiSignIn signIn, SignInStatus status) =>
        new(AccountKind.SignIn, signIn.Id,
            $"Subscription · {signIn.Name}{(status.SignedIn ? "" : " (not signed in)")}");

    public static AccountChoice For(AiProviderInstance provider, string name) =>
        new(AccountKind.Provider, provider.Id, $"API key · {name}");

    public override string ToString() => Label;
}
