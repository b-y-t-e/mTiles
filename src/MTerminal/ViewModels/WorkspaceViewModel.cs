using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class WorkspaceViewModel : ObservableObject
{
    private readonly PersistenceService _persistenceService;
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private PaneNodeViewModel? _rootPane;

    public string ProjectId { get; }
    public string WorkingDirectory { get; }
    public ObservableCollection<ShellProfile> AvailableShells { get; } = [];

    public WorkspaceViewModel(Project project, PersistenceService persistenceService, SettingsService settingsService)
    {
        ProjectId = project.Id;
        WorkingDirectory = project.DirectoryPath;
        _persistenceService = persistenceService;
        _settingsService = settingsService;

        foreach (var shell in ShellProfile.Detect())
            AvailableShells.Add(shell);

        var state = persistenceService.LoadWorkspace(project.Id);
        if (state?.RootPane != null)
            RootPane = RestoreTree(state.RootPane);
    }

    [RelayCommand]
    private void AddTerminal(ShellProfile? shell = null)
    {
        AddFirstPane(PaneContentType.Terminal, shell);
    }

    [RelayCommand]
    private void AddEditor() => AddFirstPane(PaneContentType.TextEditor);

    private void AddFirstPane(PaneContentType type, ShellProfile? shell = null)
    {
        if (RootPane != null) return;
        var content = CreateContent(type, WorkingDirectory, shell);
        var leaf = new LeafPaneNodeViewModel(type, content, WorkingDirectory, (t, d) => CreateContent(t, d))
        {
            LayoutChanged = ScheduleSave,
            RootReplaced = newRoot => RootPane = ConfigureRoot(newRoot),
            RootCleared = () => { RootPane = null; ScheduleSave(); }
        };
        RootPane = leaf;
        ScheduleSave();
    }

    private PaneNodeViewModel ConfigureRoot(PaneNodeViewModel node)
    {
        node.LayoutChanged = ScheduleSave;
        PropagateCallbacks(node);
        ScheduleSave();
        return node;
    }

    private void PropagateCallbacks(PaneNodeViewModel node)
    {
        node.LayoutChanged = ScheduleSave;
        if (node is LeafPaneNodeViewModel leaf)
        {
            leaf.RootReplaced = newRoot => RootPane = ConfigureRoot(newRoot);
            leaf.RootCleared = () => { RootPane = null; ScheduleSave(); };
        }
        else if (node is SplitPaneNodeViewModel split)
        {
            if (split.First != null) PropagateCallbacks(split.First);
            if (split.Second != null) PropagateCallbacks(split.Second);
        }
    }

    private ObservableObject CreateContent(PaneContentType type, string workingDir, ShellProfile? shell = null)
    {
        return type switch
        {
            PaneContentType.Terminal => new TerminalPaneViewModel(workingDir, shell, _settingsService.Settings),
            PaneContentType.TextEditor => CreateEditorContent(workingDir),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }

    private EditorPaneViewModel CreateEditorContent(string workingDir)
    {
        var notesDir = Path.Combine(workingDir, ".mterminal", "notes");
        var filePath = Path.Combine(notesDir, $"{Guid.NewGuid():N}.md");
        return new EditorPaneViewModel(filePath);
    }

    private void ScheduleSave()
    {
        _persistenceService.DebouncedSaveWorkspace(ProjectId, () => SerializeTree(RootPane));
    }

    private PaneNode? SerializeTree(PaneNodeViewModel? vm)
    {
        return vm switch
        {
            LeafPaneNodeViewModel leaf => new PaneNode
            {
                IsLeaf = true,
                ContentType = leaf.ContentType,
                EditorFilePath = (leaf.Content as EditorPaneViewModel)?.FilePath
            },
            SplitPaneNodeViewModel split => new PaneNode
            {
                IsLeaf = false,
                SplitOrientation = split.Orientation.ToString(),
                SplitRatio = split.SplitRatio,
                First = SerializeTree(split.First),
                Second = SerializeTree(split.Second)
            },
            _ => null
        };
    }

    private PaneNodeViewModel? RestoreTree(PaneNode dto)
    {
        if (dto.IsLeaf)
        {
            ObservableObject content;
            if (dto.ContentType == PaneContentType.TextEditor && dto.EditorFilePath != null)
                content = new EditorPaneViewModel(dto.EditorFilePath);
            else
                content = CreateContent(dto.ContentType, WorkingDirectory);

            var leaf = new LeafPaneNodeViewModel(dto.ContentType, content, WorkingDirectory, (t, d) => CreateContent(t, d))
            {
                LayoutChanged = ScheduleSave,
                RootReplaced = newRoot => RootPane = ConfigureRoot(newRoot),
                RootCleared = () => { RootPane = null; ScheduleSave(); }
            };
            return leaf;
        }

        var first = RestoreTree(dto.First!);
        var second = RestoreTree(dto.Second!);
        if (first == null || second == null) return first ?? second;

        var orientation = dto.SplitOrientation == "Horizontal"
            ? Avalonia.Layout.Orientation.Horizontal
            : Avalonia.Layout.Orientation.Vertical;

        var split = new SplitPaneNodeViewModel(orientation, first, second)
        {
            SplitRatio = dto.SplitRatio,
            LayoutChanged = ScheduleSave
        };
        first.Parent = split;
        second.Parent = split;
        return split;
    }

    public void Dispose()
    {
        DisposeTree(RootPane);
    }

    private static void DisposeTree(PaneNodeViewModel? node)
    {
        if (node is LeafPaneNodeViewModel leaf && leaf.Content is IDisposable d)
            d.Dispose();
        else if (node is SplitPaneNodeViewModel split)
        {
            DisposeTree(split.First);
            DisposeTree(split.Second);
        }
    }
}
