namespace MTerminal.Models;

public sealed class TodoItem
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsDone { get; set; }
}
