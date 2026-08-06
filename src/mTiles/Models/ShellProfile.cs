namespace mTiles.Models;

public sealed class ShellProfile
{
    public string Name { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public string[] Args { get; init; } = [];
    public ShellType Type { get; init; } = ShellType.Other;
}
