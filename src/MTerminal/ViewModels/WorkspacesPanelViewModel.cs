using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class WorkspacesPanelViewModel : ObservableObject
{
    private readonly WorkspaceService _workspaceService;

    public ObservableCollection<Workspace> Workspaces { get; } = [];

    [ObservableProperty]
    private Workspace? _selectedWorkspace;

    public Func<Task<string?>>? FolderPicker { get; set; }
    public Func<string, Task<bool>>? ConfirmAction { get; set; }

    public WorkspacesPanelViewModel(WorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
        foreach (var w in workspaceService.Workspaces)
            Workspaces.Add(w);
    }

    [RelayCommand]
    private async Task AddWorkspaceAsync()
    {
        var path = FolderPicker != null ? await FolderPicker() : null;
        if (string.IsNullOrEmpty(path)) return;

        var workspace = _workspaceService.AddWorkspace(path);
        Workspaces.Add(workspace);
        SelectedWorkspace = workspace;
    }

    [RelayCommand]
    private async Task RemoveWorkspaceAsync(Workspace workspace)
    {
        if (ConfirmAction != null)
        {
            var confirmed = await ConfirmAction($"Remove workspace \"{workspace.Name}\"?");
            if (!confirmed) return;
        }

        _workspaceService.RemoveWorkspace(workspace.Id);
        Workspaces.Remove(workspace);
        if (SelectedWorkspace == workspace)
            SelectedWorkspace = Workspaces.FirstOrDefault();
    }
}
