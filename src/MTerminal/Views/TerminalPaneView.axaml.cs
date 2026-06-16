using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using MTerminal.ViewModels;
using XTerm.Options;

namespace MTerminal.Views;

public partial class TerminalPaneView : UserControl
{
    private readonly TerminalKeyHandler _keyHandler = new();

    public TerminalPaneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not TerminalPaneViewModel vm) return;

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
            Options = new TerminalOptions
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
            }
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
}
