using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MTerminal.Models;
using MTerminal.Services;

namespace MTerminal.ViewModels;

public partial class AiToolViewModel : ObservableObject
{
    public AiToolInfo Tool { get; }
    public string Name => Tool.Name;
    public string Description => Tool.Description;
    public string BinaryName => Tool.BinaryName;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private string? _executablePath;

    [ObservableProperty]
    private string? _detectedVersion;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _isCustomPath;

    public bool IsUserDefined => Tool.IsUserDefined;
    public string? Url => Tool.Url;
    public bool HasUrl => !string.IsNullOrEmpty(Tool.Url);

    public Func<Task<string?>>? BrowseFile { get; set; }
    public Action<string, string>? OnCustomPathSet { get; set; }
    public Action<AiToolViewModel>? OnDeleteRequested { get; set; }

    public AiToolViewModel(AiToolInfo tool)
    {
        Tool = tool;
        _isInstalled = tool.IsInstalled;
        _executablePath = tool.ExecutablePath;
        _isCustomPath = tool.IsCustomPath;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsTesting || !IsInstalled) return;
        IsTesting = true;
        DetectedVersion = null;

        var version = await AiToolDetector.TestAsync(Tool);
        DetectedVersion = version;
        IsTesting = false;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (ExecutablePath != null)
            FileHelper.OpenFolderAndSelect(ExecutablePath);
    }

    [RelayCommand]
    private async Task BrowsePathAsync()
    {
        if (BrowseFile == null) return;
        var path = await BrowseFile();
        if (string.IsNullOrEmpty(path)) return;

        Tool.ExecutablePath = path;
        Tool.IsInstalled = true;
        Tool.IsCustomPath = true;
        ExecutablePath = path;
        IsInstalled = true;
        IsCustomPath = true;
        DetectedVersion = null;

        OnCustomPathSet?.Invoke(BinaryName, path);
    }

    [RelayCommand]
    private void OpenUrl()
    {
        if (Url != null)
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Delete()
    {
        OnDeleteRequested?.Invoke(this);
    }
}
