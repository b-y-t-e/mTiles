using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;

namespace mTiles.ViewModels;

public partial class WorkspaceItemViewModel : ObservableObject
{
    public Workspace Workspace { get; }

    [ObservableProperty]
    private string _branchName = "";

    [ObservableProperty]
    private bool _isSelected;

    public string Id => Workspace.Id;
    public string Name => Workspace.Name;
    public string DirectoryPath => Workspace.DirectoryPath;

    public string Initials
    {
        get
        {
            var name = Name;
            if (string.IsNullOrWhiteSpace(name)) return "?";
            var parts = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{char.ToUpper(parts[0][0])}{char.ToUpper(parts[1][0])}";
            return name.Length >= 2 ? name[..2].ToUpper() : name.ToUpper();
        }
    }

    public WorkspaceItemViewModel(Workspace workspace)
    {
        Workspace = workspace;
    }
}
