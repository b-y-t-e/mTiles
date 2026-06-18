namespace MTerminal.Models;

public sealed class AiToolInfo
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string BinaryName { get; init; } = "";
    public string VersionArgs { get; init; } = "--version";
    public string? ExecutablePath { get; set; }
    public bool IsInstalled { get; set; }
    public bool IsCustomPath { get; set; }
    public string? Url { get; init; }
    public bool IsUserDefined { get; init; }
    public string? UserToolId { get; init; }
}
