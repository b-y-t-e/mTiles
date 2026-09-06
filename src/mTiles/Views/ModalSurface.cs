using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mTiles.Services;

namespace mTiles.Views;

/// <summary>Makes one control modal to the keyboard as well as to the pointer.</summary>
/// <remarks>
/// <para>A scrim stops the pointer and nothing else. Without this, Tab walked out of a dialog into
/// the workspace list and the tile behind it, and Alt+Space dictated a sentence into that tile —
/// with the phone's Enter then able to run it. Both were reported from a real session.</para>
/// <para><b>Here rather than inside <see cref="OverlayHost"/>, because Settings is not one of its
/// overlays.</b> The settings dialog is a hand-written panel in <c>MainWindow.axaml</c>, older than
/// the host and with closing rules of its own, so a modality that lived in the host covered every
/// dialog except the one people open most. Two callers, one implementation: the host claims a
/// surface per open dialog, the main window claims one for Settings while it is showing.</para>
/// <para><b>Only the topmost claim traps.</b> These nest in practice — Settings is itself modal and
/// asks questions of its own — and without the stack the outer surface pulls focus back out of the
/// inner one, which is a fight between two traps rather than a modal dialog.</para>
/// </remarks>
public static class ModalSurface
{
    private static readonly List<Claim> Claims = [];

    /// <summary>Claims <paramref name="surface"/> as modal until the handle is disposed.</summary>
    /// <param name="focusInto">Puts the keyboard where this surface wants it. Called when focus has
    /// to be pulled back; the surface itself is focused when nothing is given.</param>
    /// <param name="ownsDictationShortcut">See <see cref="ModalScope.ShortcutIsSpokenFor"/>.</param>
    public static IDisposable Take(Control surface, Action? focusInto = null,
        bool ownsDictationShortcut = false)
    {
        var claim = new Claim(surface, focusInto, ownsDictationShortcut);
        Claims.Add(claim);
        claim.Arm();
        return claim;
    }

    /// <summary>Puts the keyboard inside <paramref name="surface"/>.</summary>
    /// <remarks>
    /// <para>A dialog's outermost element is a <see cref="Border"/> in every case here — the
    /// settings card and the overlay host's own card alike — and a border is not focusable, so
    /// focusing it is a call that does nothing and leaves the keyboard on whatever opened the
    /// dialog. From there <see cref="KeyboardNavigationMode.Cycle"/> does not apply either, because
    /// focus is not inside the surface for it to cycle within, and the trap's other half only ever
    /// pulls focus back to the same unfocusable border.</para>
    /// <para>So the first focusable control in the dialog is what takes it — the same thing Tab
    /// would have reached first — and the surface itself is only the fallback for a dialog that has
    /// no such control at all.</para>
    /// </remarks>
    public static void FocusInto(Control surface)
    {
        var first = FirstFocusable(surface);
        (first ?? surface).Focus(NavigationMethod.Tab);
    }

    private static Control? FirstFocusable(Visual at)
    {
        foreach (var child in at.GetVisualChildren())
        {
            if (child is Control c && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
                return c;

            if (FirstFocusable(child) is { } deeper)
                return deeper;
        }

        return null;
    }

    private sealed class Claim : IDisposable
    {
        private readonly Control _surface;
        private readonly Action? _focusInto;
        private readonly IDisposable _scope;
        private TopLevel? _top;
        private bool _released;

        public Claim(Control surface, Action? focusInto, bool ownsDictationShortcut)
        {
            _surface = surface;
            _focusInto = focusInto;
            _scope = ModalScope.Enter(ownsDictationShortcut);
        }

        public void Arm()
        {
            // Cycle is the half that works once focus is already inside: Tab then wraps within the
            // surface instead of stepping past its last control into whatever is behind it.
            KeyboardNavigation.SetTabNavigation(_surface, KeyboardNavigationMode.Cycle);

            // And the half Cycle cannot do: focus arriving from outside never passes through the
            // surface at all, so the handler goes on the top level rather than on the dialog.
            _top = TopLevel.GetTopLevel(_surface);
            _top?.AddHandler(InputElement.GotFocusEvent, OnFocusMoved, RoutingStrategies.Bubble);

            // Nothing has moved the keyboard into the dialog yet: it is still on the button that
            // opened it, which is outside the surface and therefore past both halves of the trap.
            // Posted so a dialog that focuses a field of its own — which is posted at the same
            // priority, and later — still has the last word on where the keyboard lands.
            PullFocusIn();
        }

        private void OnFocusMoved(object? sender, RoutedEventArgs e)
        {
            if (_released || Claims.Count == 0 || !ReferenceEquals(Claims[^1], this))
                return;

            // Already inside this surface — including a control nested any depth down in it.
            if (e.Source is not Visual v || IsInsideSurface(v))
                return;

            PullFocusIn();
        }

        /// <summary>Moves the keyboard into this surface, on the next pass rather than now.</summary>
        /// <remarks>Posted, because the caller that matters most runs *during* a focus change:
        /// moving focus again inside the notification is how a focus manager ends up in a loop with
        /// itself.</remarks>
        private void PullFocusIn()
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (_released) return;
                    if (_focusInto is not null) _focusInto();
                    else FocusInto(_surface);
                },
                DispatcherPriority.Input);
        }

        private bool IsInsideSurface(Visual v)
        {
            for (Visual? at = v; at is not null; at = at.GetVisualParent())
                if (ReferenceEquals(at, _surface))
                    return true;
            return false;
        }

        public void Dispose()
        {
            // Once. A second release would take ModalScope's count below what is open, and the first
            // symptom of that is a dialog modal to nothing.
            if (_released) return;
            _released = true;

            Claims.Remove(this);
            _top?.RemoveHandler(InputElement.GotFocusEvent, OnFocusMoved);
            _top = null;
            KeyboardNavigation.SetTabNavigation(_surface, KeyboardNavigationMode.Continue);
            _scope.Dispose();
        }
    }
}
