namespace mTiles.Models;

public sealed class AppSettings
{
    public string TerminalFontFamily { get; set; } = AppDefaults.TerminalFontFamily;
    public double TerminalFontSize { get; set; } = AppDefaults.FontSize;
    public bool TerminalCopyOnSelect { get; set; } = AppDefaults.TerminalCopyOnSelect;
    public string FontFamily { get; set; } = AppDefaults.FontFamily;
    public double FontSize { get; set; } = AppDefaults.FontSize;
    public string ColorThemeName { get; set; } = AppDefaults.ColorThemeName;
    public string DefaultShellName { get; set; } = "";
    public string CustomShellPath { get; set; } = "";
    public string CustomShellArgs { get; set; } = "";
    public ShellType CustomShellType { get; set; } = ShellType.Other;

    public List<UserShellProfile> ShellProfiles { get; set; } = [];

    public Dictionary<string, string> CustomAiToolPaths { get; set; } = [];
    public List<UserAiTool> CustomAiTools { get; set; } = [];

    public DatabaseSettings Database { get; set; } = new();

    public Dictionary<string, string> GoalDefaultModels { get; set; } = [];

    public bool DiffTrimIndent { get; set; } = true;
    public bool GitHideMTerminalDir { get; set; } = true;
    public string GitPath { get; set; } = "";

    public string? LastWorkspaceId { get; set; }
    public double WorkspacesPanelWidth { get; set; } = 240;

    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = true;
}
