namespace MTerminal.Models;

public sealed class AppSettings
{
    public string TerminalFontFamily { get; set; } = "Cascadia Mono, Consolas, monospace";
    public double TerminalFontSize { get; set; } = 14;
    public string EditorFontFamily { get; set; } = "Cascadia Mono, Consolas, monospace";
    public double EditorFontSize { get; set; } = 14;
    public string Theme { get; set; } = "Dark";

    public string TerminalThemeName { get; set; } = "Default Dark";
    public string DefaultShellName { get; set; } = "";
    public string CustomShellPath { get; set; } = "";
    public string CustomShellArgs { get; set; } = "";

    public double WorkspacesPanelWidth { get; set; } = 240;

    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = double.NaN;
    public double WindowHeight { get; set; } = double.NaN;
    public bool WindowMaximized { get; set; } = true;
}
