using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using mTiles.Services;
using mTiles.ViewModels;
using TerminalControl = Terminal.Avalonia.TerminalControl;

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
            TerminalHost.Content = cached;
            return;
        }

        var terminal = new TerminalControl
        {
            FontFamily = new FontFamily(vm.FontFamily),
            FontSize = vm.FontSize,
            ScrollbackCapacity = 5000,
            Palette = ThemeBridge.ToPalette(vm.Theme),
            // Tiles are resized constantly — splitting and dragging a splitter is the main interaction
            // here — and a width change leaves PSReadLine anchored to the old prompt width, so the next
            // keystroke overwrites the prompt. The control's fix sends one Ctrl+L at the final width,
            // primary screen only. Its cost: that Ctrl+L reaches a raw stdin reader too, so a password
            // typed at a prompt that was resized mid-entry picks up a stray control character.
            RedrawShellOnResize = true,
            // Claude Code (and other agents) read an image off the clipboard when they see Ctrl+V.
            // Swallowing the key on a non-text clipboard, which is the control's default, is the
            // difference between image paste working and silently doing nothing.
            ForwardCtrlVWhenClipboardHasNoText = true,
            // The one place a shell's process id is knowable. The control spawns through this factory
            // and keeps the connection to itself, so a tile that wants to say how much memory it is
            // holding has to be told here — and told again when that shell exits, by the connection
            // itself rather than by the control, because the connection knows which session died.
            PtyFactory = options => WatchChildProcess(Terminal.Pty.PtyConnection.Start(options), vm),
        };

        vm.AttachControl(terminal);
        TerminalHost.Content = terminal;

        AttachedToVisualTree += OnceAttached;

        async void OnceAttached(object? s, VisualTreeAttachmentEventArgs args)
        {
            AttachedToVisualTree -= OnceAttached;
            // The shell is sized from the control's measured cell grid, so launching before layout has
            // run would start it at the default 80x24 and reflow it a moment later.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

            if (vm.IsLaunched) return;
            vm.IsLaunched = true;
            TileLauncher.Launch(terminal, vm);
        }
    }

    /// <summary>Tells the tile which process its shell is, for as long as that process lives.</summary>
    private static Terminal.Pty.IPtyConnection WatchChildProcess(
        Terminal.Pty.IPtyConnection pty, TerminalTileViewModel vm)
    {
        var processId = pty.ProcessId;
        vm.TrackChildProcess(processId);
        // A shell that has already gone simply is not in the process table, so the memory reading is
        // right either way — this only keeps the tile from carrying a pid the system may hand out again.
        pty.Exited += _ => vm.ForgetChildProcess(processId);
        return pty;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TerminalTileViewModel vm) return;
        if (vm.CachedControl is not TerminalControl terminal) return;

        // Font, palette and size changes repaint on their own — no layout nudge needed.
        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(TerminalTileViewModel.Theme):
                    terminal.Palette = ThemeBridge.ToPalette(vm.Theme);
                    break;
                case nameof(TerminalTileViewModel.FontFamily):
                    terminal.FontFamily = new FontFamily(vm.FontFamily);
                    break;
                case nameof(TerminalTileViewModel.FontSize):
                    terminal.FontSize = vm.FontSize;
                    break;
            }
        });
    }
}
