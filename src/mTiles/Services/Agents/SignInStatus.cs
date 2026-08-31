namespace mTiles.Services.Agents;

/// <summary>
/// What a sign-in's directory says about itself: whether the CLI is logged in there, and as whom.
/// </summary>
/// <remarks>
/// <para>Read from the files the CLI wrote, never from anything we stored — which is the whole reason
/// this exists rather than a <c>bool</c> on the model. A row that remembered "signed in" would keep
/// saying so after the user logged out in a terminal, and the first symptom of that is a tile that
/// starts and then cannot talk to anything.</para>
/// <para><b>Tokens are never read.</b> Both files hold a refresh token beside the two fields wanted
/// here, and nothing in this application has any business touching it: what is taken is an address to
/// show on a row, and the word for what the account is.</para>
/// </remarks>
/// <param name="SignedIn">Whether credentials are present in that directory at all.</param>
/// <param name="Detail">Who and what — "a.bol@firma.pl · Max" — or empty where the CLI keeps its
/// credentials somewhere this cannot read. Empty is "could not say", never "nobody".</param>
public sealed record SignInStatus(bool SignedIn, string Detail)
{
    public static SignInStatus NotSignedIn { get; } = new(false, "");

    /// <summary>Signed in, but with nothing further to say — the honest answer for an agent whose
    /// credential file names no account.</summary>
    public static SignInStatus SignedInAnonymously { get; } = new(true, "");
}
