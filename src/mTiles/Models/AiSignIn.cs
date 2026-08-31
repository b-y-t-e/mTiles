namespace mTiles.Models;

/// <summary>
/// One login an AI CLI holds on its own — a subscription, signed in through the tool itself rather
/// than through a key we carry.
/// </summary>
/// <remarks>
/// <para><b>Why this is not an <see cref="AiProviderInstance"/>.</b> Both answer the same question —
/// as whom does this agent run — which is why they share one chooser on screen. But a provider is an
/// address and a key that <em>we</em> hand to the process, and a sign-in is a directory the CLI
/// authenticated into by itself: there is nothing here to send anywhere, only somewhere to point the
/// tool at. Two things that are configured, stored and validated differently, in one slot.</para>
/// <para><b>A slug, not a path.</b> The directory is derived — <c>AiSignInStore.DirectoryFor</c> — so
/// that <c>settings.json</c> carries nothing machine-specific: an absolute path under one user's
/// <c>%APPDATA%</c> is a directory that does not exist on the machine the file is imported into, and a
/// row pointing at nothing reads as a lost login rather than as a login that was never carried.
/// <see cref="ConfigDirectory"/> is the deliberate exception, for somebody pointing at a directory that
/// already exists.</para>
/// <para><b>There is no secret in here</b>, and that is the point of storing a location rather than a
/// token: nothing to encrypt, nothing to blank on export, nothing to restore on import. The refresh
/// token stays in the CLI's own file, where the CLI put it.</para>
/// </remarks>
public sealed class AiSignIn
{
    /// <summary>This sign-in's own identity, which is what an agent instance stores.</summary>
    /// <remarks><b>An empty one is replaced rather than kept</b>, because this id names a directory:
    /// <c>SafePathComponent.Of("")</c> answers <c>unnamed</c>, so two rows with no id would share one
    /// credential directory — which the form then creates and a CLI writes a refresh token into. A
    /// property initialiser does not survive deserialisation, and <c>NullToEmptyStringConverter</c>
    /// turns a null in the file into an empty string, so the guard belongs on the property. Nothing can
    /// refer to such a row anyway (<c>AiSignInStore.Find</c> refuses an empty id), which is what keeps
    /// this a tidying rather than a migration.</remarks>
    public string Id
    {
        get => _id;
        init => _id = value.Length > 0 ? value : Guid.NewGuid().ToString("N");
    }

    private readonly string _id = Guid.NewGuid().ToString("N");

    /// <summary>Which agent it is a login for — an <c>IAiAgent.Id</c>.</summary>
    /// <remarks>A login is one CLI's: a Claude Code account means nothing to codex, and the two put
    /// their credentials in different files under different variables. So the chooser only ever offers
    /// the sign-ins belonging to the agent being edited, exactly as it only offers providers that agent
    /// can speak to.</remarks>
    public string AgentId { get; set; } = "";

    /// <summary>What the user calls it — "work", "personal". Theirs to choose, and the only thing the
    /// New sign-in step asks for.</summary>
    public string Name { get; set; } = "";

    /// <summary>The directory the CLI keeps this login in, or empty to derive one from the id.</summary>
    /// <remarks><para>Empty is the ordinary case and the one the UI creates — there is no field for
    /// this, so today it is reachable only by editing <c>settings.json</c>. A path here is for a
    /// directory that already exists, and it is stored verbatim, so a machine that does not have it
    /// says "not signed in" rather than silently falling back to the default account.</para>
    /// <para><b>Stored verbatim is not the same as used verbatim.</b> What the CLI is pointed at is
    /// <c>IAiAgent.SignInEnv</c>'s business, and opencode appends <c>data</c> to it because
    /// <c>XDG_DATA_HOME</c> is a data root rather than a tool's own directory. A path typed here is the
    /// sign-in's directory, not necessarily the one the tool writes into.</para></remarks>
    public string ConfigDirectory { get; set; } = "";
}
