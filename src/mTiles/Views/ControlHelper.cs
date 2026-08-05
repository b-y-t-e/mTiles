using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace mTiles.Views;

internal static class ControlHelper
{
    public static void DetachFromParent(Control control)
    {
        if (control.Parent is Panel panel)
            panel.Children.Remove(control);
        else if (control.Parent is ContentControl cc)
            cc.Content = null;
        else if (control.Parent is Decorator dec)
            dec.Child = null;
    }

    public static IBrush FindBrush(this StyledElement element, string key) =>
        element.FindResource(key) as IBrush
        ?? Application.Current?.FindResource(key) as IBrush
        ?? Brushes.Magenta;
}
