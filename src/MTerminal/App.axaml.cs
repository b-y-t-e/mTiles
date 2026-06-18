using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using MTerminal.Models;
using MTerminal.Services;
using MTerminal.ViewModels;
using MTerminal.Views;

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
            ThemeBridge.Apply(TerminalTheme.GetByName(_settingsService.Settings.ColorThemeName));
            ApplyFontResources();
        };

        RequestedThemeVariant = _settingsService.Settings.Theme == "Light"
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        ThemeBridge.Apply(TerminalTheme.GetByName(_settingsService.Settings.ColorThemeName));
        ApplyFontResources();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow { DataContext = mainVm };
            mainWindow.BindWindowState(_settingsService);
            desktop.MainWindow = mainWindow;

            var updateService = new UpdateService();
            _ = Task.Run(() => updateService.CheckAndPromptAsync(desktop));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyFontResources()
    {
        var s = _settingsService.Settings;
        Resources["UiFontFamily"] = new FontFamily(s.FontFamily);
        Resources["UiFontSize"] = s.FontSize;
        Resources["LogoFontSize"] = s.FontSize * AppDefaults.LogoFontSizeRatio;
        Resources["UiFontSizeSm"] = s.FontSize * AppDefaults.SmallFontSizeRatio;
    }
}
