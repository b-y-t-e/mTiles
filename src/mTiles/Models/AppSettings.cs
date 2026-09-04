using System.Text.Json.Serialization;
using mTiles.Services;

namespace mTiles.Models;

public sealed class AppSettings
{
    public string TerminalFontFamily { get; set; } = AppDefaults.TerminalFontFamily;
    public double TerminalFontSize { get; set; } = AppDefaults.FontSize;
    public string FontFamily { get; set; } = AppDefaults.FontFamily;
    public double FontSize { get; set; } = AppDefaults.FontSize;
    public string ColorThemeName { get; set; } = AppDefaults.ColorThemeName;
    /// <summary>
    /// The shell a new terminal starts in — an <c>IShellTerminal.Id</c>, empty for "whatever this
    /// machine has".
    /// </summary>
    /// <remarks>The key keeps its name because renaming one is a migration, and the value it used to
    /// hold — a display name — is still read: <c>ShellTerminalCatalog.Find</c> matches both, so a
    /// settings file written before the ids existed still selects the same shell. <c>CMD</c> matches
    /// nothing and falls to the default, which is what removing it means.</remarks>
    public string DefaultShellName { get; set; } = "";

    /// <summary>
    /// The <see cref="DefaultShellName"/> this build has already said it does not know.
    /// </summary>
    /// <remarks>
    /// So the warning is said once instead of on every launch, <b>without the name itself being
    /// thrown away</b>. A name this build cannot match is not necessarily a name that is wrong: it is
    /// also what a shell added by a newer version looks like after a Velopack rollback, or what a
    /// settings file copied from a better-equipped machine looks like. Clearing it would make the older
    /// build the one that decides, permanently, for the newer one — the trap
    /// <c>TolerantAiBehaviourConverter</c> and <c>TileNode</c>'s dual write exist to avoid. So the
    /// value stays and this remembers that it was mentioned; a name that becomes known again clears
    /// this, so the same loss is reported again if it ever comes back.
    /// </remarks>
    public string ReportedUnknownShellName { get; set; } = "";

