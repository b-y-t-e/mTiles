using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MTerminal.ViewModels;

namespace MTerminal.Views;

public partial class TodoTileView : UserControl
{
    private TodoTileViewModel? _subscribedVm;

    public TodoTileView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AddHandler(KeyDownEvent, OnTunnelKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(TextBox.TextChangedEvent, OnTextChanged, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;

        if (DataContext is not TodoTileViewModel vm) return;

        _subscribedVm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;

        FontFamily = new FontFamily(vm.FontFamily);
        FontSize = vm.FontSize;

        AttachedToVisualTree -= OnceAttached;
        AttachedToVisualTree += OnceAttached;
    }

    private void OnceAttached(object? s, VisualTreeAttachmentEventArgs args)
    {
        AttachedToVisualTree -= OnceAttached;
        if (_subscribedVm?.Items.Count > 0)
            Dispatcher.UIThread.Post(() => FocusItemAt(0), DispatcherPriority.Input);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TodoTileViewModel vm) return;
        if (e.Source is not TextBox tb || !tb.Classes.Contains("todo-inline")) return;

        var id = tb.Tag as string;
        if (id == null) return;

        if (e.Key == Key.Enter)
        {
            var idx = IndexOfItem(vm, id);
            if (idx < 0) return;

            var newId = vm.InsertItemAfter(idx);
            Dispatcher.UIThread.Post(() => FocusItemById(newId), DispatcherPriority.Background);
            e.Handled = true;
        }
        else if ((e.Key == Key.Back || e.Key == Key.Delete) && string.IsNullOrEmpty(tb.Text))
        {
            var idx = IndexOfItem(vm, id);
            if (idx < 0 || vm.Items.Count <= 1) return;

            var focusIdx = e.Key == Key.Back && idx > 0 ? idx - 1 : Math.Min(idx, vm.Items.Count - 2);
            vm.RemoveItemCommand.Execute(id);
            FocusItemAt(focusIdx, moveCursorToEnd: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            var idx = IndexOfItem(vm, id);
            if (idx > 0) FocusItemAt(idx - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            var idx = IndexOfItem(vm, id);
            if (idx >= 0 && idx < vm.Items.Count - 1) FocusItemAt(idx + 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Home && e.KeyModifiers == KeyModifiers.Control)
        {
            FocusItemAt(0);
            e.Handled = true;
        }
        else if (e.Key == Key.End && e.KeyModifiers == KeyModifiers.Control)
        {
            FocusItemAt(vm.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp)
        {
            var idx = IndexOfItem(vm, id);
            FocusItemAt(Math.Max(0, idx - 10));
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown)
        {
            var idx = IndexOfItem(vm, id);
            FocusItemAt(Math.Min(vm.Items.Count - 1, idx + 10));
            e.Handled = true;
        }
    }

    private static int IndexOfItem(TodoTileViewModel vm, string id)
    {
        for (int i = 0; i < vm.Items.Count; i++)
            if (vm.Items[i].Id == id) return i;
        return -1;
    }

    private void FocusItemById(string id)
    {
        var tb = FindTextBoxByTag(id);
        tb?.Focus();
    }

    private void FocusItemAt(int index, bool moveCursorToEnd = false)
    {
        if (DataContext is not TodoTileViewModel vm) return;
        if (index < 0 || index >= vm.Items.Count) return;

        var id = vm.Items[index].Id;
        Dispatcher.UIThread.Post(() =>
        {
            var tb = FindTextBoxByTag(id);
            if (tb != null)
            {
                tb.Focus();
                if (moveCursorToEnd)
                    tb.CaretIndex = tb.Text?.Length ?? 0;
            }
        }, DispatcherPriority.Input);
    }

    private TextBox? FindTextBoxByTag(string id)
    {
        foreach (var tb in ItemsList.GetVisualDescendants().OfType<TextBox>())
        {
            if (tb.Tag is string tag && tag == id)
                return tb;
        }
        return null;
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (e.Source is TextBox tb && tb.Classes.Contains("todo-inline"))
            _subscribedVm?.OnItemTextChanged();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TodoTileViewModel vm) return;

        Dispatcher.UIThread.Post(() =>
        {
            switch (e.PropertyName)
            {
                case nameof(TodoTileViewModel.FontFamily):
                    FontFamily = new FontFamily(vm.FontFamily);
                    break;
                case nameof(TodoTileViewModel.FontSize):
                    FontSize = vm.FontSize;
                    break;
            }
        });
    }
}
