using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly PersistenceService _persistenceService;
    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private PaneNodeViewModel? _rootPane;

    public string WorkspaceId { get; }
    public string WorkingDirectory { get; }
    public ObservableCollection<ShellProfile> AvailableShells { get; } = [];

    private int _terminalCount;
    private int _editorCount;

    public WorkspaceViewModel(Workspace workspace, PersistenceService persistenceService, SettingsService settingsService)
    {
        WorkspaceId = workspace.Id;
        WorkingDirectory = workspace.DirectoryPath;
        _persistenceService = persistenceService;
        _settingsService = settingsService;

        foreach (var shell in ShellDetector.Detect())
            AvailableShells.Add(shell);

        var state = persistenceService.LoadLayout(workspace.Id);
        if (state?.RootPane != null)
        {
            InitCountersFromDto(state.RootPane);
            RootPane = RestoreTree(state.RootPane);
        }
    }

    [RelayCommand]
    private void AddTerminal(ShellProfile? shell = null) => AddFirstPane(PaneContentType.Terminal, shell);

    [RelayCommand]
    private void AddEditor() => AddFirstPane(PaneContentType.TextEditor);

    private void AddFirstPane(PaneContentType type, ShellProfile? shell = null)
    {
        if (RootPane != null) return;
        var content = CreateContent(type, WorkingDirectory, shell);
        RootPane = CreateLeaf(type, content, AllocatePaneName(type));
        ScheduleSave();
    }

    private LeafPaneNodeViewModel CreateLeaf(PaneContentType type, ObservableObject content, string paneName)
    {
        return new LeafPaneNodeViewModel(type, content, WorkingDirectory, (t, d) => CreateContent(t, d), AllocatePaneName)
        {
            PaneName = paneName,
            LayoutChanged = ScheduleSave,
            RootReplaced = newRoot => RootPane = ConfigureRoot(newRoot),
            RootCleared = () => { RootPane = null; ScheduleSave(); }
        };
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

    private string AllocatePaneName(PaneContentType type) => type switch
    {
        PaneContentType.Terminal => $"Terminal #{++_terminalCount}",
        PaneContentType.TextEditor => $"Note #{++_editorCount}",
        _ => type.ToString()
    };

    private static readonly System.Text.RegularExpressions.Regex PaneNumberRegex = new(@"#(\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void InitCountersFromDto(PaneNode? node)
    {
        if (node == null) return;
        if (node.IsLeaf)
        {
            if (node.PaneName != null)
            {
                var match = PaneNumberRegex.Match(node.PaneName);
                if (match.Success)
                {
                    var num = int.Parse(match.Groups[1].Value);
                    if (node.ContentType == PaneContentType.Terminal)
                        _terminalCount = Math.Max(_terminalCount, num);
                    else if (node.ContentType == PaneContentType.TextEditor)
                        _editorCount = Math.Max(_editorCount, num);
                }
            }
        }
        else
        {
            InitCountersFromDto(node.First);
            InitCountersFromDto(node.Second);
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
        var s = _settingsService.Settings;
        var notesDir = Path.Combine(workingDir, ".mterminal", "notes");
        var filePath = Path.Combine(notesDir, $"{Guid.NewGuid():N}.md");
        return new EditorPaneViewModel(filePath, s.EditorFontFamily, s.EditorFontSize);
    }

    private void ScheduleSave()
    {
        _persistenceService.DebouncedSaveLayout(WorkspaceId, () => SerializeTree(RootPane));
    }

    private PaneNode? SerializeTree(PaneNodeViewModel? vm)
    {
        return vm switch
        {
            LeafPaneNodeViewModel leaf => new PaneNode
            {
                IsLeaf = true,
                ContentType = leaf.ContentType,
                PaneName = leaf.PaneName,
                EditorFilePath = (leaf.Content as EditorPaneViewModel)?.FilePath
            },
            SplitPaneNodeViewModel split => new PaneNode
            {
                IsLeaf = false,
                SplitOrientation = split.Orientation,
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
            {
                var s = _settingsService.Settings;
                content = new EditorPaneViewModel(dto.EditorFilePath, s.EditorFontFamily, s.EditorFontSize);
            }
            else
                content = CreateContent(dto.ContentType, WorkingDirectory);

            return CreateLeaf(dto.ContentType, content, dto.PaneName ?? AllocatePaneName(dto.ContentType));
        }

        var first = RestoreTree(dto.First!);
        var second = RestoreTree(dto.Second!);
        if (first == null || second == null) return first ?? second;

        var split = new SplitPaneNodeViewModel(dto.SplitOrientation, first, second)
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
