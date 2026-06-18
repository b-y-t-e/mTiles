using System.Collections.ObjectModel;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public static string[] Themes { get; } = ["Dark", "Light"];
    public static string CustomShellOption => "Custom...";
    public static string[] ColorThemeNames { get; } = TerminalTheme.BuiltIn.Select(t => t.Name).ToArray();

    private readonly SettingsService _settingsService;

    [ObservableProperty]
    private int _selectedTab;

    public bool IsGeneralTab => SelectedTab == 0;
    public bool IsProfilesTab => SelectedTab == 1;
    public bool IsAiToolsTab => SelectedTab == 2;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsGeneralTab));
        OnPropertyChanged(nameof(IsProfilesTab));
        OnPropertyChanged(nameof(IsAiToolsTab));
        if (value == 2 && !_aiToolsLoaded)
            _ = LoadAiToolsSafeAsync();
    }

    [RelayCommand]
    private void SelectTab(int tab) => SelectedTab = tab;

    public static string[] ShellTypeNames { get; } = Enum.GetNames<ShellType>();

    public static readonly FuncValueConverter<string, string> ShellTypeConverter = new(shellName =>
    {
        if (string.IsNullOrEmpty(shellName)) return "";
        var t = ShellDetector.GetTypeByName(shellName);
        return t != ShellType.Other ? $"({t})" : "";
    });

    public ObservableCollection<string> ShellOptions { get; } = [];
    public List<string> ProfileShellOptions { get; } = [];
    public ObservableCollection<UserShellProfile> ShellProfiles { get; } = [];

    [ObservableProperty]
    private string _colorThemeName;

    [ObservableProperty]
    private string _terminalFontFamily;

    [ObservableProperty]
    private double _terminalFontSize;

    [ObservableProperty]
    private string _fontFamily;

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private string _theme;

    [ObservableProperty]
    private string _selectedShell;

    [ObservableProperty]
    private string _customShellPath;

    [ObservableProperty]
    private string _customShellArgs;

    [ObservableProperty]
    private bool _isCustomShell;

    [ObservableProperty]
    private string _customShellType;

    [ObservableProperty]
    private bool _isEditingProfile;

    [ObservableProperty]
    private string _editProfileName = "";

    [ObservableProperty]
    private string _editProfileShell = "";

    [ObservableProperty]
    private string _editProfileScript = "";

    [ObservableProperty]
    private string _editProfileShellType = "";

    private UserShellProfile? _editingProfile;

    private bool _aiToolsLoaded;

    public ObservableCollection<AiToolViewModel> AiTools { get; } = [];

    [ObservableProperty]
    private bool _isLoadingAiTools;

    public Func<Task<string?>>? BrowseAiToolFile { get; set; }

    [ObservableProperty]
    private bool _isEditingAiTool;

    [ObservableProperty]
    private string _editAiToolName = "";

    [ObservableProperty]
    private string _editAiToolBinary = "";

    [ObservableProperty]
    private string _editAiToolVersionArgs = "--version";

    [ObservableProperty]
    private string _editAiToolPath = "";

    private UserAiTool? _editingAiTool;

    public SettingsViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        var s = settingsService.Settings;
        _colorThemeName = s.ColorThemeName;
        _terminalFontFamily = s.TerminalFontFamily;
        _terminalFontSize = s.TerminalFontSize;
        _fontFamily = s.FontFamily;
        _fontSize = s.FontSize;
        _theme = s.Theme;
        _customShellPath = s.CustomShellPath;
        _customShellArgs = s.CustomShellArgs;
        _customShellType = s.CustomShellType.ToString();

        var detected = ShellDetector.Detect();
        foreach (var shell in detected)
        {
            ShellOptions.Add(shell.Name);
            ProfileShellOptions.Add(shell.Name);
        }
        ShellOptions.Add(CustomShellOption);

        if (!string.IsNullOrEmpty(s.CustomShellPath))
        {
            _selectedShell = CustomShellOption;
            _isCustomShell = true;
        }
        else if (!string.IsNullOrEmpty(s.DefaultShellName) && ShellOptions.Contains(s.DefaultShellName))
        {
            _selectedShell = s.DefaultShellName;
        }
        else
        {
            _selectedShell = ShellOptions.Count > 1 ? ShellOptions[0] : CustomShellOption;
        }

        foreach (var p in s.ShellProfiles)
            ShellProfiles.Add(p);
    }

    partial void OnColorThemeNameChanged(string value) { _settingsService.Settings.ColorThemeName = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontFamilyChanged(string value) { _settingsService.Settings.TerminalFontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnTerminalFontSizeChanged(double value) { _settingsService.Settings.TerminalFontSize = value; _settingsService.NotifyChanged(); }
    partial void OnFontFamilyChanged(string value) { _settingsService.Settings.FontFamily = value; _settingsService.NotifyChanged(); }
    partial void OnFontSizeChanged(double value) { _settingsService.Settings.FontSize = value; _settingsService.NotifyChanged(); }
    partial void OnThemeChanged(string value) { _settingsService.Settings.Theme = value; _settingsService.NotifyChanged(); }

    partial void OnSelectedShellChanged(string value)
    {
        IsCustomShell = value == CustomShellOption;
        if (IsCustomShell)
        {
            _settingsService.Settings.DefaultShellName = "";
        }
        else
        {
            _settingsService.Settings.DefaultShellName = value;
            _settingsService.Settings.CustomShellPath = "";
            _settingsService.Settings.CustomShellArgs = "";
            _settingsService.Settings.CustomShellType = ShellType.Other;
        }
        _settingsService.NotifyChanged();
    }

    partial void OnCustomShellPathChanged(string value) { _settingsService.Settings.CustomShellPath = value; _settingsService.NotifyChanged(); }
    partial void OnCustomShellArgsChanged(string value) { _settingsService.Settings.CustomShellArgs = value; _settingsService.NotifyChanged(); }
    partial void OnCustomShellTypeChanged(string value)
    {
        if (Enum.TryParse<ShellType>(value, out var t))
        {
            _settingsService.Settings.CustomShellType = t;
            _settingsService.NotifyChanged();
        }
    }

    partial void OnEditProfileShellChanged(string value)
    {
        EditProfileShellType = GetShellTypeForName(value);
    }

    private static string GetShellTypeForName(string shellName)
    {
        var t = ShellDetector.GetTypeByName(shellName);
        return t != ShellType.Other ? t.ToString() : "";
    }

    [RelayCommand]
    private void AddProfile()
    {
        var defaultShell = IsCustomShell
            ? (ProfileShellOptions.Count > 0 ? ProfileShellOptions[0] : "")
            : SelectedShell;
        _editingProfile = new UserShellProfile { Name = "New Profile", ShellName = defaultShell };
        EditProfileName = _editingProfile.Name;
        EditProfileShell = _editingProfile.ShellName;
        EditProfileScript = "";
        IsEditingProfile = true;
    }

    [RelayCommand]
    private void EditProfile(UserShellProfile profile)
    {
        _editingProfile = profile;
        EditProfileName = profile.Name;
        EditProfileShell = profile.ShellName;
        EditProfileScript = profile.StartupScript;
        IsEditingProfile = true;
    }

    [RelayCommand]
    private void DeleteProfile(UserShellProfile profile)
    {
        ShellProfiles.Remove(profile);
        _settingsService.Settings.ShellProfiles.Remove(profile);
        if (_editingProfile == profile)
            IsEditingProfile = false;
        _settingsService.NotifyChanged();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (_editingProfile == null) return;

        _editingProfile.Name = EditProfileName;
        _editingProfile.ShellName = EditProfileShell;
        _editingProfile.StartupScript = EditProfileScript;

        if (!ShellProfiles.Contains(_editingProfile))
        {
            ShellProfiles.Add(_editingProfile);
            _settingsService.Settings.ShellProfiles.Add(_editingProfile);
        }
        else
        {
            var idx = ShellProfiles.IndexOf(_editingProfile);
            ShellProfiles.RemoveAt(idx);
            ShellProfiles.Insert(idx, _editingProfile);
        }

        IsEditingProfile = false;
        _editingProfile = null;
        _settingsService.NotifyChanged();
    }

    [RelayCommand]
    private void CancelEditProfile()
    {
        IsEditingProfile = false;
        _editingProfile = null;
    }

    private async Task LoadAiToolsSafeAsync()
    {
        try { await LoadAiToolsAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Failed to load AI tools: {0}", ex.Message); }
    }

    private async Task LoadAiToolsAsync()
    {
        if (_aiToolsLoaded) return;
        IsLoadingAiTools = true;

        var customPaths = _settingsService.Settings.CustomAiToolPaths;
        var userTools = _settingsService.Settings.CustomAiTools;
        var detected = await Task.Run(() => AiToolDetector.Detect(customPaths, userTools));
        var tools = detected
            .OrderByDescending(t => t.IsInstalled)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var tool in tools)
            {
                var vm = new AiToolViewModel(tool)
                {
                    BrowseFile = BrowseAiToolFile,
                    OnCustomPathSet = (binaryName, path) =>
                    {
                        _settingsService.Settings.CustomAiToolPaths[binaryName] = path;
                        _settingsService.NotifyChanged();
                    },
                    OnDeleteRequested = DeleteAiTool
                };
                AiTools.Add(vm);
            }
            _aiToolsLoaded = true;
            IsLoadingAiTools = false;
        });
    }

    [RelayCommand]
    private async Task TestAllToolsAsync()
    {
        var tasks = AiTools.Where(t => t.IsInstalled).Select(vm => vm.TestCommand.ExecuteAsync(null));
        await Task.WhenAll(tasks);
    }

    [RelayCommand]
    private void AddAiTool()
    {
        _editingAiTool = new UserAiTool();
        EditAiToolName = "";
        EditAiToolBinary = "";
        EditAiToolVersionArgs = "--version";
        EditAiToolPath = "";
        IsEditingAiTool = true;
    }

    [RelayCommand]
    private void SaveAiTool()
    {
        if (_editingAiTool == null || string.IsNullOrWhiteSpace(EditAiToolName) || string.IsNullOrWhiteSpace(EditAiToolBinary))
            return;

        _editingAiTool.Name = EditAiToolName.Trim();
        _editingAiTool.BinaryName = EditAiToolBinary.Trim();
        _editingAiTool.VersionArgs = EditAiToolVersionArgs.Trim();
        _editingAiTool.CustomPath = EditAiToolPath.Trim();

        if (!_settingsService.Settings.CustomAiTools.Contains(_editingAiTool))
            _settingsService.Settings.CustomAiTools.Add(_editingAiTool);

        _settingsService.NotifyChanged();
        IsEditingAiTool = false;
        _editingAiTool = null;

        _ = ReloadAiToolsAsync();
    }

    [RelayCommand]
    private void CancelEditAiTool()
    {
        IsEditingAiTool = false;
        _editingAiTool = null;
    }

    private void DeleteAiTool(AiToolViewModel vm)
    {
        var id = vm.Tool.UserToolId;
        if (id == null) return;

        _settingsService.Settings.CustomAiTools.RemoveAll(t => t.Id == id);
        AiTools.Remove(vm);
        _settingsService.NotifyChanged();
    }

    private async Task ReloadAiToolsAsync()
    {
        _aiToolsLoaded = false;
        AiTools.Clear();
        await LoadAiToolsAsync();
    }
}
