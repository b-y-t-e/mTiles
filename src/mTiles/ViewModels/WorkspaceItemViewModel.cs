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
    [NotifyPropertyChangedFor(nameof(ShowsDirectoryPath))]
    private bool? _hasRepository;

    /// <summary>The one state the row offers to do something about.</summary>
    /// <remarks>
    /// Not every directory without a repository gets the offer: the user's home directory, the root of
    /// a drive and the system directories are places where <c>git init</c> is a mistake rather than a
    /// missing step (<see cref="SpecialDirectories.AllowsRepository"/>), so their rows say where they
    /// are instead — see <see cref="ShowsDirectoryPath"/>.
    /// </remarks>
    public bool HasNoRepository => HasRepository == false && SpecialDirectories.AllowsRepository(DirectoryPath);

    /// <summary>Whether the row says where it is, in the branch's place.</summary>
    /// <remarks>
    /// <para>The complement of <see cref="HasNoRepository"/> among the rows that have no repository:
    /// the home directory, the root of a drive and the system directories, which are told there is no
    /// offer here and were then told nothing at all. The meta line is reserved on every row whatever it
    /// holds, so a blank one spends the height and says nothing — and these are the rows whose name is
    /// least able to cover for it, because on exactly these the name is a kind of place rather than
    /// which one: "Home directory" is an alias this application chose, and a second "Program Files" is
    /// a folder somebody could plausibly have made.</para>
    /// <para>The path rather than a word for the kind, and that is the whole of the choice: a word
    /// would be a second spelling of the name for the one row that already reads "Home directory", and
    /// it is the path that tells a reader which profile, which drive, which of the two Program Files.
    /// It has its own line and trims, which is what the row could not give it beside the name — the
    /// reason it lives in the tooltip everywhere else.</para>
    /// <para>Not while the check is still running: <see cref="HasRepository"/> is null until something
    /// has looked, and a blank line is the truthful answer to a question nobody has answered yet.</para>
    /// </remarks>
    public bool ShowsDirectoryPath => HasRepository == false && !HasNoRepository;

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

    /// <summary>What kind of place this row sits in.</summary>
    /// <remarks>
    /// <para>What the glyph on the path line is drawn from. It used to be a house docked in front of
    /// the name, which said which directory this was twice over — the name is already
    /// "Home directory" — while the line that actually needed a mark carried a generic folder. The
    /// glyph moved to the path and became the three it should always have been: a house, a disk, a cog.
    /// One picture per kind, on the line where the kind is the thing being said.</para>
    /// <para>Not cached: the path a workspace sits at does not change while its row is on screen, and a
    /// stored copy is a second answer waiting to disagree with <see cref="SpecialDirectories"/>.</para>
    /// <para>There was an <c>IsHome</c> beside this for a while, from when the house was docked in
    /// front of the name. When the glyph moved here nothing read it any more except two tests, which
    /// went on passing and pinned nothing that was drawn — a property kept alive by its own tests is
    /// worse than a missing one, because it reads as a rule somebody relies on.</para>
    /// </remarks>
    public SpecialDirectoryKind SpecialKind => SpecialDirectories.Kind(DirectoryPath);

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