    /// <summary>
    /// The shell somebody nominated by path, from a build that let them — read once, so the loss is
    /// reported rather than silent.
    /// </summary>
    /// <remarks>
    /// <para>A shell is now a class in <c>Services/Shells/</c>, so a path to an arbitrary binary has
    /// nowhere to go: nothing in the catalog knows how to quote for it, how to run one command in it, or
    /// how to unset a variable in it. There is no answer to carry forward, which is what makes this
    /// different from <see cref="LegacyGitHideMTerminalDir"/> — the value cannot be honoured, only
    /// mentioned. <c>SettingsService.MigrateLegacySettings</c> writes it to the log and drops it, so the
    /// user who nominated <c>nushell</c> and now lands in PowerShell has somewhere to read why.</para>
    /// <para>Nullable and never written back: it exists to be read from a file an older version wrote.</para>
    /// </remarks>
    [JsonPropertyName("CustomShellPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCustomShellPath { get; set; }

    /// <summary>The arguments that went with <see cref="LegacyCustomShellPath"/>, logged beside it so
    /// what is quoted back to the user is the whole of what they had set.</summary>
    [JsonPropertyName("CustomShellArgs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyCustomShellArgs { get; set; }

    /// <summary>
    /// The shell profiles this application used to have.
    /// </summary>
    /// <remarks><b>Read, never written to and never seeded.</b> Profiles are gone — an AI CLI in a shell
    /// is an agent tile now — but the list is still what says which of somebody's existing terminal
    /// tiles were one, so <c>AgentTileMigration</c> reads it once per workspace and the key stays in the
    /// file until that migration is deleted a release from now. Removing it sooner would turn every AI
    /// tile anybody has into a bare shell.</remarks>
    public List<UserShellProfile> ShellProfiles
    {
        get => _shellProfiles;
        set => _shellProfiles = value ?? [];
    }
    private List<UserShellProfile> _shellProfiles = [];

    /// <summary>
    /// Every configured way of running an agent, one seeded per agent on first use.
    /// </summary>
    /// <remarks>Global rather than per workspace: a key and a model are facts about this machine, and
    /// duplicating them per workspace would mean rotating a key in six places. A tile stores the
    /// instance's id, so an instance deleted here leaves a tile that falls back rather than a tile that
    /// cannot load.</remarks>
    public List<AiAgentInstance> AiAgentInstances
    {
        get => _aiAgentInstances;
        set => _aiAgentInstances = value ?? [];
    }
    private List<AiAgentInstance> _aiAgentInstances = [];

    /// <summary>
    /// Every configured way of reaching a provider — a key, an address, a name.
    /// </summary>
    /// <remarks>Not seeded: an agent's own configuration is what a first run uses, and it needs no
    /// key, no address and no row here. A provider instance exists exactly when somebody has set one
    /// up, which is what keeps an empty list meaning "nothing has been configured" rather than
    /// "six services, none of which work".</remarks>
    public List<AiProviderInstance> AiProviderInstances
    {
        get => _aiProviderInstances;
        set => _aiProviderInstances = value ?? [];
    }
    private List<AiProviderInstance> _aiProviderInstances = [];

    /// <summary>
    /// Every login an AI CLI holds on its own — a second subscription, and a third.
    /// </summary>
    /// <remarks>Not seeded, for the reason <see cref="AiProviderInstances"/> is not: the account a CLI
    /// is already signed into needs no row here, and an empty list means "nobody has set up a second
    /// one" rather than "one per agent, none of which is logged in". <b>Nothing in it is a secret</b> —
    /// a name and a location — so unlike the list above it needs no blanking on export and no restoring
    /// on import.</remarks>
    public List<AiSignIn> AiSignIns
    {
        get => _aiSignIns;
        set => _aiSignIns = value ?? [];
    }
    private List<AiSignIn> _aiSignIns = [];

    public DatabaseSettings Database
    {
        get => _database;
        set => _database = value ?? new();
    }
    private DatabaseSettings _database = new();

    public SpeechSettings Speech
    {
        get => _speech;
        set => _speech = value ?? new();
    }
    private SpeechSettings _speech = new();

    public PhoneSettings Phone
    {
        get => _phone;
        set => _phone = value ?? new();
    }
    private PhoneSettings _phone = new();

    /// <summary>
    /// How much the Goal tile's AI runs may do without asking — see <see cref="AiBehaviour"/>.
    /// <para>Here rather than in the goal file so that it cannot travel with a branch, and one setting
    /// for every Goal tile rather than one each: it describes this machine's appetite for unattended
    /// edits, which does not change from tile to tile.</para>
    /// </summary>
    [JsonConverter(typeof(TolerantAiBehaviourConverter))]
    public AiBehaviour GoalPermissionMode { get; set; } = AiBehaviour.Auto;

    /// <summary>
    /// How hard the Goal tile's AI runs are asked to think — see <see cref="AiEffort"/>.
    /// </summary>
    /// <remarks>
    /// Read tolerantly, as <see cref="GoalPermissionMode"/> is and for the same reason: a level written
    /// by a newer build and read after a rollback would otherwise be a JsonException, and this file also
    /// holds the profiles, the tool paths and the DPAPI-encrypted database passwords. One unknown word
    /// must not quarantine all of it.
    /// </remarks>
    [JsonConverter(typeof(TolerantAiEffortConverter))]
    public AiEffort GoalEffort { get; set; } = AiEffort.High;

    public bool DiffTrimIndent { get; set; } = true;
    /// <summary>
    /// Whether <c>.mtiles/</c> is listed in each workspace's <c>.gitignore</c>.
    /// <para>It used to mean "hide those files in the Git tile", which left them untracked and
    /// unignored: invisible here and waiting in every other git client the user opens. Ignoring them is
    /// what people did by hand anyway — the directory holds this application's own workspace state, and
    /// nothing in it belongs in someone else's repository.</para>
    /// </summary>
    public bool GitIgnoreWorkspaceDir { get; set; } = true;

    /// <summary>
    /// The master switch for mirroring <c>CLAUDE.md</c> and <c>AGENTS.md</c> content between each other.
    /// <para>Each workspace still has its own on/off flag (<see cref="Models.WorkspaceAgentFileSyncConfig"/>)
    /// and its own first-run wizard; this is the one switch that overrides every workspace's answer at
    /// once, for a user who never wants two files to shadow each other regardless of what any workspace
    /// decided before this existed.</para>
    /// </summary>
    public bool AgentFileSyncEnabled { get; set; } = true;

    /// <summary>
    /// The setting this replaced, read from existing files so an explicit "off" is honoured.
    /// <para>Without it, renaming the property means every user starts again at the default — and this
    /// default writes to their repository. Somebody who turned the old switch off had said, as clearly
    /// as the old feature let them, that they wanted mTiles to leave that directory alone; coming
    /// back after an update and editing their <c>.gitignore</c> anyway is the one thing this feature
    /// must not do. Its meaning did change — hiding versus ignoring — but the answer to "no, thank you"
    /// carries across the two readings.</para>
    /// <para>Nullable so "never said" and "said no" are different, and never written back: the property
    /// exists to be read once, from a file written by an older version.</para>
    /// </summary>
    [JsonPropertyName("GitHideMTerminalDir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyGitHideMTerminalDir { get; set; }

    /// <summary>
    /// The same answer again, under the name it had between the two renames.
    /// </summary>
    /// <remarks>
    /// A second legacy property rather than a cleverer one, because these are two different questions
    /// asked at two different times and a user may have answered either. The order they are applied in
    /// is what makes them a chain: the older is read first and the newer overrides it, so somebody who
    /// said no once and yes later gets yes. Renaming this property was a rename of the *application*,
    /// which is no reason at all to stop hearing "leave my repository alone" — and this default
    /// writes to it.
    /// </remarks>
    [JsonPropertyName("GitIgnoreMTerminalDir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyGitIgnoreMTerminalDir { get; set; }
    public string GitPath { get; set; } = "";

    public string? LastWorkspaceId { get; set; }
    public double WorkspacesPanelWidth { get; set; } = 240;

    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = true;
}
