using System.Text.Json.Serialization;
using mTiles.Services;

namespace mTiles.Models;

/// <summary>
/// One configured way of running an agent — what the user actually picks in a tile.
/// </summary>
/// <remarks>
/// <para>Deliberately separate from <c>IAiAgent</c>: that is the behaviour of a CLI and there is one of
/// each, while this is configuration and there are as many as somebody wants. Every agent has at least
/// one instance, seeded on first run; further instances are how "Claude Code on GLM 5.3 via OpenRouter"
/// exists at all, and the reason the two could never be one type.</para>
/// <para><see cref="DefaultEffort"/> and <see cref="DefaultBehaviour"/> apply <b>wherever the instance
/// is used</b> — the agent tile included, not only the Goal tile. Which is why the two "defaults" have
/// to be named apart on screen or they become a trap: a Goal tile's combo offers <em>from the
/// agent</em>, meaning take the instance's setting, while the instance editor offers <em>tool
/// default</em>, meaning pass no flag at all.</para>
/// <para>Global (<c>settings.json</c>), not per workspace: a key and a model are facts about this
/// machine, and duplicating them per workspace would mean rotating a key in six places.</para>
/// </remarks>
public sealed class AiAgentInstance
{
    /// <summary>This instance's own identity, which is what a tile stores.</summary>
    /// <remarks>An empty one is replaced, for the reason <c>AiSignIn.Id</c> gives: it names a file —
    /// <c>OpenCodeProviderConfig.PathFor</c> — and <c>SafePathComponent</c> turns nothing into
    /// <c>unnamed</c>, so two such instances would launch against one generated provider document.
    /// </remarks>
    public string Id
    {
        get => _id;
        init => _id = value.Length > 0 ? value : Guid.NewGuid().ToString("N");
    }

    private readonly string _id = Guid.NewGuid().ToString("N");

    /// <summary>Which agent it runs — an <c>IAiAgent.Id</c>, so an instance naming an agent this build
    /// does not have is a row that finds nothing rather than a load that fails.</summary>
    public string AgentId { get; set; } = "";

    /// <summary>What the user calls it. Seeded as the agent's own display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The provider instance this authenticates through, or empty for the agent's own
    /// configuration — the case that needs no setting up at all, and the one every seeded instance
    /// starts in.</summary>
    /// <remarks><para>Empty rather than null, and that is not a style choice:
    /// <c>NullToEmptyStringConverter</c> turns every null string in the settings file into an empty one,
    /// so a <c>string?</c> here would be a promise the file cannot keep — see
    /// <c>SettingsNullGuardTests</c>.</para>
    /// <para><b>The name on disk is the old one, deliberately.</b> On screen this slot is now "account"
    /// — a provider and a sign-in are two answers to it — and the property follows that word, but
    /// renaming the JSON key is a migration: an installation Velopack has rolled back would find no
    /// <c>ApiAccountId</c>, read nothing, and put every instance silently back on the CLI's own account.
    /// A different subscription being billed is not something to discover from a bill.</para></remarks>
    [JsonPropertyName("ProviderInstanceId")]
    public string ApiAccountId { get; set; } = "";

    /// <summary>The CLI's own login this runs under, or empty for the account it is already signed
    /// into.</summary>
    /// <remarks>Mutually exclusive with <see cref="ApiAccountId"/> — they are one chooser on screen and
    /// <c>AgentRuntime.For</c> is where that is enforced, because a file holding both is something a
    /// hand edit or an older build can produce and the launch is the last place it can be caught.
    /// </remarks>
    public string SignInId { get; set; } = "";

    /// <summary>The model to ask for, or empty for whatever the agent would pick.</summary>
    public string Model { get; set; } = "";

    /// <summary>The model for the cheap, frequent calls an agent makes beside the real ones, or empty
    /// for the agent's own choice.</summary>
    public string FastModel { get; set; } = "";

    /// <summary>How hard this instance thinks unless a tile says otherwise.</summary>
    /// <remarks>Read tolerantly, as <c>AppSettings.GoalEffort</c> is and for the same reason: a level
    /// written by a newer build and read after a Velopack rollback would otherwise be a
    /// <c>JsonException</c>, which <c>SettingsService.Load</c> quite correctly treats as a damaged file
    /// — and that file also holds the provider keys and the DPAPI-encrypted database passwords. One
    /// unknown word must not quarantine all of it.</remarks>
    [JsonConverter(typeof(TolerantAiEffortConverter))]
    public AiEffort DefaultEffort { get; set; } = AiEffort.High;

    /// <summary>How much this instance may do without asking, unless a tile says otherwise.</summary>
    /// <remarks>Seeded as <see cref="AiBehaviour.ToolDefault"/> — no flag at all — because a seeded
    /// instance is one nobody has been asked about: anything else would have a fresh install start
    /// every agent tile with the CLI's own asking turned off, and the first symptom of that is an edit
    /// that already happened. Loosening it is a choice made in the instance editor.
    /// <para>Read tolerantly for the reason <see cref="DefaultEffort"/> gives — this page writes the
    /// whole vocabulary and the vocabulary has grown once already — and to
    /// <see cref="AiBehaviour.ToolDefault"/> rather than to the settings file's <c>Auto</c>: an answer
    /// that cannot be read must not come back <em>more</em> permissive than what it replaced, which is
    /// the same asymmetry <c>AiBehaviours.RoundDown</c> enforces at launch.</para></remarks>
    [JsonConverter(typeof(TolerantAiInstanceBehaviourConverter))]
    public AiBehaviour DefaultBehaviour { get; set; } = AiBehaviour.ToolDefault;

    /// <summary>
    /// Extra environment for this instance's processes. A <c>null</c> value <b>unsets</b> the variable.
    /// </summary>
    /// <remarks>The unset half is the reason this is a nullable-valued dictionary all the way down to
    /// <c>PtyOptions.Environment</c>: a machine that exports a global <c>ANTHROPIC_API_KEY</c> cannot
    /// otherwise have an instance that authenticates some other way, which is exactly the
    /// misconfiguration several instances of one agent exist to make possible.
    /// <para>Its own converter, because the settings file's general rule turns every null string into an
    /// empty one — which here is the difference between removing a variable and setting it to nothing,
    /// and the second of those leaves an inherited key in place.</para></remarks>
    [JsonConverter(typeof(UnsettableEnvironmentConverter))]
    public Dictionary<string, string?> ExtraEnv
    {
        get;
        set => field = value ?? [];
    } = [];

    /// <summary>Arguments appended to whatever the agent builds, for the flag this application has not
    /// heard of yet.</summary>
    public List<string> ExtraArgs
    {
        get;
        set => field = value ?? [];
    } = [];
}
