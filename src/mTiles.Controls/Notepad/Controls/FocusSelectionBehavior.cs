using System;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;

namespace Notepad.Avalonia.Controls;

/// <summary>
/// Focus plumbing shared by <see cref="NoteEditor"/> and <see cref="MarkdownViewer"/>:
/// dropping the selection when focus goes away, and keeping an enclosing
/// <c>ScrollViewer</c> from jumping when the user clicks to select.
/// </summary>
/// <remarks>
/// The two controls model their selection differently (caret plus anchor versus an
/// offset range), so the behaviour asks the owner what to do through callbacks
/// rather than owning the selection itself. Repainting is likewise the owner's
/// business: <paramref name="clearSelection"/> is expected to invalidate.
/// </remarks>
internal sealed class FocusSelectionBehavior
{
    private readonly Control _owner;
    private readonly Func<bool> _shouldClearSelection;
    private readonly Action _clearSelection;

    private bool _suppressBringIntoView;
    private ContextMenu? _contextMenu;
    private bool _subscribedToContextMenu;
    private bool _isAttached;

    /// <param name="shouldClearSelection">
    /// Whether there is a selection that the owner's settings allow dropping.
    /// </param>
    /// <param name="clearSelection">Drops the selection and repaints.</param>
    public FocusSelectionBehavior(Control owner, Func<bool> shouldClearSelection, Action clearSelection)
    {
        _owner = owner;
        _shouldClearSelection = shouldClearSelection;
        _clearSelection = clearSelection;
        _contextMenu = owner.ContextMenu;

        // Handling an event the owner raises on itself cannot keep it alive, so
        // this one needs no teardown. The context menu is different: a menu shared
        // by the host outlives the control, hence Attach/Detach below.
        owner.AddHandler(Control.RequestBringIntoViewEvent, OnRequestBringIntoView);
    }

    /// <summary>Call from <c>OnAttachedToVisualTree</c>.</summary>
    public void Attach()
    {
        _isAttached = true;
        SubscribeToContextMenu();
    }

    /// <summary>Call from <c>OnDetachedFromVisualTree</c>.</summary>
    public void Detach()
    {
        UnsubscribeFromContextMenu();
        _isAttached = false;
    }

    // An enclosing ScrollViewer answers a focus change by scrolling the focused
    // control into view, and the request covers the WHOLE control — for a long
    // document that drags the text out from under the pointer, so a click meant to
    // start a selection lands somewhere else. Only pointer focus is suppressed;
    // Tab navigation must still scroll the control into view.
    //
    // The framework focuses on pointer press before PointerPressed reaches the
    // control (GotFocus -> RequestBringIntoView -> PointerPressed) and the request
    // is raised while the focus event is still bubbling, so the flag only has to
    // survive that. Dropping it on the next dispatcher turn keeps the window narrow
    // enough that a BringIntoView() the host calls later still works.
    public void HandleGotFocus(FocusChangedEventArgs e)
    {
        if (e.NavigationMethod != NavigationMethod.Pointer) return;

        _suppressBringIntoView = true;
        Dispatcher.UIThread.Post(() => _suppressBringIntoView = false, DispatcherPriority.Input);
    }

    public void HandleLostFocus()
    {
        // A context menu takes focus while it is open, and clearing now would leave
        // its commands nothing to act on. OnContextMenuClosed finishes the job once
        // the menu is gone — without it a selection made before a right-click would
        // be stranded, because no further LostFocus is ever raised.
        if (_contextMenu?.IsOpen == true) return;

        ClearSelection();
    }

    public void HandleContextMenuChanged(ContextMenu? menu)
    {
        if (ReferenceEquals(_contextMenu, menu)) return;

        UnsubscribeFromContextMenu();
        _contextMenu = menu;
        SubscribeToContextMenu();
    }

    private void SubscribeToContextMenu()
    {
        if (_subscribedToContextMenu || !_isAttached || _contextMenu == null) return;
        _contextMenu.Closed += OnContextMenuClosed;
        _subscribedToContextMenu = true;
    }

    private void UnsubscribeFromContextMenu()
    {
        if (!_subscribedToContextMenu || _contextMenu == null) return;
        _contextMenu.Closed -= OnContextMenuClosed;
        _subscribedToContextMenu = false;
    }

    private void OnContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        // Deferred for two reasons: focus may come back to the owner as the menu
        // unwinds, and a menu command runs synchronously around the close — it must
        // still see the selection it was invoked on.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_owner.IsFocused)
                ClearSelection();
        }, DispatcherPriority.Input);
    }

    private void ClearSelection()
    {
        if (_shouldClearSelection())
            _clearSelection();
    }

    private void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (_suppressBringIntoView && ReferenceEquals(e.TargetObject, _owner))
            e.Handled = true;
    }
}
