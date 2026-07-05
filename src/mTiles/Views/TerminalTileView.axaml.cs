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
            PasteOnCtrlV = true,
            // Left-drag always selects locally, even when a TUI app (claude, opencode,
            // vim) enables mouse tracking — copying from agent tiles is the primary
            // workflow. Wheel scrolling is still forwarded to the app.
            SelectionOverridesMouseTracking = true,
            Background = new SolidColorBrush(Color.Parse(theme.Background)),
            Foreground = new SolidColorBrush(Color.Parse(theme.Foreground)),
            Options = CreateOptions(theme)
        };

        AttachTerminalViewHooks(terminal);
        vm.CachedControl = terminal;
        Content = terminal;

        AttachedToVisualTree += OnceAttached;

        async void OnceAttached(object? s, VisualTreeAttachmentEventArgs args)
        {
            AttachedToVisualTree -= OnceAttached;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

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

    // One-time setup on the inner TerminalView (fires once per TerminalControl —
    // cached controls keep their applied template across re-parenting):
    // 1. Registration in TerminalClipboardCoordinator (window-level Ctrl+C copy).
    // 2. Alt-buffer cleanup: TUI apps (opencode, vim) enable SGR mouse tracking but
    //    may not disable it on exit (especially via Ctrl+C). Without this reset the
    //    shell gets flooded with raw escape sequences like "35;65;20M…" on mouse move.
    private static void AttachTerminalViewHooks(TerminalControl terminal)
    {
        terminal.TemplateApplied += (_, e) =>
        {
            var tv = e.NameScope.Find<TerminalView>("PART_TerminalView");
            if (tv == null) return;

            TerminalClipboardCoordinator.Register(tv);

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
