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

    /// <summary>Whether something is running in this workspace right now.</summary>
    /// <remarks>Set from outside — the row is told, it does not look. Only workspaces that have been
    /// opened have a view model producing tiles to be busy, so an unopened one stays dark, which is the
    /// truthful answer: nothing of it is running.</remarks>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Told when the star is pressed, so the row never has to know the service that stores it.
    /// </summary>
    public Action<WorkspaceItemViewModel, bool>? FavoriteChanged { get; set; }

    /// <summary>Whether the user pinned this workspace to the top of the list.</summary>
    public bool IsFavorite
    {
        get => Workspace.IsFavorite;
        set
        {
            if (Workspace.IsFavorite == value) return;
            // The row owns the value and then tells whoever stores it: a callback nobody wired, or a
            // store that cannot find the workspace, must not leave the star saying one thing and the
            // sort order another.
            Workspace.IsFavorite = value;
            OnPropertyChanged();
            FavoriteChanged?.Invoke(this, value);
        }
    }

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
