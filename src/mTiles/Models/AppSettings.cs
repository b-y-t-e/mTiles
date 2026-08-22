using System.Text.Json.Serialization;

namespace mTiles.Models;

public sealed class AppSettings
{
    public string TerminalFontFamily { get; set; } = AppDefaults.TerminalFontFamily;
    public double TerminalFontSize { get; set; } = AppDefaults.FontSize;
    public string FontFamily { get; set; } = AppDefaults.FontFamily;
    public double FontSize { get; set; } = AppDefaults.FontSize;
    public string ColorThemeName { get; set; } = AppDefaults.ColorThemeName;
    public string DefaultShellName { get; set; } = "";
    public string CustomShellPath { get; set; } = "";
    public string CustomShellArgs { get; set; } = "";
    public ShellType CustomShellType { get; set; } = ShellType.Other;

    public List<UserShellProfile> ShellProfiles
    {
        get => _shellProfiles;
        set => _shellProfiles = value ?? [];
    }
    private List<UserShellProfile> _shellProfiles = [];

    public Dictionary<string, string> CustomAiToolPaths
    {
        get => _customAiToolPaths;
        set => _customAiToolPaths = value ?? [];
    }
    private Dictionary<string, string> _customAiToolPaths = [];
    public List<UserAiTool> CustomAiTools
    {
        get => _customAiTools;
        set => _customAiTools = value ?? [];
    }
    private List<UserAiTool> _customAiTools = [];

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

    public Dictionary<string, string> GoalDefaultModels
    {
        get => _goalDefaultModels;
        set => _goalDefaultModels = value ?? [];
    }
    private Dictionary<string, string> _goalDefaultModels = [];

    public bool DiffTrimIndent { get; set; } = true;
    /// <summary>
    /// Whether <c>.mterminal/</c> is listed in each workspace's <c>.gitignore</c>.
    /// <para>It used to mean "hide those files in the Git tile", which left them untracked and
    /// unignored: invisible here and waiting in every other git client the user opens. Ignoring them is
    /// what people did by hand anyway — the directory holds this application's own workspace state, and
    /// nothing in it belongs in someone else's repository.</para>
    /// </summary>
    public bool GitIgnoreMTerminalDir { get; set; } = true;

    /// <summary>
    /// The setting this replaced, read from existing files so an explicit "off" is honoured.
    /// <para>Without it, renaming the property means every user starts again at the default — and this
    /// default writes to their repository. Somebody who turned the old switch off had said, as clearly
    /// as the old feature let them, that they wanted mTiles to leave <c>.mterminal/</c> alone; coming
    /// back after an update and editing their <c>.gitignore</c> anyway is the one thing this feature
    /// must not do. Its meaning did change — hiding versus ignoring — but the answer to "no, thank you"
    /// carries across the two readings.</para>
    /// <para>Nullable so "never said" and "said no" are different, and never written back: the property
    /// exists to be read once, from a file written by an older version.</para>
    /// </summary>
    [JsonPropertyName("GitHideMTerminalDir")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyGitHideMTerminalDir { get; set; }
    public string GitPath { get; set; } = "";

    public string? LastWorkspaceId { get; set; }
    public double WorkspacesPanelWidth { get; set; } = 240;

    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = true;
}
