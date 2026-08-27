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

    /// <summary>
    /// Whether this directory is a git repository — <c>null</c> until anything has looked.
    /// </summary>
    /// <remarks>
    /// Three states, not two, and the third is the one that matters: the check is asynchronous, and a
    /// plain <c>bool</c> would have every repository in the list announce it had none for as long as the
    /// first pass takes. While it is null the row shows neither a branch nor an offer to create one, and
    /// keeps its height either way.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRepository))]
    private bool? _hasRepository;

    /// <summary>The one state the row offers to do something about.</summary>
    public bool HasNoRepository => HasRepository == false;

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
