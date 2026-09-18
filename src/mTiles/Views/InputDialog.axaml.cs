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

    public InputDialog(string title, string placeholder, IEnumerable<string>? suggestions = null,
        string? description = null, bool secret = false)
    {
        InitializeComponent();

        TitleText.Text = title;
        InputBox.PlaceholderText = placeholder;

        if (description is { Length: > 0 })
        {
            DescriptionText.Text = description;
            DescriptionText.IsVisible = true;
        }

        // A passphrase is not trimmed: leading and trailing spaces are part of it, and silently eating
        // them here would write a file nothing can open — including this application, which trims on the
        // way back in as well.
        _trim = !secret;
        if (secret) InputBox.PasswordChar = '•';

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

        OkButton.Click += (_, _) => OverlayHost.CloseWith(this, Value());
        CancelButton.Click += (_, _) => OverlayHost.CloseWith(this, null);

        SuggestionsList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems is { Count: > 0 } && e.AddedItems[0] is string selected)
                InputBox.Text = selected;
        };

        InputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                OverlayHost.CloseWith(this, Value());
        };
    }

    private readonly bool _trim = true;

    private string? Value() => _trim ? InputBox.Text?.Trim() : InputBox.Text ?? "";

    public void FocusOnOpen() => InputBox.Focus();

    /// <summary>Asks for one line of text, in an overlay over <paramref name="owner"/>'s window.</summary>
    public static Task<string?> ShowAsync(Visual owner, string title, string placeholder,
        IEnumerable<string>? suggestions = null)
    {
        if (OverlayHost.For(owner) is not { } host)
            return Task.FromResult<string?>(null);

        return host.ShowAsync<string>(new InputDialog(title, placeholder, suggestions), width: 360);
    }

    /// <summary>
    /// Asks for a passphrase — masked, untrimmed — in an overlay over <paramref name="owner"/>'s window.
    /// </summary>
    /// <remarks>An empty answer is an answer (export without the secrets); cancelling is null and stops
    /// whatever asked. The two are different and the description is where that is said.</remarks>
    public static Task<string?> ShowSecretAsync(Visual owner, string title, string description,
        string placeholder = "Passphrase")
    {
        if (OverlayHost.For(owner) is not { } host)
            return Task.FromResult<string?>(null);

        return host.ShowAsync<string>(
            new InputDialog(title, placeholder, null, description, secret: true), width: 420);
    }
}
