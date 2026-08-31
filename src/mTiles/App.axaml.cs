using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using mTiles.Models;
using mTiles.Services;
using mTiles.Services.Database;
using mTiles.Services.Phone;
using mTiles.Services.Speech;
using mTiles.Services.Tiles;
using mTiles.ViewModels;
using mTiles.Views;

namespace mTiles;

public partial class App : Application
{
    private SettingsService _settingsService = null!;
    private DatabaseServiceManager? _dbManager;
    private DictationService? _dictation;
    private PhoneBridgeManager? _phoneBridge;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _settingsService = new SettingsService();
        var workspaceService = new WorkspaceService();
        var persistenceService = new PersistenceService();

        // Before the main window, because that is what reads the list and picks which workspace to
        // open — seeded afterwards, the first run would still show an empty canvas.
        DefaultWorkspace.SeedFirstRun(workspaceService, persistenceService);

        _dbManager = new DatabaseServiceManager(_settingsService);
        if (_settingsService.Settings.Database.Enabled)
            _dbManager.Start();

        // The router is what lets a phone be dictated from without the dictation service knowing there
        // is a choice: it is an IAudioCapture in front of the local microphone and a phone-fed one, picked
        // per recording. Neither end opens anything until somebody actually dictates.
        var router = new RoutedAudioCapture(new PortAudioCapture(), new PhoneAudioCapture());

        // Built unconditionally and cheaply: it opens no microphone and loads no model until somebody
        // dictates, so the switch in settings only has to gate the UI.
        _dictation = new DictationService(_settingsService, router);

        // Captured before the view model exists, and read only when a phone actually streams — which
        // breaks the circle between the two without either of them holding a half-built reference.
        MainWindowViewModel? mainVmRef = null;
        _phoneBridge = new PhoneBridgeManager(_settingsService, _dictation, router,
            () => mainVmRef?.CurrentWorkspace?.ActiveTile);

        var mainVm = new MainWindowViewModel(workspaceService, persistenceService, _settingsService,
            BuildTileCatalog(_dbManager), _dbManager, _dictation, _phoneBridge);
        mainVmRef = mainVm;

        // The other half of the Func above: it says what the active tile is, this says when to look
        // again. Wired here because this is where the bridge is given its view of the view model tree —
        // the manager keeps no reference to it, and the view models keep none to the bridge.
        mainVm.ActiveTileChanged += _phoneBridge.NotifyActiveTileChanged;

        // Asked for explicitly, so a phone paired yesterday can reconnect without the panel being opened
        // first. Off by default: this is the one server here that listens to the network. The condition
        // lives on the manager so this and "may it stop now" cannot drift apart — it takes dictation into
        // account too, because a bridge nothing can dictate through is a listening socket and nothing else.
        if (_phoneBridge.ShouldKeepRunning)
            _ = _phoneBridge.StartAsync();

        _settingsService.SettingsChanged += () =>
        {
            var colorTheme = TerminalTheme.GetByName(_settingsService.Settings.ColorThemeName);
            RequestedThemeVariant = colorTheme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
            ThemeBridge.Apply(colorTheme);
            ApplyFontResources();
        };

        var initialColorTheme = TerminalTheme.GetByName(_settingsService.Settings.ColorThemeName);
        RequestedThemeVariant = initialColorTheme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        ThemeBridge.Apply(initialColorTheme);
        ApplyFontResources();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow { DataContext = mainVm };
            mainWindow.BindWindowState(_settingsService);
            desktop.MainWindow = mainWindow;

            desktop.ShutdownRequested += (_, _) =>
            {
                // The bridge first: it subscribes to the dictation service and drives the shared audio
                // router, so tearing the service down underneath it left a phone that was mid-utterance
                // writing samples into a disposed capture. Blocking, and deliberately — a listening socket
                // that outlives the process holds the port against the next launch.
                //
                // Each step wrapped, because Wait() throws an AggregateException on a faulted task and an
                // escape here skipped the two below it: a bridge that failed to shut down cleanly took the
                // dictation service and the database bridge with it.
                //
                // The three seconds are a bound, not an expectation, and whether they were enough is
                // worth knowing: a bridge still shutting down when the process leaves is exactly the
                // thing that holds the port against the next launch, and discarding the answer meant the
                // one symptom the next run would show had no trace anywhere explaining it.
                Shutdown("phone bridge", () =>
                {
                    if (_phoneBridge is { } bridge && !bridge.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)))
                        Trace.TraceWarning(
                            "The phone bridge did not shut down within 3s; its port may still be held.");
                });
                Shutdown("dictation", () => _dictation?.Dispose());
                Shutdown("database bridge", () => _dbManager?.Dispose());
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Every kind of tile this application can build, and the view that draws each.
    /// </summary>
    /// <remarks>
    /// <para><b>One line per kind, and adding a seventh is one more.</b> The two halves are registered
    /// together on purpose: kinds in one list and views in another is the arrangement that has already
    /// cost this codebase a bug, and it is the reason a tile's view is resolved by a dictionary lookup
    /// rather than by a switch over view-model types.</para>
    /// <para>Here rather than anywhere lower down because this is the one file allowed to see both
    /// <c>ViewModels/</c> and <c>Views/</c>. The order is the order the empty tile's chooser offers
    /// them in.</para>
    /// </remarks>
    internal static TileCatalog BuildTileCatalog(DatabaseServiceManager databases) =>
        new TileCatalog()
            // The same view as a terminal, because an agent tile is a terminal: what differs is
            // where its commands come from, and that is the view model's answer to give.
            .Register(new AgentTileKind(), tile => new TerminalTileView { DataContext = tile })
            .Register(new TerminalTileKind(), tile => new TerminalTileView { DataContext = tile })
            .Register(new NoteTileKind(), tile => new NoteTileView { DataContext = tile })
            .Register(new TodoTileKind(), tile => new TodoTileView { DataContext = tile })
            .Register(new GitTileKind(), tile => new GitTileView { DataContext = tile })
            .Register(new DatabaseTileKind(databases), tile => new DatabaseTileView { DataContext = tile })
            .Register(new GoalTileKind(), tile => new GoalTileView { DataContext = tile });

    /// <summary>Runs one shutdown step, so a failure in it cannot cost the others.</summary>
    private static void Shutdown(string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("Closing the {0} failed: {1}", what, ex); }
    }

    private void ApplyFontResources()
    {
        var s = _settingsService.Settings;
        Resources["UiFontFamily"] = new FontFamily(s.FontFamily);

        // Monospace, for the places where character shapes carry meaning: an address, a URL, a command to
        // copy. Referenced from AXAML as a DynamicResource and, until now, never defined — so every one of
        // those fell back to the proportional face without a word from the binding system, which is how a
        // missing resource fails. It follows the terminal font because that is the monospace face the user
        // has already chosen.
        Resources["TerminalFontFamily"] = new FontFamily(s.TerminalFontFamily);
        Resources["UiFontSize"] = s.FontSize;
        Resources["LogoFontSize"] = s.FontSize * AppDefaults.LogoFontSizeRatio;
        Resources["UiFontSizeSm"] = s.FontSize * AppDefaults.SmallFontSizeRatio;
    }
}
