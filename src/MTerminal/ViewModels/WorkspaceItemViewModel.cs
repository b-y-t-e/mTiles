using CommunityToolkit.Mvvm.ComponentModel;
using MTerminal.Models;

namespace MTerminal.ViewModels;

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

    public WorkspaceItemViewModel(Workspace workspace)
    {
        Workspace = workspace;
    }
}
