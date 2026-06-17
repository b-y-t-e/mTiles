namespace MTerminal.Models;

public sealed class AppSettings
{
    public string TerminalFontFamily { get; set; } = AppDefaults.FontFamily;
    public double TerminalFontSize { get; set; } = AppDefaults.FontSize;
    public string FontFamily { get; set; } = AppDefaults.FontFamily;
    public double FontSize { get; set; } = AppDefaults.FontSize;
    public string Theme { get; set; } = AppDefaults.Theme;

    public string ColorThemeName { get; set; } = AppDefaults.ColorThemeName;
    public string DefaultShellName { get; set; } = "";
    public string CustomShellPath { get; set; } = "";
    public string CustomShellArgs { get; set; } = "";

    public bool DiffTrimIndent { get; set; } = true;

    public double WorkspacesPanelWidth { get; set; } = 240;

    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = true;
}
