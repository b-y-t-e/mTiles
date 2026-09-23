using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace mTiles.Controls;

/// <summary>One drawn row: the option, and what the picker currently thinks of it.</summary>
/// <remarks>
/// <para>The picker owns these and the caller never sees one. It exists because highlight and selection
/// are the <i>picker's</i> state and the row's template has to read them: binding a row's appearance back
/// up to the control that contains it means a template that only works inside one control's tree, and an
/// <c>ItemsControl</c> that rebuilds its items every keystroke.</para>
/// <para><b>Highlight is not selection.</b> Selection is the row this picker is on; highlight is the row
/// the keyboard is over, which moves with the arrows and starts on the selection when the list opens. The
/// application's own design rules make the same distinction for the workspace list, and for the same
/// reason: one treatment for both makes the current row unfindable the moment the pointer enters the list.</para>
/// </remarks>
public sealed class PickerRow : INotifyPropertyChanged
{
    private bool _isHighlighted;
    private bool _isSelected;

    public PickerRow(PickerOption option) => Option = option;

    public PickerOption Option { get; }

    public string Title => Option.Title;
    public string? Description => Option.Description;
    public string? Detail => Option.Detail;
    public string? Badge => Option.Badge;
    public string? Shortcut => Option.Shortcut;
    public object? Icon => Option.Icon;
    public bool IsAction => Option.IsAction;
    public bool CanBePicked => Option.IsEnabled;

    /// <summary>Why not, drawn under the row. Empty while the row can be picked, so the template can bind
    /// visibility to the text rather than to a second flag that could disagree with it.</summary>
    public string? Reason => Option.IsEnabled ? null : Option.DisabledReason;

    public bool HasIcon => Option.Icon is not null;
    public bool HasDescription => !string.IsNullOrEmpty(Option.Description);
    public bool HasDetail => !string.IsNullOrEmpty(Option.Detail);
    public bool HasBadge => !string.IsNullOrEmpty(Option.Badge);
    public bool HasShortcut => !string.IsNullOrEmpty(Option.Shortcut);
    public bool HasReason => !string.IsNullOrEmpty(Reason);

    public bool IsHighlighted
    {
        get => _isHighlighted;
        set => Set(ref _isHighlighted, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
