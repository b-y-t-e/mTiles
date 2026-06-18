using CommunityToolkit.Mvvm.ComponentModel;

namespace MTerminal.Models;

public partial class GitFileChange : ObservableObject
{
    public required string FilePath { get; init; }
    public string? OldFilePath { get; init; }
    public required string Status { get; init; }
    public required string StatusDisplay { get; init; }

    public string DisplayPath => OldFilePath != null ? $"{OldFilePath} → {FilePath}" : FilePath;

    public DateTime SnapshotMtime { get; set; }

    [ObservableProperty]
    private bool _isChecked;
}
