using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using mTiles.Models;
using mTiles.ViewModels;
using XTerm.Options;

namespace mTiles.Views;

public partial class TerminalTileView : UserControl
{
    private readonly TerminalKeyHandler _keyHandler = new();
    private TerminalTileViewModel? _subscribedVm;

    public TerminalTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;

        if (DataContext is not TerminalTileViewModel vm) return;

        _subscribedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;

        if (vm.CachedControl is TerminalControl cached)
        {
            ControlHelper.DetachFromParent(cached);
            Content = cached;
            Dispatcher.UIThread.Post(() => _keyHandler.Attach(this, cached), DispatcherPriority.Loaded);
            return;
        }

        var theme = vm.Theme;
        var terminal = new TerminalControl
        {
            Process = string.Empty,
            FontFamily = new FontFamily(vm.FontFamily),
            FontSize = vm.FontSize,
            BufferSize = 5000,
            CopyOnSelect = vm.CopyOnSelect,
            Background = new SolidColorBrush(Color.Parse(theme.Background)),
            Foreground = new SolidColorBrush(Color.Parse(theme.Foreground)),
            Options = CreateOptions(theme)
        };

        AttachAltBufferCleanup(terminal);
        vm.CachedControl = terminal;
        Content = terminal;

        AttachedToVisualTree += OnceAttached;

        async void OnceAttached(object? s, VisualTreeAttachmentEventArgs args)
        {
            AttachedToVisualTree -= OnceAttached;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            _keyHandler.Attach(this, terminal);

            if (!vm.IsLaunched)
            {
                vm.IsLaunched = true;

                if (vm.IsDirectLaunch && vm.StartupScript != null)
                {
                    var commands = DirectLauncher.BuildCommands(vm.StartupScript!, vm.FallbackScript ?? "", vm.TileId);
                    await DirectLauncher.LaunchWithFallback(terminal, vm.WorkingDirectory, commands, vm.Shell);
                }
                else
                {
                    if (vm.StartupScript != null)
                        PtyWriter.AttachStartupScript(terminal, vm.StartupScript, vm.TileId);

                    await terminal.LaunchProcess(vm.WorkingDirectory, vm.Shell.ExecutablePath, vm.Shell.Args);
                }
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TerminalTileViewModel vm) return;
        if (vm.CachedControl is not TerminalControl terminal) return;

        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(TerminalTileViewModel.Theme):
                    ApplyTheme(terminal, vm.Theme);
                    break;
                case nameof(TerminalTileViewModel.FontFamily):
                    terminal.FontFamily = new FontFamily(vm.FontFamily);
                    NudgeRerender(terminal);
                    break;
                case nameof(TerminalTileViewModel.FontSize):
                    terminal.FontSize = vm.FontSize;
                    NudgeRerender(terminal);
                    break;
                case nameof(TerminalTileViewModel.CopyOnSelect):
                    terminal.CopyOnSelect = vm.CopyOnSelect;
                    break;
            }
        });
    }

    private static void ApplyTheme(TerminalControl terminal, TerminalTheme theme)
    {
        terminal.Background = new SolidColorBrush(Color.Parse(theme.Background));
        terminal.Foreground = new SolidColorBrush(Color.Parse(theme.Foreground));
        terminal.Options = CreateOptions(theme);
        NudgeRerender(terminal);
    }

    // TerminalControl re-renders only on actual size change.
    // Margin nudge forces layout recalc even under an overlay.
    private static async void NudgeRerender(TerminalControl terminal)
    {
        terminal.Margin = new Thickness(0, 0, 20, 0);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        terminal.Margin = default;
    }

    // TUI apps (opencode, vim) enable SGR mouse tracking but may not disable it on
    // exit (especially via Ctrl+C). Without this reset the shell gets flooded with
    // raw escape sequences like "35;65;20M…" on every mouse move.
    private static void AttachAltBufferCleanup(TerminalControl terminal)
    {
        terminal.TemplateApplied += (_, e) =>
        {
            var tv = e.NameScope.Find<TerminalView>("PART_TerminalView");
            if (tv == null) return;

            tv.PropertyChanged += (_, args) =>
            {
                if (args.Property.Name != "IsAlternateBuffer") return;
                if (args.NewValue is true || args.OldValue is not true) return;

                var xterm = terminal.Terminal;
                if (xterm == null) return;

                var tracker = xterm.GetType()
                    .GetField("_mouseTracker", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(xterm);
                if (tracker == null) return;

                tracker.GetType().GetProperty("TrackingMode")?.SetValue(tracker, 0);
                tracker.GetType().GetProperty("Encoding")?.SetValue(tracker, 0);
            };
        };
    }

    private static TerminalOptions CreateOptions(TerminalTheme theme) => new()
    {
        Theme = new ThemeOptions
        {
            Foreground = theme.Foreground,
            Background = theme.Background,
            Cursor = theme.Cursor,
            Selection = theme.Selection,
            Black = theme.Black,
            Red = theme.Red,
            Green = theme.Green,
            Yellow = theme.Yellow,
            Blue = theme.Blue,
            Magenta = theme.Magenta,
            Cyan = theme.Cyan,
            White = theme.White,
            BrightBlack = theme.BrightBlack,
            BrightRed = theme.BrightRed,
            BrightGreen = theme.BrightGreen,
            BrightYellow = theme.BrightYellow,
            BrightBlue = theme.BrightBlue,
            BrightMagenta = theme.BrightMagenta,
            BrightCyan = theme.BrightCyan,
            BrightWhite = theme.BrightWhite
        }
    };
}
