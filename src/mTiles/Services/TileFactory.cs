using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using mTiles.Models;
using mTiles.Services.Database;
using mTiles.ViewModels;

namespace mTiles.Services;

public sealed class TileFactory
{
    private readonly SettingsService _settingsService;
    private readonly Action? _onTileSettingsChanged;
    private readonly DatabaseServiceManager? _dbManager;

    public TileFactory(SettingsService settingsService, Action? onTileSettingsChanged = null,
        DatabaseServiceManager? dbManager = null)
    {
        _settingsService = settingsService;
        _onTileSettingsChanged = onTileSettingsChanged;
        _dbManager = dbManager;
    }

    public ObservableObject CreateContent(TileContentType type, string workingDir, ShellProfile? shell = null)
    {
        return type switch
        {
            TileContentType.Terminal => new TerminalTileViewModel(workingDir, shell, _settingsService),
            TileContentType.Note => CreateNote(workingDir),
            TileContentType.Todo => CreateTodo(workingDir),
            TileContentType.Git => new GitTileViewModel(workingDir, _settingsService) { TileSettingsChanged = _onTileSettingsChanged },
            TileContentType.Database when _dbManager != null =>
                new DatabaseTileViewModel(workingDir, _settingsService, _dbManager) { TileSettingsChanged = _onTileSettingsChanged },
            TileContentType.Database => throw new InvalidOperationException("DatabaseServiceManager is not initialized."),
            TileContentType.Goal => new GoalTileViewModel(workingDir, _settingsService) { TileSettingsChanged = _onTileSettingsChanged },
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    public ObservableObject CreateContent(TileContentType type, string workingDir, UserShellProfile userProfile)
    {
        if (type != TileContentType.Terminal)
            return CreateContent(type, workingDir);

        var shell = ShellDetector.ResolveFromUserProfile(userProfile, _settingsService.Settings);
        var hasFallback = !string.IsNullOrEmpty(userProfile.FallbackScript);
        return new TerminalTileViewModel(workingDir, shell, _settingsService,
            userProfile.StartupScript, userProfile.FallbackScript, userProfile.Id, isDirectLaunch: hasFallback);
    }

    public ObservableObject? CreateFromDto(TileNode dto, string workingDir, IReadOnlyList<ShellProfile> availableShells,
        Action? scheduleSave = null)
    {
        if (dto.ContentType == TileContentType.Empty)
            return null;

        return dto.ContentType switch
        {
            TileContentType.Note when dto.NoteFilePath != null =>
                new NoteTileViewModel(dto.NoteFilePath, _settingsService),
            TileContentType.Todo when dto.TodoFilePath != null =>
                new TodoTileViewModel(dto.TodoFilePath, _settingsService),
            TileContentType.Git =>
                CreateGitFromDto(workingDir, dto.Settings, scheduleSave),
            TileContentType.Database when _dbManager != null =>
                new DatabaseTileViewModel(workingDir, _settingsService, _dbManager) { TileSettingsChanged = scheduleSave },
            TileContentType.Database => null,
            TileContentType.Goal when dto.GoalFilePath != null =>
                new GoalTileViewModel(dto.GoalFilePath, workingDir, _settingsService) { TileSettingsChanged = scheduleSave },
            TileContentType.Terminal =>
                CreateTerminalFromDto(workingDir, dto.ShellName, dto.UserProfileId, availableShells),
            _ => CreateContent(dto.ContentType, workingDir)
        };
    }

    public static Dictionary<string, object?>? SerializeSettings(LeafTileNodeViewModel leaf)
    {
        if (leaf.Content is GitTileViewModel git && !git.ShowDiffPanel)
            return new Dictionary<string, object?> { ["showDiffPanel"] = git.ShowDiffPanel };
        return null;
    }

    public static void RestoreSettings(ObservableObject content, Dictionary<string, object?>? settings)
    {
        if (settings == null || content is not GitTileViewModel git) return;
        if (settings.TryGetValue("showDiffPanel", out var val) && val is JsonElement el)
            git.ShowDiffPanel = el.GetBoolean();
    }

    public static string AllocateTileName(TileContentType type, ref int noteCount, ref int todoCount, ref int gitCount, ref int dbCount, ref int goalCount)
    {
        return type switch
        {
            TileContentType.Note => $"Note#{++noteCount}",
            TileContentType.Todo => $"Todo#{++todoCount}",
            TileContentType.Git => $"Git#{++gitCount}",
            TileContentType.Database => $"DB#{++dbCount}",
            TileContentType.Goal => $"Goal#{++goalCount}",
            TileContentType.Empty => "",
            _ => type.ToString()
        };
    }

    private NoteTileViewModel CreateNote(string workingDir)
    {
        var notesDir = Path.Combine(workingDir, ".mterminal", "notes");
        var filePath = Path.Combine(notesDir, $"{Guid.NewGuid():N}.md");
        return new NoteTileViewModel(filePath, _settingsService);
    }

    private TodoTileViewModel CreateTodo(string workingDir)
    {
        var todosDir = Path.Combine(workingDir, ".mterminal", "todos");
        var filePath = Path.Combine(todosDir, $"{Guid.NewGuid():N}.md");
        return new TodoTileViewModel(filePath, _settingsService);
    }

    private GitTileViewModel CreateGitFromDto(string workingDir, Dictionary<string, object?>? settings, Action? scheduleSave)
    {
        var git = new GitTileViewModel(workingDir, _settingsService);
        RestoreSettings(git, settings);
        git.TileSettingsChanged = scheduleSave;
        return git;
    }

    private ObservableObject CreateTerminalFromDto(string workingDir, string? shellName, string? userProfileId,
        IReadOnlyList<ShellProfile> availableShells)
    {
        if (userProfileId != null)
        {
            var profile = _settingsService.Settings.ShellProfiles
                .FirstOrDefault(p => p.Id == userProfileId);
            if (profile != null)
                return CreateContent(TileContentType.Terminal, workingDir, profile);
        }

        ShellProfile? shell = null;
        if (shellName != null)
            shell = availableShells.FirstOrDefault(s =>
                s.Name.Equals(shellName, StringComparison.OrdinalIgnoreCase));
        return CreateContent(TileContentType.Terminal, workingDir, shell);
    }
}
