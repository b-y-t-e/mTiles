using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace mTiles.Views;

public partial class InputDialog : UserControl, OverlayHost.IFocusOnOpen
{
    public InputDialog()
    {
        InitializeComponent();
    }

    public InputDialog(string title, string placeholder, IEnumerable<string>? suggestions = null)
    {
        InitializeComponent();

        TitleText.Text = title;
        InputBox.PlaceholderText = placeholder;

        if (suggestions != null)
        {
            var list = suggestions.ToList();
            if (list.Count > 0)
            {
                SuggestionsList.ItemsSource = list;
            }
            else
            {
                SuggestionsList.IsVisible = false;
                SuggestionsLabel.IsVisible = false;
            }
        }
        else
        {
            SuggestionsList.IsVisible = false;
            SuggestionsLabel.IsVisible = false;
        }

        OkButton.Click += (_, _) => OverlayHost.CloseWith(this, InputBox.Text?.Trim());
        CancelButton.Click += (_, _) => OverlayHost.CloseWith(this, null);

        SuggestionsList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems is { Count: > 0 } && e.AddedItems[0] is string selected)
                InputBox.Text = selected;
        };

        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                OverlayHost.CloseWith(this, InputBox.Text?.Trim());
        };
    }

    public void FocusOnOpen() => InputBox.Focus();

    /// <summary>Asks for one line of text, in an overlay over <paramref name="owner"/>'s window.</summary>
    public static Task<string?> ShowAsync(Visual owner, string title, string placeholder,
        IEnumerable<string>? suggestions = null)
    {
        if (OverlayHost.For(owner) is not { } host)
            return Task.FromResult<string?>(null);

        return host.ShowAsync<string>(new InputDialog(title, placeholder, suggestions), width: 360);
    }
}
