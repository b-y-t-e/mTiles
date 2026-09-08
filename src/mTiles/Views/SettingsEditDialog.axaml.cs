using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Linq;

namespace mTiles.Views;

/// <summary>The form for one settings entry, drawn by <see cref="OverlayHost"/> like any other dialog.</summary>
/// <remarks>
/// <para>It was a hand-written <c>Panel</c> inside <c>SettingsView</c> — its own scrim, its own card —
/// which made it the third implementation of the same dialog, and it behaved differently from the
/// others: a click in its scrim called <c>CancelEditing</c>, so a misclick beside a half-filled
/// provider row threw away the key that had just been pasted into it.</para>
/// <para>The two model boxes and the first-field focus moved here with the markup. They belong to the
/// form, and leaving them behind would have been <c>SettingsView</c> reaching across a dialog boundary
/// into controls it no longer contains.</para>
/// </remarks>
public partial class SettingsEditDialog : UserControl, OverlayHost.IFocusOnOpen
{
    /// <remarks>
    /// <b>No hand-written <c>InitializeComponent</c> here.</b> One that only calls
    /// <c>AvaloniaXamlLoader.Load(this)</c> hides the generated one, and it is the generated one that
    /// assigns the <c>x:Name</c> fields — so <see cref="AgentModelBox"/> was null and the constructor
    /// threw a <c>NullReferenceException</c> on the line below. The form is built inside an
    /// <c>async void</c> handler, so that failure never reached a user as anything but a button that
    /// did nothing: <b>every one of the four forms — agent, provider, sign-in and manual database
    /// connection — stopped opening at all.</b>
    /// </remarks>
    public SettingsEditDialog()
    {
        InitializeComponent();

        // Both model fields complete against the same catalogue and must narrow it the same way, so
        // the rule is one function used twice rather than an attribute repeated — see ModelSearch for
        // why "contains" is not enough.
        AgentModelBox.ItemFilter = MatchesModel;
        AgentFastModelBox.ItemFilter = MatchesModel;
    }

    private static bool MatchesModel(string? search, object? item) =>
        ModelSearch.Matches(search, item as string);

    /// <summary>Puts the caret in the form the moment it opens.</summary>
    /// <remarks>
    /// Posted at <c>Loaded</c> rather than done here: the form is built as the overlay is shown, and a
    /// control that has not been laid out yet cannot take focus. The first field that is actually on
    /// screen and enabled, because which one that is depends on which of the four forms this is.
    /// </remarks>
    public void FocusOnOpen() => Dispatcher.UIThread.Post(() =>
    {
        var first = this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled);
        first?.Focus();
        first?.SelectAll();
    }, DispatcherPriority.Loaded);
}
