using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MTerminal.Services;
using MTerminal.ViewModels;
using MTerminal.Views;
using Velopack;

namespace MTerminal;

public partial class App : Application
{
    private SettingsService _settingsService = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _settingsService = new SettingsService();
        var workspaceService = new WorkspaceService();
        var persistenceService = new PersistenceService();

        var mainVm = new MainWindowViewModel(workspaceService, persistenceService, _settingsService);

        _settingsService.SettingsChanged += () =>
        {
            var theme = _settingsService.Settings.Theme;
            RequestedThemeVariant = theme == "Light"
                ? ThemeVariant.Light
                : ThemeVariant.Dark;
        };

        RequestedThemeVariant = _settingsService.Settings.Theme == "Light"
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow { DataContext = mainVm };
            mainWindow.BindWindowState(_settingsService);
            desktop.MainWindow = mainWindow;
        }

        Task.Run(CheckForUpdates);

        base.OnFrameworkInitializationCompleted();
    }

    private static void CheckForUpdates()
    {
        try
        {
            var updateUrl = Environment.GetEnvironmentVariable("MTERMINAL_UPDATE_URL")
                            ?? "https://else.net.pl/mterminal/";
            var mgr = new UpdateManager(updateUrl);
            var newVersion = mgr.CheckForUpdates();
            if (newVersion == null)
                return;

            mgr.DownloadUpdates(newVersion);
            mgr.ApplyUpdatesAndRestart(newVersion);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
    }
}
