namespace MTerminal.Models;

public sealed class UserShellProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ShellName { get; set; } = "";
    public string StartupScript { get; set; } = "";
    public string? RequiredAiToolBinaryName { get; set; }
}
