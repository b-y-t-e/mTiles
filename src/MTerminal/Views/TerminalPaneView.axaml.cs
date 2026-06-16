using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Iciclecreek.Terminal;
using MTerminal.ViewModels;

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
            if (cached.Parent is Panel p) p.Children.Remove(cached);
            else if (cached.Parent is ContentControl cc) cc.Content = null;
            else if (cached.Parent is Decorator d) d.Child = null;
            Content = cached;
            return;
        }

        var terminal = new TerminalControl
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            FontSize = 13,
            BufferSize = 5000
        };
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
}
