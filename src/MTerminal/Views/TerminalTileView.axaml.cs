using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using MTerminal.Models;
using MTerminal.ViewModels;
using XTerm.Options;

namespace MTerminal.Views;

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
            Background = new SolidColorBrush(Color.Parse(theme.Background)),
            Foreground = new SolidColorBrush(Color.Parse(theme.Foreground)),
            Options = CreateOptions(theme)
        };

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
                await terminal.LaunchProcess(vm.WorkingDirectory, vm.Shell.ExecutablePath, vm.Shell.Args);
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TerminalTileViewModel.Theme)) return;
        if (sender is not TerminalTileViewModel vm) return;
        if (vm.CachedControl is not TerminalControl terminal) return;

        Dispatcher.UIThread.Post(() => ApplyTheme(terminal, vm.Theme));
    }

    private static async void ApplyTheme(TerminalControl terminal, TerminalTheme theme)
    {
        terminal.Background = new SolidColorBrush(Color.Parse(theme.Background));
        terminal.Foreground = new SolidColorBrush(Color.Parse(theme.Foreground));
        terminal.Options = CreateOptions(theme);

        // TerminalControl re-renders only on actual size change.
        // Margin nudge forces layout recalc even under an overlay.
        terminal.Margin = new Thickness(0, 0, 20, 0);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        terminal.Margin = default;
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
