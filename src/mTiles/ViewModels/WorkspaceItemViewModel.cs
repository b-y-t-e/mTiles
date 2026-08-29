using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services;

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
    /// <remarks>
    /// Not every directory without a repository gets the offer: the user's home directory, the root of
    /// a drive and the system directories are places where <c>git init</c> is a mistake rather than a
    /// missing step, so their rows say nothing instead (<see cref="SpecialDirectories.AllowsRepository"/>).
    /// </remarks>
    public bool HasNoRepository => HasRepository == false && SpecialDirectories.AllowsRepository(DirectoryPath);

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

    /// <summary>What this workspace is called in the panel.</summary>
    /// <remarks>Read through <see cref="WorkspaceDisplayName"/> rather than straight off the model, so
    /// the home directory shows a name instead of the login. The stored name is untouched.</remarks>
    public string Name => WorkspaceDisplayName.For(Workspace.Name, Workspace.DirectoryPath);

    public string DirectoryPath => Workspace.DirectoryPath;

    /// <summary>Whether this row is the user's own directory.</summary>
    /// <remarks>The name it shows is words any other row could also be called — a row in a list of
    /// folders is read as a folder — so the row carries the glyph as well: together they say which
    /// directory this is, where either alone only hints at it. Same rule as
    /// <see cref="Name"/>, asked of the path rather than of the stored name.</remarks>
    public bool IsHome => SpecialDirectories.IsHome(DirectoryPath);

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
