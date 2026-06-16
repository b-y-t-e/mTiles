using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Iciclecreek.Terminal;
using MTerminal.Models;
using MTerminal.ViewModels;
using XTerm.Options;

namespace MTerminal.Views;

public partial class TerminalPaneView : UserControl
{
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
            return;
        }

        var theme = vm.Theme;
        var terminal = new TerminalControl
        {
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

        terminal.AddHandler(KeyDownEvent, OnTerminalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);

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
                await terminal.LaunchProcess(vm.WorkingDirectory, vm.Shell.ExecutablePath, vm.Shell.Args);
            }
        }
    }

    private async void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        try
        {
            if (sender is not TerminalControl tc) return;

            if (e.Key == Key.V && (e.KeyModifiers == KeyModifiers.Alt || e.KeyModifiers == KeyModifiers.Control))
            {
                e.Handled = true;
                var tv = tc.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
                if (tv != null)
                    await tv.PasteAsync();
            }
            else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                if (tc.Terminal?.Selection?.HasSelection == true)
                {
                    e.Handled = true;
                    var tv = tc.GetVisualDescendants().OfType<TerminalView>().FirstOrDefault();
                    if (tv != null)
                        await tv.CopyAsync();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Terminal key handler failed: {0}", ex.Message);
        }
    }
}
